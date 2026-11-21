using BsDiff6.Matching;

namespace BsDiff6.Tests.Matching;

public class FftTests
{
    private static (float[] re, float[] im) RandomSignal(int n, ulong seed)
    {
        var rng = new Xoshiro256(seed);
        var re = new float[n];
        var im = new float[n];
        for (int i = 0; i < n; i++)
        {
            re[i] = (float)(rng.NextDouble() * 2 - 1);
            im[i] = (float)(rng.NextDouble() * 2 - 1);
        }
        return (re, im);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(64)]
    [InlineData(1024)]
    [InlineData(16384)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(12)]
    [InlineData(24)]
    [InlineData(3072)]
    [InlineData(12288)]
    public void RoundTrip(int n)
    {
        var (re, im) = RandomSignal(n, 1);
        float[] re0 = (float[])re.Clone(), im0 = (float[])im.Clone();
        var fft = new Fft(n);
        fft.Forward(re, im);
        fft.Inverse(re, im);
        double tol = 1e-5 * Math.Log2(n + 1) + 1e-6;
        for (int i = 0; i < n; i++)
        {
            Assert.True(Math.Abs(re[i] - re0[i]) < tol, $"re[{i}]");
            Assert.True(Math.Abs(im[i] - im0[i]) < tol, $"im[{i}]");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8)]
    [InlineData(32)]
    [InlineData(256)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(12)]
    [InlineData(48)]
    [InlineData(384)]
    public void MatchesNaiveDft(int n)
    {
        var (re, im) = RandomSignal(n, 2);
        var expRe = new double[n];
        var expIm = new double[n];
        for (int k = 0; k < n; k++)
        {
            double sr = 0, si = 0;
            for (int t = 0; t < n; t++)
            {
                double a = -2 * Math.PI * ((long)k * t % n) / n;
                sr += re[t] * Math.Cos(a) - im[t] * Math.Sin(a);
                si += re[t] * Math.Sin(a) + im[t] * Math.Cos(a);
            }
            expRe[k] = sr;
            expIm[k] = si;
        }
        var fft = new Fft(n);
        fft.Forward(re, im);
        double tol = 1e-4 * Math.Sqrt(n) + 1e-5;
        for (int k = 0; k < n; k++)
        {
            Assert.True(Math.Abs(re[k] - expRe[k]) < tol, $"re[{k}] {re[k]} vs {expRe[k]}");
            Assert.True(Math.Abs(im[k] - expIm[k]) < tol, $"im[{k}] {im[k]} vs {expIm[k]}");
        }
    }

    [Theory]
    [InlineData(4096)]
    [InlineData(2048)]
    [InlineData(3 * 1024)]
    [InlineData(3 * 512)]
    public void ScrambledPathsComposeToIdentityTimesN(int n)
    {
        var (re, im) = RandomSignal(n, 3);
        float[] re0 = (float[])re.Clone(), im0 = (float[])im.Clone();
        var fft = new Fft(n);
        fft.ForwardScrambled(re, im);
        // Check against the natural-order transform.
        float[] nr = (float[])re0.Clone(), ni = (float[])im0.Clone();
        fft.Forward(nr, ni);
        int[] rev = fft.FrequencyOfPosition;
        for (int i = 0; i < n; i++)
        {
            Assert.Equal(nr[rev[i]], re[i]);
            Assert.Equal(ni[rev[i]], im[i]);
        }
        fft.InverseFromScrambled(re, im);
        for (int i = 0; i < n; i++)
        {
            Assert.True(Math.Abs(re[i] / n - re0[i]) < 1e-4);
            Assert.True(Math.Abs(im[i] / n - im0[i]) < 1e-4);
        }
    }

    [Theory]
    [InlineData(16)]
    [InlineData(512)]
    [InlineData(48)]
    [InlineData(768)]
    public void CyclicCorrelationMatchesNaive(int n)
    {
        // c[j] = Σ_r a[(r + j) mod n] · b[r] for real a, b, via A · conj(B).
        var rng = new Xoshiro256(4);
        var a = new float[n];
        var b = new float[n];
        for (int i = 0; i < n; i++) { a[i] = (float)(rng.NextDouble() * 2 - 1); b[i] = (float)(rng.NextDouble() * 2 - 1); }
        var expected = new double[n];
        for (int j = 0; j < n; j++)
            for (int r = 0; r < n; r++)
                expected[j] += (double)a[(r + j) % n] * b[r];

        var fft = new Fft(n);
        float[] ar = (float[])a.Clone(), ai = new float[n];
        float[] br = (float[])b.Clone(), bi = new float[n];
        fft.ForwardScrambled(ar, ai);
        fft.ForwardScrambled(br, bi);
        var cr = new float[n];
        var ci = new float[n];
        for (int k = 0; k < n; k++)
        {
            cr[k] = ar[k] * br[k] + ai[k] * bi[k];
            ci[k] = ai[k] * br[k] - ar[k] * bi[k];
        }
        fft.InverseFromScrambled(cr, ci);
        for (int j = 0; j < n; j++)
        {
            Assert.True(Math.Abs(cr[j] / n - expected[j]) < 1e-3 * Math.Sqrt(n), $"c[{j}] {cr[j] / n} vs {expected[j]}");
            Assert.True(Math.Abs(ci[j] / n) < 1e-3 * Math.Sqrt(n));
        }
    }

    [Fact]
    public void RejectsNonPowerOfTwo()
    {
        Assert.Throws<ArgumentException>(() => new Fft(10));
        Assert.Throws<ArgumentException>(() => new Fft(9));
        Assert.Throws<ArgumentException>(() => new Fft(0));
        Assert.Equal(64, Fft.NextSize(64));
        Assert.Equal(96, Fft.NextSize(65));
        Assert.Equal(128, Fft.NextSize(65, allowRadix3: false));
        Assert.Equal(128, Fft.NextSize(97));
        Assert.Equal(3, Fft.NextSize(3));
        Assert.Equal(1, Fft.NextSize(1));
    }

    [Fact]
    public void XoshiroIsDeterministicAndUniformish()
    {
        var a = new Xoshiro256(42);
        var b = new Xoshiro256(42);
        for (int i = 0; i < 100; i++) Assert.Equal(a.NextUInt64(), b.NextUInt64());
        var c = new Xoshiro256(7);
        var counts = new int[10];
        for (int i = 0; i < 100000; i++) counts[c.NextInt(10)]++;
        foreach (int k in counts) Assert.InRange(k, 9000, 11000);
    }
}
