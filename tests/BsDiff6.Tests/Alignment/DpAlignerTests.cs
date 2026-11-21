using BsDiff6.Alignment;

namespace BsDiff6.Tests.Alignment;

public class DpAlignerTests
{
    private static void AssertCanonical(Segment[] segs, int n, int m)
    {
        AlignmentUtil.Validate(segs, n, m);
        Assert.Equal(AlignmentUtil.Canonicalize(segs), segs);
    }

    private static Segment[] RunChecked(byte[] old, byte[] nw, LocalMatches? local, IReadOnlyList<Segment>? block,
        DpOptions? opt = null, DpDiagnostics? diag = null)
    {
        opt ??= new DpOptions();
        diag ??= new DpDiagnostics();
        Segment[] segs = DpAligner.Align(old, nw, local, block, opt, diag);
        AssertCanonical(segs, old.Length, nw.Length);
        Assert.Equal(diag.Cost, DpAligner.PathCost(old, nw, segs, opt));
        return segs;
    }

    /// <summary>Random block alignment over the new file, valid for the old file.</summary>
    private static Segment[] RandomBlock(Random rng, int n, int m)
    {
        var list = new List<Segment>();
        int pos = 0;
        while (pos < m)
        {
            int len = rng.Next(1, Math.Max(2, m / 3));
            len = Math.Min(len, m - pos);
            int oldPos = -1;
            if (n >= len && rng.Next(3) > 0) oldPos = rng.Next(n - len + 1);
            list.Add(new Segment(pos, len, oldPos));
            pos += len;
        }
        return AlignmentUtil.Canonicalize(list);
    }

    [Fact]
    public void EmptyAndTinyInputs()
    {
        Assert.Empty(RunChecked(Array.Empty<byte>(), Array.Empty<byte>(), LocalAligner.Compute(default, default), null));
        Assert.Empty(RunChecked(new byte[] { 1 }, Array.Empty<byte>(), null, null));
        Assert.Equal(new[] { new Segment(0, 3, -1) },
            RunChecked(Array.Empty<byte>(), new byte[] { 1, 2, 3 }, LocalAligner.Compute(default, new byte[] { 1, 2, 3 }), null));
        Assert.Equal(new[] { new Segment(0, 1, 0) },
            RunChecked(new byte[] { 5 }, new byte[] { 5 }, LocalAligner.Compute(new byte[] { 5 }, new byte[] { 5 }), null));
        Assert.Equal(new[] { new Segment(0, 1, -1) },
            RunChecked(new byte[] { 5 }, new byte[] { 6 }, LocalAligner.Compute(new byte[] { 5 }, new byte[] { 6 }), null));
        Assert.Equal(new[] { new Segment(0, 1, -1) }, RunChecked(new byte[] { 5 }, new byte[] { 5 }, null, null));
        Assert.Equal(new[] { new Segment(0, 1, 0) },
            RunChecked(new byte[] { 5 }, new byte[] { 5 }, null, new[] { new Segment(0, 1, 0) }));
    }

    [Fact]
    public void MatchesReferenceOnRandomInputs()
    {
        var rng = new Random(2024);
        for (int iter = 0; iter < 400; iter++)
        {
            int alphabet = new[] { 2, 3, 4, 16, 256 }[iter % 5];
            int n = rng.Next(0, 120), m = rng.Next(1, 160);
            byte[] old = new byte[n];
            for (int i = 0; i < n; i++) old[i] = (byte)rng.Next(alphabet);
            byte[] nw = Mutate(rng, old, m, alphabet);
            // The reference implements the thesis's exact candidate handling (no pruning).
            var opt = new DpOptions { PruneHopeless = false };
            if (iter % 4 == 1) { opt.LocalCandidates = rng.Next(0, 6); opt.BestCandidates = rng.Next(0, 6); }
            if (iter % 4 == 2) { opt.MinLocalMatchLength = rng.Next(1, 5); opt.SwitchCost = rng.Next(0, 30); }
            if (iter % 4 == 3) { opt.BestCandidates = rng.Next(1, 4); }
            LocalMatches? local = iter % 7 == 6 ? null : LocalAligner.Compute(old, nw);
            Segment[]? block = iter % 3 == 0 ? null : RandomBlock(rng, n, nw.Length);

            var diag = new DpDiagnostics();
            Segment[] segs = RunChecked(old, nw, local, block, opt, diag);
            ReferenceDp.Result r = ReferenceDp.Run(old, nw, local, block, opt);
            Assert.True(r.Cost == diag.Cost, $"iter {iter}: reference {r.Cost}, dp {diag.Cost}");
            Assert.Equal(r.Cost, DpAligner.PathCost(old, nw, r.Path, opt));
        }
    }

    [Fact]
    public void ExhaustiveOnTinyInputs()
    {
        var rng = new Random(11);
        for (int iter = 0; iter < 300; iter++)
        {
            int alphabet = 2 + iter % 2;
            int n = rng.Next(0, 6), m = rng.Next(1, 7);
            byte[] old = new byte[n], nw = new byte[m];
            for (int i = 0; i < n; i++) old[i] = (byte)rng.Next(alphabet);
            for (int i = 0; i < m; i++) nw[i] = (byte)rng.Next(alphabet);
            var opt = new DpOptions { SwitchCost = rng.Next(0, 5), PruneHopeless = false };
            LocalMatches local = LocalAligner.Compute(old, nw);
            Segment[]? block = iter % 2 == 0 ? null : RandomBlock(rng, n, m);
            var diag = new DpDiagnostics();
            RunChecked(old, nw, local, block, opt, diag);
            ReferenceDp.Result r = ReferenceDp.Run(old, nw, local, block, opt);
            long ex = ReferenceDp.Exhaustive(old, nw, r.Candidates, opt);
            Assert.Equal(ex, diag.Cost);
        }
    }

    private static byte[] Mutate(Random rng, byte[] old, int approxLen, int alphabet)
    {
        var res = new List<byte>();
        while (res.Count < approxLen)
        {
            int kind = rng.Next(4);
            if (kind == 0 || old.Length == 0)
            {
                int len = rng.Next(1, 10);
                for (int i = 0; i < len; i++) res.Add((byte)rng.Next(alphabet));
            }
            else
            {
                int len = rng.Next(1, Math.Min(60, old.Length) + 1);
                int s = rng.Next(old.Length - len + 1);
                for (int i = 0; i < len; i++)
                    res.Add(rng.Next(10) == 0 ? (byte)rng.Next(alphabet) : old[s + i]);
            }
        }
        return res.Take(approxLen).ToArray();
    }

    private static byte[] RandomBytes(Random rng, int len)
    {
        byte[] b = new byte[len];
        rng.NextBytes(b);
        return b;
    }

    [Fact]
    public void ScatteredSubstitutionsGiveOneSegment()
    {
        var rng = new Random(1);
        byte[] old = RandomBytes(rng, 50_000);
        byte[] nw = (byte[])old.Clone();
        for (int i = 0; i < 500; i++) nw[rng.Next(nw.Length)] ^= (byte)rng.Next(1, 256);
        Segment[] segs = RunChecked(old, nw, LocalAligner.Compute(old, nw), null);
        Assert.Equal(new[] { new Segment(0, nw.Length, 0) }, segs);
    }

    private static void AssertNear(int expected, int actual, int slack)
        => Assert.True(Math.Abs(expected - actual) <= slack, $"expected {expected} ± {slack}, got {actual}");

    /// <summary>The segment covering new-file position <paramref name="pos"/>.</summary>
    private static Segment At(Segment[] segs, int pos) => segs.First(s => s.NewPos <= pos && pos < s.NewEnd);

    private static (byte[] old, byte[] nw) InsertDelete(Random rng)
    {
        byte[] old = RandomBytes(rng, 40_000);
        var nw = new List<byte>();
        nw.AddRange(old.AsSpan(0, 10_000).ToArray());        // offset 0
        nw.AddRange(RandomBytes(rng, 300));                  // insertion: unmatched
        nw.AddRange(old.AsSpan(10_000, 15_000).ToArray());   // offset -300
        nw.AddRange(old.AsSpan(26_000, 14_000).ToArray());   // deletion of 1000: offset +700
        return (old, nw.ToArray());
    }

    private static void CheckInsertDelete(Segment[] segs)
    {
        Assert.Equal(0, At(segs, 5_000).Offset);
        Assert.True(At(segs, 5_000).IsMatched);
        Assert.False(At(segs, 10_150).IsMatched);
        Assert.Equal(-300, At(segs, 15_000).Offset);
        Assert.Equal(700, At(segs, 30_000).Offset);
        Segment first = At(segs, 5_000), ins = At(segs, 10_150), mid = At(segs, 15_000), last = At(segs, 30_000);
        AssertNear(10_000, first.NewEnd, 3);
        AssertNear(10_300, ins.NewEnd, 3);
        AssertNear(25_300, mid.NewEnd, 3);
        Assert.Equal(mid.NewEnd, last.NewPos);
        Assert.True(segs.Length <= 4, $"{segs.Length} segments");
    }

    [Fact]
    public void InsertionAndDeletionLocalOnly()
    {
        var (old, nw) = InsertDelete(new Random(2));
        CheckInsertDelete(RunChecked(old, nw, LocalAligner.Compute(old, nw), null));
    }

    [Fact]
    public void InsertionAndDeletionBlockOnly()
    {
        var (old, nw) = InsertDelete(new Random(3));
        // Block prediction with boundaries tens of bytes early (block-only mode can extend an
        // offset forwards, through the carried states, but never backwards before its first
        // prediction), and the insertion predicted badly.
        Segment[] block =
        {
            new Segment(0, 9_960, 0),
            new Segment(9_960, 240, -1),
            new Segment(10_200, 15_060, 9_900),
            new Segment(25_260, nw.Length - 25_260, 25_960),
        };
        CheckInsertDelete(RunChecked(old, nw, null, block));
    }

    [Fact]
    public void InsertionAndDeletionBoth()
    {
        var (old, nw) = InsertDelete(new Random(4));
        Segment[] block = { new Segment(0, 10_000, 0), new Segment(10_000, nw.Length - 10_000, 9_700) };
        CheckInsertDelete(RunChecked(old, nw, LocalAligner.Compute(old, nw), block));
    }

    /// <summary>
    /// Thesis footnote 11: a table of 32-bit addresses where the low bytes changed. Every 4-byte word
    /// has its low byte changed and its second byte changed 60% of the time (~40% mismatches), so the
    /// longest exact match is 3 bytes; only block alignment can find it.
    /// </summary>
    [Fact]
    public void AddressTableFollowsBlockPrediction()
    {
        var rng = new Random(5);
        byte[] old = RandomBytes(rng, 60_000);
        byte[] nw = old.AsSpan(0, 30_000).ToArray();
        const int tableStart = 10_000, tableLen = 4_000, tableOld = 40_000;
        for (int i = 0; i < tableLen; i++)
        {
            byte v = old[tableOld + i];
            if (i % 4 == 0 || (i % 4 == 1 && rng.Next(10) < 6)) v ^= (byte)rng.Next(1, 256);
            nw[tableStart + i] = v;
        }
        LocalMatches local = LocalAligner.Compute(old, nw);
        for (int t = tableStart; t < tableStart + tableLen - 8; t++) Assert.True(local.Length[t] <= 4);

        Segment[] block =
        {
            new Segment(0, tableStart, 0),
            new Segment(tableStart, tableLen, tableOld),
            new Segment(tableStart + tableLen, nw.Length - tableStart - tableLen, tableStart + tableLen),
        };
        Segment[] segs = RunChecked(old, nw, local, block);
        Segment table = At(segs, tableStart + tableLen / 2);
        Assert.Equal(tableOld - tableStart, table.Offset);
        AssertNear(tableStart, table.NewPos, 4);
        AssertNear(tableStart + tableLen, table.NewEnd, 4);

        // Without the block prediction, the 3-byte exact matches still put the table's offset into
        // the candidate window often enough for the DP to find it (the DP is stronger than the
        // thesis's local-only method here) ...
        Segment[] localOnly = RunChecked(old, nw, local, null);
        Assert.Equal(tableOld - tableStart, At(localOnly, tableStart + tableLen / 2).Offset);

        // ... but once such short matches are not candidates, only the block prediction finds it.
        var opt = new DpOptions { MinLocalMatchLength = 4 };
        Segment[] withBlock = RunChecked(old, nw, local, block, opt);
        Assert.Equal(tableOld - tableStart, At(withBlock, tableStart + tableLen / 2).Offset);
        Segment[] without = RunChecked(old, nw, local, null, opt);
        int matchedAtTableOffset = without.Where(s => s.IsMatched && s.Offset == tableOld - tableStart).Sum(s => s.Length);
        Assert.True(matchedAtTableOffset < tableLen / 10);
    }

    [Fact]
    public void NoiseIsUnmatched()
    {
        var rng = new Random(6);
        byte[] old = RandomBytes(rng, 30_000);
        var nw = new List<byte>();
        nw.AddRange(old.AsSpan(0, 12_000).ToArray());
        nw.AddRange(RandomBytes(rng, 5_000));
        nw.AddRange(old.AsSpan(12_000).ToArray());
        byte[] nwa = nw.ToArray();
        Segment[] segs = RunChecked(old, nwa, LocalAligner.Compute(old, nwa), null);
        Segment noise = At(segs, 14_500);
        Assert.False(noise.IsMatched);
        AssertNear(12_000, noise.NewPos, 3);
        AssertNear(17_000, noise.NewEnd, 3);
        Assert.Equal(3, segs.Length);
    }

    [Fact]
    public void NoLocalNoBlockIsAllUnmatched()
    {
        var rng = new Random(8);
        byte[] old = RandomBytes(rng, 1000);
        Assert.Equal(new[] { new Segment(0, 1000, -1) }, RunChecked(old, old, null, null));
    }

    [Fact]
    public void BlockWithEmptyOldFile()
    {
        byte[] nw = { 1, 2, 3 };
        Assert.Equal(new[] { new Segment(0, 3, -1) }, RunChecked(Array.Empty<byte>(), nw, null, new[] { new Segment(0, 3, -1) }));
    }

    [Fact]
    public void RunRecordPoolStaysSmall()
    {
        var rng = new Random(9);
        // 2 MB of structured data: a mix of copies (with substitutions), noise, and repeats.
        byte[] old = new byte[2 << 20];
        for (int i = 0; i < old.Length;)
        {
            int len = rng.Next(16, 4096);
            if (rng.Next(3) == 0 && i > 8192)
            {
                int src = rng.Next(i - len);
                for (int j = 0; j < len && i < old.Length; j++) old[i++] = old[src + j];
            }
            else
            {
                for (int j = 0; j < len && i < old.Length; j++) old[i++] = (byte)(rng.Next(16) == 0 ? rng.Next(256) : rng.Next(8));
            }
        }
        byte[] nw = Mutate(rng, old, old.Length, 8);
        var diag = new DpDiagnostics();
        Segment[] segs = RunChecked(old, nw, LocalAligner.Compute(old, nw), null, null, diag);
        // Live records are the ancestors of at most ~64 states: the shared trunk (which is the
        // eventual output) plus short branches.
        Assert.True(diag.PeakLiveRecords <= 2 * segs.Length + 4096,
            $"peak live records {diag.PeakLiveRecords}, segments {segs.Length}");
    }

    [Fact]
    public void PruneHopelessIsTheDefault() => Assert.True(new DpOptions().PruneHopeless);

    [Fact]
    public void PruneHopelessIsValidAndNearlyAsGood()
    {
        var rng = new Random(12);
        for (int iter = 0; iter < 20; iter++)
        {
            byte[] old = new byte[20_000];
            for (int i = 0; i < old.Length; i++) old[i] = (byte)rng.Next(iter % 2 == 0 ? 4 : 256);
            byte[] nw = Mutate(rng, old, 20_000, iter % 2 == 0 ? 4 : 256);
            LocalMatches local = LocalAligner.Compute(old, nw);
            var full = new DpDiagnostics();
            var pruned = new DpDiagnostics();
            RunChecked(old, nw, local, null, new DpOptions { PruneHopeless = false }, full);
            RunChecked(old, nw, local, null, new DpOptions { PruneHopeless = true }, pruned);
            Assert.True(pruned.Cost <= full.Cost * 1.02 + 20, $"pruned {pruned.Cost} vs full {full.Cost}");
        }
    }
}
