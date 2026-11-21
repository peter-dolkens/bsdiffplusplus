using System.Numerics;

namespace BsDiff6.Format;

/// <summary>
/// Burrows–Wheeler transform of the binary difference map (DESIGN 7.5 step 1), with an implicit
/// end-of-string symbol that sorts before 0 and is removed from the output; its row is
/// <c>primary ∈ [1, D]</c>.
/// </summary>
internal static class Bwt
{
    /// <summary>Forward transform. <paramref name="map"/> holds 0/1 bytes; <c>D &gt;= 1</c>.</summary>
    public static byte[] Forward(ReadOnlySpan<byte> map, out int primary)
    {
        int d = map.Length;
        if (d == 0) throw new ArgumentException("empty map");
        int[] sa = new int[d];
        SuffixArray.Build(map, sa);
        byte[] l = new byte[d];
        l[0] = map[d - 1];
        int j = 1;
        primary = -1;
        for (int i = 0; i < d; i++)
        {
            int s = sa[i];
            if (s == 0) primary = i + 1;
            else l[j++] = map[s - 1];
        }
        return l;
    }

    /// <summary>
    /// Inverse transform by LF mapping on a bit-packed <paramref name="lBits"/> (bit i of word
    /// i/64 = L[i]) holding <paramref name="ones"/> ones in <paramref name="d"/> bits. Throws
    /// <see cref="PatchFormatException"/> if the input is not the BWT of any string (the LF walk
    /// reaches the end-of-string row early or not at all). Uses D/8 + D/16 bytes besides the output.
    /// </summary>
    public static void Inverse(ulong[] lBits, int d, long ones, int primary, Span<byte> map)
    {
        if (map.Length != d) throw new ArgumentException("map length");
        if (primary < 1 || primary > d) throw new PatchFormatException("BWT primary index out of range");
        int words = (d + 63) >> 6;
        int[] wordRank = new int[words];
        int acc = 0;
        for (int w = 0; w < words; w++)
        {
            wordRank[w] = acc;
            acc += BitOperations.PopCount(lBits[w]);
        }
        if (acc != ones) throw new ArgumentException("ones count mismatch");

        long zeros = d - ones;
        long c1 = 1 + zeros;   // first row whose first symbol is 1 (row 0 is the EOS row)
        long r = 0;
        for (int k = d - 1; k >= 0; k--)
        {
            if (r == primary) throw new PatchFormatException("difference map is not a valid BWT");
            int idx = (int)(r < primary ? r : r - 1);   // position in L of row r's symbol
            ulong word = lBits[idx >> 6];
            int bit = (int)((word >> (idx & 63)) & 1);
            map[k] = (byte)bit;
            // rank of that symbol among L'[0 .. r): ones before idx in L (EOS excluded).
            int onesBefore = wordRank[idx >> 6] + BitOperations.PopCount(word & ((1UL << (idx & 63)) - 1));
            r = bit != 0 ? c1 + onesBefore : 1 + (idx - onesBefore);
        }
        if (r != primary) throw new PatchFormatException("difference map is not a valid BWT");
    }
}
