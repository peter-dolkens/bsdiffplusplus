using BsDiff6.Matching;
using Xunit.Abstractions;

namespace BsDiff6.Tests.Matching;

public class MismatchIndexTests
{
    private readonly ITestOutputHelper _out;
    public MismatchIndexTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData(3, true)]   // odd map count exercises the unpaired map; N = 384 = 3·128
    [InlineData(2, false)]  // N = 512
    public void CorrelationMatchesNaiveSum(int maps, bool radix3)
    {
        byte[] old = TestData.Random(5000, 11);
        var opt = new BlockAlignOptions { MapsPerPrime = maps, AllowRadix3Fft = radix3, MinPrimeChoices = 2 };
        var ix = new MismatchIndex(old, 64, ByteWeights.InverseSqrtFrequency(old, default), opt);
        Assert.Equal(radix3, ix.FftSize % 3 == 0);
        byte[] block = TestData.Random(64, 12);
        MismatchQuery q = ix.CreateQuery();
        q.ComputeCorrelations(block);

        for (int prime = 0; prime < 2; prime++)
        {
            int p = prime == 0 ? ix.P1 : ix.P2;
            var expected = new double[p];
            for (int map = 0; map < opt.MapsPerPrime; map++)
            {
                ReadOnlySpan<float> phi = ix.Phi(prime, map);
                var a = new double[p];
                for (int t = 0; t < old.Length; t++) a[t % p] += phi[old[t]];
                for (int j = 0; j < p; j++)
                    for (int r = 0; r < block.Length; r++)
                        expected[j] += a[(r + j) % p] * phi[block[r]];
            }
            ReadOnlySpan<float> got = q.Correlation(prime);
            Assert.Equal(p, got.Length);
            double scale = 0;
            for (int j = 0; j < p; j++) scale = Math.Max(scale, Math.Abs(expected[j]));
            for (int j = 0; j < p; j++)
                Assert.True(Math.Abs(got[j] - expected[j]) < 1e-4 * scale + 1e-3, $"prime {prime} j {j}: {got[j]} vs {expected[j]}");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrimesAreDistinctPrimesInTheInterval(bool fill)
    {
        byte[] old = TestData.Random(100_000, 1);
        var seen = new HashSet<int>();
        for (ulong seed = 0; seed < 12; seed++)
        {
            var ix = new MismatchIndex(old, 1000, ByteWeights.Uniform(), new BlockAlignOptions { Seed = seed, FillFftSize = fill });
            Assert.NotEqual(ix.P1, ix.P2);
            foreach (int p in new[] { ix.P1, ix.P2 })
            {
                seen.Add(p);
                if (fill)
                    Assert.InRange(p, 4000, ix.FftSize - 1000);
                else
                    Assert.InRange(p, 4000, (int)Math.Ceiling(4000 * (1 + 2 / Math.Log(4000))) - 1);
                for (int d = 2; d * d <= p; d++) Assert.NotEqual(0, p % d);
            }
            Assert.True(ix.MaxQueryLength >= 1000);
            Assert.Equal(6144, ix.FftSize); // 3·2048 ≥ L + B (fill) or max(p) + B
            var p2 = new MismatchIndex(old, 1000, ByteWeights.Uniform(), new BlockAlignOptions { Seed = seed, FillFftSize = fill, AllowRadix3Fft = false });
            Assert.Equal(8192, p2.FftSize);
        }
        Assert.True(seen.Count > 10); // the seed matters
    }

    /// <summary>Fraction of trials whose top verified candidate is the true position.</summary>
    private double LocateRate(byte[] old, double mismatch, int trials, ulong seed, bool weighted, out double atLeastAsGood)
    {
        int b = BlockAlignOptions.DefaultBlockLength(old.Length);
        float[] w = weighted ? ByteWeights.InverseSqrtFrequency(old, default) : ByteWeights.Uniform();
        var ix = new MismatchIndex(old, b, w, new BlockAlignOptions { Seed = seed });
        MismatchQuery q = ix.CreateQuery();
        var rng = new Xoshiro256(seed * 31 + 7);
        int hits = 0, good = 0;
        var block = new byte[b];
        for (int t = 0; t < trials; t++)
        {
            int x = rng.NextInt(old.Length - b);
            old.AsSpan(x, b).CopyTo(block);
            TestData.Substitute(block, mismatch, rng);
            int trueMatches = MatchCounter.Count(old, x, block);
            (int pos, int matches) = TestData.BestVerified(q, old, block, 8);
            if (pos == x) hits++;
            if (matches >= trueMatches) good++;
        }
        atLeastAsGood = (double)good / trials;
        return (double)hits / trials;
    }

    [Theory]
    [InlineData(0.0, 1.0)]
    [InlineData(0.3, 1.0)]
    [InlineData(0.5, 0.95)]
    [InlineData(0.6, 0.6)]
    public void LocatesSubstitutedCopiesInRandomData(double mismatch, double minRate)
    {
        byte[] old = TestData.Random(1 << 18, 5);
        double rate = LocateRate(old, mismatch, 24, 3, weighted: true, out _);
        _out.WriteLine($"random, {mismatch:P0} mismatches: located {rate:P1}");
        Assert.True(rate >= minRate, $"located {rate:P1}");
    }

    [Theory]
    [InlineData(0.0, 1.0)]
    [InlineData(0.3, 0.9)]
    [InlineData(0.5, 0.6)] // ~75% over 200 trials: uniform random substitutes in skewed data are
                           // mostly rare bytes, which carry large weights (noise) under §1.6 note 4
    public void LocatesSubstitutedCopiesInBinaryLikeData(double mismatch, double minRate)
    {
        byte[] old = TestData.BinaryLike(1 << 18, 6);
        double rate = LocateRate(old, mismatch, 24, 4, weighted: true, out double asGood);
        _out.WriteLine($"binary-like, {mismatch:P0} mismatches: located {rate:P1}, at least as good {asGood:P1}");
        // Repeated regions can make another position equally good; that counts as found.
        Assert.True(asGood >= minRate, $"found {asGood:P1}");
    }

    [Fact]
    public void ShortQueriesAndOverhangAtStart()
    {
        byte[] old = TestData.Random(1 << 17, 8);
        int b = BlockAlignOptions.DefaultBlockLength(old.Length);
        var ix = new MismatchIndex(old, b, ByteWeights.Uniform());
        MismatchQuery q = ix.CreateQuery();
        // Block whose first quarter lies before the start of the old file.
        var block = new byte[b];
        TestData.Random(b / 4, 99).CopyTo(block, 0);
        old.AsSpan(0, b - b / 4).CopyTo(block.AsSpan(b / 4));
        (int pos, _) = TestData.BestVerified(q, old, block, 8);
        Assert.Equal(-(b / 4), pos);
        // Half-length block.
        byte[] half = old.AsSpan(5000, b / 2).ToArray();
        TestData.Substitute(half, 0.3, new Xoshiro256(1));
        (pos, _) = TestData.BestVerified(q, old, half, 8);
        Assert.Equal(5000, pos);
    }
}
