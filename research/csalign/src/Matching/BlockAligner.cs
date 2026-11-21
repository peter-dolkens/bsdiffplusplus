using System.Diagnostics;

namespace BsDiff6.Matching;

/// <summary>Diagnostics of one <see cref="BlockAligner"/> run.</summary>
public sealed class BlockAlignStats
{
    public int BlockLength { get; internal set; }
    public int P1 { get; internal set; }
    public int P2 { get; internal set; }
    public int FftSize { get; internal set; }
    public int MinRunLength { get; internal set; }
    public int Blocks { get; internal set; }
    public int QueriedBlocks { get; internal set; }
    public int PredictedBlocks { get; internal set; }
    /// <summary>Blocks whose chosen offset came from the continuity candidate (it beat the index's best).</summary>
    public int ContinuityChosen { get; internal set; }
    public int RunsBeforeTweak { get; internal set; }
    public int BoundariesMoved { get; internal set; }
    public int RunsRemoved { get; internal set; }
    public TimeSpan IndexTime { get; internal set; }
    public TimeSpan QueryTime { get; internal set; }
    public TimeSpan TweakTime { get; internal set; }
    /// <summary>True when block alignment was skipped because the old file is too small.</summary>
    public bool Skipped { get; internal set; }

    public override string ToString() =>
        Skipped ? "skipped" :
        $"B={BlockLength} p=({P1},{P2}) N={FftSize} blocks={Blocks} queried={QueriedBlocks} predicted={PredictedBlocks} " +
        $"continuity={ContinuityChosen} runs={RunsBeforeTweak} moved={BoundariesMoved} removed={RunsRemoved} " +
        $"index={IndexTime.TotalMilliseconds:F0}ms query={QueryTime.TotalMilliseconds:F0}ms tweak={TweakTime.TotalMilliseconds:F0}ms";
}

/// <summary>Block alignment of the new file against the old file (thesis §2.4, DESIGN §4).</summary>
public static class BlockAligner
{
    /// <summary>
    /// Aligns blocks of <paramref name="new"/> against <paramref name="old"/>. Returns a canonical
    /// alignment (DESIGN §3) in which an unmatched segment means "no prediction". The inputs are
    /// copied (worker threads cannot share a span); the <see cref="ReadOnlyMemory{T}"/> overload
    /// avoids the copy.
    /// </summary>
    public static Segment[] Align(ReadOnlySpan<byte> old, ReadOnlySpan<byte> @new, BlockAlignOptions? options = null) =>
        Align(new ReadOnlyMemory<byte>(old.ToArray()), new ReadOnlyMemory<byte>(@new.ToArray()), options, out _);

    /// <summary>As <see cref="Align(ReadOnlySpan{byte}, ReadOnlySpan{byte}, BlockAlignOptions?)"/>, without copying the inputs, and with diagnostics.</summary>
    public static Segment[] Align(ReadOnlyMemory<byte> old, ReadOnlyMemory<byte> @new, BlockAlignOptions? options,
                                  out BlockAlignStats stats)
    {
        options = (options ?? new BlockAlignOptions()).Validated();
        stats = new BlockAlignStats();
        int n = old.Length, m = @new.Length;
        if (m == 0) return Array.Empty<Segment>();

        int blockLen = options.ResolveBlockLength(n);
        double lValue = options.LFactor * blockLen;
        if (n < options.MinOldLength || lValue >= n || n < 2)
        {
            stats.Skipped = true;
            return new[] { new Segment(0, m, Segment.Unmatched) };
        }

        var sw = Stopwatch.StartNew();
        float[] weights = options.FrequencyWeighting
            ? ByteWeights.InverseSqrtFrequency(old.Span, @new.Span, options.WeightFrequencyFloor)
            : ByteWeights.Uniform();
        var index = new MismatchIndex(old.Span, blockLen, weights, options);
        stats.IndexTime = sw.Elapsed;
        stats.BlockLength = blockLen;
        stats.P1 = index.P1;
        stats.P2 = index.P2;
        stats.FftSize = index.FftSize;

        sw.Restart();
        int[] offsets = ChooseOffsets(index, old, @new, options, stats);
        stats.QueryTime = sw.Elapsed;

        sw.Restart();
        var runs = new List<OffsetRun>();
        for (int b = 0; b < offsets.Length; b++)
        {
            int start = b * blockLen, end = (int)Math.Min((long)start + blockLen, m);
            if (runs.Count > 0 && runs[runs.Count - 1].Offset == offsets[b])
            {
                OffsetRun last = runs[runs.Count - 1];
                last.End = end;
                runs[runs.Count - 1] = last;
            }
            else runs.Add(new OffsetRun(start, end, offsets[b]));
        }
        stats.RunsBeforeTweak = runs.Count;
        int minRun = options.ResolveMinRunLength(blockLen);
        stats.MinRunLength = minRun;
        if (options.Tweak)
        {
            var tweaker = new Tweaker(runs, minRun, options.UnmatchedCost, (int)Math.Min(int.MaxValue, (long)options.TweakWindowBlocks * blockLen));
            tweaker.Run(old.Span, @new.Span);
            stats.BoundariesMoved = tweaker.BoundariesMoved;
            stats.RunsRemoved = tweaker.RunsRemoved;
        }
        Segment[] result = ToSegments(runs, n);
        stats.TweakTime = sw.Elapsed;
        return result;
    }

    /// <summary>Per-block offset (old - new), or <see cref="OffsetRun.None"/>.</summary>
    private static int[] ChooseOffsets(MismatchIndex index, ReadOnlyMemory<byte> old, ReadOnlyMemory<byte> @new,
                                       BlockAlignOptions options, BlockAlignStats stats)
    {
        int m = @new.Length;
        int blockLen = index.BlockLength;
        int blocks = (int)(((long)m + blockLen - 1) / blockLen);
        stats.Blocks = blocks;
        var bestX = new long[blocks];
        var bestMatches = new int[blocks];
        var queried = new bool[blocks];
        int shortLimit = (int)Math.Ceiling(options.ShortBlockFraction * blockLen);

        for (int b = 0; b < blocks; b++)
        {
            int len = BlockLen(b, blockLen, m);
            queried[b] = !(b > 0 && b == blocks - 1 && len < shortLimit);
            bestMatches[b] = -1;
        }

        int verifyCount = options.VerifyCount;
        Action<int, MismatchQuery, MismatchCandidate[]> body = (b, query, cands) =>
        {
            if (!queried[b]) return;
            int t0 = b * blockLen;
            ReadOnlySpan<byte> block = @new.Span.Slice(t0, BlockLen(b, blockLen, m));
            int count = query.Find(block, cands);
            ReadOnlySpan<byte> oldSpan = old.Span;
            int best = -1;
            long bx = 0;
            for (int c = 0; c < count; c++)
            {
                int matches = MatchCounter.Count(oldSpan, cands[c].OldPos, block);
                if (matches > best) { best = matches; bx = cands[c].OldPos; }
            }
            bestMatches[b] = best;
            bestX[b] = bx;
        };

        if (options.Parallel && blocks > 1)
        {
            var po = new ParallelOptions();
            if (options.MaxDegreeOfParallelism > 0) po.MaxDegreeOfParallelism = options.MaxDegreeOfParallelism;
            Parallel.For(0, blocks, po,
                () => (index.CreateQuery(), new MismatchCandidate[verifyCount]),
                (b, _, local) => { body(b, local.Item1, local.Item2); return local; },
                _ => { });
        }
        else
        {
            MismatchQuery query = index.CreateQuery();
            var cands = new MismatchCandidate[verifyCount];
            for (int b = 0; b < blocks; b++) body(b, query, cands);
        }

        // Sequential passes: continuity candidates and the acceptance threshold.
        var offsets = new int[blocks];
        var counts = new int[blocks];
        bool both = options.BidirectionalContinuity;
        int prev = OffsetRun.None;
        ReadOnlySpan<byte> o = old.Span, nw = @new.Span;
        for (int b = 0; b < blocks; b++)
        {
            if (queried[b]) stats.QueriedBlocks++;
            int t0 = b * blockLen;
            int len = BlockLen(b, blockLen, m);
            long x = bestX[b];
            int matches = bestMatches[b];
            if ((options.ContinuityCandidate || !queried[b]) && prev != OffsetRun.None)
            {
                long xc = (long)t0 + prev;
                int mc = MatchCounter.Count(o, xc, nw.Slice(t0, len));
                if (mc >= matches)
                {
                    if (mc > matches && queried[b] && matches >= 0) stats.ContinuityChosen++;
                    matches = mc;
                    x = xc;
                }
            }
            if (matches > 0 && matches >= options.MinMatchFraction * len)
            {
                offsets[b] = (int)(x - t0);
                counts[b] = matches;
            }
            else offsets[b] = OffsetRun.None;
            // Design: the previous block's offset. Bidirectional: the nearest predicted block's.
            if (!both || offsets[b] != OffsetRun.None) prev = offsets[b];
        }

        if (both && options.ContinuityCandidate)
        {
            int next = OffsetRun.None;
            for (int b = blocks - 1; b >= 0; b--)
            {
                if (next != OffsetRun.None && next != offsets[b])
                {
                    int t0 = b * blockLen;
                    int len = BlockLen(b, blockLen, m);
                    int mc = MatchCounter.Count(o, (long)t0 + next, nw.Slice(t0, len));
                    if (mc > counts[b] && mc >= options.MinMatchFraction * len)
                    {
                        stats.ContinuityChosen++;
                        offsets[b] = next;
                        counts[b] = mc;
                    }
                }
                if (offsets[b] != OffsetRun.None) next = offsets[b];
            }
        }
        for (int b = 0; b < blocks; b++) if (offsets[b] != OffsetRun.None) stats.PredictedBlocks++;
        return offsets;
    }

    private static int BlockLen(int b, int blockLen, int m) => (int)Math.Min(blockLen, (long)m - (long)b * blockLen);

    /// <summary>Converts runs to segments, making positions whose old position is out of range unmatched.</summary>
    private static Segment[] ToSegments(List<OffsetRun> runs, int n)
    {
        var segs = new List<Segment>(runs.Count + 4);
        foreach (OffsetRun r in runs)
        {
            if (!r.HasOffset)
            {
                segs.Add(new Segment(r.Start, r.Length, Segment.Unmatched));
                continue;
            }
            long lo = Math.Max(r.Start, -(long)r.Offset);
            long hi = Math.Min(r.End, (long)n - r.Offset);
            if (lo >= hi)
            {
                segs.Add(new Segment(r.Start, r.Length, Segment.Unmatched));
                continue;
            }
            if (lo > r.Start) segs.Add(new Segment(r.Start, (int)(lo - r.Start), Segment.Unmatched));
            segs.Add(new Segment((int)lo, (int)(hi - lo), (int)(lo + r.Offset)));
            if (hi < r.End) segs.Add(new Segment((int)hi, (int)(r.End - hi), Segment.Unmatched));
        }
        return AlignmentUtil.Canonicalize(segs);
    }
}
