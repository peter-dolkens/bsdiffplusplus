namespace BsDiff6.Format;

/// <summary>Difference modes of DESIGN 7.4. The numeric value is the id stored in the header.</summary>
public enum DiffMode
{
    Correction = 0,
    Bytewise = 1,
    LittleEndian = 2,
    BigEndian = 3,
}

/// <summary>
/// The four difference modes (DESIGN 7.4), split into a difference map (one 0/1 byte per
/// difference-string position) and a non-zero string, and the inverse.
/// </summary>
internal static class DifferenceCoder
{
    /// <summary>
    /// Computes map and non-zero string for <paramref name="mode"/> over the matched segments
    /// (each one a control record; carries restart per segment). <paramref name="map"/> must have
    /// length <c>D = Σ Length</c>. Returns the non-zero string.
    /// </summary>
    public static byte[] Split(DiffMode mode, ReadOnlySpan<byte> old, ReadOnlySpan<byte> @new,
        IReadOnlyList<Segment> matched, Span<byte> map)
    {
        var nz = new List<byte>();
        int p = 0;
        foreach (Segment s in matched)
        {
            ReadOnlySpan<byte> o = old.Slice(s.OldPos, s.Length);
            ReadOnlySpan<byte> n = @new.Slice(s.NewPos, s.Length);
            Span<byte> m = map.Slice(p, s.Length);
            switch (mode)
            {
                case DiffMode.Correction:
                    for (int i = 0; i < o.Length; i++)
                    {
                        if (n[i] != o[i]) { m[i] = 1; nz.Add(n[i]); } else m[i] = 0;
                    }
                    break;
                case DiffMode.Bytewise:
                    for (int i = 0; i < o.Length; i++)
                    {
                        byte d = (byte)(n[i] - o[i]);
                        if (d != 0) { m[i] = 1; nz.Add(d); } else m[i] = 0;
                    }
                    break;
                case DiffMode.LittleEndian:
                case DiffMode.BigEndian:
                {
                    // Compute digits into m temporarily (as raw bytes), then split in order.
                    int c = 0;
                    if (mode == DiffMode.LittleEndian)
                        for (int i = 0; i < o.Length; i++) m[i] = Digit(n[i], o[i], ref c);
                    else
                        for (int i = o.Length - 1; i >= 0; i--) m[i] = Digit(n[i], o[i], ref c);
                    for (int i = 0; i < m.Length; i++)
                    {
                        if (m[i] != 0) { nz.Add(m[i]); m[i] = 1; }
                    }
                    break;
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(mode));
            }
            p += s.Length;
        }
        return nz.ToArray();
    }

    /// <summary>Balanced multi-precision digit: <c>v = new - old + c</c>, <c>d = ((v+128) mod 256) - 128</c>, <c>c = (v-d)/256</c>.</summary>
    internal static byte Digit(byte newByte, byte oldByte, ref int carry)
    {
        int v = newByte - oldByte + carry;
        int d = ((v + 128) & 0xFF) - 128;
        carry = (v - d) >> 8;
        return (byte)d;
    }

    /// <summary>Inverse of <see cref="Digit"/>: <c>new = (old + d - c) mod 256</c>, then recompute <c>c</c>.</summary>
    internal static byte UnDigit(byte oldByte, byte digit, ref int carry)
    {
        int d = (sbyte)digit;
        byte n = (byte)(oldByte + d - carry);
        int v = n - oldByte + carry;
        carry = (v - d) >> 8;
        return n;
    }

    /// <summary>
    /// Reconstructs one control record's copied bytes. <paramref name="map"/> is this record's slice
    /// of the difference map (0/1 bytes), <paramref name="nz"/> the non-zero string and
    /// <paramref name="nzPos"/> the read position in it (advanced). Caller guarantees that the
    /// non-zero string holds enough bytes (the total count of 1s in the map equals its length).
    /// </summary>
    public static void Apply(DiffMode mode, ReadOnlySpan<byte> old, ReadOnlySpan<byte> map,
        ReadOnlySpan<byte> nz, ref int nzPos, Span<byte> output)
    {
        int len = output.Length;
        switch (mode)
        {
            case DiffMode.Correction:
                for (int i = 0; i < len; i++)
                    output[i] = map[i] != 0 ? nz[nzPos++] : old[i];
                break;
            case DiffMode.Bytewise:
                for (int i = 0; i < len; i++)
                    output[i] = map[i] != 0 ? (byte)(old[i] + nz[nzPos++]) : old[i];
                break;
            case DiffMode.LittleEndian:
            {
                int c = 0;
                for (int i = 0; i < len; i++)
                {
                    byte d = map[i] != 0 ? nz[nzPos++] : (byte)0;
                    output[i] = UnDigit(old[i], d, ref c);
                }
                break;
            }
            case DiffMode.BigEndian:
            {
                int ones = 0;
                for (int i = 0; i < len; i++) ones += map[i];
                int k = nzPos + ones;
                int c = 0;
                for (int i = len - 1; i >= 0; i--)
                {
                    byte d = map[i] != 0 ? nz[--k] : (byte)0;
                    output[i] = UnDigit(old[i], d, ref c);
                }
                nzPos += ones;
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }
}
