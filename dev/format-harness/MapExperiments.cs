using System.IO.Compression;
using System.Numerics;
using BsDiff6.Format;
using ICSharpCode.SharpZipLib.BZip2;

namespace BsDiff6.FormatHarness;

/// <summary>Candidate difference-map models, measured by exact range-coded size.</summary>
internal static class MapExperiments
{
    // ---- adaptive bit helper (12-bit probability of a 0) ----
    internal static void Bit(RangeEncoder e, ushort[] probs, int ctx, int bit, int shift)
    {
        uint p = probs[ctx];
        e.EncodeBit(bit, p);
        if (bit == 0) p += (4096 - p) >> shift; else p -= p >> shift;
        probs[ctx] = (ushort)p;
    }

    internal static ushort[] NewProbs(int n)
    {
        var a = new ushort[n];
        Array.Fill(a, (ushort)2048);
        return a;
    }

    internal delegate void XCoder(RangeEncoder e, uint x, uint r, int cnt, int w, int depth);
    internal delegate void XCoder2(RangeEncoder e, uint x, uint r, int cnt, int w, int side, int parentPos);

    /// <summary>As <see cref="Midpoint"/>, also passing side (0 root, 1 left, 2 right child) and the parent's x position bucket (x*4/R).</summary>
    internal static int Midpoint2(int[] q, int d, XCoder2 code)
    {
        if (q.Length == 0 || q.Length == d) return 0;
        var e = new RangeEncoder();
        void Rec(int iLo, int iHi, int vLo, int vHi, int side, int ppos)
        {
            int cnt = iHi - iLo;
            if (cnt == 0 || cnt == vHi - vLo) return;
            int mid = iLo + cnt / 2;
            int a = mid - iLo;
            uint r = (uint)((vHi - vLo) - cnt + 1);
            uint x = (uint)(q[mid] - (vLo + a));
            code(e, x, r, cnt, vHi - vLo, side, ppos);
            int pos = (int)((ulong)x * 4 / r);
            Rec(iLo, mid, vLo, q[mid], 1, pos);
            Rec(mid + 1, iHi, q[mid] + 1, vHi, 2, pos);
        }
        Rec(0, q.Length, 0, d, 0, 4);
        return e.Finish().Length;
    }

    /// <summary>Adaptive bisection (no extremes flag) with context (log2 cnt, log2 W/cnt, optional side and parent position).</summary>
    internal static XCoder2 Bisect(int levels, int shift, bool useSide, bool useParent, int lcMax = 13)
    {
        int RB = 9;
        int ctxCount = lcMax * RB * 3 * 5;
        int nodes = 1 << (levels + 1);
        ushort[] bis = NewProbs(ctxCount * nodes);
        return (e, x, r, cnt, w, side, ppos) =>
        {
            int lc = Math.Min(lcMax - 1, Log2(cnt));
            int rb = Math.Min(RB - 1, Log2(w / cnt));
            int ctx = ((lc * RB + rb) * 3 + (useSide ? side : 0)) * 5 + (useParent ? ppos : 0);
            uint lo = 0, hi = r;
            int node = 1;
            for (int lvl = 0; lvl < levels && hi - lo >= 2; lvl++)
            {
                uint mid = lo + (hi - lo) / 2;
                int bit = x >= mid ? 1 : 0;
                Bit(e, bis, ctx * nodes + node, bit, shift);
                node = node * 2 + bit;
                if (bit == 1) lo = mid; else hi = mid;
            }
            Uniform(e, x - lo, hi - lo);
        };
    }

    /// <summary>Runs the DESIGN 7.5 pre-order midpoint recursion with a pluggable value coder.</summary>
    internal static int Midpoint(int[] q, int d, XCoder code)
    {
        if (q.Length == 0 || q.Length == d) return 0;
        var e = new RangeEncoder();
        void Rec(int iLo, int iHi, int vLo, int vHi, int depth)
        {
            int cnt = iHi - iLo;
            if (cnt == 0 || cnt == vHi - vLo) return;
            int mid = iLo + cnt / 2;
            int a = mid - iLo;
            uint r = (uint)((vHi - vLo) - cnt + 1);
            uint x = (uint)(q[mid] - (vLo + a));
            code(e, x, r, cnt, vHi - vLo, depth);
            Rec(iLo, mid, vLo, q[mid], depth + 1);
            Rec(mid + 1, iHi, q[mid] + 1, vHi, depth + 1);
        }
        Rec(0, q.Length, 0, d, 0);
        return e.Finish().Length;
    }

    internal static int Log2(int v) => v <= 0 ? 0 : 31 - BitOperations.LeadingZeroCount((uint)v);

    /// <summary>
    /// "Extremes" model: adaptive flag "x at an end of [0,R)", then which end, in context
    /// (bucketed log2 cnt, bucketed log2 W/cnt); interior uniform or by adaptive bisection of
    /// the first <paramref name="bisectLevels"/> levels.
    /// </summary>
    internal static XCoder Extremes(int bisectLevels, int shift, bool useDensity = true, bool extremes = true)
    {
        const int LC = 13, RB = 9;
        int ctxCount = LC * RB;
        ushort[] ext = NewProbs(ctxCount), side = NewProbs(ctxCount);
        int nodes = 1 << (bisectLevels + 1);
        ushort[] bis = NewProbs(ctxCount * nodes);
        return (e, x, r, cnt, w, depth) =>
        {
            int lc = Math.Min(LC - 1, Log2(cnt));
            int rb = useDensity ? Math.Min(RB - 1, Log2(w / cnt)) : 0;
            int ctx = lc * RB + rb;
            uint lo = 0, hi = r;
            if (extremes)
            {
                if (r == 2) { Bit(e, side, ctx, (int)x, shift); return; }
                int isExt = (x == 0 || x == r - 1) ? 1 : 0;
                Bit(e, ext, ctx, isExt, shift);
                if (isExt == 1) { Bit(e, side, ctx, x == 0 ? 0 : 1, shift); return; }
                lo = 1; hi = r - 1;
            }
            int node = 1;
            for (int lvl = 0; lvl < bisectLevels && hi - lo >= 2; lvl++)
            {
                uint mid = lo + (hi - lo) / 2;
                int bit = x >= mid ? 1 : 0;
                Bit(e, bis, ctx * nodes + node, bit, shift);
                node = node * 2 + bit;
                if (bit == 1) lo = mid; else hi = mid;
            }
            Uniform(e, x - lo, hi - lo);
        };
    }

    internal static void Uniform(RangeEncoder e, uint x, uint r)
    {
        if (r <= 1) return;
        MidpointCoder.EncodeUniform(e, x, r);
    }

    internal static XCoder Model0() => (e, x, r, cnt, w, depth) => MidpointCoder.EncodeUniform(e, x, r);

    // ---- reference points ----

    internal static byte[] PackBits(byte[] bits)
    {
        var o = new byte[(bits.Length + 7) / 8];
        for (int i = 0; i < bits.Length; i++) if (bits[i] != 0) o[i >> 3] |= (byte)(0x80 >> (i & 7));
        return o;
    }

    internal static int Zlib(byte[] data) => StringCodec.Compress(data, Codec.Zlib).Length;
    internal static int Bzip2(byte[] data) => StringCodec.Compress(data, Codec.Bzip2).Length;

    /// <summary>Order-k adaptive binary coder over a bit sequence (context = previous k bits).</summary>
    internal static int OrderK(byte[] bits, int k, int shift = 4)
    {
        if (bits.Length == 0) return 0;
        ushort[] probs = NewProbs(1 << k);
        var e = new RangeEncoder();
        int mask = (1 << k) - 1, h = 0;
        foreach (byte b in bits)
        {
            Bit(e, probs, h, b, shift);
            h = ((h << 1) | b) & mask;
        }
        return e.Finish().Length;
    }

    /// <summary>
    /// Adaptive binary coder over the raw map with the aligned old-file byte(s) as context
    /// (the decoder knows them): ctx = old[i] (8 bits) ‖ old[i-1] top bits ‖ last j map bits.
    /// </summary>
    internal static int OldByteContext(byte[] bits, byte[] oldAligned, int prevMapBits, bool prevOldByte, int shift = 4)
    {
        if (bits.Length == 0) return 0;
        int ctxBits = 8 + (prevOldByte ? 8 : 0) + prevMapBits;
        ushort[] probs = NewProbs(1 << ctxBits);
        var e = new RangeEncoder();
        int hmask = (1 << prevMapBits) - 1, h = 0;
        for (int i = 0; i < bits.Length; i++)
        {
            int ctx = oldAligned[i];
            if (prevOldByte) ctx = (ctx << 8) | (i > 0 ? oldAligned[i - 1] : 0);
            ctx = (ctx << prevMapBits) | h;
            Bit(e, probs, ctx, bits[i], shift);
            h = ((h << 1) | bits[i]) & hmask;
        }
        return e.Finish().Length;
    }

    /// <summary>
    /// Run-length coder over a bit sequence: alternating run lengths (starting with a 0-run, which
    /// may be empty), each coded as an adaptive Elias-gamma: unary bucket = floor(log2(len+1)) with
    /// adaptive bits in context (run type, previous bucket of the same type, position in unary),
    /// then the mantissa bits, the top two adaptive and the rest uniform.
    /// </summary>
    internal static int RunLength(byte[] bits)
    {
        if (bits.Length == 0) return 0;
        var e = new RangeEncoder();
        ushort[] unary = NewProbs(2 * 33 * 33);
        ushort[] mant = NewProbs(2 * 33 * 4);
        int[] prevBucket = new int[2];
        int i = 0, type = 0;
        while (i < bits.Length)
        {
            int len = 0;
            while (i < bits.Length && bits[i] == type) { len++; i++; }
            uint v = (uint)len + 1;
            int bucket = Log2((int)v);
            int pc = prevBucket[type];
            for (int u = 0; u < bucket; u++) Bit(e, unary, (type * 33 + pc) * 33 + u, 1, 4);
            Bit(e, unary, (type * 33 + pc) * 33 + bucket, 0, 4);
            int node = 1;
            for (int b = bucket - 1; b >= 0; b--)
            {
                int bit = (int)((v >> b) & 1);
                if (bucket - 1 - b < 2)
                {
                    Bit(e, mant, (type * 33 + bucket) * 4 + node, bit, 4);
                    node = node * 2 + bit;
                }
                else e.EncodeUniform((uint)bit, 2);
            }
            prevBucket[type] = bucket;
            type ^= 1;
        }
        return e.Finish().Length;
    }

    internal static double Log2Binomial(long n, long k)
    {
        // lgamma via Stirling-series sum of logs is fine at these sizes.
        double s = 0;
        k = Math.Min(k, n - k);
        for (long i = 0; i < k; i++) s += Math.Log2((double)(n - i) / (k - i));
        return s;
    }
}
