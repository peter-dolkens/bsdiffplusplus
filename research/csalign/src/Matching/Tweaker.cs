using System.Numerics;

namespace BsDiff6.Matching;

/// <summary>A run of new-file positions [Start, End) predicted at one offset, or with no prediction.</summary>
internal struct OffsetRun
{
    public const int None = int.MinValue;
    public int Start, End, Offset;
    public OffsetRun(int start, int end, int offset) { Start = start; End = end; Offset = offset; }
    public int Length => End - Start;
    public bool HasOffset => Offset != None;
    public override string ToString() => $"[{Start},{End}) @{(HasOffset ? Offset.ToString() : "none")}";
}

/// <summary>Boundary tweaking (thesis §2.4, DESIGN §4.3).</summary>
internal sealed class Tweaker
{
    private readonly int _minRun;
    // Integer costs: a mismatch (or out-of-range position) costs MismatchCost, a byte of a
    // no-prediction run costs _unmatchedCost, a match 0.
    private const int MismatchCost = 1 << 12;
    private readonly int _unmatchedCost;
    private readonly List<OffsetRun> _runs;

    public int BoundariesMoved { get; private set; }
    public int RunsRemoved { get; private set; }

    /// <param name="window">Maximum distance a boundary may move per tweak; 0 = unlimited.</param>
    public Tweaker(List<OffsetRun> runs, int minRunLength, double unmatchedCost, int window)
    {
        _window = window;
        _runs = runs;
        _minRun = minRunLength;
        _unmatchedCost = (int)Math.Round(unmatchedCost * MismatchCost);
    }

    public void Run(ReadOnlySpan<byte> old, ReadOnlySpan<byte> @new)
    {
        Normalize();
        // Forward pass.
        int i = 0;
        while (i < _runs.Count - 1)
        {
            if (TweakBoundary(old, @new, i)) i = Math.Max(0, i - 1);
            else i++;
        }
        // Backward pass.
        i = _runs.Count - 2;
        while (i >= 0)
        {
            if (TweakBoundary(old, @new, i)) i = Math.Min(i + 1, _runs.Count - 2);
            else i--;
        }
    }

    private readonly int _window;
    private readonly int[] _costA = new int[ChunkSize], _costB = new int[ChunkSize];
    private const int ChunkSize = 4096;

    /// <summary>Writes the cost of new-file bytes [start, start + dst.Length) under <paramref name="offset"/>.</summary>
    private void FillCosts(ReadOnlySpan<byte> old, ReadOnlySpan<byte> @new, int offset, int start, Span<int> dst)
    {
        if (offset == OffsetRun.None) { dst.Fill(_unmatchedCost); return; }
        int count = dst.Length;
        // Positions p with 0 <= p + offset < old.Length.
        long vlo = Math.Max(start, -(long)offset), vhi = Math.Min((long)start + count, (long)old.Length - offset);
        if (vlo >= vhi) { dst.Fill(MismatchCost); return; }
        int a = (int)(vlo - start), b = (int)(vhi - start);
        dst.Slice(0, a).Fill(MismatchCost);
        dst.Slice(b).Fill(MismatchCost);
        ReadOnlySpan<byte> o = old.Slice((int)(vlo + offset), b - a);
        ReadOnlySpan<byte> nw = @new.Slice((int)vlo, b - a);
        Span<int> d = dst.Slice(a, b - a);
        for (int i = 0; i < d.Length && i < o.Length && i < nw.Length; i++)
            d[i] = o[i] == nw[i] ? 0 : MismatchCost;
    }

    /// <summary>Moves the boundary between runs i and i+1; returns true if the run list changed shape.</summary>
    private bool TweakBoundary(ReadOnlySpan<byte> old, ReadOnlySpan<byte> @new, int i)
    {
        OffsetRun a = _runs[i], b = _runs[i + 1];
        int sa = a.Start, e = a.End, eb = b.End;
        int oa = a.Offset, ob = b.Offset;
        int lo = sa, hi = eb;
        if (_window > 0)
        {
            lo = (int)Math.Max(sa, (long)e - _window);
            hi = (int)Math.Min(eb, (long)e + _window);
        }

        // cost(q) = const + D(q), D(q) = Σ_{lo <= p < q} (cost_a(p) - cost_b(p)), q in [lo, hi].
        long d = 0, bestD = 0;
        int bestQ = lo;
        for (int c0 = lo; c0 < hi; c0 += ChunkSize)
        {
            int len = Math.Min(ChunkSize, hi - c0);
            Span<int> ca = _costA.AsSpan(0, len), cb = _costB.AsSpan(0, len);
            FillCosts(old, @new, oa, c0, ca);
            FillCosts(old, @new, ob, c0, cb);
            for (int k = 0; k < ca.Length && k < cb.Length; k++)
            {
                d += ca[k] - cb[k];
                if (d <= bestD)
                {
                    int q = c0 + k + 1;
                    if (d < bestD || Better(q, bestQ, e))
                    {
                        bestD = d;
                        bestQ = q;
                    }
                }
            }
        }

        if (bestQ == e) return false;
        BoundariesMoved++;
        a.End = bestQ;
        b.Start = bestQ;
        bool structural = false;
        if (bestQ < e && a.HasOffset && a.Length < _minRun)
        {
            if (a.Length > 0) RunsRemoved++;
            a.Offset = OffsetRun.None;
            structural = true;
        }
        if (bestQ > e && b.HasOffset && b.Length < _minRun)
        {
            if (b.Length > 0) RunsRemoved++;
            b.Offset = OffsetRun.None;
            structural = true;
        }
        if (a.Length == 0 || b.Length == 0) structural = true;
        _runs[i] = a;
        _runs[i + 1] = b;
        if (structural) Normalize();
        return structural;
    }

    /// <summary>Tie-break: larger power of two dividing q, then nearer the old boundary, then smaller.</summary>
    private static bool Better(int q, int best, int original)
    {
        int tq = q == 0 ? 32 : BitOperations.TrailingZeroCount(q);
        int tb = best == 0 ? 32 : BitOperations.TrailingZeroCount(best);
        if (tq != tb) return tq > tb;
        int dq = Math.Abs(q - original), db = Math.Abs(best - original);
        if (dq != db) return dq < db;
        return q < best;
    }

    /// <summary>Drops empty runs and merges neighbours with the same offset (or both none).</summary>
    private void Normalize()
    {
        int w = 0;
        for (int r = 0; r < _runs.Count; r++)
        {
            OffsetRun cur = _runs[r];
            if (cur.Length == 0) continue;
            if (w > 0 && _runs[w - 1].Offset == cur.Offset)
            {
                OffsetRun prev = _runs[w - 1];
                prev.End = cur.End;
                _runs[w - 1] = prev;
                continue;
            }
            _runs[w++] = cur;
        }
        _runs.RemoveRange(w, _runs.Count - w);
    }
}
