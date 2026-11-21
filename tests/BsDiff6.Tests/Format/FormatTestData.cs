using BsDiff6.Format;

namespace BsDiff6.Tests.Format;

/// <summary>Generators of old/new/alignment triples for the format tests.</summary>
internal static class FormatTestData
{
    public static byte[] RandomBytes(Random rng, int n)
    {
        var b = new byte[n];
        rng.NextBytes(b);
        return b;
    }

    /// <summary>"Code-like" data: small alphabet, repeated motifs, embedded 32-bit addresses.</summary>
    public static byte[] Structured(Random rng, int n)
    {
        var b = new byte[n];
        int i = 0;
        while (i < n)
        {
            int kind = rng.Next(4);
            if (kind == 0 && i + 4 <= n)
            {
                int addr = 0x10000 + rng.Next(0x4000) * 4;
                BitConverter.TryWriteBytes(b.AsSpan(i, 4), addr);
                i += 4;
            }
            else if (kind == 1)
            {
                int run = Math.Min(n - i, rng.Next(1, 12));
                b.AsSpan(i, run).Fill(0);
                i += run;
            }
            else
            {
                b[i++] = (byte)(0x40 + rng.Next(16));
            }
        }
        return b;
    }

    /// <summary>
    /// Builds a random valid alignment of a new file of length <paramref name="m"/> against an old
    /// file of length <paramref name="n"/>, then a new file consistent with it: matched bytes copy
    /// old with sparse mutations (single bytes, and 16/32-bit additions in either byte order),
    /// unmatched bytes are random.
    /// </summary>
    public static (byte[] newFile, Segment[] alignment) Derive(Random rng, byte[] old, int m,
        double matchedFraction = 0.8, double mutationRate = 0.02, int maxSeg = 200)
    {
        int n = old.Length;
        var segs = new List<Segment>();
        int pos = 0;
        int lastOld = 0;
        while (pos < m)
        {
            int len = Math.Min(m - pos, rng.Next(1, maxSeg + 1));
            bool match = n > 0 && rng.NextDouble() < matchedFraction;
            if (match)
            {
                len = Math.Min(len, n);
                int op;
                int r = rng.Next(4);
                if (r == 0) op = lastOld;                              // reuse / continue
                else if (r == 1) op = Math.Max(0, lastOld - rng.Next(0, 500)); // backwards
                else op = rng.Next(0, n - len + 1);
                if (op + len > n) op = n - len;
                segs.Add(new Segment(pos, len, op));
                lastOld = op + len;
                if (lastOld >= n) lastOld = 0;
            }
            else
            {
                segs.Add(new Segment(pos, len, Segment.Unmatched));
            }
            pos += len;
        }
        byte[] @new = new byte[m];
        foreach (Segment s in segs)
        {
            if (!s.IsMatched)
            {
                rng.NextBytes(@new.AsSpan(s.NewPos, s.Length));
                continue;
            }
            old.AsSpan(s.OldPos, s.Length).CopyTo(@new.AsSpan(s.NewPos, s.Length));
            for (int i = 0; i < s.Length; i++)
            {
                if (rng.NextDouble() >= mutationRate) continue;
                int at = s.NewPos + i;
                int kind = rng.Next(3);
                if (kind == 0 || i + 4 > s.Length)
                {
                    @new[at] = (byte)rng.Next(256);
                }
                else
                {
                    uint v = kind == 1
                        ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(@new.AsSpan(at, 4))
                        : System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(@new.AsSpan(at, 4));
                    v += (uint)rng.Next(-300, 300);
                    if (kind == 1) System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(@new.AsSpan(at, 4), v);
                    else System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(@new.AsSpan(at, 4), v);
                }
            }
        }
        return (@new, segs.ToArray());
    }

    public static void AssertRoundTrip(byte[] old, byte[] @new, IReadOnlyList<Segment> alignment, EncodeOptions? options = null)
    {
        byte[] patch = PatchWriter.Encode(old, @new, alignment, options);
        byte[] back = PatchReader.Apply(old, patch);
        Assert.Equal(@new, back);
        PatchInfo info = PatchReader.ReadInfo(patch);
        Assert.Equal(patch.Length, info.TotalLength);
    }
}
