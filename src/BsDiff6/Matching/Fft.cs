using System.Numerics;
using System.Runtime.InteropServices;

namespace BsDiff6.Matching;

/// <summary>
/// Complex FFT on <see cref="float"/> for sizes 2^k and 3·2^k, split (structure-of-arrays)
/// storage. Power-of-two part: radix-2² (fused pairs of radix-2 stages, plus one radix-2 stage
/// when log2 N is odd), decimation in frequency for the forward transform and decimation in time
/// for the inverse. Sizes 3·2^k add one radix-3 stage (first in the forward direction, last in
/// the inverse) around three independent power-of-two transforms of size M = N/3.
/// <para>
/// The forward transform takes natural order and leaves the spectrum in a scrambled order
/// (<see cref="FrequencyOfPosition"/>); the inverse takes that order and returns natural order.
/// Pointwise products therefore never need a permutation. Scrambled order: for N = 2^k,
/// position i holds frequency bitreverse(i); for N = 3M, position r·M + i holds frequency
/// 3·bitreverse_M(i) + r.
/// </para>
/// Inner loops use <see cref="Vector{T}"/>; every output element is computed by the same
/// sequence of operations whatever the vector width, so results do not depend on the hardware's
/// SIMD width. Instances are immutable after construction and may be shared between threads.
/// </summary>
public sealed class Fft
{
    private readonly float[] _twRe; // for span h: entries [h-1, 2h-1) hold cos(pi k / h)
    private readonly float[] _twIm; //                                     sin(pi k / h)
    private readonly float[] _tw3Re; // for span h, q = h/2: entries [q-1, 2q-1) hold cos(3 pi k / h)
    private readonly float[] _tw3Im; //                                              sin(3 pi k / h)
    private readonly int _log2; // of the power-of-two part
    // Radix-3 sizes only: transform of size N/3, and twiddles e^{-2πi t/N}, e^{-4πi t/N} for t < N/3.
    private readonly Fft? _inner;
    private readonly float[]? _w1Re, _w1Im, _w2Re, _w2Im;
    private int[]? _order;

    public int Size { get; }

    /// <summary>True when the size is 3·2^k.</summary>
    public bool IsRadix3 => _inner != null;

    public Fft(int size)
    {
        if (!IsSupportedSize(size))
            throw new ArgumentException("FFT size must be 2^k or 3·2^k", nameof(size));
        Size = size;
        if ((size & (size - 1)) != 0)
        {
            int m = size / 3;
            _inner = new Fft(m);
            _twRe = _twIm = _tw3Re = _tw3Im = Array.Empty<float>();
            _w1Re = new float[m]; _w1Im = new float[m]; _w2Re = new float[m]; _w2Im = new float[m];
            for (int t = 0; t < m; t++)
            {
                double a = 2 * Math.PI * t / size;
                _w1Re[t] = (float)Math.Cos(a); _w1Im[t] = (float)Math.Sin(a);
                _w2Re[t] = (float)Math.Cos(2 * a); _w2Im[t] = (float)Math.Sin(2 * a);
            }
            return;
        }
        _log2 = BitOperations.Log2((uint)size);
        _twRe = new float[Math.Max(1, size - 1)];
        _twIm = new float[Math.Max(1, size - 1)];
        _tw3Re = new float[Math.Max(1, size - 1)];
        _tw3Im = new float[Math.Max(1, size - 1)];
        for (int h = 1; h < size; h <<= 1)
        {
            for (int k = 0; k < h; k++)
            {
                double angle = Math.PI * k / h;
                _twRe[h - 1 + k] = (float)Math.Cos(angle);
                _twIm[h - 1 + k] = (float)Math.Sin(angle);
                // h here plays the role of q for the fused stage of span 2h.
                double angle3 = Math.PI * 3 * k / (2.0 * h);
                _tw3Re[h - 1 + k] = (float)Math.Cos(angle3);
                _tw3Im[h - 1 + k] = (float)Math.Sin(angle3);
            }
        }
    }

    public static bool IsSupportedSize(int n)
    {
        if (n < 1) return false;
        if ((n & (n - 1)) == 0) return true;
        return n % 3 == 0 && ((n / 3) & (n / 3 - 1)) == 0;
    }

    public static int NextPowerOfTwo(int x)
    {
        if (x <= 1) return 1;
        return (int)BitOperations.RoundUpToPowerOf2((uint)x);
    }

    /// <summary>Smallest supported size (2^k or, if <paramref name="allowRadix3"/>, 3·2^k) that is ≥ x.</summary>
    public static int NextSize(int x, bool allowRadix3 = true)
    {
        int p2 = NextPowerOfTwo(x);
        if (!allowRadix3) return p2;
        int p3 = 3 * NextPowerOfTwo((x + 2) / 3);
        return Math.Min(p2, p3);
    }

    /// <summary>Maps a storage position of the scrambled spectrum to its frequency index.</summary>
    public int[] FrequencyOfPosition
    {
        get
        {
            if (_order != null) return _order;
            var t = new int[Size];
            if (_inner == null)
            {
                for (int i = 0; i < Size; i++)
                    t[i] = _log2 == 0 ? 0 : (int)(ReverseBits((uint)i) >> (32 - _log2));
            }
            else
            {
                int m = Size / 3;
                int[] inner = _inner.FrequencyOfPosition;
                for (int r = 0; r < 3; r++)
                    for (int i = 0; i < m; i++) t[r * m + i] = 3 * inner[i] + r;
            }
            _order = t;
            return t;
        }
    }

    private static uint ReverseBits(uint v)
    {
        v = ((v >> 1) & 0x55555555u) | ((v & 0x55555555u) << 1);
        v = ((v >> 2) & 0x33333333u) | ((v & 0x33333333u) << 2);
        v = ((v >> 4) & 0x0F0F0F0Fu) | ((v & 0x0F0F0F0Fu) << 4);
        v = ((v >> 8) & 0x00FF00FFu) | ((v & 0x00FF00FFu) << 8);
        return (v >> 16) | (v << 16);
    }

    /// <summary>Forward DFT, X[k] = Σ x[t] e^{-2πikt/N}, natural order in and out.</summary>
    public void Forward(Span<float> re, Span<float> im)
    {
        ForwardScrambled(re, im);
        int[] f = FrequencyOfPosition;
        float[] r0 = re.ToArray(), i0 = im.ToArray();
        for (int i = 0; i < Size; i++) { re[f[i]] = r0[i]; im[f[i]] = i0[i]; }
    }

    /// <summary>Inverse DFT including the 1/N factor, natural order in and out.</summary>
    public void Inverse(Span<float> re, Span<float> im)
    {
        int[] f = FrequencyOfPosition;
        float[] r0 = re.ToArray(), i0 = im.ToArray();
        for (int i = 0; i < Size; i++) { re[i] = r0[f[i]]; im[i] = i0[f[i]]; }
        InverseFromScrambled(re, im);
        float scale = 1f / Size;
        for (int i = 0; i < Size; i++) { re[i] *= scale; im[i] *= scale; }
    }

    /// <summary>
    /// Forward DFT (sign -1), natural-order input; output position <c>i</c> holds frequency
    /// <c>FrequencyOfPosition[i]</c>.
    /// </summary>
    public void ForwardScrambled(Span<float> re, Span<float> im)
    {
        CheckLength(re, im);
        if (_inner != null)
        {
            int m = Size / 3;
            Radix3Forward(re, im, m);
            for (int r = 0; r < 3; r++) _inner.ForwardScrambled(re.Slice(r * m, m), im.Slice(r * m, m));
            return;
        }
        int n = Size;
        int h = n >> 1;
        if ((_log2 & 1) == 1)
        {
            Radix2Forward(re, im, h);
            h >>= 1;
        }
        for (; h >= 2; h >>= 2) Radix4Forward(re, im, h);
    }

    /// <summary>
    /// Unscaled inverse DFT (sign +1). Input position <c>i</c> holds frequency
    /// <c>FrequencyOfPosition[i]</c>; output is in natural order and is N times the true inverse.
    /// </summary>
    public void InverseFromScrambled(Span<float> re, Span<float> im)
    {
        CheckLength(re, im);
        if (_inner != null)
        {
            int m = Size / 3;
            for (int r = 0; r < 3; r++) _inner.InverseFromScrambled(re.Slice(r * m, m), im.Slice(r * m, m));
            Radix3Inverse(re, im, m);
            return;
        }
        int n = Size;
        int top = (_log2 & 1) == 1 ? n >> 2 : n >> 1;
        for (int h = 2; h <= top; h <<= 2) Radix4Inverse(re, im, h);
        if ((_log2 & 1) == 1) Radix2Inverse(re, im, n >> 1);
    }

    private const float HalfSqrt3 = 0.8660254037844386f;

    // Radix-3 DIF stage. For t < M, with a, b, c = x[t], x[t+M], x[t+2M], s = b + c, d = b - c:
    //   y0 = a + s
    //   y1 = (a - s/2 - i(√3/2) d) e^{-2πi t/N}
    //   y2 = (a - s/2 + i(√3/2) d) e^{-4πi t/N}
    private void Radix3Forward(Span<float> re, Span<float> im, int m)
    {
        float[] w1r = _w1Re!, w1i = _w1Im!, w2r = _w2Re!, w2i = _w2Im!;
        int vc = Vector<float>.Count;
        int t = 0;
        if (m % vc == 0)
        {
            Span<Vector<float>> vre = MemoryMarshal.Cast<float, Vector<float>>(re);
            Span<Vector<float>> vim = MemoryMarshal.Cast<float, Vector<float>>(im);
            ReadOnlySpan<Vector<float>> a1r = MemoryMarshal.Cast<float, Vector<float>>(w1r.AsSpan()), a1i = MemoryMarshal.Cast<float, Vector<float>>(w1i.AsSpan());
            ReadOnlySpan<Vector<float>> a2r = MemoryMarshal.Cast<float, Vector<float>>(w2r.AsSpan()), a2i = MemoryMarshal.Cast<float, Vector<float>>(w2i.AsSpan());
            int mv = m / vc;
            var half = new Vector<float>(0.5f);
            var h3 = new Vector<float>(HalfSqrt3);
            for (int k = 0; k < mv; k++)
            {
                Vector<float> ar = vre[k], ai = vim[k], br = vre[k + mv], bi = vim[k + mv], cr = vre[k + 2 * mv], ci = vim[k + 2 * mv];
                Vector<float> sr = br + cr, si = bi + ci, dr = (br - cr) * h3, di = (bi - ci) * h3;
                Vector<float> mr = ar - sr * half, mi = ai - si * half;
                vre[k] = ar + sr; vim[k] = ai + si;
                // p = m - i·d' = (mr + di, mi - dr); q = m + i·d' = (mr - di, mi + dr)
                Vector<float> pr = mr + di, pim = mi - dr, qr = mr - di, qi = mi + dr;
                Vector<float> c = a1r[k], sn = a1i[k];
                vre[k + mv] = pr * c + pim * sn; vim[k + mv] = pim * c - pr * sn;
                c = a2r[k]; sn = a2i[k];
                vre[k + 2 * mv] = qr * c + qi * sn; vim[k + 2 * mv] = qi * c - qr * sn;
            }
            t = m;
        }
        for (; t < m; t++)
        {
            float ar = re[t], ai = im[t], br = re[t + m], bi = im[t + m], cr = re[t + 2 * m], ci = im[t + 2 * m];
            float sr = br + cr, si = bi + ci, dr = (br - cr) * HalfSqrt3, di = (bi - ci) * HalfSqrt3;
            float mr = ar - sr * 0.5f, mi = ai - si * 0.5f;
            re[t] = ar + sr; im[t] = ai + si;
            float pr = mr + di, pim = mi - dr, qr = mr - di, qi = mi + dr;
            float c = w1r[t], sn = w1i[t];
            re[t + m] = pr * c + pim * sn; im[t + m] = pim * c - pr * sn;
            c = w2r[t]; sn = w2i[t];
            re[t + 2 * m] = qr * c + qi * sn; im[t + 2 * m] = qi * c - qr * sn;
        }
    }

    // Radix-3 DIT stage (inverse). u0 = y0, u1 = y1 e^{+2πi t/N}, u2 = y2 e^{+4πi t/N}, s = u1 + u2,
    // d = u1 - u2:  x[t] = u0 + s,  x[t+M] = u0 - s/2 + i(√3/2) d,  x[t+2M] = u0 - s/2 - i(√3/2) d.
    private void Radix3Inverse(Span<float> re, Span<float> im, int m)
    {
        float[] w1r = _w1Re!, w1i = _w1Im!, w2r = _w2Re!, w2i = _w2Im!;
        int vc = Vector<float>.Count;
        int t = 0;
        if (m % vc == 0)
        {
            Span<Vector<float>> vre = MemoryMarshal.Cast<float, Vector<float>>(re);
            Span<Vector<float>> vim = MemoryMarshal.Cast<float, Vector<float>>(im);
            ReadOnlySpan<Vector<float>> a1r = MemoryMarshal.Cast<float, Vector<float>>(w1r.AsSpan()), a1i = MemoryMarshal.Cast<float, Vector<float>>(w1i.AsSpan());
            ReadOnlySpan<Vector<float>> a2r = MemoryMarshal.Cast<float, Vector<float>>(w2r.AsSpan()), a2i = MemoryMarshal.Cast<float, Vector<float>>(w2i.AsSpan());
            int mv = m / vc;
            var half = new Vector<float>(0.5f);
            var h3 = new Vector<float>(HalfSqrt3);
            for (int k = 0; k < mv; k++)
            {
                Vector<float> c = a1r[k], sn = a1i[k], xr = vre[k + mv], xi = vim[k + mv];
                Vector<float> u1r = xr * c - xi * sn, u1i = xi * c + xr * sn;
                c = a2r[k]; sn = a2i[k]; xr = vre[k + 2 * mv]; xi = vim[k + 2 * mv];
                Vector<float> u2r = xr * c - xi * sn, u2i = xi * c + xr * sn;
                Vector<float> u0r = vre[k], u0i = vim[k];
                Vector<float> sr = u1r + u2r, si = u1i + u2i, dr = (u1r - u2r) * h3, di = (u1i - u2i) * h3;
                Vector<float> mr = u0r - sr * half, mi = u0i - si * half;
                vre[k] = u0r + sr; vim[k] = u0i + si;
                vre[k + mv] = mr - di; vim[k + mv] = mi + dr;
                vre[k + 2 * mv] = mr + di; vim[k + 2 * mv] = mi - dr;
            }
            t = m;
        }
        for (; t < m; t++)
        {
            float c = w1r[t], sn = w1i[t], xr = re[t + m], xi = im[t + m];
            float u1r = xr * c - xi * sn, u1i = xi * c + xr * sn;
            c = w2r[t]; sn = w2i[t]; xr = re[t + 2 * m]; xi = im[t + 2 * m];
            float u2r = xr * c - xi * sn, u2i = xi * c + xr * sn;
            float u0r = re[t], u0i = im[t];
            float sr = u1r + u2r, si = u1i + u2i, dr = (u1r - u2r) * HalfSqrt3, di = (u1i - u2i) * HalfSqrt3;
            float mr = u0r - sr * 0.5f, mi = u0i - si * 0.5f;
            re[t] = u0r + sr; im[t] = u0i + si;
            re[t + m] = mr - di; im[t + m] = mi + dr;
            re[t + 2 * m] = mr + di; im[t + 2 * m] = mi - dr;
        }
    }

    // One radix-2 DIF stage with span h.
    private void Radix2Forward(Span<float> re, Span<float> im, int h)
    {
        int n = Size, vc = Vector<float>.Count;
        ReadOnlySpan<float> wr = _twRe.AsSpan(h - 1, h);
        ReadOnlySpan<float> wi = _twIm.AsSpan(h - 1, h);
        if (h % vc == 0)
        {
            Span<Vector<float>> vre = MemoryMarshal.Cast<float, Vector<float>>(re);
            Span<Vector<float>> vim = MemoryMarshal.Cast<float, Vector<float>>(im);
            ReadOnlySpan<Vector<float>> vwr = MemoryMarshal.Cast<float, Vector<float>>(wr);
            ReadOnlySpan<Vector<float>> vwi = MemoryMarshal.Cast<float, Vector<float>>(wi);
            int hv = h / vc;
            for (int s = 0; s < n / vc; s += 2 * hv)
            {
                for (int k = 0; k < hv; k++)
                {
                    int i0 = s + k, i1 = i0 + hv;
                    Vector<float> ar = vre[i0], ai = vim[i0], br = vre[i1], bi = vim[i1];
                    vre[i0] = ar + br;
                    vim[i0] = ai + bi;
                    Vector<float> dr = ar - br, di = ai - bi;
                    Vector<float> c = vwr[k], sn = vwi[k];
                    vre[i1] = dr * c + di * sn;
                    vim[i1] = di * c - dr * sn;
                }
            }
            return;
        }
        for (int s = 0; s < n; s += 2 * h)
        {
            for (int k = 0; k < h; k++)
            {
                int i0 = s + k, i1 = i0 + h;
                float ar = re[i0], ai = im[i0], br = re[i1], bi = im[i1];
                re[i0] = ar + br;
                im[i0] = ai + bi;
                float dr = ar - br, di = ai - bi;
                float c = wr[k], sn = wi[k];
                re[i1] = dr * c + di * sn;
                im[i1] = di * c - dr * sn;
            }
        }
    }

    // One radix-2 DIT stage with span h (inverse direction).
    private void Radix2Inverse(Span<float> re, Span<float> im, int h)
    {
        int n = Size, vc = Vector<float>.Count;
        ReadOnlySpan<float> wr = _twRe.AsSpan(h - 1, h);
        ReadOnlySpan<float> wi = _twIm.AsSpan(h - 1, h);
        if (h % vc == 0)
        {
            Span<Vector<float>> vre = MemoryMarshal.Cast<float, Vector<float>>(re);
            Span<Vector<float>> vim = MemoryMarshal.Cast<float, Vector<float>>(im);
            ReadOnlySpan<Vector<float>> vwr = MemoryMarshal.Cast<float, Vector<float>>(wr);
            ReadOnlySpan<Vector<float>> vwi = MemoryMarshal.Cast<float, Vector<float>>(wi);
            int hv = h / vc;
            for (int s = 0; s < n / vc; s += 2 * hv)
            {
                for (int k = 0; k < hv; k++)
                {
                    int i0 = s + k, i1 = i0 + hv;
                    Vector<float> c = vwr[k], sn = vwi[k];
                    Vector<float> xr = vre[i1], xi = vim[i1];
                    Vector<float> br = xr * c - xi * sn;
                    Vector<float> bi = xi * c + xr * sn;
                    Vector<float> ar = vre[i0], ai = vim[i0];
                    vre[i0] = ar + br;
                    vim[i0] = ai + bi;
                    vre[i1] = ar - br;
                    vim[i1] = ai - bi;
                }
            }
            return;
        }
        for (int s = 0; s < n; s += 2 * h)
        {
            for (int k = 0; k < h; k++)
            {
                int i0 = s + k, i1 = i0 + h;
                float c = wr[k], sn = wi[k];
                float xr = re[i1], xi = im[i1];
                float br = xr * c - xi * sn;
                float bi = xi * c + xr * sn;
                float ar = re[i0], ai = im[i0];
                re[i0] = ar + br;
                im[i0] = ai + bi;
                re[i1] = ar - br;
                im[i1] = ai - bi;
            }
        }
    }

    // Two DIF stages (spans h and h/2) fused: 3 complex multiplications per 4 points.
    // With W = e^{-iπ/h}, q = h/2, for k < q and x0..x3 at s+k, s+k+q, s+k+h, s+k+h+q:
    //   y0 = (x0+x2) + (x1+x3)          y1 = ((x0+x2) - (x1+x3)) W^2k
    //   y2 = (u + v) W^k                y3 = (u - v) W^3k,    u = x0-x2, v = -i (x1-x3)
    private void Radix4Forward(Span<float> re, Span<float> im, int h)
    {
        int n = Size, vc = Vector<float>.Count, q = h >> 1;
        if (q == 1)
        {
            for (int s = 0; s < n; s += 4)
            {
                float x0r = re[s], x0i = im[s], x1r = re[s + 1], x1i = im[s + 1];
                float x2r = re[s + 2], x2i = im[s + 2], x3r = re[s + 3], x3i = im[s + 3];
                float t0r = x0r + x2r, t0i = x0i + x2i, t1r = x1r + x3r, t1i = x1i + x3i;
                float ur = x0r - x2r, ui = x0i - x2i;
                float vr = x1i - x3i, vi = x3r - x1r;
                re[s] = t0r + t1r; im[s] = t0i + t1i;
                re[s + 1] = t0r - t1r; im[s + 1] = t0i - t1i;
                re[s + 2] = ur + vr; im[s + 2] = ui + vi;
                re[s + 3] = ur - vr; im[s + 3] = ui - vi;
            }
            return;
        }
        ReadOnlySpan<float> w1r = _twRe.AsSpan(h - 1, q), w1i = _twIm.AsSpan(h - 1, q);
        ReadOnlySpan<float> w2r = _twRe.AsSpan(q - 1, q), w2i = _twIm.AsSpan(q - 1, q);
        ReadOnlySpan<float> w3r = _tw3Re.AsSpan(q - 1, q), w3i = _tw3Im.AsSpan(q - 1, q);
        if (q % vc == 0)
        {
            Span<Vector<float>> vre = MemoryMarshal.Cast<float, Vector<float>>(re);
            Span<Vector<float>> vim = MemoryMarshal.Cast<float, Vector<float>>(im);
            ReadOnlySpan<Vector<float>> a1r = MemoryMarshal.Cast<float, Vector<float>>(w1r), a1i = MemoryMarshal.Cast<float, Vector<float>>(w1i);
            ReadOnlySpan<Vector<float>> a2r = MemoryMarshal.Cast<float, Vector<float>>(w2r), a2i = MemoryMarshal.Cast<float, Vector<float>>(w2i);
            ReadOnlySpan<Vector<float>> a3r = MemoryMarshal.Cast<float, Vector<float>>(w3r), a3i = MemoryMarshal.Cast<float, Vector<float>>(w3i);
            int qv = q / vc, hv = h / vc;
            for (int s = 0; s < n / vc; s += 2 * hv)
            {
                for (int k = 0; k < qv; k++)
                {
                    int i0 = s + k, i1 = i0 + qv, i2 = i0 + hv, i3 = i2 + qv;
                    Vector<float> x0r = vre[i0], x0i = vim[i0], x1r = vre[i1], x1i = vim[i1];
                    Vector<float> x2r = vre[i2], x2i = vim[i2], x3r = vre[i3], x3i = vim[i3];
                    Vector<float> t0r = x0r + x2r, t0i = x0i + x2i, t1r = x1r + x3r, t1i = x1i + x3i;
                    Vector<float> ur = x0r - x2r, ui = x0i - x2i;
                    Vector<float> vr = x1i - x3i, vi = x3r - x1r;
                    vre[i0] = t0r + t1r; vim[i0] = t0i + t1i;
                    Vector<float> dr = t0r - t1r, di = t0i - t1i;
                    Vector<float> c = a2r[k], sn = a2i[k];
                    vre[i1] = dr * c + di * sn; vim[i1] = di * c - dr * sn;
                    dr = ur + vr; di = ui + vi; c = a1r[k]; sn = a1i[k];
                    vre[i2] = dr * c + di * sn; vim[i2] = di * c - dr * sn;
                    dr = ur - vr; di = ui - vi; c = a3r[k]; sn = a3i[k];
                    vre[i3] = dr * c + di * sn; vim[i3] = di * c - dr * sn;
                }
            }
            return;
        }
        for (int s = 0; s < n; s += 2 * h)
        {
            for (int k = 0; k < q; k++)
            {
                int i0 = s + k, i1 = i0 + q, i2 = i0 + h, i3 = i2 + q;
                float x0r = re[i0], x0i = im[i0], x1r = re[i1], x1i = im[i1];
                float x2r = re[i2], x2i = im[i2], x3r = re[i3], x3i = im[i3];
                float t0r = x0r + x2r, t0i = x0i + x2i, t1r = x1r + x3r, t1i = x1i + x3i;
                float ur = x0r - x2r, ui = x0i - x2i;
                float vr = x1i - x3i, vi = x3r - x1r;
                re[i0] = t0r + t1r; im[i0] = t0i + t1i;
                float dr = t0r - t1r, di = t0i - t1i;
                float c = w2r[k], sn = w2i[k];
                re[i1] = dr * c + di * sn; im[i1] = di * c - dr * sn;
                dr = ur + vr; di = ui + vi; c = w1r[k]; sn = w1i[k];
                re[i2] = dr * c + di * sn; im[i2] = di * c - dr * sn;
                dr = ur - vr; di = ui - vi; c = w3r[k]; sn = w3i[k];
                re[i3] = dr * c + di * sn; im[i3] = di * c - dr * sn;
            }
        }
    }

    // Two DIT stages (spans h/2 and h) fused, inverse direction. With conj twiddles
    // Y1 = y1 W^-2k, Y2 = y2 W^-k, Y3 = y3 W^-3k, e = y0 ± Y1, f = Y2 ± Y3:
    //   x0 = e0 + f0, x2 = e0 - f0, x1 = e1 + i f1, x3 = e1 - i f1.
    private void Radix4Inverse(Span<float> re, Span<float> im, int h)
    {
        int n = Size, vc = Vector<float>.Count, q = h >> 1;
        if (q == 1)
        {
            for (int s = 0; s < n; s += 4)
            {
                float y0r = re[s], y0i = im[s], y1r = re[s + 1], y1i = im[s + 1];
                float y2r = re[s + 2], y2i = im[s + 2], y3r = re[s + 3], y3i = im[s + 3];
                float e0r = y0r + y1r, e0i = y0i + y1i, e1r = y0r - y1r, e1i = y0i - y1i;
                float f0r = y2r + y3r, f0i = y2i + y3i, f1r = y2r - y3r, f1i = y2i - y3i;
                re[s] = e0r + f0r; im[s] = e0i + f0i;
                re[s + 2] = e0r - f0r; im[s + 2] = e0i - f0i;
                re[s + 1] = e1r - f1i; im[s + 1] = e1i + f1r;
                re[s + 3] = e1r + f1i; im[s + 3] = e1i - f1r;
            }
            return;
        }
        ReadOnlySpan<float> w1r = _twRe.AsSpan(h - 1, q), w1i = _twIm.AsSpan(h - 1, q);
        ReadOnlySpan<float> w2r = _twRe.AsSpan(q - 1, q), w2i = _twIm.AsSpan(q - 1, q);
        ReadOnlySpan<float> w3r = _tw3Re.AsSpan(q - 1, q), w3i = _tw3Im.AsSpan(q - 1, q);
        if (q % vc == 0)
        {
            Span<Vector<float>> vre = MemoryMarshal.Cast<float, Vector<float>>(re);
            Span<Vector<float>> vim = MemoryMarshal.Cast<float, Vector<float>>(im);
            ReadOnlySpan<Vector<float>> a1r = MemoryMarshal.Cast<float, Vector<float>>(w1r), a1i = MemoryMarshal.Cast<float, Vector<float>>(w1i);
            ReadOnlySpan<Vector<float>> a2r = MemoryMarshal.Cast<float, Vector<float>>(w2r), a2i = MemoryMarshal.Cast<float, Vector<float>>(w2i);
            ReadOnlySpan<Vector<float>> a3r = MemoryMarshal.Cast<float, Vector<float>>(w3r), a3i = MemoryMarshal.Cast<float, Vector<float>>(w3i);
            int qv = q / vc, hv = h / vc;
            for (int s = 0; s < n / vc; s += 2 * hv)
            {
                for (int k = 0; k < qv; k++)
                {
                    int i0 = s + k, i1 = i0 + qv, i2 = i0 + hv, i3 = i2 + qv;
                    Vector<float> c = a2r[k], sn = a2i[k], xr = vre[i1], xi = vim[i1];
                    Vector<float> y1r = xr * c - xi * sn, y1i = xi * c + xr * sn;
                    c = a1r[k]; sn = a1i[k]; xr = vre[i2]; xi = vim[i2];
                    Vector<float> y2r = xr * c - xi * sn, y2i = xi * c + xr * sn;
                    c = a3r[k]; sn = a3i[k]; xr = vre[i3]; xi = vim[i3];
                    Vector<float> y3r = xr * c - xi * sn, y3i = xi * c + xr * sn;
                    Vector<float> y0r = vre[i0], y0i = vim[i0];
                    Vector<float> e0r = y0r + y1r, e0i = y0i + y1i, e1r = y0r - y1r, e1i = y0i - y1i;
                    Vector<float> f0r = y2r + y3r, f0i = y2i + y3i, f1r = y2r - y3r, f1i = y2i - y3i;
                    vre[i0] = e0r + f0r; vim[i0] = e0i + f0i;
                    vre[i2] = e0r - f0r; vim[i2] = e0i - f0i;
                    vre[i1] = e1r - f1i; vim[i1] = e1i + f1r;
                    vre[i3] = e1r + f1i; vim[i3] = e1i - f1r;
                }
            }
            return;
        }
        for (int s = 0; s < n; s += 2 * h)
        {
            for (int k = 0; k < q; k++)
            {
                int i0 = s + k, i1 = i0 + q, i2 = i0 + h, i3 = i2 + q;
                float c = w2r[k], sn = w2i[k], xr = re[i1], xi = im[i1];
                float y1r = xr * c - xi * sn, y1i = xi * c + xr * sn;
                c = w1r[k]; sn = w1i[k]; xr = re[i2]; xi = im[i2];
                float y2r = xr * c - xi * sn, y2i = xi * c + xr * sn;
                c = w3r[k]; sn = w3i[k]; xr = re[i3]; xi = im[i3];
                float y3r = xr * c - xi * sn, y3i = xi * c + xr * sn;
                float y0r = re[i0], y0i = im[i0];
                float e0r = y0r + y1r, e0i = y0i + y1i, e1r = y0r - y1r, e1i = y0i - y1i;
                float f0r = y2r + y3r, f0i = y2i + y3i, f1r = y2r - y3r, f1i = y2i - y3i;
                re[i0] = e0r + f0r; im[i0] = e0i + f0i;
                re[i2] = e0r - f0r; im[i2] = e0i - f0i;
                re[i1] = e1r - f1i; im[i1] = e1i + f1r;
                re[i3] = e1r + f1i; im[i3] = e1i - f1r;
            }
        }
    }

    private void CheckLength(Span<float> re, Span<float> im)
    {
        if (re.Length != Size || im.Length != Size)
            throw new ArgumentException($"buffers must have length {Size}");
    }
}
