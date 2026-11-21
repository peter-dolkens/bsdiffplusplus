using BsDiff6.Format;

namespace BsDiff6.Tests.EndToEnd;

/// <summary><c>BsPatch.Apply(old, BsDiff.Create(old, new)) == new</c> over synthetic and real pairs.</summary>
public class EndToEndTests
{
    /// <summary>Option combinations: name → options factory.</summary>
    private static BsDiffOptions Options(string name)
    {
        var o = new BsDiffOptions();
        switch (name)
        {
            case "default": break;
            case "block-only": o.UseLocalAlignment = false; break;
            case "local-only": o.UseBlockAlignment = false; break;
            case "no-prune": o.Dp.PruneHopeless = false; break;
            case "no-parallel": o.Parallel = false; break;
            case "mm8-seq": o.Dp.MinLocalMatchLength = 8; o.Parallel = false; break;
            case "block-only-seq": o.UseLocalAlignment = false; o.Parallel = false; break;
            case "none": o.UseLocalAlignment = false; o.UseBlockAlignment = false; break;
            default: throw new ArgumentException(name);
        }
        return o;
    }

    private static readonly string[] AllOptions =
        { "default", "block-only", "local-only", "no-prune", "no-parallel", "mm8-seq", "block-only-seq", "none" };

    private static (byte[] old, byte[] @new) Pair(string kind, int seed)
    {
        var rng = new Random(seed);
        switch (kind)
        {
            case "edits": { byte[] o = E2EData.Random(rng, 60_000); return (o, E2EData.PlantEdits(rng, o, 25)); }
            case "shifted": { byte[] o = E2EData.Random(rng, 80_000); return (o, E2EData.Shuffle(rng, o, 12)); }
            case "noise": { byte[] o = E2EData.Random(rng, 70_000); return (o, E2EData.Noise(rng, o, 0.05)); }
            case "noise-edits": { byte[] o = E2EData.Random(rng, 70_000); return (o, E2EData.PlantEdits(rng, E2EData.Noise(rng, o, 0.10), 10)); }
            case "structured":
            {
                byte[] o = E2EData.Structured(rng, 100_000);
                return (o, E2EData.PlantEdits(rng, E2EData.Relocate(o, 0x340), 15));
            }
            case "unrelated": return (E2EData.Random(rng, 20_000), E2EData.Random(rng, 25_000));
            default: throw new ArgumentException(kind);
        }
    }

    private static readonly string[] Kinds = { "edits", "shifted", "noise", "noise-edits", "structured", "unrelated" };

    public static IEnumerable<object[]> KindsTimesOptions() =>
        from k in Kinds from o in AllOptions select new object[] { k, o };

    private static byte[] RoundTrip(byte[] old, byte[] @new, BsDiffOptions? options)
    {
        byte[] patch = BsDiff.Create(old, @new, options);
        Assert.Equal(@new, BsPatch.Apply(old, patch));
        return patch;
    }

    [Theory]
    [MemberData(nameof(KindsTimesOptions))]
    public void SyntheticPairs(string kind, string options)
    {
        var (old, @new) = Pair(kind, 1);
        byte[] patch = RoundTrip(old, @new, Options(options));
        if (kind != "unrelated" && options != "none" && kind != "noise-edits")
            Assert.True(patch.Length < @new.Length / 4, $"{kind}/{options}: patch {patch.Length} for new {@new.Length}");
    }

    [Fact]
    public void DetailedResultIsConsistent()
    {
        var (old, @new) = Pair("structured", 3);
        BsDiffResult r = BsDiff.CreateDetailed(old, @new);
        Assert.Equal(@new, BsPatch.Apply(old, r.Patch));
        AlignmentUtil.Validate(r.Alignment, old.Length, @new.Length);
        Assert.Same(r.Patch, r.Encode.Patch);
        Assert.Equal(r.Patch.Length, r.Encode.Info.TotalLength);
        Assert.True(r.UsedBlockAlignment && r.UsedLocalAlignment && !r.LocalAlignmentFellBack);
        Assert.NotNull(r.BlockStats);
        Assert.NotNull(r.Dp);
        Assert.Equal(BsDiff6.Alignment.DpAligner.PathCost(old, @new, r.Alignment), r.Dp!.Cost);
        Assert.Equal(@new.Length, r.Coverage.NewLength);
        Assert.True(r.Coverage.CoveredFraction > 0.9);
        Assert.Equal(4, r.Encode.Candidates.Count);
        Assert.True(r.Timings.TotalWall >= r.Timings.EncodeWall);
    }

    [Fact]
    public void NoAlignmentMeansEverythingUnmatched()
    {
        var (old, @new) = Pair("edits", 4);
        BsDiffResult r = BsDiff.CreateDetailed(old, @new, Options("none"));
        Assert.Single(r.Alignment);
        Assert.False(r.Alignment[0].IsMatched);
        Assert.Null(r.Dp);
        Assert.Equal(@new.Length, r.Encode.Info.ExtraLength);
        Assert.Equal(@new, BsPatch.Apply(old, r.Patch));
    }

    public static IEnumerable<object[]> EdgeCases()
    {
        var rng = new Random(9);
        byte[] r5k = E2EData.Random(rng, 5000);
        byte[] big = E2EData.Structured(rng, 50_000);
        yield return new object[] { "both empty", Array.Empty<byte>(), Array.Empty<byte>() };
        yield return new object[] { "empty old", Array.Empty<byte>(), r5k };
        yield return new object[] { "empty new", r5k, Array.Empty<byte>() };
        yield return new object[] { "identical", big, big.ToArray() };
        yield return new object[] { "identical small", new byte[] { 1, 2, 3 }, new byte[] { 1, 2, 3 } };
        yield return new object[] { "1-byte both", new byte[] { 7 }, new byte[] { 7 } };
        yield return new object[] { "1-byte differ", new byte[] { 7 }, new byte[] { 8 } };
        yield return new object[] { "1-byte old", new byte[] { 7 }, r5k };
        yield return new object[] { "1-byte new", r5k, new byte[] { r5k[100] } };
        yield return new object[] { "old >> new", big, big.AsSpan(20_000, 3000).ToArray() };
        yield return new object[] { "new >> old", big.AsSpan(1000, 2000).ToArray(), big };
        yield return new object[] { "all zeros", new byte[40_000], new byte[45_000] };
        yield return new object[] { "zeros vs data", new byte[30_000], big };
        yield return new object[] { "data vs zeros", big, new byte[30_000] };
        yield return new object[] { "repetitive", Enumerable.Range(0, 30_000).Select(i => (byte)(i % 3)).ToArray(),
                                    Enumerable.Range(0, 31_000).Select(i => (byte)(i % 3 == 0 && i % 1000 == 0 ? 9 : i % 3)).ToArray() };
    }

    [Theory]
    [MemberData(nameof(EdgeCases))]
    public void EdgeCasesRoundTripUnderEveryOption(string name, byte[] old, byte[] @new)
    {
        foreach (string o in AllOptions)
        {
            byte[] patch = BsDiff.Create(old, @new, Options(o));
            Assert.True(@new.AsSpan().SequenceEqual(BsPatch.Apply(old, patch)), $"{name} / {o}");
        }
    }

    [Fact]
    public void IdenticalFilesGiveATinyPatch()
    {
        byte[] data = E2EData.Structured(new Random(3), 200_000);
        byte[] patch = RoundTrip(data, data.ToArray(), null);
        Assert.True(patch.Length < 200, $"patch {patch.Length}");
        PatchInfo h = PatchReader.ReadInfo(patch);
        Assert.Equal(0, h.ExtraLength);
        Assert.Equal(0, h.NonZeroLength);
    }

    [Theory]
    [InlineData("structured")]
    [InlineData("noise-edits")]
    [InlineData("shifted")]
    public void DeterministicWithAndWithoutParallel(string kind)
    {
        var (old, @new) = Pair(kind, 21);
        byte[] a = BsDiff.Create(old, @new, new BsDiffOptions());
        byte[] b = BsDiff.Create(old, @new, new BsDiffOptions());
        byte[] c = BsDiff.Create(old, @new, new BsDiffOptions { Parallel = false });
        Assert.Equal(a, b);
        Assert.Equal(a, c);

        var s1 = new BsDiffOptions(); s1.Block.Seed = 12345;
        var s2 = new BsDiffOptions { Parallel = false }; s2.Block.Seed = 12345;
        Assert.Equal(BsDiff.Create(old, @new, s1), BsDiff.Create(old, @new, s2));

        var bo1 = Options("block-only");
        var bo2 = Options("block-only-seq");
        Assert.Equal(BsDiff.Create(old, @new, bo1), BsDiff.Create(old, @new, bo2));
    }

    [Fact]
    public void OptionsObjectIsNotModified()
    {
        var o = new BsDiffOptions { Parallel = false };
        var (old, @new) = Pair("edits", 2);
        BsDiff.Create(old, @new, o);
        Assert.True(o.Block.Parallel);
    }

    [Fact]
    public void ParallelEncodeMatchesSequentialEncode()
    {
        var (old, @new) = Pair("noise-edits", 8);
        Segment[] align = BsDiff.CreateDetailed(old, @new).Alignment;
        EncodeResult seq = PatchWriter.EncodeDetailed(old, @new, align);
        EncodeResult par = PatchWriter.EncodeDetailed(old, @new, align, null, parallel: true);
        Assert.Equal(seq.Patch, par.Patch);
        Assert.Equal(seq.Candidates.Select(c => c.Total), par.Candidates.Select(c => c.Total));
    }

    [CorpusTheory]
    [InlineData("patch-libz.a-1.3-1.3.1-macos-x86_64")]
    [InlineData("patch-zlib-minigzip-1.3-1.3.1-macos-arm64")]
    [InlineData("patch-bzip2-1.0.6-1.0.8-macos-x86_64")]
    public void RealPairs(string pair)
    {
        string dir = Path.Combine(E2EData.CorpusDir!, pair);
        if (!File.Exists(Path.Combine(dir, "old")) || !File.Exists(Path.Combine(dir, "new"))) return; // pair absent: nothing to test
        byte[] old = File.ReadAllBytes(Path.Combine(dir, "old"));
        byte[] @new = File.ReadAllBytes(Path.Combine(dir, "new"));
        byte[] patch = RoundTrip(old, @new, null);
        Assert.True(patch.Length < @new.Length / 5, $"patch {patch.Length}");
        RoundTrip(old, @new, Options("block-only"));
        RoundTrip(old, @new, Options("local-only"));
    }
}
