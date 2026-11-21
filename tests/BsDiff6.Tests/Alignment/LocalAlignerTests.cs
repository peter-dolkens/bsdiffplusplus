using BsDiff6.Alignment;

namespace BsDiff6.Tests.Alignment;

public class LocalAlignerTests
{
    private static int Lcp(byte[] a, int i, byte[] b, int j)
    {
        int h = 0;
        while (i + h < a.Length && j + h < b.Length && a[i + h] == b[j + h]) h++;
        return h;
    }

    private static void Check(byte[] old, byte[] nw)
    {
        LocalMatches lm = LocalAligner.Compute(old, nw);
        Assert.Equal(nw.Length, lm.OldPos.Length);
        Assert.Equal(nw.Length, lm.Length.Length);
        for (int t = 0; t < nw.Length; t++)
        {
            int best = 0;
            for (int s = 0; s < old.Length; s++) best = Math.Max(best, Lcp(old, s, nw, t));
            Assert.True(best == lm.Length[t], $"t={t}: expected length {best}, got {lm.Length[t]}");
            if (best == 0)
            {
                Assert.Equal(-1, lm.OldPos[t]);
            }
            else
            {
                int p = lm.OldPos[t];
                Assert.InRange(p, 0, old.Length - best);
                Assert.True(old.AsSpan(p, best).SequenceEqual(nw.AsSpan(t, best)), $"t={t}: not a match");
            }
        }
    }

    [Fact]
    public void EmptyInputs()
    {
        Check(Array.Empty<byte>(), Array.Empty<byte>());
        Check(new byte[] { 1, 2 }, Array.Empty<byte>());
        Check(Array.Empty<byte>(), new byte[] { 1, 2 });
        Check(new byte[] { 7 }, new byte[] { 7 });
        Check(new byte[] { 7 }, new byte[] { 8 });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(256)]
    public void RandomAgainstBruteForce(int alphabet)
    {
        var rng = new Random(42 + alphabet);
        for (int iter = 0; iter < 1500; iter++)
        {
            int n = rng.Next(0, 50), m = rng.Next(0, 50);
            byte[] old = new byte[n], nw = new byte[m];
            for (int i = 0; i < n; i++) old[i] = (byte)rng.Next(alphabet);
            for (int i = 0; i < m; i++) nw[i] = (byte)rng.Next(alphabet);
            if (m > 4 && n > 4 && rng.Next(2) == 0)
            {
                // plant a copy so that long matches exist
                int len = rng.Next(1, Math.Min(n, m));
                int s = rng.Next(n - len + 1), t = rng.Next(m - len + 1);
                Array.Copy(old, s, nw, t, len);
            }
            Check(old, nw);
        }
    }

    [Fact]
    public void MatchMustNotRunFromOldIntoNew()
    {
        // old ends with "ab"; new = "abab…": the suffix "ab" of old followed by new would match
        // "abab" without the cap. The longest real match is "ab" (length 2) everywhere, except
        // where old has a longer one.
        byte[] old = { (byte)'x', (byte)'a', (byte)'b' };
        byte[] nw = { (byte)'a', (byte)'b', (byte)'a', (byte)'b', (byte)'a', (byte)'b' };
        LocalMatches lm = LocalAligner.Compute(old, nw);
        Assert.Equal(new[] { 2, 1, 2, 1, 2, 1 }, lm.Length);
        Check(old, nw);

        // The case where the nearest old suffix in SA order is the overrunning one and a farther
        // old suffix has the longer capped match.
        Check(System.Text.Encoding.ASCII.GetBytes("aqwa"), System.Text.Encoding.ASCII.GetBytes("qaq"));
        Check(System.Text.Encoding.ASCII.GetBytes("aqaqwaqa"), System.Text.Encoding.ASCII.GetBytes("qaqaqa"));
    }

    [Fact]
    public void TiePrefersPreviousOffset()
    {
        // "abcd" occurs twice in old; new = "zabcd". Position 1..4 should all pick the same copy.
        byte[] old = System.Text.Encoding.ASCII.GetBytes("abcdQQabcdQQ");
        byte[] nw = System.Text.Encoding.ASCII.GetBytes("abcd");
        LocalMatches lm = LocalAligner.Compute(old, nw);
        int off = lm.OldPos[0] - 0;
        for (int t = 1; t < 4; t++) Assert.Equal(off, lm.OldPos[t] - t);
    }

    [Fact]
    public void LargerRandomWithCopies()
    {
        var rng = new Random(7);
        byte[] old = new byte[200_000];
        for (int i = 0; i < old.Length; i++) old[i] = (byte)rng.Next(4);
        byte[] nw = new byte[150_000];
        rng.NextBytes(nw);
        for (int k = 0; k < 50; k++)
        {
            int len = rng.Next(10, 3000);
            int s = rng.Next(old.Length - len), t = rng.Next(nw.Length - len);
            Array.Copy(old, s, nw, t, len);
        }
        LocalMatches lm = LocalAligner.Compute(old, nw);
        // spot-check against brute force on a sample of positions, via a naive search bounded by
        // the reported length (correct match, and no match one longer exists)
        for (int k = 0; k < 300; k++)
        {
            int t = rng.Next(nw.Length);
            int len = lm.Length[t];
            if (len > 0)
                Assert.True(old.AsSpan(lm.OldPos[t], len).SequenceEqual(nw.AsSpan(t, len)));
            if (t + len < nw.Length)
                Assert.True(old.AsSpan().IndexOf(nw.AsSpan(t, len + 1)) < 0, $"t={t}: longer match exists");
        }
    }
}
