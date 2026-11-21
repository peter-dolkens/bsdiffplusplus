namespace BsDiff6.Format;

/// <summary>
/// Carry-propagating range coder (the LZMA "rc" design: 32-bit range, 33-bit low, a cached byte
/// plus a count of pending 0xFF bytes to absorb carries).
/// <para>Byte format, so that another implementation can reproduce it exactly:</para>
/// <list type="bullet">
/// <item>Encoder state: <c>low = 0</c> (64-bit, uses 33 bits), <c>range = 0xFFFFFFFF</c>,
/// <c>cache = 0</c>, <c>cacheSize = 1</c>. Normalisation: while <c>range &lt; 2^24</c>,
/// <c>range &lt;&lt;= 8</c> and ShiftLow. ShiftLow: if <c>(uint)low &lt; 0xFF000000</c> or
/// <c>low &gt;= 2^32</c>: emit <c>cache + carry</c> then <c>cacheSize - 1</c> bytes of
/// <c>0xFF + carry</c> (carry = <c>low &gt;&gt; 32</c>), set <c>cache = (low &gt;&gt; 24) &amp; 0xFF</c>,
/// <c>cacheSize = 0</c>; then (always) <c>cacheSize++</c>, <c>low = (low &amp; 0xFFFFFF) &lt;&lt; 8</c>.
/// Finish: ShiftLow five times.</item>
/// <item>The first byte produced this way is always 0 and is <b>not stored</b>. If no symbol at
/// all was coded the output is empty (zero bytes); otherwise it is <c>4 + (number of
/// normalisation shifts)</c> bytes, and the decoder consumes exactly that many.</item>
/// <item>Decoder: <c>range = 0xFFFFFFFF</c>, <c>code</c> = first 4 bytes big-endian; normalise
/// identically, shifting in one input byte per step.</item>
/// <item>Uniform/frequency symbol (<c>total &lt;= 2^16</c>): <c>r = range / total</c>,
/// <c>low += r * start</c>, <c>range = r * size</c>. Decoder: <c>v = code / r</c>; <c>v &gt;= total</c>
/// is malformed.</item>
/// <item>Adaptive bit with 12-bit probability <c>p</c> of a 0 (<c>0 &lt; p &lt; 4096</c>):
/// <c>bound = (range &gt;&gt; 12) * p</c>; 0: <c>range = bound</c>; 1: <c>low += bound</c>,
/// <c>range -= bound</c>. Model update is done by the caller.</item>
/// </list>
/// </summary>
internal sealed class RangeEncoder
{
    public const uint TopValue = 1u << 24;
    public const int ProbBits = 12;
    public const uint MaxTotal = 1u << 16;

    private readonly List<byte> _out = new List<byte>();
    private ulong _low;
    private uint _range = 0xFFFFFFFFu;
    private byte _cache;
    private long _cacheSize = 1;
    private bool _firstByte = true;
    private bool _any;
    private bool _finished;

    /// <summary>Codes <c>[start, start + size)</c> out of <c>total</c>, <c>total &lt;= 2^16</c>.</summary>
    public void Encode(uint start, uint size, uint total)
    {
        if (total == 0 || total > MaxTotal || size == 0 || start + size > total)
            throw new ArgumentOutOfRangeException(nameof(total));
        _any = true;
        uint r = _range / total;
        _low += (ulong)r * start;
        _range = r * size;
        Normalize();
    }

    public void EncodeUniform(uint value, uint total) => Encode(value, 1, total);

    /// <summary>Codes one bit with probability <paramref name="p0"/>/4096 of it being 0.</summary>
    public void EncodeBit(int bit, uint p0)
    {
        _any = true;
        uint bound = (_range >> ProbBits) * p0;
        if (bit == 0)
        {
            _range = bound;
        }
        else
        {
            _low += bound;
            _range -= bound;
        }
        Normalize();
    }

    private void Normalize()
    {
        while (_range < TopValue)
        {
            _range <<= 8;
            ShiftLow();
        }
    }

    private void ShiftLow()
    {
        if ((uint)_low < 0xFF000000u || (_low >> 32) != 0)
        {
            byte carry = (byte)(_low >> 32);
            byte temp = _cache;
            do
            {
                Emit((byte)(temp + carry));
                temp = 0xFF;
            } while (--_cacheSize != 0);
            _cache = (byte)(_low >> 24);
        }
        _cacheSize++;
        _low = (_low & 0x00FFFFFFul) << 8;
    }

    private void Emit(byte b)
    {
        if (_firstByte)
        {
            _firstByte = false;
            if (b != 0) throw new InvalidOperationException("range coder invariant violated");
            return;
        }
        _out.Add(b);
    }

    public byte[] Finish()
    {
        if (_finished) throw new InvalidOperationException("already finished");
        _finished = true;
        if (!_any) return Array.Empty<byte>();
        for (int i = 0; i < 5; i++) ShiftLow();
        return _out.ToArray();
    }
}

/// <summary>Decoder for <see cref="RangeEncoder"/>. Never reads outside its input span.</summary>
internal ref struct RangeDecoder
{
    private readonly ReadOnlySpan<byte> _src;
    private int _pos;
    private uint _range;
    private uint _code;

    public RangeDecoder(ReadOnlySpan<byte> src)
    {
        _src = src;
        _pos = 0;
        _range = 0xFFFFFFFFu;
        _code = 0;
        if (src.Length < 4) throw new PatchFormatException("difference map: range coder stream too short");
        for (int i = 0; i < 4; i++) _code = (_code << 8) | _src[_pos++];
    }

    /// <summary>Bytes consumed so far.</summary>
    public int Position => _pos;

    public bool AtEnd => _pos == _src.Length;

    public uint DecodeUniform(uint total)
    {
        uint r = _range / total;
        uint v = _code / r;
        if (v >= total) throw new PatchFormatException("difference map: range coder value out of range");
        _code -= r * v;
        _range = r;
        Normalize();
        return v;
    }

    public int DecodeBit(uint p0)
    {
        uint bound = (_range >> RangeEncoder.ProbBits) * p0;
        int bit;
        if (_code < bound)
        {
            _range = bound;
            bit = 0;
        }
        else
        {
            _code -= bound;
            _range -= bound;
            bit = 1;
        }
        Normalize();
        return bit;
    }

    private void Normalize()
    {
        while (_range < RangeEncoder.TopValue)
        {
            if (_pos >= _src.Length) throw new PatchFormatException("difference map: range coder ran past the end of the map section");
            _range <<= 8;
            _code = (_code << 8) | _src[_pos++];
        }
    }
}
