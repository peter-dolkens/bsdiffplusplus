using BsDiff6.Matching;
using Xunit.Abstractions;

namespace BsDiff6.Tests.Matching;

public class BlockAlignerTests
{
    private readonly ITestOutputHelper _out;
    public BlockAlignerTests(ITestOutputHelper output) => _out = output;

    private sealed class Piece
    {
        public int NewPos, Length, OldPos; // OldPos -1 = inserted random bytes
        public double Mismatch;
    }

    /// <summary>
    /// new = regions of old in shuffled order, each with substitutions, separated by inserted
    /// random regions; some neighbouring regions are adjacent with no insert between them.
    /// </summary>
    private static (byte[] newData, List<Piece> pieces) Build(byte[] old, int blockLen, ulong seed)
    {
        var rng = new Xoshiro256(seed);
        int regions = 8;
        int regionLen = old.Length / regions;
        var order = Enumerable.Range(0, regions).ToArray();
        for (int i = regions - 1; i > 0; i--) { int j = rng.NextInt(i + 1); (order[i], order[j]) = (order[j], order[i]); }
        double[] rates = { 0.0, 0.3, 0.4, 0.5, 0.0, 0.3, 0.4, 0.5 };
        // insert lengths before each region: 0 = adjacent, small, and "several blocks"
        int[] inserts = { 0, 0, 500, 4 * blockLen, 0, 3 * blockLen, 1500, 5 * blockLen };
        var data = new List<byte>();
        var pieces = new List<Piece>();
        for (int k = 0; k < regions; k++)
        {
            if (inserts[k] > 0)
            {
                pieces.Add(new Piece { NewPos = data.Count, Length = inserts[k], OldPos = -1 });
                data.AddRange(TestData.Random(inserts[k], rng.NextUInt64()));
            }
            int start = order[k] * regionLen + rng.NextInt(regionLen / 8);
            int len = regionLen - regionLen / 8;
            byte[] chunk = old.AsSpan(start, len).ToArray();
            TestData.Substitute(chunk, rates[k], rng);
            pieces.Add(new Piece { NewPos = data.Count, Length = len, OldPos = start, Mismatch = rates[k] });
            data.AddRange(chunk);
        }
        return (data.ToArray(), pieces);
    }

    private static int[] OffsetMap(Segment[] segs, int m)
    {
        var map = new int[m];
        Array.Fill(map, int.MinValue);
        foreach (Segment s in segs)
            if (s.IsMatched)
                for (int i = s.NewPos; i < s.NewEnd; i++) map[i] = s.Offset;
        return map;
    }

    private static void AssertCanonical(Segment[] segs, int n, int m)
    {
        AlignmentUtil.Validate(segs, n, m);
        Assert.Equal(segs.Length, AlignmentUtil.Canonicalize(segs).Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecoversShuffledRegionsWithSubstitutions(bool binaryLike)
    {
        byte[] old = binaryLike ? TestData.BinaryLike(1 << 18, 21) : TestData.Random(1 << 18, 21);
        int b = BlockAlignOptions.DefaultBlockLength(old.Length);
        var (nw, pieces) = Build(old, b, 77);
        Segment[] segs = BlockAligner.Align(old, nw);
        AssertCanonical(segs, old.Length, nw.Length);
        int[] map = OffsetMap(segs, nw.Length);

        long copied = 0, right = 0;
        foreach (Piece p in pieces.Where(p => p.OldPos >= 0))
        {
            int expected = p.OldPos - p.NewPos, ok = 0;
            for (int i = p.NewPos; i < p.NewPos + p.Length; i++) if (map[i] == expected) ok++;
            copied += p.Length;
            right += ok;
            _out.WriteLine($"region new@{p.NewPos} len {p.Length} mismatch {p.Mismatch:P0}: right offset {(double)ok / p.Length:P2}");
            Assert.True(ok >= 0.9 * p.Length, $"region at {p.NewPos} ({p.Mismatch:P0}) only {(double)ok / p.Length:P1} right");
        }
        _out.WriteLine($"copied bytes with right offset: {(double)right / copied:P2}");
        Assert.True(right >= 0.97 * copied);

        // Inserts of several blocks: mostly no prediction, and no long wrong run inside.
        foreach (Piece p in pieces.Where(p => p.OldPos < 0 && p.Length >= 3 * b))
        {
            int unmatched = 0, longestWrong = 0, cur = 0;
            for (int i = p.NewPos; i < p.NewPos + p.Length; i++)
            {
                if (map[i] == int.MinValue) { unmatched++; cur = 0; }
                else longestWrong = Math.Max(longestWrong, ++cur);
            }
            _out.WriteLine($"insert new@{p.NewPos} len {p.Length}: unmatched {(double)unmatched / p.Length:P1}, longest claimed run {longestWrong}");
            Assert.True(unmatched >= 0.6 * p.Length);
            Assert.True(longestWrong <= 2 * b);
        }

        var cov = AlignmentCoverage.Measure(old, nw, segs);
        _out.WriteLine($"covered {cov.CoveredFraction:P1}, matching {cov.MatchFraction:P1}");
    }

    [Fact]
    public void BoundariesBetweenAdjacentRegionsAreExactWhenOneSideIsClean()
    {
        byte[] old = TestData.Random(1 << 18, 5);
        int b = BlockAlignOptions.DefaultBlockLength(old.Length);
        var rng = new Xoshiro256(3);
        // Four adjacent regions (no inserts): clean, 40%, clean, clean; boundaries not block-aligned.
        int[] starts = { 150_000, 10_000, 205_000, 75_000 };
        int[] lens = { 7 * b + 123, 9 * b + 77, 8 * b + 1, 6 * b + 999 };
        double[] rates = { 0.0, 0.4, 0.0, 0.0 };
        var data = new List<byte>();
        var bounds = new List<int>();
        for (int k = 0; k < 4; k++)
        {
            if (k > 0) bounds.Add(data.Count);
            byte[] chunk = old.AsSpan(starts[k], lens[k]).ToArray();
            TestData.Substitute(chunk, rates[k], rng);
            data.AddRange(chunk);
        }
        byte[] nw = data.ToArray();
        Segment[] segs = BlockAligner.Align(old, nw);
        AssertCanonical(segs, old.Length, nw.Length);
        var found = segs.Skip(1).Select(s => s.NewPos).ToList();
        _out.WriteLine("true: " + string.Join(",", bounds) + "  found: " + string.Join(",", found));
        Assert.Equal(bounds, found);
        Assert.All(segs, s => Assert.True(s.IsMatched));
    }

    [Fact]
    public void IsDeterministicAcrossRunsAndParallelism()
    {
        byte[] old = TestData.BinaryLike(1 << 17, 9);
        int b = BlockAlignOptions.DefaultBlockLength(old.Length);
        var (nw, _) = Build(old, b, 4);
        Segment[] a = BlockAligner.Align(old, nw);
        Segment[] c = BlockAligner.Align(old, nw);
        Segment[] d = BlockAligner.Align(old, nw, new BlockAlignOptions { Parallel = false });
        Segment[] e = BlockAligner.Align(old, nw, new BlockAlignOptions { MaxDegreeOfParallelism = 3 });
        Assert.Equal(a, c);
        Assert.Equal(a, d);
        Assert.Equal(a, e);
        Segment[] f = BlockAligner.Align(old, nw, new BlockAlignOptions { Seed = 12345 });
        AssertCanonical(f, old.Length, nw.Length); // another seed: different primes, still valid
    }

    [Fact]
    public void DesignVariantsStillProduceValidAlignments()
    {
        byte[] old = TestData.BinaryLike(1 << 17, 10);
        int b = BlockAlignOptions.DefaultBlockLength(old.Length);
        var (nw, _) = Build(old, b, 5);
        var variants = new[]
        {
            new BlockAlignOptions { FillFftSize = false, UnmatchedCost = 0.5, BidirectionalContinuity = false, TweakWindowBlocks = 0 },
            new BlockAlignOptions { MapsPerPrime = 1, FrequencyWeighting = false },
            new BlockAlignOptions { MapsPerPrime = 3, TopK = 8, VerifyCount = 2 },
            new BlockAlignOptions { Tweak = false, ContinuityCandidate = false },
        };
        foreach (var o in variants)
        {
            Segment[] segs = BlockAligner.Align(old, nw, o);
            AssertCanonical(segs, old.Length, nw.Length);
        }
    }

    [Fact]
    public void EmptyAndTinyInputs()
    {
        byte[] some = TestData.Random(5000, 1);
        Assert.Empty(BlockAligner.Align(Array.Empty<byte>(), Array.Empty<byte>()));
        Assert.Empty(BlockAligner.Align(some, Array.Empty<byte>()));
        Assert.Equal(new[] { new Segment(0, 5000, -1) }, BlockAligner.Align(Array.Empty<byte>(), some));
        byte[] tiny = TestData.Random(1000, 2);
        Assert.Equal(new[] { new Segment(0, 1000, -1) }, BlockAligner.Align(tiny, tiny)); // n < 1024: skipped
        Assert.Equal(new[] { new Segment(0, 1, -1) }, BlockAligner.Align(some, new byte[] { 7 }));
        // Smallest old file that is indexed, with a new file shorter than one block.
        byte[] small = TestData.Random(1024, 3);
        Segment[] s = BlockAligner.Align(small, small.AsSpan(100, 40).ToArray());
        AssertCanonical(s, 1024, 40);
    }

    [Fact]
    public void IdenticalFilesGiveOneSegment()
    {
        byte[] old = TestData.BinaryLike(200_000, 4);
        Segment[] segs = BlockAligner.Align(old, old);
        Assert.Equal(new[] { new Segment(0, old.Length, 0) }, segs);
    }

    [Fact]
    public void OldMuchLargerThanNew()
    {
        byte[] old = TestData.Random(400_000, 6);
        byte[] nw = old.AsSpan(345_678, 20_000).ToArray();
        TestData.Substitute(nw, 0.3, new Xoshiro256(1));
        Segment[] segs = BlockAligner.Align(old, nw);
        AssertCanonical(segs, old.Length, nw.Length);
        Assert.Equal(new[] { new Segment(0, nw.Length, 345_678) }, segs);
    }

    [Fact]
    public void NewMuchLargerThanOld()
    {
        byte[] old = TestData.Random(4096, 7);
        var rng = new Xoshiro256(2);
        var data = new List<byte>();
        for (int k = 0; k < 40; k++)
        {
            byte[] copy = (byte[])old.Clone();
            TestData.Substitute(copy, 0.3, rng);
            data.AddRange(copy);
        }
        byte[] nw = data.ToArray();
        Segment[] segs = BlockAligner.Align(old, nw);
        AssertCanonical(segs, old.Length, nw.Length);
        var cov = AlignmentCoverage.Measure(old, nw, segs);
        _out.WriteLine($"{segs.Length} segments, covered {cov.CoveredFraction:P1}, matching {cov.MatchFraction:P1}");
        Assert.True(cov.CoveredFraction > 0.95);
        Assert.True(cov.MatchFraction > 0.65);
    }

    [Fact]
    public void AllZeros()
    {
        byte[] zeros = new byte[100_000];
        Segment[] segs = BlockAligner.Align(zeros, zeros);
        AssertCanonical(segs, zeros.Length, zeros.Length);
        var cov = AlignmentCoverage.Measure(zeros, zeros, segs);
        Assert.Equal(zeros.Length, cov.CoveredBytes);
        Assert.Equal(zeros.Length, cov.MatchingBytes);

        byte[] random = TestData.Random(100_000, 8);
        segs = BlockAligner.Align(zeros, random);
        Assert.Equal(new[] { new Segment(0, random.Length, -1) }, segs);
    }

    [Fact]
    public void CoverageCountsMatchedAndEqualBytes()
    {
        byte[] old = { 1, 2, 3, 4, 5, 6, 7, 8 };
        byte[] nw = { 9, 2, 3, 0, 5, 9, 9 };
        var segs = new[] { new Segment(0, 1, -1), new Segment(1, 4, 1), new Segment(5, 2, 0) };
        var cov = AlignmentCoverage.Measure(old, nw, segs);
        Assert.Equal(6, cov.CoveredBytes);
        Assert.Equal(3, cov.MatchingBytes); // 2,3,5 at offset 0; nothing at offset -5
        Assert.Equal(7, cov.NewLength);
        Assert.Equal(0, MatchCounter.Count(old, -10, nw.AsSpan(0, 5)));
        Assert.Equal(2, MatchCounter.Count(old, -1, new byte[] { 0, 1, 2, 0 })); // old[0]=1, old[1]=2
        byte[] big1 = TestData.Random(10_000, 1), big2 = (byte[])big1.Clone();
        for (int i = 0; i < big2.Length; i += 3) big2[i] ^= 0x55;
        Assert.Equal(10_000 - 3334, MatchCounter.CountEqual(big1, big2));
    }
}
