using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BsDiff6.Alignment;

/// <summary>
/// Linear-time suffix array construction by induced sorting (SA-IS; Nong, Zhang and Chan 2009).
/// </summary>
/// <remarks>
/// <para>Convention (same as <see cref="SuffixArray"/>): suffixes are sorted lexicographically by
/// unsigned byte value and a suffix that is a proper prefix of another sorts first, i.e. there is
/// an implicit sentinel, smaller than every symbol, just past the end of the text. The sentinel is
/// never stored; it is simulated by seeding every L-type induction pass with suffix n-1.</para>
/// <para>Memory: besides <c>sa</c> itself, each recursion level allocates a type bit vector of
/// n/8 bytes (n halves at least per level, so n/4 bytes in total). The reduced problem of size m
/// (m &lt;= n/2) is solved in place: its text lives in the last m entries of <c>sa</c>, its suffix
/// array in the first m, and its count and bucket arrays (k ints each, k = number of distinct
/// LMS substrings) in the n - 2m entries in between when they fit; otherwise they are allocated.
/// In practice that happens on near-random data, where the first reduced alphabet is large
/// (100 MB of random bytes: about 0.3n extra ints at level 1). Recursion depth is at most
/// log2(n) and every pass is iterative.</para>
/// </remarks>
public static unsafe class SaIs
{
    /// <summary>Writes the suffix array of <paramref name="text"/> into <paramref name="sa"/>.</summary>
    public static void Build(ReadOnlySpan<byte> text, Span<int> sa)
    {
        if (sa.Length != text.Length) throw new ArgumentException("sa must have the same length as text");
        int n = text.Length;
        if (n == 0) return;
        fixed (byte* t = text)
        fixed (int* s = sa)
            Sort(new ByteText(t), s, n, 256, null, 0);
    }

    /// <summary>
    /// Suffix array of the virtual string <c>joined[0..sep) ‖ # ‖ joined[sep+1..)</c> where the byte
    /// at <paramref name="sep"/> is ignored and replaced by a unique symbol smaller than every byte.
    /// <paramref name="sa"/> has the length of <paramref name="joined"/>.
    /// </summary>
    internal static void BuildSeparated(ReadOnlySpan<byte> joined, int sep, Span<int> sa)
    {
        if (sa.Length != joined.Length) throw new ArgumentException("sa must have the same length as text");
        if ((uint)sep >= (uint)joined.Length) throw new ArgumentOutOfRangeException(nameof(sep));
        fixed (byte* t = joined)
        fixed (int* s = sa)
            Sort(new SeparatedText(t, sep), s, joined.Length, 257, null, 0);
    }

    /// <summary>
    /// Alphabets up to this size get freshly allocated count and bucket arrays; larger ones (only in
    /// the recursion) use the free middle of the suffix array when it is big enough. Tests lower it
    /// to exercise the other bucket placements; the output does not depend on it.
    /// </summary>
    internal static int SmallAlphabet = 4096;

    /// <summary>When the counts do not fit in free space: allocate them (true) or recount (false).</summary>
    internal static bool KeepCounts = true;

    // ---------------------------------------------------------------------------------------------
    // Text accessors. Generic over structs so that the JIT emits one specialised copy per text type.

    private interface IText
    {
        int this[int i] { get; }
    }

    private readonly struct ByteText : IText
    {
        private readonly byte* _p;
        public ByteText(byte* p) { _p = p; }
        public int this[int i] { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _p[i]; }
    }

    private readonly struct SeparatedText : IText
    {
        private readonly byte* _p;
        private readonly int _sep;
        public SeparatedText(byte* p, int sep) { _p = p; _sep = sep; }
        public int this[int i]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => i == _sep ? 0 : _p[i] + 1;
        }
    }

    private readonly struct IntText : IText
    {
        private readonly int* _p;
        public IntText(int* p) { _p = p; }
        public int this[int i] { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _p[i]; }
    }

    // ---------------------------------------------------------------------------------------------

    /// <summary>Bucket storage: either counts + pointers, or pointers only (counts recomputed).</summary>
    private struct Buckets
    {
        public int* C;      // symbol counts, or null when they must be recounted from the text
        public int* B;      // working bucket pointers
        public int K;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsS(ulong* types, int i) => ((types[i >> 6] >> i) & 1) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsLms(ulong* types, int i) => i > 0 && IsS(types, i) && !IsS(types, i - 1);

    private static void GetBuckets<T>(T s, int n, in Buckets bk, bool end) where T : struct, IText
    {
        int k = bk.K;
        int* B = bk.B;
        int* C = bk.C;
        if (C == null)
        {
            new Span<int>(B, k).Clear();
            for (int i = 0; i < n; i++) B[s[i]]++;
            C = B;
        }
        int sum = 0;
        if (end)
        {
            for (int c = 0; c < k; c++) { sum += C[c]; B[c] = sum; }
        }
        else
        {
            for (int c = 0; c < k; c++) { int cnt = C[c]; B[c] = sum; sum += cnt; }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void InduceL<T>(T s, int* SA, int n, in Buckets bk) where T : struct, IText
    {
        GetBuckets(s, n, bk, end: false);
        int* B = bk.B;
        // The virtual sentinel at position n induces suffix n-1, which is always L-type.
        SA[B[s[n - 1]]++] = n - 1;
        // Scanned entries are either LMS seeds or L-type suffixes induced in this pass. If SA[i] = j+1
        // is an LMS seed, j is L-type and s[j] > s[j+1]; if it is L-type, j is L-type iff
        // s[j] >= s[j+1]. So j is L-type iff s[j] >= s[j+1], and no type lookup is needed.
        for (int i = 0; i < n; i++)
        {
            int j = SA[i] - 1;
            if (j >= 0)
            {
                int c0 = s[j];
                if (c0 >= s[j + 1]) SA[B[c0]++] = j;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void InduceS<T>(T s, int* SA, int n, in Buckets bk) where T : struct, IText
    {
        GetBuckets(s, n, bk, end: true);
        int* B = bk.B;
        // j = SA[i]-1 is S-type iff s[j] < s[j+1], or s[j] == s[j+1] and j+1 is S-type. In the
        // latter case j+1 is in bucket s[j]; the S-type suffixes of a bucket are exactly the slots
        // this pass has already filled, i.e. those at or above the bucket's insertion pointer.
        for (int i = n - 1; i >= 0; i--)
        {
            int j = SA[i] - 1;
            if (j >= 0)
            {
                int c0 = s[j], c1 = s[j + 1];
                if (c0 < c1 || (c0 == c1 && i >= B[c0])) SA[--B[c0]] = j;
            }
        }
    }

    /// <summary>
    /// The S-type pass of stage 1. Like <see cref="InduceS"/>, but every induced LMS suffix j is stored
    /// as ~j, so that the sorted LMS suffixes can be collected without type lookups. An LMS entry
    /// never induces anything in this pass (its predecessor is L-type), so marked entries are skipped.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void InduceSMarkLms<T>(T s, int* SA, int n, in Buckets bk) where T : struct, IText
    {
        GetBuckets(s, n, bk, end: true);
        int* B = bk.B;
        for (int i = n - 1; i >= 0; i--)
        {
            int j = SA[i] - 1;
            if (j >= 0)
            {
                int c0 = s[j], c1 = s[j + 1];
                if (c0 < c1 || (c0 == c1 && i >= B[c0]))
                    SA[--B[c0]] = j > 0 && s[j - 1] > c0 ? ~j : j;
            }
        }
    }

    /// <summary>
    /// Suffix array of s[0..n) over alphabet [0, k) into SA[0..n). <paramref name="free"/> points to
    /// <paramref name="freeLen"/> ints this call may use as scratch (may be null).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Sort<T>(T s, int* SA, int n, int k, int* free, int freeLen) where T : struct, IText
    {
        if (n == 1) { SA[0] = 0; return; }

        // ---- types (bit set = S-type); position n-1 is L-type because of the virtual sentinel.
        ulong[] typeArr = GC.AllocateArray<ulong>((n + 63) >> 6, pinned: true);
        ulong* types = (ulong*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(typeArr));
        {
            bool nextS = false;
            int next = s[n - 1];
            for (int i = n - 2; i >= 0; i--)
            {
                int c = s[i];
                bool isS = c < next || (c == next && nextS);
                if (isS) types[i >> 6] |= 1UL << i;
                nextS = isS;
                next = c;
            }
        }

        // ---- buckets
        int[]? cArr = null, bArr = null;
        Buckets bk;
        bk.K = k;
        if (k <= SmallAlphabet)
        {
            cArr = GC.AllocateArray<int>(k, pinned: true);
            bArr = GC.AllocateArray<int>(k, pinned: true);
            bk.C = (int*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(cArr));
            bk.B = (int*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(bArr));
        }
        else if ((long)2 * k <= freeLen)
        {
            bk.C = free;
            bk.B = free + k;
        }
        else
        {
            // Keeping the counts saves a random-access recount pass per induction step, which on
            // large alphabets (e.g. the first reduced string of random data) is worth the memory.
            if (KeepCounts)
            {
                cArr = GC.AllocateUninitializedArray<int>(k, pinned: true);
                bk.C = (int*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(cArr));
            }
            else bk.C = null;
            if (k <= freeLen) bk.B = free;
            else
            {
                bArr = GC.AllocateUninitializedArray<int>(k, pinned: true);
                bk.B = (int*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(bArr));
            }
        }
        if (bk.C != null)
        {
            new Span<int>(bk.C, k).Clear();
            for (int i = 0; i < n; i++) bk.C[s[i]]++;
        }

        // ---- stage 1: sort the LMS substrings
        new Span<int>(SA, n).Fill(-1);
        GetBuckets(s, n, bk, end: true);
        int m = 0;
        for (int i = 1; i < n; i++)
        {
            if (IsLms(types, i))
            {
                SA[--bk.B[s[i]]] = i;
                m++;
            }
        }

        if (m > 0)
        {
            InduceL(s, SA, n, bk);
            InduceSMarkLms(s, SA, n, bk);

            // compact the sorted LMS positions (the marked entries) into SA[0..m)
            for (int i = 0, m2 = 0; i < n; i++)
            {
                int p = SA[i];
                if (p < 0) SA[m2++] = ~p;
            }

            // ---- name the LMS substrings (as in Mori's sais-lite): first store the length of every
            // LMS substring (up to and including the next LMS position; the last one includes the
            // virtual sentinel and is therefore unique) at SA[m + p/2], in one sequential pass.
            // Two substrings are then equal iff their lengths and bytes are equal (equal bytes and an
            // S-type last position determine equal types).
            new Span<int>(SA + m, n - m).Clear();
            for (int i = n - 1, next = n; i >= 1; i--)
            {
                if (IsLms(types, i))
                {
                    SA[m + (i >> 1)] = next - i + 1;
                    next = i;
                }
            }
            int name = 0;
            {
                int q = 0, qlen = 0;
                for (int i = 0; i < m; i++)
                {
                    int p = SA[i];
                    int plen = SA[m + (p >> 1)];
                    bool diff = true;
                    if (plen == qlen && p + plen <= n && q + plen <= n)
                    {
                        int d = 0;
                        while (d < plen && s[p + d] == s[q + d]) d++;
                        diff = d < plen;
                    }
                    if (diff) { name++; q = p; qlen = plen; }
                    SA[m + (p >> 1)] = name;       // 1-based so that 0 marks an empty slot
                }
            }

            // move the names to the tail, in text order: the reduced string s1 = SA[n-m..n)
            for (int i = n - 1, j = n - 1; i >= m; i--)
            {
                if (SA[i] != 0) SA[j--] = SA[i] - 1;
            }
            int* s1 = SA + n - m;

            // ---- stage 2: suffix array of the reduced string into SA[0..m)
            if (name < m)
                Sort(new IntText(s1), SA, m, name, SA + m, n - 2 * m);
            else
                for (int i = 0; i < m; i++) SA[s1[i]] = i;

            // map reduced indices back to LMS positions
            for (int i = 1, j = 0; i < n; i++)
            {
                if (IsLms(types, i)) s1[j++] = i;
            }
            for (int i = 0; i < m; i++) SA[i] = s1[SA[i]];

            // ---- stage 3: place the sorted LMS suffixes at their bucket ends, then induce
            new Span<int>(SA + m, n - m).Fill(-1);
            GetBuckets(s, n, bk, end: true);
            for (int i = m - 1; i >= 0; i--)
            {
                int j = SA[i];
                SA[i] = -1;
                SA[--bk.B[s[j]]] = j;
            }
        }

        InduceL(s, SA, n, bk);
        InduceS(s, SA, n, bk);

        GC.KeepAlive(typeArr);
        GC.KeepAlive(cArr);
        GC.KeepAlive(bArr);
    }
}
