using System.Numerics;
using System.Runtime.CompilerServices;

namespace BsDiff6.Alignment;

/// <summary>Longest exact matches against the old file, indexed by position in the new file.</summary>
public sealed class LocalMatches
{
    /// <summary>Start in the old file of the longest match starting at this new-file position, or -1.</summary>
    public int[] OldPos;

    /// <summary>Length of that match (0 if none).</summary>
    public int[] Length;

    public LocalMatches(int[] oldPos, int[] length)
    {
        if (oldPos.Length != length.Length) throw new ArgumentException("arrays must have equal length");
        OldPos = oldPos;
        Length = length;
    }

    public int Count => OldPos.Length;
}

/// <summary>Local alignment (thesis §2.5, first half; DESIGN §5).</summary>
public static class LocalAligner
{
    /// <summary>
    /// For every position t of <paramref name="new"/>, the old-file position and length of a longest
    /// substring of <paramref name="old"/> that matches <c>new[t..]</c>.
    /// </summary>
    /// <remarks>
    /// Suffix-sorts the virtual string <c>old ‖ # ‖ new</c>, where <c>#</c> is a unique symbol smaller
    /// than every byte. DESIGN §5 sorts <c>old ‖ new</c> without a separator and caps lengths
    /// afterwards, but that is not exact: the nearest old-file suffix in SA order maximises the
    /// uncapped common prefix, which may run from the end of old into new, and after capping a
    /// farther suffix can be longer. With the separator every common prefix with an old-file suffix
    /// stops at the end of old, so the nearest old-file suffix on each side is a longest capped
    /// match. The separator is virtual (alphabet 257), so it costs one byte of memory.
    /// Memory besides the inputs: the joined text (n+m+1 bytes), SA and PLCP (n+m+1 ints each) and
    /// the two result arrays (m ints each).
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static LocalMatches Compute(ReadOnlySpan<byte> old, ReadOnlySpan<byte> @new)
    {
        int n = old.Length, m = @new.Length;
        if ((long)n + m > int.MaxValue - 64)
            throw new NotSupportedException("old and new together are too large for local alignment");

        if (m == 0 || n == 0)
        {
            int[] none = new int[m];
            none.AsSpan().Fill(-1);
            return new LocalMatches(none, new int[m]);
        }

        int total = n + m + 1;
        byte[] joined = GC.AllocateUninitializedArray<byte>(total);
        old.CopyTo(joined);
        joined[n] = 0;
        @new.CopyTo(joined.AsSpan(n + 1));

        int[] sa = GC.AllocateUninitializedArray<int>(total);
        SaIs.BuildSeparated(joined, n, sa);

        // PLCP via the Φ array (Kärkkäinen, Manzini, Puglisi 2009), in place.
        int[] plcp = GC.AllocateUninitializedArray<int>(total);
        plcp[sa[0]] = -1;
        for (int i = 1; i < total; i++) plcp[sa[i]] = sa[i - 1];
        {
            int h = 0;
            for (int i = 0; i < total; i++)
            {
                int j = plcp[i];
                if (j < 0) { plcp[i] = 0; h = 0; continue; }
                h = CommonPrefix(joined, n, i, j, h);
                plcp[i] = h;
                if (h > 0) h--;
            }
        }

        // The joined text is dead from here on. Peak memory is reached in the scans below (SA, PLCP
        // and the two result arrays alive together), so release it before allocating the results.
        joined = null!;
        if (total >= 1 << 22) GC.Collect();
        int[] oldPos = new int[m];
        int[] length = new int[m];
        oldPos.AsSpan().Fill(-1);

        // Forward scan: nearest old-file suffix before each new-file suffix in SA order.
        {
            int lastS = -1, run = 0;
            for (int i = 0; i < total; i++)
            {
                int p = sa[i];
                int h = plcp[p];                  // lcp(sa[i-1], sa[i])
                if (h < run) run = h;
                if (p < n) { lastS = p; run = int.MaxValue; }
                else if (p > n && lastS >= 0 && run > 0)
                {
                    int t = p - n - 1;
                    oldPos[t] = lastS;
                    length[t] = run;
                }
            }
        }

        // Backward scan: nearest old-file suffix after. A strictly longer match replaces the forward
        // one; on a tie the alternative position is parked in the (now dead) PLCP slot of the suffix.
        {
            int lastS = -1, run = 0, hNext = 0;
            for (int i = total - 1; i >= 0; i--)
            {
                int p = sa[i];
                if (hNext < run) run = hNext;     // lcp(sa[i], sa[i+1])
                hNext = plcp[p];                  // read before the slot is reused
                if (p < n) { lastS = p; run = int.MaxValue; }
                else if (p > n)
                {
                    int t = p - n - 1, alt = -1;
                    if (lastS >= 0 && run > 0)
                    {
                        if (run > length[t]) { oldPos[t] = lastS; length[t] = run; }
                        else if (run == length[t]) alt = lastS;
                    }
                    plcp[p] = alt;
                }
            }
        }

        // Tie-break in new-file order: prefer the offset of position t-1's match, else the backward
        // neighbour (the preceding old-file suffix in SA order, i.e. the forward scan's choice).
        {
            long prevOff = long.MinValue;
            for (int t = 0; t < m; t++)
            {
                int alt = plcp[n + 1 + t];
                if (alt >= 0 && alt != oldPos[t] && (long)alt - t == prevOff)
                    oldPos[t] = alt;
                prevOff = length[t] > 0 ? (long)oldPos[t] - t : long.MinValue;
            }
        }

        return new LocalMatches(oldPos, length);
    }

    /// <summary>
    /// Length of the common prefix of virtual suffixes i and j (i != j) of old ‖ # ‖ new, known to be
    /// at least <paramref name="h"/>. The separator at <paramref name="sep"/> matches nothing.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe int CommonPrefix(byte[] joined, int sep, int i, int j, int h)
    {
        if (i == sep || j == sep) return 0;
        int li = i < sep ? sep - i : joined.Length - i;
        int lj = j < sep ? sep - j : joined.Length - j;
        int lim = li < lj ? li : lj;
        fixed (byte* p = joined)
        {
            byte* a = p + i, b = p + j;
            if (BitConverter.IsLittleEndian)
            {
                while (h + 8 <= lim)
                {
                    ulong x = Unsafe.ReadUnaligned<ulong>(a + h) ^ Unsafe.ReadUnaligned<ulong>(b + h);
                    if (x != 0) return h + (BitOperations.TrailingZeroCount(x) >> 3);
                    h += 8;
                }
            }
            while (h < lim && a[h] == b[h]) h++;
        }
        return h;
    }
}
