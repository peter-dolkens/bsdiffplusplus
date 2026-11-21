using BsDiff6.Format;

namespace BsDiff6.Tests.Format;

public class RoundTripTests
{
    private static readonly Segment[] None = Array.Empty<Segment>();

    [Fact]
    public void EmptyNewAndEmptyOld()
    {
        FormatTestData.AssertRoundTrip(Array.Empty<byte>(), Array.Empty<byte>(), None);
        FormatTestData.AssertRoundTrip(new byte[] { 1, 2, 3 }, Array.Empty<byte>(), None);
        byte[] @new = { 9, 8, 7 };
        FormatTestData.AssertRoundTrip(Array.Empty<byte>(), @new, new[] { new Segment(0, 3, -1) });
    }

    [Fact]
    public void IdenticalFiles()
    {
        byte[] data = FormatTestData.Structured(new Random(1), 5000);
        byte[] patch = PatchWriter.Encode(data, data, new[] { new Segment(0, data.Length, 0) });
        Assert.Equal(data, PatchReader.Apply(data, patch));
        PatchInfo info = PatchReader.ReadInfo(patch);
        Assert.Equal(0, info.NonZeroLength);           // no differing bytes, K = 0
        Assert.Equal(0, info.MapCompressedLength);
        Assert.Equal(0, info.ExtraLength);
    }

    [Fact]
    public void EverythingUnmatched()
    {
        var rng = new Random(2);
        byte[] old = FormatTestData.RandomBytes(rng, 300), @new = FormatTestData.RandomBytes(rng, 500);
        FormatTestData.AssertRoundTrip(old, @new, new[] { new Segment(0, 500, -1) });
        // Also split into several unmatched segments (non-canonical input).
        FormatTestData.AssertRoundTrip(old, @new, new[] { new Segment(0, 100, -1), new Segment(100, 400, -1) });
    }

    [Fact]
    public void SingleMatchedSegmentAllBytesDiffer()
    {
        byte[] old = new byte[100];
        byte[] @new = Enumerable.Repeat((byte)5, 100).ToArray();
        foreach (DiffMode mode in Enum.GetValues<DiffMode>())
            FormatTestData.AssertRoundTrip(old, @new, new[] { new Segment(0, 100, 0) }, new EncodeOptions { DiffMode = mode });
    }

    [Fact]
    public void ReusedAndBackwardsSegments()
    {
        var rng = new Random(3);
        byte[] old = FormatTestData.Structured(rng, 1000);
        var segs = new[]
        {
            new Segment(0, 200, 500),
            new Segment(200, 200, 500),      // same old region again
            new Segment(400, 50, -1),
            new Segment(450, 300, 0),        // backwards
            new Segment(750, 250, 700),
            new Segment(1000, 10, 990),      // ends exactly at n
        };
        byte[] @new = new byte[1010];
        foreach (Segment s in segs)
            if (s.IsMatched) old.AsSpan(s.OldPos, s.Length).CopyTo(@new.AsSpan(s.NewPos));
            else rng.NextBytes(@new.AsSpan(s.NewPos, s.Length));
        @new[10] ^= 1; @new[620] += 3; @new[1005] = 0;
        foreach (DiffMode mode in Enum.GetValues<DiffMode>())
            FormatTestData.AssertRoundTrip(old, @new, segs, new EncodeOptions { DiffMode = mode });
    }

    [Fact]
    public void LeadingUnmatchedThenMatched()
    {
        var rng = new Random(4);
        byte[] old = FormatTestData.RandomBytes(rng, 100);
        byte[] @new = new byte[150];
        rng.NextBytes(@new.AsSpan(0, 50));
        old.CopyTo(@new, 50);
        FormatTestData.AssertRoundTrip(old, @new, new[] { new Segment(0, 50, -1), new Segment(50, 100, 0) });
    }

    [Fact]
    public void InvalidAlignmentThrowsArgumentException()
    {
        byte[] old = new byte[10], @new = new byte[10];
        Assert.ThrowsAny<ArgumentException>(() => PatchWriter.Encode(old, @new, new[] { new Segment(0, 5, 0) }));
        Assert.ThrowsAny<ArgumentException>(() => PatchWriter.Encode(old, @new, new[] { new Segment(0, 10, 1) }));
        Assert.ThrowsAny<ArgumentException>(() => PatchWriter.Encode(old, @new, new[] { new Segment(0, 10, -2) }));
    }

    [Fact]
    public void RandomAlignmentsAllModesAndCodecs()
    {
        var rng = new Random(12345);
        for (int iter = 0; iter < 60; iter++)
        {
            int n = iter % 10 == 0 ? 0 : rng.Next(1, 4000);
            int m = iter % 13 == 0 ? 0 : rng.Next(1, 4000);
            byte[] old = iter % 2 == 0 ? FormatTestData.RandomBytes(rng, n) : FormatTestData.Structured(rng, n);
            var (@new, align) = FormatTestData.Derive(rng, old, m,
                matchedFraction: rng.NextDouble(), mutationRate: rng.NextDouble() * 0.2, maxSeg: rng.Next(1, 500));
            var opts = new EncodeOptions();
            if (iter % 3 == 1)
            {
                opts.DiffMode = (DiffMode)rng.Next(4);
                opts.ControlCodec = (Codec)rng.Next(3);
                opts.NonZeroCodec = (Codec)rng.Next(3);
                opts.ExtraCodec = (Codec)rng.Next(3);
            }
            FormatTestData.AssertRoundTrip(old, @new, align, opts);
        }
    }

    [Fact]
    public void ChoosesSmallestModeAndReportsBreakdown()
    {
        var rng = new Random(8);
        byte[] old = FormatTestData.Structured(rng, 20000);
        var (@new, align) = FormatTestData.Derive(rng, old, 20000, 0.9, 0.03, 2000);
        EncodeResult r = PatchWriter.EncodeDetailed(old, @new, align);
        Assert.Equal(4, r.Candidates.Count);
        Assert.Equal(r.Candidates.Min(c => c.Total), r.Candidates.Single(c => c.Mode == r.Info.DiffMode).Total);
        Assert.Equal(r.Patch.Length, r.Info.TotalLength);
        PatchInfo read = PatchReader.ReadInfo(r.Patch);
        Assert.Equal(r.Info.ToString(), read.ToString());
        Assert.Equal(@new, PatchReader.Apply(old, r.Patch));
        foreach (DiffModeCandidate c in r.Candidates)
        {
            byte[] forced = PatchWriter.Encode(old, @new, align, new EncodeOptions { DiffMode = c.Mode });
            Assert.True(forced.Length >= r.Patch.Length);
            Assert.Equal(@new, PatchReader.Apply(old, forced));
        }
    }

    [Fact]
    public void MapModelsRoundTrip()
    {
        var rng = new Random(21);
        for (int id = 0; id < MidpointCoder.ModelCount; id++)
        {
            for (int iter = 0; iter < 20; iter++)
            {
                byte[] old = FormatTestData.Structured(rng, rng.Next(1, 3000));
                var (@new, align) = FormatTestData.Derive(rng, old, rng.Next(1, 3000), 0.9, rng.NextDouble() * 0.5);
                FormatTestData.AssertRoundTrip(old, @new, align, new EncodeOptions { MapModel = (MapModel)id });
            }
        }
    }
}
