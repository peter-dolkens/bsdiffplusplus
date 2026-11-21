namespace BsDiff6.Format;

/// <summary>
/// DESIGN 7.1 integers: unsigned little-endian base 128, low group first, the last (most
/// significant) byte has its top bit set. 0 = <c>80</c>, 127 = <c>FF</c>, 128 = <c>00 81</c>.
/// Values are limited to 63 bits. The reader also rejects non-minimal encodings (a final byte
/// whose 7 payload bits are zero, unless it is the only byte), so every value has exactly one
/// encoding and the longest valid varint is 9 bytes.
/// </summary>
internal static class VarInt
{
    public const int MaxBytes = 9;

    public static void Write(List<byte> output, ulong value)
    {
        if (value > long.MaxValue) throw new ArgumentOutOfRangeException(nameof(value));
        while (value >= 0x80)
        {
            output.Add((byte)(value & 0x7F));
            value >>= 7;
        }
        output.Add((byte)(value | 0x80));
    }

    public static void WriteSigned(List<byte> output, long value)
    {
        if (value < -(1L << 62) || value >= (1L << 62)) throw new ArgumentOutOfRangeException(nameof(value));
        Write(output, ZigZag(value));
    }

    public static ulong ZigZag(long v) => (ulong)((v << 1) ^ (v >> 63));

    public static long UnZigZag(ulong u) => (long)(u >> 1) ^ -(long)(u & 1);

    public static int Length(ulong value)
    {
        int n = 1;
        while (value >= 0x80) { value >>= 7; n++; }
        return n;
    }

    /// <summary>
    /// Reads a varint at <paramref name="pos"/>, advancing it. Returns false (pos unspecified) if the
    /// input ends first, the encoding is non-minimal, or the value does not fit in 63 bits.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> src, ref int pos, out ulong value)
    {
        value = 0;
        for (int i = 0; i < MaxBytes; i++)
        {
            if (pos >= src.Length) return false;
            byte b = src[pos++];
            ulong payload = (ulong)(b & 0x7F);
            value |= payload << (7 * i);
            if ((b & 0x80) != 0)
                return i == 0 || payload != 0;   // minimal: last group non-zero unless single byte
        }
        return false;                            // a 10th byte could only carry bits 63+
    }

    public static bool TryReadSigned(ReadOnlySpan<byte> src, ref int pos, out long value)
    {
        bool ok = TryRead(src, ref pos, out ulong u);
        value = UnZigZag(u);
        return ok;
    }
}
