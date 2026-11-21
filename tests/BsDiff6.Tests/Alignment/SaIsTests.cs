using BsDiff6.Alignment;

namespace BsDiff6.Tests.Alignment;

public class SaIsTests
{
    internal static int[] NaiveSuffixArray(byte[] text)
    {
        int[] sa = Enumerable.Range(0, text.Length).ToArray();
        Array.Sort(sa, (a, b) => text.AsSpan(a).SequenceCompareTo(text.AsSpan(b)));
        return sa;
    }

    private static void Check(byte[] text)
    {
        int[] sa = new int[text.Length];
        SaIs.Build(text, sa);
        Assert.Equal(NaiveSuffixArray(text), sa);
    }

    private static void CheckSorted(byte[] text)
    {
        // For larger inputs: verify it is a permutation and adjacent suffixes are in order.
        int n = text.Length;
        int[] sa = new int[n];
        SaIs.Build(text, sa);
        bool[] seen = new bool[n];
        foreach (int p in sa)
        {
            Assert.InRange(p, 0, n - 1);
            Assert.False(seen[p]);
            seen[p] = true;
        }
        for (int i = 1; i < n; i++)
        {
            int a = sa[i - 1], b = sa[i];
            // Compare via rank array to keep this linear-ish: rank[a+1] < rank[b+1] when first bytes equal.
            if (text[a] != text[b]) { Assert.True(text[a] < text[b], $"order at {i}"); continue; }
            if (a + 1 == n) continue;          // empty suffix sorts first
            Assert.True(b + 1 < n, $"order at {i}");
            // checked below with the inverse
        }
        int[] rank = new int[n + 1];
        rank[n] = -1;
        for (int i = 0; i < n; i++) rank[sa[i]] = i;
        for (int i = 1; i < n; i++)
        {
            int a = sa[i - 1], b = sa[i];
            if (text[a] == text[b])
                Assert.True(rank[a + 1] < rank[b + 1], $"order at {i}");
        }
    }

    [Fact]
    public void Empty() => SaIs.Build(ReadOnlySpan<byte>.Empty, Span<int>.Empty);

    [Fact]
    public void Banana() => Check(System.Text.Encoding.ASCII.GetBytes("banana"));

    [Fact]
    public void MatchesPlaceholderConvention()
    {
        byte[] text = System.Text.Encoding.ASCII.GetBytes("mississippi\0\0ab\0");
        int[] a = new int[text.Length], b = new int[text.Length];
        SaIs.Build(text, a);
        SuffixArray.Build(text, b);
        Assert.Equal(b, a);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(256)]
    public void RandomSmall(int alphabet)
    {
        var rng = new Random(1234 + alphabet);
        for (int iter = 0; iter < 3000; iter++)
        {
            int n = rng.Next(1, iter < 2000 ? 40 : 400);
            byte[] t = new byte[n];
            for (int i = 0; i < n; i++) t[i] = (byte)(alphabet == 256 ? rng.Next(256) : 250 + rng.Next(alphabet) - alphabet);
            Check(t);
        }
    }

    [Fact]
    public void RandomRunsAndRepeats()
    {
        var rng = new Random(99);
        for (int iter = 0; iter < 1000; iter++)
        {
            int n = rng.Next(1, 300);
            byte[] t = new byte[n];
            int i = 0;
            while (i < n)
            {
                int kind = rng.Next(3);
                if (kind == 0) { byte v = (byte)rng.Next(3); int len = rng.Next(1, 30); for (int j = 0; j < len && i < n; j++) t[i++] = v; }
                else if (kind == 1 && i > 0) { int src = rng.Next(i); int len = rng.Next(1, 40); for (int j = 0; j < len && i < n; j++) t[i++] = t[src + j]; }
                else t[i++] = (byte)rng.Next(3);
            }
            Check(t);
        }
    }

    internal static byte[] Fibonacci(int n)
    {
        var a = new List<byte> { 0 };
        var b = new List<byte> { 0, 1 };
        while (b.Count < n) { var c = new List<byte>(b); c.AddRange(a); a = b; b = c; }
        return b.Take(n).ToArray();
    }

    [Fact]
    public void Pathological()
    {
        CheckSorted(new byte[1 << 20]);
        Check(new byte[1000]);
        Check(Fibonacci(2000));
        CheckSorted(Fibonacci(1 << 20));

        var rng = new Random(5);
        byte[] runs = new byte[1 << 20];
        for (int i = 0; i < runs.Length;)
        {
            int len = rng.Next(1, 5000);
            byte v = (byte)rng.Next(2);
            for (int j = 0; j < len && i < runs.Length; j++) runs[i++] = v;
        }
        CheckSorted(runs);

        byte[] rnd = new byte[1 << 20];
        rng.NextBytes(rnd);
        CheckSorted(rnd);

        // period-2 and period-3 strings: maximal LMS density
        byte[] ab = new byte[1 << 20];
        for (int i = 0; i < ab.Length; i++) ab[i] = (byte)(i & 1);
        CheckSorted(ab);
        byte[] abc = new byte[(1 << 20) + 1];
        for (int i = 0; i < abc.Length; i++) abc[i] = (byte)(i % 3 == 1 ? 0 : 1);
        CheckSorted(abc);
    }

    [Fact]
    public void AllBucketPlacements()
    {
        // With SmallAlphabet = 0 the recursion levels use free space in SA for counts + buckets,
        // for buckets only (recounting), or a separate allocation, depending on how much is free.
        int saved = SaIs.SmallAlphabet;
        try
        {
            SaIs.SmallAlphabet = 0;
            var rng = new Random(31);
            for (int iter = 0; iter < 1500; iter++)
            {
                int n = rng.Next(1, 200);
                int alphabet = 1 + iter % 4;
                byte[] t = new byte[n];
                for (int i = 0; i < n; i++) t[i] = (byte)rng.Next(alphabet);
                Check(t);
            }
            byte[] ab = new byte[20001];
            for (int i = 0; i < ab.Length; i++) ab[i] = (byte)(i % 2 == 0 ? 1 : rng.Next(2));
            Check(ab.Take(3000).ToArray());
            CheckSorted(ab);
            CheckSorted(Fibonacci(50000));
        }
        finally
        {
            SaIs.SmallAlphabet = saved;
        }
    }

    [Fact]
    public void SeparatedMatchesExplicitAlphabetShift()
    {
        var rng = new Random(77);
        for (int iter = 0; iter < 500; iter++)
        {
            int n = rng.Next(0, 30), m = rng.Next(0, 30);
            byte[] joined = new byte[n + m + 1];
            for (int i = 0; i < joined.Length; i++) joined[i] = (byte)(rng.Next(3) + 254 - 2 * (iter & 1) * 100);
            joined[n] = (byte)rng.Next(256);
            // reference: sort with the separator as -1
            int[] expect = Enumerable.Range(0, joined.Length).ToArray();
            int[] key = joined.Select((b, i) => i == n ? -1 : (int)b).ToArray();
            Array.Sort(expect, (a, b) =>
            {
                for (int d = 0; ; d++)
                {
                    bool ea = a + d >= key.Length, eb = b + d >= key.Length;
                    if (ea || eb) return ea == eb ? 0 : ea ? -1 : 1;
                    int c = key[a + d].CompareTo(key[b + d]);
                    if (c != 0) return c;
                }
            });
            int[] sa = new int[joined.Length];
            SaIs.BuildSeparated(joined, n, sa);
            Assert.Equal(expect, sa);
        }
    }
}
