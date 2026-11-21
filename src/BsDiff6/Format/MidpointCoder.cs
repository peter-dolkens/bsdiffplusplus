namespace BsDiff6.Format;

/// <summary>Probability model for the midpoint values of the difference map (header byte <c>mapModel</c>).</summary>
public enum MapModel
{
    /// <summary>DESIGN 7.5 model 0: every midpoint value uniform over its range.</summary>
    Uniform = 0,
}

/// <summary>
/// Recursive midpoint coding of the 1-positions of the BWT'd difference map (DESIGN 7.5 steps
/// 2–4). Values are visited in pre-order: a node's midpoint, then its left subtree, then its right
/// subtree. Recursion depth is at most ⌈log2(K+1)⌉ ≤ 32 regardless of input.
/// </summary>
internal static class MidpointCoder
{
    public const int ModelCount = 1;

    public static bool IsKnownModel(int id) => id >= 0 && id < ModelCount;

    /// <summary>True when the map section is empty: nothing to code.</summary>
    public static bool IsTrivial(long d, long k) => d == 0 || k == 0 || k == d;

    /// <summary>Codes strictly increasing positions <paramref name="q"/> in <c>[0, d)</c>.</summary>
    public static byte[] Encode(ReadOnlySpan<int> q, int d, MapModel model)
    {
        if (!IsKnownModel((int)model)) throw new ArgumentOutOfRangeException(nameof(model));
        var enc = new RangeEncoder();
        if (!IsTrivial(d, q.Length))
            EncodeRange(enc, q, 0, q.Length, 0, d);
        return enc.Finish();
    }

    private static void EncodeRange(RangeEncoder enc, ReadOnlySpan<int> q, int iLo, int iHi, int vLo, int vHi)
    {
        int cnt = iHi - iLo;
        if (cnt == 0 || cnt == vHi - vLo) return;
        int mid = iLo + cnt / 2;
        int a = mid - iLo;
        uint r = (uint)((vHi - vLo) - cnt + 1);
        uint x = (uint)(q[mid] - (vLo + a));
        if (x >= r) throw new ArgumentException("positions are not strictly increasing within [0, D)");
        EncodeUniform(enc, x, r);
        EncodeRange(enc, q, iLo, mid, vLo, q[mid]);
        EncodeRange(enc, q, mid + 1, iHi, q[mid] + 1, vHi);
    }

    internal static void EncodeUniform(RangeEncoder enc, uint x, uint r)
    {
        if (r <= RangeEncoder.MaxTotal)
        {
            enc.EncodeUniform(x, r);
        }
        else
        {
            enc.EncodeUniform(x >> 16, ((r - 1) >> 16) + 1);
            enc.EncodeUniform(x & 0xFFFF, 1u << 16);
        }
    }

    /// <summary>
    /// Decodes <paramref name="k"/> positions in <c>[0, d)</c> into a bit vector (bit i of word
    /// i/64), requiring the coded stream to occupy <paramref name="src"/> exactly.
    /// </summary>
    public static ulong[] Decode(ReadOnlySpan<byte> src, int d, int k, int modelId)
    {
        if (!IsKnownModel(modelId)) throw new PatchFormatException("unknown map model " + modelId);
        if (k < 0 || k > d) throw new PatchFormatException("non-zero string longer than the difference string");
        ulong[] bits = new ulong[(d + 63) >> 6];
        if (k == d)
        {
            for (int i = 0; i < d; i++) bits[i >> 6] |= 1UL << (i & 63);
        }
        if (IsTrivial(d, k))
        {
            if (src.Length != 0) throw new PatchFormatException("difference map section should be empty");
            return bits;
        }
        var dec = new RangeDecoder(src);
        DecodeRange(ref dec, bits, k, 0, d);
        if (!dec.AtEnd) throw new PatchFormatException("trailing bytes in difference map section");
        return bits;
    }

    private static void DecodeRange(ref RangeDecoder dec, ulong[] bits, int cnt, int vLo, int vHi)
    {
        // cnt values lie in [vLo, vHi); 0 <= cnt <= vHi - vLo holds by construction.
        if (cnt == 0) return;
        if (cnt == vHi - vLo)
        {
            for (int i = vLo; i < vHi; i++) bits[i >> 6] |= 1UL << (i & 63);
            return;
        }
        int a = cnt / 2;
        int b = cnt - a - 1;
        uint r = (uint)((vHi - vLo) - cnt + 1);
        uint x = DecodeUniform(ref dec, r);
        int v = vLo + a + (int)x;
        bits[v >> 6] |= 1UL << (v & 63);
        DecodeRange(ref dec, bits, a, vLo, v);
        DecodeRange(ref dec, bits, b, v + 1, vHi);
    }

    internal static uint DecodeUniform(ref RangeDecoder dec, uint r)
    {
        if (r <= RangeEncoder.MaxTotal) return dec.DecodeUniform(r);
        uint hi = dec.DecodeUniform(((r - 1) >> 16) + 1);
        uint lo = dec.DecodeUniform(1u << 16);
        uint x = (hi << 16) | lo;
        if (x >= r) throw new PatchFormatException("difference map: midpoint value out of range");
        return x;
    }
}
