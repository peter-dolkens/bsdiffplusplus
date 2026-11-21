using BsDiff6.Format;

namespace BsDiff6.Tests.Format;

/// <summary>
/// DESIGN 7.8: a compressed section must be consumed exactly by its codec. Bytes after the end of
/// a zlib or bzip2 stream inside a section are malformed.
/// </summary>
public class TrailingDataTests
{
    private static (byte[] old, byte[] @new, byte[] patch) Sample(Codec codec, int seed)
    {
        var rng = new Random(seed);
        byte[] old = FormatTestData.Structured(rng, 3000);
        var (@new, align) = FormatTestData.Derive(rng, old, 3300, 0.8, 0.08, 300);
        var opts = new EncodeOptions { ControlCodec = codec, NonZeroCodec = codec, ExtraCodec = codec };
        return (old, @new, PatchWriter.Encode(old, @new, align, opts));
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (Codec c in new[] { Codec.Zlib, Codec.Bzip2 })
            foreach (string section in new[] { "control", "nonzero", "extra" })
                foreach (int junk in new[] { 1, 7, -1 }) // -1: a whole second copy of the stream
                    yield return new object[] { c, section, junk };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TrailingBytesAfterStreamAreRejected(Codec codec, string section, int junk)
    {
        var (old, @new, patch) = Sample(codec, 5);
        Assert.Equal(@new, PatchReader.Apply(old, patch));
        var parts = PatchParts.Split(patch);
        byte[] Append(byte[] s) => junk < 0 ? s.Concat(s).ToArray() : s.Concat(Enumerable.Repeat((byte)0, junk)).ToArray();
        switch (section)
        {
            case "control": parts.Control = Append(parts.Control); break;
            case "nonzero": parts.NonZero = Append(parts.NonZero); break;
            default: parts.Extra = Append(parts.Extra); break;
        }
        byte[] bad = parts.Build();
        var e = Assert.Throws<PatchFormatException>(() => PatchReader.Apply(old, bad));
        Assert.Contains("trailing data", e.Message);
    }

    [Theory]
    [InlineData(Codec.Zlib)]
    [InlineData(Codec.Bzip2)]
    public void DecompressRequiresExactConsumption(Codec codec)
    {
        var rng = new Random(11);
        foreach (int len in new[] { 1, 2, 100, 5000, 70000 })
        {
            byte[] data = FormatTestData.Structured(rng, len);
            byte[] c = StringCodec.Compress(data, codec);
            Assert.Equal(data, StringCodec.Decompress(c, (int)codec, len, "t"));
            foreach (byte junk in new byte[] { 0, 0xFF, c[0] })
                Assert.Throws<PatchFormatException>(() => StringCodec.Decompress(c.Append(junk).ToArray(), (int)codec, len, "t"));
            // Truncation is still rejected.
            Assert.Throws<PatchFormatException>(() => StringCodec.Decompress(c.AsSpan(0, c.Length - 1), (int)codec, len, "t"));
            // A corrupted checksum is still rejected.
            byte[] corrupt = (byte[])c.Clone();
            corrupt[^1] ^= 0x55;
            Assert.Throws<PatchFormatException>(() => StringCodec.Decompress(corrupt, (int)codec, len, "t"));
        }
    }

    [Fact]
    public void EmptyStrings()
    {
        // .NET's ZLibStream writes nothing for empty input: an empty zlib section is the empty string.
        byte[] z = StringCodec.Compress(Array.Empty<byte>(), Codec.Zlib);
        Assert.Empty(StringCodec.Decompress(z, (int)Codec.Zlib, 0, "t"));
        Assert.Empty(StringCodec.Decompress(Array.Empty<byte>(), (int)Codec.Zlib, 0, "t"));
        Assert.Throws<PatchFormatException>(() => StringCodec.Decompress(Array.Empty<byte>(), (int)Codec.Zlib, 1, "t"));
        byte[] b = StringCodec.Compress(Array.Empty<byte>(), Codec.Bzip2);
        Assert.Empty(StringCodec.Decompress(b, (int)Codec.Bzip2, 0, "t"));
        Assert.Throws<PatchFormatException>(() => StringCodec.Decompress(b.Append((byte)0).ToArray(), (int)Codec.Bzip2, 0, "t"));
        Assert.Throws<PatchFormatException>(() => StringCodec.Decompress(new byte[] { 0x78, 0xDA, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00 }, (int)Codec.Zlib, 0, "t"));
    }

    /// <summary>Fuzz-style: random junk appended to random compressed sections, lengths fixed up, is always rejected.</summary>
    [Fact]
    public void RandomTrailingJunkIsAlwaysRejected()
    {
        var rng = new Random(77);
        for (int it = 0; it < 300; it++)
        {
            Codec codec = rng.Next(2) == 0 ? Codec.Zlib : Codec.Bzip2;
            var (old, _, patch) = Sample(codec, 100 + it % 10);
            var parts = PatchParts.Split(patch);
            byte[] junk = new byte[rng.Next(1, 40)];
            rng.NextBytes(junk);
            switch (rng.Next(3))
            {
                case 0: parts.Control = parts.Control.Concat(junk).ToArray(); break;
                case 1: parts.NonZero = parts.NonZero.Concat(junk).ToArray(); break;
                default: parts.Extra = parts.Extra.Concat(junk).ToArray(); break;
            }
            Assert.Throws<PatchFormatException>(() => PatchReader.Apply(old, parts.Build()));
        }
    }
}
