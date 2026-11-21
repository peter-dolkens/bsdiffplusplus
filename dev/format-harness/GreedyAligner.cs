namespace BsDiff6.FormatHarness;

/// <summary>
/// Throwaway aligner, roughly bsdiff 4.3 in spirit: exact seeds from a hash of 8-byte windows
/// (plus "keep the previous offset"), extended forwards and backwards by maximising
/// 2·matches − length, i.e. while at least half the bytes match. Only good enough to produce
/// realistic difference maps for the map-model experiment.
/// </summary>
internal static class GreedyAligner
{
    private const int Seed = 8;
    private const int TableBits = 22;

    private static uint Hash(ReadOnlySpan<byte> s, int i)
    {
        ulong v = BitConverter.ToUInt64(s.Slice(i, 8));
        return (uint)((v * 0x9E3779B97F4A7C15UL) >> (64 - TableBits));
    }

    public static Segment[] Align(byte[] old, byte[] nw)
    {
        int n = old.Length, m = nw.Length;
        var segs = new List<Segment>();
        int[] table = new int[1 << TableBits];
        Array.Fill(table, -1);
        for (int i = 0; i + Seed <= n; i++) table[Hash(old, i)] = i;

        int p = 0, prevEnd = 0;
        long lastOff = 0;
        while (p + Seed <= m)
        {
            int bestPos = -1, bestLen = 0;
            long c1 = p + lastOff;
            if (c1 >= 0 && c1 < n)
            {
                int l = ExactLen(old, (int)c1, nw, p);
                if (l >= 4) { bestPos = (int)c1; bestLen = l + 12; } // bias towards continuing
            }
            int c2 = table[Hash(nw, p)];
            if (c2 >= 0)
            {
                int l = ExactLen(old, c2, nw, p);
                if (l >= 16 && l > bestLen) { bestPos = c2; bestLen = l; }
            }
            if (bestPos < 0) { p++; continue; }

            long off = (long)bestPos - p;
            // Backward extension into [prevEnd, p).
            int lenb = 0, s = 0, bestScore = 0;
            for (int j = 1; p - j >= prevEnd && p - j + off >= 0; j++)
            {
                if (old[p - j + off] == nw[p - j]) s++;
                int score = 2 * s - j;
                if (score > bestScore) { bestScore = score; lenb = j; }
                if (score < bestScore - 128) break;
            }
            // Forward extension.
            int lenf = 0; s = 0; bestScore = 0;
            for (int i = 0; p + i < m && p + i + off < n; i++)
            {
                if (old[p + i + off] == nw[p + i]) s++;
                int score = 2 * s - (i + 1);
                if (score > bestScore) { bestScore = score; lenf = i + 1; }
                if (score < bestScore - 128) break;
            }
            if (lenf == 0) { p++; continue; }
            int start = p - lenb, end = p + lenf;
            if (start > prevEnd) segs.Add(new Segment(prevEnd, start - prevEnd, Segment.Unmatched));
            segs.Add(new Segment(start, end - start, (int)(start + off)));
            prevEnd = end;
            p = end;
            lastOff = off;
        }
        if (prevEnd < m) segs.Add(new Segment(prevEnd, m - prevEnd, Segment.Unmatched));
        Segment[] result = AlignmentUtil.Canonicalize(segs);
        AlignmentUtil.Validate(result, n, m);
        return result;
    }

    private static int ExactLen(byte[] old, int o, byte[] nw, int p)
    {
        int l = 0;
        while (o + l < old.Length && p + l < nw.Length && old[o + l] == nw[p + l] && l < 1 << 16) l++;
        return l;
    }
}
