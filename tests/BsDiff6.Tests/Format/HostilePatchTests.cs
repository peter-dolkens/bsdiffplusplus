using System.Diagnostics;
using System.Security.Cryptography;
using BsDiff6.Format;

namespace BsDiff6.Tests.Format;

/// <summary>Helpers to take patches apart and reassemble them with modified fields.</summary>
internal sealed class PatchParts
{
    public PatchInfo Info = new PatchInfo();
    public byte[] Control = Array.Empty<byte>(), NonZero = Array.Empty<byte>(), Map = Array.Empty<byte>(), Extra = Array.Empty<byte>();

    public static PatchParts Split(byte[] patch)
    {
        PatchInfo h = PatchReader.ReadInfo(patch);
        int p = h.HeaderLength;
        var parts = new PatchParts { Info = h };
        parts.Control = patch.AsSpan(p, (int)h.ControlCompressedLength).ToArray(); p += parts.Control.Length;
        parts.NonZero = patch.AsSpan(p, (int)h.NonZeroCompressedLength).ToArray(); p += parts.NonZero.Length;
        parts.Map = patch.AsSpan(p, (int)h.MapCompressedLength).ToArray(); p += parts.Map.Length;
        parts.Extra = patch.AsSpan(p, (int)h.ExtraCompressedLength).ToArray();
        return parts;
    }

    /// <summary>
    /// Reassembles. Compressed lengths are taken from the section arrays unless
    /// <paramref name="keepDeclaredLengths"/>; other header fields from <see cref="Info"/>
    /// (customise with <paramref name="edit"/>).
    /// </summary>
    public byte[] Build(Func<PatchInfo, PatchInfo>? edit = null, bool keepDeclaredLengths = false)
    {
        PatchInfo i = Info;
        var h = new PatchInfo
        {
            Version = i.Version, DiffMode = i.DiffMode, ControlCodec = i.ControlCodec, NonZeroCodec = i.NonZeroCodec,
            ExtraCodec = i.ExtraCodec, MapModel = i.MapModel, OldSize = i.OldSize, NewSize = i.NewSize,
            OldMd5 = i.OldMd5, NewMd5 = i.NewMd5,
            ControlCompressedLength = keepDeclaredLengths ? i.ControlCompressedLength : Control.Length,
            ControlLength = i.ControlLength,
            NonZeroCompressedLength = keepDeclaredLengths ? i.NonZeroCompressedLength : NonZero.Length,
            NonZeroLength = i.NonZeroLength,
            ExtraCompressedLength = keepDeclaredLengths ? i.ExtraCompressedLength : Extra.Length,
            ExtraLength = i.ExtraLength,
            MapCompressedLength = keepDeclaredLengths ? i.MapCompressedLength : Map.Length,
            BwtPrimary = i.BwtPrimary,
        };
        if (edit != null) h = edit(h);
        return PatchHeader.Write(h).Concat(Control).Concat(NonZero).Concat(Map).Concat(Extra).ToArray();
    }

    public static PatchInfo With(PatchInfo i, Action<Dictionary<string, long>> change)
    {
        var f = new Dictionary<string, long>
        {
            ["version"] = i.Version, ["mode"] = (int)i.DiffMode, ["cc"] = (int)i.ControlCodec, ["nc"] = (int)i.NonZeroCodec,
            ["ec"] = (int)i.ExtraCodec, ["model"] = i.MapModel, ["old"] = i.OldSize, ["new"] = i.NewSize,
            ["ctrlC"] = i.ControlCompressedLength, ["ctrl"] = i.ControlLength, ["nzC"] = i.NonZeroCompressedLength,
            ["nz"] = i.NonZeroLength, ["exC"] = i.ExtraCompressedLength, ["ex"] = i.ExtraLength,
            ["mapC"] = i.MapCompressedLength, ["primary"] = i.BwtPrimary,
        };
        change(f);
        return new PatchInfo
        {
            Version = (int)f["version"], DiffMode = (DiffMode)f["mode"], ControlCodec = (Codec)f["cc"], NonZeroCodec = (Codec)f["nc"],
            ExtraCodec = (Codec)f["ec"], MapModel = (int)f["model"], OldSize = f["old"], NewSize = f["new"],
            OldMd5 = i.OldMd5, NewMd5 = i.NewMd5,
            ControlCompressedLength = f["ctrlC"], ControlLength = f["ctrl"], NonZeroCompressedLength = f["nzC"],
            NonZeroLength = f["nz"], ExtraCompressedLength = f["exC"], ExtraLength = f["ex"], MapCompressedLength = f["mapC"],
            BwtPrimary = f["primary"],
        };
    }
}

public class HostilePatchTests
{
    private static readonly EncodeOptions Raw = new EncodeOptions
    { ControlCodec = Codec.None, NonZeroCodec = Codec.None, ExtraCodec = Codec.None };

    private static (byte[] old, byte[] @new, byte[] patch) Sample(EncodeOptions? opts = null, int seed = 1)
    {
        var rng = new Random(seed);
        byte[] old = FormatTestData.Structured(rng, 2000);
        var (@new, align) = FormatTestData.Derive(rng, old, 2200, 0.85, 0.05, 300);
        return (old, @new, PatchWriter.Encode(old, @new, align, opts ?? Raw));
    }

    private static void Rejects(byte[] old, byte[] patch)
    {
        var sw = Stopwatch.StartNew();
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<PatchFormatException>(() => PatchReader.Apply(old, patch));
        Assert.True(sw.ElapsedMilliseconds < 5000);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 256L << 20, "excessive allocation");
    }

    private static byte[] Ctrl(params long[] triples)
    {
        var o = new List<byte>();
        for (int i = 0; i < triples.Length; i += 3)
        {
            VarInt.WriteSigned(o, triples[i]);
            VarInt.Write(o, (ulong)triples[i + 1]);
            VarInt.Write(o, (ulong)triples[i + 2]);
        }
        return o.ToArray();
    }

    [Fact]
    public void SampleIsValid()
    {
        var (old, @new, patch) = Sample();
        Assert.Equal(@new, PatchReader.Apply(old, patch));
    }

    [Fact]
    public void BadMagicVersionAndIds()
    {
        var (old, _, patch) = Sample();
        var parts = PatchParts.Split(patch);
        byte[] p = (byte[])patch.Clone(); p[0] = (byte)'X'; Rejects(old, p);
        Rejects(old, parts.Build(h => PatchParts.With(h, f => f["version"] = 2)));
        Rejects(old, parts.Build(h => PatchParts.With(h, f => f["version"] = 0)));
        Rejects(old, parts.Build(h => PatchParts.With(h, f => f["cc"] = 3)));
        Rejects(old, parts.Build(h => PatchParts.With(h, f => f["nc"] = 3)));
        Rejects(old, parts.Build(h => PatchParts.With(h, f => f["ec"] = 3)));
        Rejects(old, parts.Build(h => PatchParts.With(h, f => f["model"] = 200)));
        Rejects(old, parts.Build(h => PatchParts.With(h, f => f["model"] = MidpointCoder.ModelCount)));
        // Wrong codec id for the data actually present.
        Rejects(old, parts.Build(h => PatchParts.With(h, f => f["cc"] = 1)));
        Rejects(old, parts.Build(h => PatchParts.With(h, f => f["ec"] = 2)));
        // A different diff mode yields a wrong new file: caught by MD5.
        Rejects(old, parts.Build(h => PatchParts.With(h, f => f["mode"] = (f["mode"] + 1) % 4)));
    }

    [Fact]
    public void TruncationsAndTrailingGarbage()
    {
        var (old, _, patch) = Sample();
        Rejects(old, patch.Concat(new byte[] { 0 }).ToArray());
        Rejects(old, patch.AsSpan(0, patch.Length - 1).ToArray());
        Rejects(old, Array.Empty<byte>());
        Rejects(old, patch.AsSpan(0, 8).ToArray());
    }

    [Fact]
    public void WrongMd5sAndWrongOld()
    {
        var (old, _, patch) = Sample();
        var parts = PatchParts.Split(patch);
        byte[] badOld = (byte[])old.Clone(); badOld[5] ^= 1;
        Rejects(badOld, patch);
        Rejects(old.AsSpan(0, old.Length - 1).ToArray(), patch);
        parts.Info = new PatchInfo
        {
            Version = parts.Info.Version, DiffMode = parts.Info.DiffMode, ControlCodec = parts.Info.ControlCodec,
            NonZeroCodec = parts.Info.NonZeroCodec, ExtraCodec = parts.Info.ExtraCodec, MapModel = parts.Info.MapModel,
            OldSize = parts.Info.OldSize, NewSize = parts.Info.NewSize, OldMd5 = parts.Info.OldMd5,
            NewMd5 = new byte[16], ControlLength = parts.Info.ControlLength, NonZeroLength = parts.Info.NonZeroLength,
            ExtraLength = parts.Info.ExtraLength, BwtPrimary = parts.Info.BwtPrimary,
        };
        Rejects(old, parts.Build());
    }

    [Fact]
    public void HugeDeclaredSizes()
    {
        var (old, _, patch) = Sample(new EncodeOptions());   // compressed sections
        var parts = PatchParts.Split(patch);
        const long Max = int.MaxValue - 64;
        Rejects(old, parts.Build(h => PatchParts.With(h, f => f["new"] = Max)));
        Rejects(old, parts.Build(h => PatchParts.With(h, f => f["new"] = Max + 1)));
        Rejects(old, parts.Build(h => PatchParts.With(h, f => f["ctrl"] = Max)));
        Rejects(old, parts.Build(h => PatchParts.With(h, f => f["nz"] = Max)));
        Rejects(old, parts.Build(h => PatchParts.With(h, f => f["ex"] = Max)));
        Rejects(old, parts.Build(h => PatchParts.With(h, f => { f["new"] = Max; f["ex"] = Max; f["nz"] = Max; f["ctrl"] = Max; })));
        Rejects(old, parts.Build(h => PatchParts.With(h, f => f["primary"] = Max)));
        Rejects(old, parts.Build(h => PatchParts.With(h, f => f["mapC"] = Max), keepDeclaredLengths: true));
        Rejects(old, parts.Build(h => PatchParts.With(h, f => f["old"] = Max)));

        // A bzip2 bomb: 4 MB of zeros declared as only 1 MB of extra string.
        byte[] bomb = StringCodec.Compress(new byte[4 << 20], Codec.Bzip2);
        var p2 = PatchParts.Split(patch);
        p2.Extra = bomb;
        Rejects(old, p2.Build(h => PatchParts.With(h, f => { f["ec"] = 2; f["ex"] = 1 << 20; })));
    }

    [Fact]
    public void ControlStringViolations()
    {
        byte[] old = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
        byte[] Make(byte[] control, int newSize, int extraLen, int d)
        {
            // Raw sections, no differences (K = 0), extra = zeros.
            var info = new PatchInfo
            {
                Version = 1, DiffMode = DiffMode.Bytewise, OldSize = old.Length, NewSize = newSize,
                OldMd5 = MD5.HashData(old), NewMd5 = new byte[16],
                ControlCompressedLength = control.Length, ControlLength = control.Length,
                ExtraCompressedLength = extraLen, ExtraLength = extraLen,
            };
            return PatchHeader.Write(info).Concat(control).Concat(new byte[extraLen]).ToArray();
        }

        // Sanity: a correct patch built this way works (copy 10 from 5, then 3 extra).
        byte[] good = Make(Ctrl(5, 10, 3), 13, 3, 10);
        byte[] expected = old.Skip(5).Take(10).Concat(new byte[3]).ToArray();
        var parts = PatchParts.Split(good);
        byte[] fixedMd5 = PatchHeader.Write(new PatchInfo
        {
            Version = 1, DiffMode = DiffMode.Bytewise, OldSize = old.Length, NewSize = 13, OldMd5 = MD5.HashData(old),
            NewMd5 = MD5.HashData(expected), ControlCompressedLength = parts.Control.Length, ControlLength = parts.Control.Length,
            ExtraCompressedLength = 3, ExtraLength = 3,
        }).Concat(parts.Control).Concat(new byte[3]).ToArray();
        Assert.Equal(expected, PatchReader.Apply(old, fixedMd5));

        Rejects(old, Make(Ctrl(-1, 10, 3), 13, 3, 10));             // seek before start
        Rejects(old, Make(Ctrl(95, 10, 3), 13, 3, 10));             // copy past end
        Rejects(old, Make(Ctrl(101, 0, 3), 3, 3, 0));               // cursor past end
        Rejects(old, Make(Ctrl(1L << 61, 10, 3), 13, 3, 10));       // huge seek
        Rejects(old, Make(Ctrl(-(1L << 61), 10, 3), 13, 3, 10));
        Rejects(old, Make(Ctrl(5, 10, 3), 12, 3, 10));              // too much output
        Rejects(old, Make(Ctrl(5, 10, 3), 14, 3, 10));              // too little output
        Rejects(old, Make(Ctrl(5, 10, 3, 0, 0, 0), 13, 3, 10));     // empty record
        Rejects(old, Make(Ctrl(5, 10, 3, 80, 10, 0), 23, 3, 20));   // second record out of range
        Rejects(old, Make(Ctrl(5, 10, 2), 13, 3, 10));              // extra mismatch
        Rejects(old, Make(Ctrl(5, 10, 3).Take(2).ToArray(), 13, 3, 10)); // truncated record
        Rejects(old, Make(Ctrl(5, (long)int.MaxValue - 1, 0), 13, 0, 10));
        // Many records each copying the whole old file: legit-looking, but too much output.
        var many = Enumerable.Range(0, 1000).SelectMany(i => new long[] { i == 0 ? 0 : -100, 100, 0 }).ToArray();
        Rejects(old, Make(Ctrl(many), 100, 0, 0));
    }

    [Fact]
    public void MapSectionViolations()
    {
        var (old, _, patch) = Sample(new EncodeOptions { DiffMode = DiffMode.Bytewise, ControlCodec = Codec.None, NonZeroCodec = Codec.None, ExtraCodec = Codec.None });
        var parts = PatchParts.Split(patch);
        Assert.True(parts.Map.Length > 0);
        long d = parts.Info.NewSize - parts.Info.ExtraLength;
        Rejects(old, parts.Build(h => PatchParts.With(h, f => f["primary"] = 0)));
        Rejects(old, parts.Build(h => PatchParts.With(h, f => f["primary"] = d + 1)));
        Rejects(old, parts.Build(h => PatchParts.With(h, f => f["primary"] = f["primary"] % d + 1)));
        var cut = PatchParts.Split(patch); cut.Map = cut.Map.Take(cut.Map.Length - 1).ToArray(); Rejects(old, cut.Build());
        var longer = PatchParts.Split(patch); longer.Map = longer.Map.Concat(new byte[] { 0 }).ToArray(); Rejects(old, longer.Build());
        var empty = PatchParts.Split(patch); empty.Map = Array.Empty<byte>(); Rejects(old, empty.Build());
        var junk = PatchParts.Split(patch); junk.Map = Enumerable.Repeat((byte)0xFF, junk.Map.Length).ToArray(); Rejects(old, junk.Build());
        // K > D, and non-zero string length disagreeing with the map.
        var kd = PatchParts.Split(patch); kd.NonZero = new byte[d + 1];
        Rejects(old, kd.Build(h => PatchParts.With(h, f => f["nz"] = d + 1)));
        var k1 = PatchParts.Split(patch); k1.NonZero = k1.NonZero.Take(k1.NonZero.Length - 1).ToArray();
        Rejects(old, k1.Build(h => PatchParts.With(h, f => f["nz"] = f["nz"] - 1)));
        // Empty map section with primary set (K = 0 but primary != 0).
        var (old2, _, identical) = (old, old, PatchWriter.Encode(old, old, new[] { new Segment(0, old.Length, 0) }, Raw));
        var ip = PatchParts.Split(identical);
        Rejects(old2, ip.Build(h => PatchParts.With(h, f => f["primary"] = 1)));
        ip.Map = new byte[] { 1, 2, 3, 4 };
        Rejects(old2, ip.Build());
    }
}
