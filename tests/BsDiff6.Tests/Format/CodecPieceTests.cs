using BsDiff6.Format;

namespace BsDiff6.Tests.Format;

public class VarIntTests
{
    [Theory]
    [InlineData(0UL, new byte[] { 0x80 })]
    [InlineData(1UL, new byte[] { 0x81 })]
    [InlineData(127UL, new byte[] { 0xFF })]
    [InlineData(128UL, new byte[] { 0x00, 0x81 })]
    [InlineData(300UL, new byte[] { 0x2C, 0x82 })]
    [InlineData(16383UL, new byte[] { 0x7F, 0xFF })]
    [InlineData(16384UL, new byte[] { 0x00, 0x00, 0x81 })]
    [InlineData((ulong)long.MaxValue, new byte[] { 0x7F, 0x7F, 0x7F, 0x7F, 0x7F, 0x7F, 0x7F, 0x7F, 0xFF })]
    public void EncodesKnownValues(ulong value, byte[] expected)
    {
        var o = new List<byte>();
        VarInt.Write(o, value);
        Assert.Equal(expected, o.ToArray());
        Assert.Equal(expected.Length, VarInt.Length(value));
        int pos = 0;
        Assert.True(VarInt.TryRead(expected, ref pos, out ulong back));
        Assert.Equal(value, back);
        Assert.Equal(expected.Length, pos);
    }

    [Fact]
    public void RoundTripsBoundaries()
    {
        var values = new List<ulong>();
        for (int bits = 0; bits <= 63; bits++)
        {
            ulong p = bits == 63 ? 0 : 1UL << bits;
            if (bits < 63) { values.Add(p); values.Add(p - 1); values.Add(p + 1); }
        }
        values.Add((ulong)long.MaxValue);
        foreach (ulong v in values)
        {
            if (v > long.MaxValue) continue;
            var o = new List<byte>();
            VarInt.Write(o, v);
            int pos = 0;
            Assert.True(VarInt.TryRead(o.ToArray(), ref pos, out ulong back));
            Assert.Equal(v, back);
        }
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(1L)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    [InlineData((1L << 62) - 1)]
    [InlineData(-(1L << 62))]
    public void SignedRoundTrip(long v)
    {
        var o = new List<byte>();
        VarInt.WriteSigned(o, v);
        int pos = 0;
        Assert.True(VarInt.TryReadSigned(o.ToArray(), ref pos, out long back));
        Assert.Equal(v, back);
    }

    [Fact]
    public void ZigZagMapping()
    {
        Assert.Equal(0UL, VarInt.ZigZag(0));
        Assert.Equal(1UL, VarInt.ZigZag(-1));
        Assert.Equal(2UL, VarInt.ZigZag(1));
        Assert.Equal(3UL, VarInt.ZigZag(-2));
    }

    [Theory]
    [InlineData(new byte[] { })]                       // empty
    [InlineData(new byte[] { 0x00 })]                  // truncated
    [InlineData(new byte[] { 0x7F, 0x7F })]            // truncated
    [InlineData(new byte[] { 0x00, 0x80 })]            // non-minimal zero
    [InlineData(new byte[] { 0x05, 0x80 })]            // non-minimal 5
    [InlineData(new byte[] { 0x7F, 0x7F, 0x7F, 0x7F, 0x7F, 0x7F, 0x7F, 0x7F, 0x7F, 0x81 })] // 2^63: overflow
    [InlineData(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x81 })] // 11 bytes
    public void RejectsMalformed(byte[] bytes)
    {
        int pos = 0;
        Assert.False(VarInt.TryRead(bytes, ref pos, out _));
    }

    [Fact]
    public void WriteRejectsOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => VarInt.Write(new List<byte>(), 1UL << 63));
        Assert.Throws<ArgumentOutOfRangeException>(() => VarInt.WriteSigned(new List<byte>(), 1L << 62));
    }
}

public class RangeCoderTests
{
    [Theory]
    [InlineData(2u)]
    [InlineData(3u)]
    [InlineData(255u)]
    [InlineData(256u)]
    [InlineData(65535u)]
    [InlineData(65536u)]
    public void UniformRoundTrip(uint total)
    {
        var rng = new Random((int)total);
        var values = new uint[5000];
        var enc = new RangeEncoder();
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = i % 7 == 0 ? total - 1 : i % 11 == 0 ? 0 : (uint)rng.NextInt64(total);
            enc.EncodeUniform(values[i], total);
        }
        byte[] bytes = enc.Finish();
        var dec = new RangeDecoder(bytes);
        foreach (uint v in values) Assert.Equal(v, dec.DecodeUniform(total));
        Assert.True(dec.AtEnd);
    }

    [Fact]
    public void MixedSymbolsAndBitsRoundTrip()
    {
        var rng = new Random(5);
        var ops = new List<(int kind, uint total, uint value)>();
        var enc = new RangeEncoder();
        for (int i = 0; i < 20000; i++)
        {
            if (rng.Next(2) == 0)
            {
                uint total = (uint)rng.Next(1, 65537);
                uint v = (uint)rng.NextInt64(total);
                ops.Add((0, total, v));
                enc.EncodeUniform(v, total);
            }
            else
            {
                uint p = (uint)rng.Next(1, 4096);
                int bit = rng.Next(4096) < p ? 0 : 1;
                ops.Add((1, p, (uint)bit));
                enc.EncodeBit(bit, p);
            }
        }
        byte[] bytes = enc.Finish();
        var dec = new RangeDecoder(bytes);
        foreach (var op in ops)
        {
            if (op.kind == 0) Assert.Equal(op.value, dec.DecodeUniform(op.total));
            else Assert.Equal((int)op.value, dec.DecodeBit(op.total));
        }
        Assert.True(dec.AtEnd);
    }

    [Theory]
    [InlineData(65537u)]
    [InlineData(100000u)]
    [InlineData(1u << 20)]
    [InlineData((1u << 31) - 7)]
    [InlineData((uint)int.MaxValue)]
    [InlineData(uint.MaxValue)]
    public void LargeTotalsViaSplitRoundTrip(uint r)
    {
        // Model 0 codes R > 2^16 as (x >> 16) uniform, then the low 16 bits uniform.
        var rng = new Random(9);
        var values = new List<uint> { 0, r - 1, r / 2, (r - 1) & ~0xFFFFu, ((r - 1) & ~0xFFFFu) - 1 };
        for (int i = 0; i < 2000; i++) values.Add((uint)rng.NextInt64(r));
        var enc = new RangeEncoder();
        foreach (uint v in values) MidpointCoder.EncodeUniform(enc, v, r);
        byte[] bytes = enc.Finish();
        var dec = new RangeDecoder(bytes);
        foreach (uint v in values) Assert.Equal(v, MidpointCoder.DecodeUniform(ref dec, r));
        Assert.True(dec.AtEnd);
    }

    [Fact]
    public void SplitDecodeRejectsValueBeyondR()
    {
        // R = 65537: high part 1 allows low parts up to 65535, but only x = 65536 (low 0) is valid.
        var enc = new RangeEncoder();
        enc.EncodeUniform(1, 2);
        enc.EncodeUniform(5, 1u << 16);
        byte[] bytes = enc.Finish();
        Assert.Throws<PatchFormatException>(() =>
        {
            var dec = new RangeDecoder(bytes);
            MidpointCoder.DecodeUniform(ref dec, 65537);
        });
    }

    [Fact]
    public void EmptyStreamWhenNothingCoded()
    {
        Assert.Empty(new RangeEncoder().Finish());
    }

    [Fact]
    public void DecoderStopsAtEndOfInput()
    {
        var enc = new RangeEncoder();
        for (int i = 0; i < 1000; i++) enc.EncodeUniform((uint)(i * 37 % 1000), 1000);
        byte[] bytes = enc.Finish();
        byte[] cut = bytes.AsSpan(0, bytes.Length / 2).ToArray();
        Assert.Throws<PatchFormatException>(() =>
        {
            var dec = new RangeDecoder(cut);
            for (int i = 0; i < 1000; i++) dec.DecodeUniform(1000);
        });
    }
}

public class DifferenceCoderTests
{
    // Big-endian hex pairs (most significant first) as in the thesis.
    [Theory]
    [InlineData(0x1200, 0x11FC, 0x00, 0x04)]
    [InlineData(0x1210, 0x120C, 0x00, 0x04)]
    [InlineData(0x1280, 0x11C8, 0x01, -0x48)]
    [InlineData(0x12B8, 0x1200, 0x01, -0x48)]
    public void ThesisExamplesBothByteOrders(int newV, int oldV, int hiDigit, int loDigit)
    {
        byte[] oldBe = { (byte)(oldV >> 8), (byte)oldV };
        byte[] newBe = { (byte)(newV >> 8), (byte)newV };
        byte[] expectedBe = { (byte)hiDigit, (byte)(sbyte)loDigit };
        CheckMode(DiffMode.BigEndian, oldBe, newBe, expectedBe);

        byte[] oldLe = { oldBe[1], oldBe[0] };
        byte[] newLe = { newBe[1], newBe[0] };
        byte[] expectedLe = { expectedBe[1], expectedBe[0] };
        CheckMode(DiffMode.LittleEndian, oldLe, newLe, expectedLe);
    }

    private static void CheckMode(DiffMode mode, byte[] old, byte[] @new, byte[] expectedDigits)
    {
        var seg = new[] { new Segment(0, old.Length, 0) };
        byte[] map = new byte[old.Length];
        byte[] nz = DifferenceCoder.Split(mode, old, @new, seg, map);
        byte[] expectedNz = expectedDigits.Where(b => b != 0).ToArray();
        Assert.Equal(expectedNz, nz);
        Assert.Equal(expectedDigits.Select(b => b != 0 ? (byte)1 : (byte)0).ToArray(), map);
        byte[] back = new byte[old.Length];
        int nzPos = 0;
        DifferenceCoder.Apply(mode, old, map, nz, ref nzPos, back);
        Assert.Equal(@new, back);
        Assert.Equal(nz.Length, nzPos);
    }

    [Fact]
    public void BytewiseDiffersFromMultiPrecisionOnThesisExample()
    {
        // 1200 - 11FC bytewise = 01 04
        byte[] old = { 0x11, 0xFC }, @new = { 0x12, 0x00 };
        byte[] map = new byte[2];
        byte[] nz = DifferenceCoder.Split(DiffMode.Bytewise, old, @new, new[] { new Segment(0, 2, 0) }, map);
        Assert.Equal(new byte[] { 0x01, 0x04 }, nz);
    }

    public static IEnumerable<object[]> Modes() =>
        Enum.GetValues<DiffMode>().Select(m => new object[] { m });

    [Theory]
    [MemberData(nameof(Modes))]
    public void RandomRoundTripsIncludingLongCarries(DiffMode mode)
    {
        var rng = new Random(77 + (int)mode);
        for (int iter = 0; iter < 300; iter++)
        {
            int len = rng.Next(1, 200);
            byte[] old = new byte[len], @new = new byte[len];
            int style = iter % 4;
            for (int i = 0; i < len; i++)
            {
                switch (style)
                {
                    case 0: old[i] = (byte)rng.Next(256); @new[i] = (byte)rng.Next(256); break;
                    case 1: old[i] = 0xFF; @new[i] = 0x00; break;            // borrow/carry chains
                    case 2: old[i] = 0x00; @new[i] = 0xFF; break;
                    default:
                        old[i] = (byte)(rng.Next(2) == 0 ? 0xFF : 0x00);
                        @new[i] = rng.Next(5) == 0 ? (byte)rng.Next(256) : old[i];
                        break;
                }
            }
            if (style == 1 || style == 2) { @new[rng.Next(len)] ^= (byte)rng.Next(1, 256); }
            // Several records over the same buffers, carries restart per record.
            var segs = new List<Segment>();
            int p = 0;
            while (p < len)
            {
                int l = Math.Min(len - p, rng.Next(1, 40));
                segs.Add(new Segment(p, l, p));
                p += l;
            }
            byte[] map = new byte[len];
            byte[] nz = DifferenceCoder.Split(mode, old, @new, segs, map);
            Assert.Equal(map.Count(b => b != 0), nz.Length);
            byte[] back = new byte[len];
            int nzPos = 0, mp = 0;
            foreach (Segment s in segs)
            {
                DifferenceCoder.Apply(mode, old.AsSpan(s.OldPos, s.Length), map.AsSpan(mp, s.Length), nz, ref nzPos,
                    back.AsSpan(s.NewPos, s.Length));
                mp += s.Length;
            }
            Assert.Equal(@new, back);
            Assert.Equal(nz.Length, nzPos);
        }
    }

    [Fact]
    public void MultiPrecisionTurnsAddressShiftIntoSingleDigit()
    {
        // 0x0001FFFC + 8 = 0x00020004 little-endian: one non-zero digit in LE mode.
        byte[] old = { 0xFC, 0xFF, 0x01, 0x00 }, @new = { 0x04, 0x00, 0x02, 0x00 };
        byte[] map = new byte[4];
        byte[] nz = DifferenceCoder.Split(DiffMode.LittleEndian, old, @new, new[] { new Segment(0, 4, 0) }, map);
        Assert.Equal(new byte[] { 8 }, nz);
    }
}

public class BwtTests
{
    private static void RoundTrip(byte[] map)
    {
        byte[] l = Bwt.Forward(map, out int primary);
        Assert.InRange(primary, 1, map.Length);
        Assert.Equal(map.Count(b => b != 0), l.Count(b => b != 0));
        ulong[] bits = new ulong[(map.Length + 63) / 64];
        int ones = 0;
        for (int i = 0; i < l.Length; i++) if (l[i] != 0) { bits[i >> 6] |= 1UL << (i & 63); ones++; }
        byte[] back = new byte[map.Length];
        Bwt.Inverse(bits, map.Length, ones, primary, back);
        Assert.Equal(map, back);
    }

    [Fact]
    public void KnownSmallExample()
    {
        // map = 0 1 1 0; rotations of "0110$" sorted with $ smallest.
        byte[] map = { 0, 1, 1, 0 };
        byte[] l = Bwt.Forward(map, out int primary);
        // SA of 0110: suffixes "0"(3), "0110"(0), "10"(2), "110"(1) -> [3,0,2,1]
        // L = map[3]=0, then sa=3 -> map[2]=1, sa=0 -> primary=2, sa=2 -> map[1]=1, sa=1 -> map[0]=0
        Assert.Equal(new byte[] { 0, 1, 1, 0 }, l);
        Assert.Equal(2, primary);
        RoundTrip(map);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(1000)]
    public void SpecialMaps(int n)
    {
        RoundTrip(new byte[n]);                                  // all zero
        RoundTrip(Enumerable.Repeat((byte)1, n).ToArray());      // all one
        var single = new byte[n]; single[n / 2] = 1; RoundTrip(single);
        var first = new byte[n]; first[0] = 1; RoundTrip(first);
        var last = new byte[n]; last[n - 1] = 1; RoundTrip(last);
        RoundTrip(Enumerable.Range(0, n).Select(i => (byte)(i % 2)).ToArray());
        RoundTrip(Enumerable.Range(0, n).Select(i => (byte)(i % 7 == 3 ? 1 : 0)).ToArray());
        RoundTrip(Enumerable.Range(0, n).Select(i => (byte)((i / 5) % 2)).ToArray());
    }

    [Fact]
    public void RandomMaps()
    {
        var rng = new Random(3);
        for (int iter = 0; iter < 200; iter++)
        {
            int n = rng.Next(1, 3000);
            double density = rng.NextDouble();
            RoundTrip(Enumerable.Range(0, n).Select(_ => (byte)(rng.NextDouble() < density ? 1 : 0)).ToArray());
        }
    }

    [Fact]
    public void InverseRejectsNonBwtInput()
    {
        // L = 0 0 with primary 1 (string "00" has L = 0 0, primary 2): primary 1 is invalid.
        int rejected = 0, accepted = 0;
        for (int d = 1; d <= 6; d++)
            for (int lv = 0; lv < (1 << d); lv++)
                for (int primary = 1; primary <= d; primary++)
                {
                    ulong[] bits = { (ulong)lv };
                    int ones = System.Numerics.BitOperations.PopCount((uint)lv);
                    byte[] map = new byte[d];
                    try
                    {
                        Bwt.Inverse(bits, d, ones, primary, map);
                        // Must be consistent: re-transforming gives the same L and primary.
                        byte[] l = Bwt.Forward(map, out int p2);
                        Assert.Equal(primary, p2);
                        for (int i = 0; i < d; i++) Assert.Equal((lv >> i) & 1, l[i]);
                        accepted++;
                    }
                    catch (PatchFormatException) { rejected++; }
                }
        Assert.True(rejected > 0);
        Assert.Equal(126, accepted); // exactly one (L, primary) per binary string of length 1..6
    }
}

public class MidpointCoderTests
{
    private static void RoundTrip(int[] q, int d)
    {
        byte[] coded = MidpointCoder.Encode(q, d, MapModel.Uniform);
        if (MidpointCoder.IsTrivial(d, q.Length)) Assert.Empty(coded);
        ulong[] bits = MidpointCoder.Decode(coded, d, q.Length, 0);
        var back = new List<int>();
        for (int i = 0; i < d; i++) if (((bits[i >> 6] >> (i & 63)) & 1) != 0) back.Add(i);
        Assert.Equal(q, back.ToArray());
    }

    [Fact]
    public void EdgeCounts()
    {
        RoundTrip(Array.Empty<int>(), 0);
        RoundTrip(Array.Empty<int>(), 100);
        RoundTrip(Enumerable.Range(0, 100).ToArray(), 100);
        RoundTrip(new[] { 0 }, 1);
        RoundTrip(new[] { 0 }, 100);
        RoundTrip(new[] { 99 }, 100);
        RoundTrip(new[] { 42 }, 100);
        RoundTrip(Enumerable.Range(0, 99).ToArray(), 100);
        RoundTrip(Enumerable.Range(1, 99).ToArray(), 100);
    }

    [Fact]
    public void RandomAndClustered()
    {
        var rng = new Random(11);
        for (int iter = 0; iter < 300; iter++)
        {
            int d = rng.Next(1, 5000);
            var set = new SortedSet<int>();
            if (iter % 2 == 0)
            {
                int k = rng.Next(0, d + 1);
                while (set.Count < k) set.Add(rng.Next(d));
            }
            else
            {
                int clusters = rng.Next(1, 10);
                for (int c = 0; c < clusters; c++)
                {
                    int start = rng.Next(d), len = rng.Next(1, 200);
                    for (int i = start; i < Math.Min(d, start + len); i++) set.Add(i);
                }
            }
            RoundTrip(set.ToArray(), d);
        }
    }

    [Fact]
    public void DecodeRejectsTrailingBytesAndWrongModel()
    {
        byte[] coded = MidpointCoder.Encode(new[] { 3, 9, 20 }, 50, MapModel.Uniform);
        byte[] longer = coded.Concat(new byte[] { 0 }).ToArray();
        Assert.Throws<PatchFormatException>(() => MidpointCoder.Decode(longer, 50, 3, 0));
        Assert.Throws<PatchFormatException>(() => MidpointCoder.Decode(coded, 50, 3, 200));
        Assert.Throws<PatchFormatException>(() => MidpointCoder.Decode(new byte[] { 1 }, 50, 0, 0));
    }
}

public class StringCodecTests
{
    [Fact]
    public void AllCodecsRoundTrip()
    {
        var rng = new Random(1);
        foreach (byte[] data in new[] { Array.Empty<byte>(), new byte[] { 7 }, FormatTestData.RandomBytes(rng, 5000), new byte[100000] })
            foreach (Codec c in Enum.GetValues<Codec>())
            {
                byte[] z = StringCodec.Compress(data, c);
                Assert.Equal(data, StringCodec.Decompress(z, (int)c, data.Length, "t"));
                if (data.Length > 0)
                {
                    Assert.Throws<PatchFormatException>(() => StringCodec.Decompress(z, (int)c, data.Length - 1, "t"));
                    Assert.Throws<PatchFormatException>(() => StringCodec.Decompress(z, (int)c, data.Length + 1, "t"));
                }
            }
    }

    [Fact]
    public void HugeDeclaredLengthFailsFastWithoutAllocating()
    {
        byte[] z = StringCodec.Compress(new byte[20], Codec.Zlib);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<PatchFormatException>(() => StringCodec.Decompress(z, 1, int.MaxValue - 64, "t"));
        byte[] bz = StringCodec.Compress(new byte[20], Codec.Bzip2);
        Assert.Throws<PatchFormatException>(() => StringCodec.Decompress(bz, 2, int.MaxValue - 64, "t"));
        Assert.Throws<PatchFormatException>(() => StringCodec.Decompress(new byte[20], 0, int.MaxValue - 64, "t"));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 64L << 20, $"allocated {allocated} bytes");
    }

    [Fact]
    public void BestPicksSmallest()
    {
        long[] sizes = new long[3];
        byte[] z = StringCodec.CompressBest(new byte[10000], null, out Codec c, sizes);
        Assert.Equal(sizes.Min(), z.Length);
        Assert.NotEqual(Codec.None, c);
        StringCodec.CompressBest(new byte[0], null, out c, sizes);
        Assert.Equal(Codec.None, c);
    }
}
