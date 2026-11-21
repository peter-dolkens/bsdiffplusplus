using System.Numerics;

namespace BsDiff6.Matching;

/// <summary>Direct byte-match counting, used for candidate verification and diagnostics.</summary>
public static class MatchCounter
{
    /// <summary>
    /// Number of r in [0, block.Length) with old[oldPos + r] == block[r]; positions outside the
    /// old file count as mismatches. <paramref name="oldPos"/> may be negative.
    /// </summary>
    public static int Count(ReadOnlySpan<byte> old, long oldPos, ReadOnlySpan<byte> block)
    {
        long start = Math.Max(0, oldPos);
        long end = Math.Min(old.Length, oldPos + block.Length);
        if (start >= end) return 0;
        int len = (int)(end - start);
        return CountEqual(old.Slice((int)start, len), block.Slice((int)(start - oldPos), len));
    }

    /// <summary>Number of positions where the two equal-length spans agree.</summary>
    public static int CountEqual(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length) throw new ArgumentException("lengths differ");
        int n = a.Length;
        int count = 0;
        int i = 0;
        int vc = Vector<byte>.Count;
        if (Vector.IsHardwareAccelerated && n >= vc)
        {
            while (i + vc <= n)
            {
                // Accumulate at most 255 vectors in byte lanes before widening.
                Vector<byte> acc = Vector<byte>.Zero;
                int limit = Math.Min(n - vc, i + 254 * vc); // inclusive: at most 255 iterations
                for (; i <= limit; i += vc)
                    acc -= Vector.Equals(new Vector<byte>(a.Slice(i)), new Vector<byte>(b.Slice(i)));
                Vector.Widen(acc, out Vector<ushort> lo, out Vector<ushort> hi);
                Vector<ushort> s = lo + hi;
                for (int k = 0; k < Vector<ushort>.Count; k++) count += s[k];
            }
        }
        for (; i < n; i++) if (a[i] == b[i]) count++;
        return count;
    }
}

/// <summary>How much of the new file an alignment predicts, and how much of that is right.</summary>
public readonly record struct AlignmentCoverage(long NewLength, long CoveredBytes, long MatchingBytes)
{
    /// <summary>Fraction of the new file covered by matched segments.</summary>
    public double CoveredFraction => NewLength == 0 ? 0 : (double)CoveredBytes / NewLength;

    /// <summary>Fraction of covered bytes that equal the old byte they are aligned to.</summary>
    public double MatchFraction => CoveredBytes == 0 ? 0 : (double)MatchingBytes / CoveredBytes;

    /// <summary>
    /// Counts the new-file bytes covered by matched segments and, of those, the ones equal to the
    /// old byte they are aligned with. Segments need not be canonical or complete, but matched
    /// ones must lie inside both files.
    /// </summary>
    public static AlignmentCoverage Measure(ReadOnlySpan<byte> old, ReadOnlySpan<byte> @new, IReadOnlyList<Segment> segments)
    {
        long covered = 0, matching = 0;
        foreach (Segment s in segments)
        {
            if (!s.IsMatched) continue;
            if (s.NewPos < 0 || (long)s.NewPos + s.Length > @new.Length || (long)s.OldPos + s.Length > old.Length)
                throw new ArgumentException($"segment {s} lies outside the files");
            covered += s.Length;
            matching += MatchCounter.CountEqual(old.Slice(s.OldPos, s.Length), @new.Slice(s.NewPos, s.Length));
        }
        return new AlignmentCoverage(@new.Length, covered, matching);
    }
}
