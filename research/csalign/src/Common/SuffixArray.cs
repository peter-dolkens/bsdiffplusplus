namespace BsDiff6;

/// <summary>
/// Suffix sorting entry point shared by local alignment and the difference-map BWT.
/// Convention: suffixes are sorted lexicographically by unsigned byte value, and a suffix that is
/// a proper prefix of another sorts first (an implicit sentinel smaller than every byte).
/// </summary>
/// <remarks>
/// PLACEHOLDER: O(n log^2 n) prefix doubling, here only so that areas can be developed in
/// parallel. The integration phase replaces the body with a call to the SA-IS implementation in
/// Alignment/. Do not tune this; do not depend on its speed.
/// </remarks>
public static class SuffixArray
{
    public static void Build(ReadOnlySpan<byte> text, Span<int> sa)
    {
        int n = text.Length;
        if (sa.Length != n) throw new ArgumentException("sa must have the same length as text");
        if (n == 0) return;

        int[] order = new int[n];
        int[] rank = new int[n];
        int[] tmp = new int[n];
        for (int i = 0; i < n; i++) { order[i] = i; rank[i] = text[i]; }

        for (int k = 1; ; k <<= 1)
        {
            int step = k;
            int[] r = rank;
            Comparison<int> cmp = (a, b) =>
            {
                if (r[a] != r[b]) return r[a] < r[b] ? -1 : 1;
                int ra = a + step < n ? r[a + step] : -1;
                int rb = b + step < n ? r[b + step] : -1;
                return ra.CompareTo(rb);
            };
            Array.Sort(order, cmp);
            tmp[order[0]] = 0;
            for (int i = 1; i < n; i++)
                tmp[order[i]] = tmp[order[i - 1]] + (cmp(order[i - 1], order[i]) < 0 ? 1 : 0);
            (rank, tmp) = (tmp, rank);
            if (rank[order[n - 1]] == n - 1) break;
        }
        order.CopyTo(sa);
    }
}
