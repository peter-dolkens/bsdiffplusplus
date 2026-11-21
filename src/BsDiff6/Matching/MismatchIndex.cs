using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BsDiff6.Matching;

/// <summary>A candidate old-file position for a block, with its ranking score (sum of z-scores).</summary>
public readonly record struct MismatchCandidate(int OldPos, float Score);

/// <summary>
/// Indexed matching with mismatches (thesis Chapter 1, Algorithm 1.2 with the practical notes of
/// §1.6; DESIGN §4.1–4.2). Built once from the old file; then any number of blocks of length at
/// most <see cref="MaxQueryLength"/> can be located through <see cref="MismatchQuery"/> objects
/// (one per thread).
/// </summary>
/// <remarks>
/// For each of the two primes p, and each of its R maps φ, the index holds
/// A[j] = Σ_λ φ(S[j + λp]). Maps are packed in pairs as the real and imaginary parts of one
/// complex sequence, extended periodically to the FFT length N ≥ p + B, and stored transformed
/// (bit-reversed order). A query transforms the block under the same maps, multiplies by the
/// conjugate, and recovers the real correlation sums of both primes with one inverse transform.
/// </remarks>
public sealed class MismatchIndex
{
    internal readonly Fft Fft;
    internal readonly float[][][] PhiTables;   // [prime][map][byte]
    internal readonly float[][] SpecRe;        // [prime * groups + group][N], bit-reversed order
    internal readonly float[][] SpecIm;
    internal readonly int Groups;
    internal readonly long CrtModulus;
    internal readonly long P1InverseModP2;

    public int OldLength { get; }
    public int BlockLength { get; }
    public int P1 { get; }
    public int P2 { get; }
    public int FftSize => Fft.Size;
    public int MapsPerPrime { get; }
    public int TopK { get; }

    /// <summary>Longest block a query accepts (≥ <see cref="BlockLength"/>).</summary>
    public int MaxQueryLength => FftSize - Math.Max(P1, P2);

    /// <param name="old">The old file S.</param>
    /// <param name="blockLength">B; queries must be at most this long (a little more is tolerated).</param>
    /// <param name="byteWeights">256 weights w(c), see <see cref="ByteWeights"/>.</param>
    /// <param name="options">Uses Seed, LFactor, MapsPerPrime, TopK, FillFftSize, AllowRadix3Fft, MinPrimeChoices.</param>
    public MismatchIndex(ReadOnlySpan<byte> old, int blockLength, ReadOnlySpan<float> byteWeights,
                         BlockAlignOptions? options = null)
    {
        options = (options ?? new BlockAlignOptions()).Validated();
        if (blockLength < 1) throw new ArgumentOutOfRangeException(nameof(blockLength));
        if (byteWeights.Length != 256) throw new ArgumentException("need 256 byte weights", nameof(byteWeights));

        OldLength = old.Length;
        BlockLength = blockLength;
        MapsPerPrime = options.MapsPerPrime;
        TopK = options.TopK;

        var rng = new Xoshiro256(options.Seed);
        long lTarget = Math.Max(5L, (long)Math.Round(options.LFactor * blockLength));
        if (lTarget > int.MaxValue / 4) throw new NotSupportedException("block length too large");
        List<int> primes;
        int fftSize;
        if (options.FillFftSize)
        {
            fftSize = Fft.NextSize((int)lTarget + blockLength, options.AllowRadix3Fft);
            while (true)
            {
                long u = (long)fftSize - blockLength + 1;
                long lo = Math.Max(lTarget, (long)(u / (1 + 2 / Math.Log(u))));
                primes = PrimesIn(lo, u);
                if (primes.Count >= Math.Max(2, options.MinPrimeChoices)) break;
                if (fftSize > int.MaxValue / 4) throw new NotSupportedException("block length too large");
                fftSize = Fft.NextSize(fftSize + 1, options.AllowRadix3Fft);
            }
        }
        else
        {
            primes = PrimesIn(lTarget, (long)Math.Ceiling(lTarget * (1 + 2 / Math.Log(lTarget))));
            // Tiny L: the interval may hold fewer than two primes; extend it upwards.
            for (long x = primes.Count > 0 ? primes[primes.Count - 1] + 1 : lTarget; primes.Count < 2; x++)
                if (IsPrime(x)) primes.Add((int)x);
            fftSize = 0;
        }
        int i1 = rng.NextInt(primes.Count);
        int i2 = rng.NextInt(primes.Count - 1);
        if (i2 >= i1) i2++;
        P1 = primes[i1];
        P2 = primes[i2];

        int maxP = Math.Max(P1, P2);
        Fft = new Fft(fftSize > 0 ? fftSize : Fft.NextSize(maxP + blockLength, options.AllowRadix3Fft));
        int n = Fft.Size;

        int r = MapsPerPrime;
        PhiTables = new float[2][][];
        for (int pi = 0; pi < 2; pi++)
        {
            PhiTables[pi] = new float[r][];
            for (int m = 0; m < r; m++)
            {
                var t = new float[256];
                for (int c = 0; c < 256; c++)
                    t[c] = rng.NextBool() ? byteWeights[c] : -byteWeights[c];
                PhiTables[pi][m] = t;
            }
        }

        CrtModulus = (long)P1 * P2;
        P1InverseModP2 = ModInverse(P1 % P2, P2);

        Groups = (r + 1) / 2;
        SpecRe = new float[2 * Groups][];
        SpecIm = new float[2 * Groups][];

        // A vectors: A[j] = Σ_λ φ(S[j + λp]), accumulated in double, S walked in chunks of p.
        var aVectors = new double[2][][];
        for (int pi = 0; pi < 2; pi++)
        {
            int p = pi == 0 ? P1 : P2;
            var a = new double[r][];
            for (int m = 0; m < r; m++)
            {
                double[] am = a[m] = new double[p];
                float[] phi = PhiTables[pi][m];
                for (int start = 0; start < old.Length; start += p)
                {
                    ReadOnlySpan<byte> chunk = old.Slice(start, Math.Min(p, old.Length - start));
                    Span<double> acc = am.AsSpan(0, chunk.Length);
                    for (int j = 0; j < chunk.Length && j < acc.Length; j++) acc[j] += phi[chunk[j]];
                }
            }
            aVectors[pi] = a;
        }

        for (int pi = 0; pi < 2; pi++)
        {
            int p = pi == 0 ? P1 : P2;
            for (int g = 0; g < Groups; g++)
            {
                var re = new float[n];
                var im = new float[n];
                double[] ar = aVectors[pi][2 * g];
                double[]? ai = 2 * g + 1 < r ? aVectors[pi][2 * g + 1] : null;
                int j = 0;
                for (int t = 0; t < n; t++)
                {
                    re[t] = (float)ar[j];
                    if (ai != null) im[t] = (float)ai[j];
                    if (++j == p) j = 0;
                }
                Fft.ForwardScrambled(re, im);
                SpecRe[pi * Groups + g] = re;
                SpecIm[pi * Groups + g] = im;
            }
        }
    }

    /// <summary>The map φ (256 entries) used for map <paramref name="map"/> of prime 0 (P1) or 1 (P2).</summary>
    public ReadOnlySpan<float> Phi(int prime, int map) => PhiTables[prime][map];

    /// <summary>Creates a query object holding scratch buffers. Not thread-safe; use one per thread.</summary>
    public MismatchQuery CreateQuery() => new MismatchQuery(this);

    /// <summary>Unique x ≡ j1 (mod P1), x ≡ j2 (mod P2) in [0, P1·P2).</summary>
    internal long Crt(int j1, int j2)
    {
        long d = (j2 - j1) % P2;
        if (d < 0) d += P2;
        long t = d * P1InverseModP2 % P2;
        return j1 + P1 * t;
    }

    private static List<int> PrimesIn(long lo, long hiExclusive)
    {
        var primes = new List<int>();
        for (long x = lo; x < hiExclusive; x++)
            if (IsPrime(x)) primes.Add((int)x);
        return primes;
    }

    internal static bool IsPrime(long x)
    {
        if (x < 2) return false;
        if (x % 2 == 0) return x == 2;
        for (long d = 3; d * d <= x; d += 2)
            if (x % d == 0) return false;
        return true;
    }

    private static long ModInverse(long a, long m)
    {
        long g = m, x = 0, x1 = 1, a1 = a;
        while (a1 != 0)
        {
            long q = g / a1;
            (g, a1) = (a1, g - q * a1);
            (x, x1) = (x1, x - q * x1);
        }
        if (g != 1) throw new InvalidOperationException("not invertible");
        x %= m;
        return x < 0 ? x + m : x;
    }
}

/// <summary>
/// Per-thread query state for a <see cref="MismatchIndex"/>. <see cref="Find"/> computes the
/// two correlation vectors for a block and returns ranked candidate positions.
/// </summary>
public sealed class MismatchQuery
{
    private readonly MismatchIndex _ix;
    // Prime 0 works in _a, prime 1 in _b; _z is extra scratch only needed with more than one map pair.
    // After ComputeCorrelations, _aRe holds C for prime 0 and _aIm holds C for prime 1.
    private readonly float[] _aRe, _aIm, _bRe, _bIm;
    private readonly float[]? _zRe, _zIm;
    private readonly int[] _heapIdx1, _heapIdx2;
    private readonly float[] _heapVal;
    private readonly List<MismatchCandidate> _pairs = new List<MismatchCandidate>();

    internal MismatchQuery(MismatchIndex ix)
    {
        _ix = ix;
        int n = ix.FftSize;
        _aRe = new float[n]; _aIm = new float[n];
        _bRe = new float[n]; _bIm = new float[n];
        if (ix.Groups > 1) { _zRe = new float[n]; _zIm = new float[n]; }
        _heapIdx1 = new int[ix.TopK];
        _heapIdx2 = new int[ix.TopK];
        _heapVal = new float[ix.TopK];
    }

    public MismatchIndex Index => _ix;

    /// <summary>
    /// After <see cref="ComputeCorrelations"/> or <see cref="Find"/>: the summed correlation
    /// C[j] = Σ_maps Σ_r A[(r + j) mod p] φ(block[r]) for prime 0 (P1) or 1 (P2), length p.
    /// </summary>
    public ReadOnlySpan<float> Correlation(int prime) =>
        prime == 0 ? _aRe.AsSpan(0, _ix.P1) : _aIm.AsSpan(0, _ix.P2);

    /// <summary>Computes both correlation vectors for <paramref name="block"/>.</summary>
    public void ComputeCorrelations(ReadOnlySpan<byte> block)
    {
        MismatchIndex ix = _ix;
        if (block.Length > ix.MaxQueryLength)
            throw new ArgumentException($"block longer than {ix.MaxQueryLength}", nameof(block));
        int n = ix.FftSize;
        Fft fft = ix.Fft;
        int r = ix.MapsPerPrime;

#if MATCHING_PROFILE
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        for (int pi = 0; pi < 2; pi++)
        {
            float[] xr = pi == 0 ? _aRe : _bRe;
            float[] xi = pi == 0 ? _aIm : _bIm;
            for (int g = 0; g < ix.Groups; g++)
            {
                float[] zr = g == 0 ? xr : _zRe!;
                float[] zi = g == 0 ? xi : _zIm!;
                float[] phiA = ix.PhiTables[pi][2 * g];
                float[]? phiB = 2 * g + 1 < r ? ix.PhiTables[pi][2 * g + 1] : null;
                Array.Clear(zr, block.Length, n - block.Length);
                for (int t = 0; t < block.Length; t++) zr[t] = phiA[block[t]];
                if (phiB != null)
                {
                    Array.Clear(zi, block.Length, n - block.Length);
                    for (int t = 0; t < block.Length; t++) zi[t] = phiB[block[t]];
                }
                else Array.Clear(zi, 0, n);
#if MATCHING_PROFILE
                long ta = System.Diagnostics.Stopwatch.GetTimestamp();
                Profile.Pack += ta - t0;
#endif
                fft.ForwardScrambled(zr, zi);
#if MATCHING_PROFILE
                long tb = System.Diagnostics.Stopwatch.GetTimestamp();
                Profile.Fft += tb - ta;
#endif
                MultiplyConj(ix.SpecRe[pi * ix.Groups + g], ix.SpecIm[pi * ix.Groups + g], zr, zi, xr, xi, accumulate: g > 0);
#if MATCHING_PROFILE
                t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                Profile.Mul += t0 - tb;
#endif
            }
        }

        CombineHermitian(n);
#if MATCHING_PROFILE
        long tc = System.Diagnostics.Stopwatch.GetTimestamp();
        Profile.Combine += tc - t0;
#endif
        fft.InverseFromScrambled(_aRe, _aIm);
#if MATCHING_PROFILE
        Profile.Fft += System.Diagnostics.Stopwatch.GetTimestamp() - tc;
#endif
    }

    // X (+)= S · conj(Z), elementwise; Z and X may be the same arrays.
    private static void MultiplyConj(float[] sr, float[] si, float[] zr, float[] zi, float[] xr, float[] xi, bool accumulate)
    {
        int n = sr.Length;
        Span<Vector<float>> vxr = MemoryMarshal.Cast<float, Vector<float>>(xr.AsSpan());
        Span<Vector<float>> vxi = MemoryMarshal.Cast<float, Vector<float>>(xi.AsSpan());
        ReadOnlySpan<Vector<float>> vsr = MemoryMarshal.Cast<float, Vector<float>>(sr.AsSpan());
        ReadOnlySpan<Vector<float>> vsi = MemoryMarshal.Cast<float, Vector<float>>(si.AsSpan());
        ReadOnlySpan<Vector<float>> vzr = MemoryMarshal.Cast<float, Vector<float>>(zr.AsSpan());
        ReadOnlySpan<Vector<float>> vzi = MemoryMarshal.Cast<float, Vector<float>>(zi.AsSpan());
        int nv = vsr.Length;
        if (accumulate)
        {
            for (int k = 0; k < nv; k++)
            {
                Vector<float> pr = vsr[k], pim = vsi[k], a = vzr[k], b = vzi[k];
                vxr[k] += pr * a + pim * b;
                vxi[k] += pim * a - pr * b;
            }
        }
        else
        {
            for (int k = 0; k < nv; k++)
            {
                Vector<float> pr = vsr[k], pim = vsi[k], a = vzr[k], b = vzi[k];
                vxr[k] = pr * a + pim * b;
                vxi[k] = pim * a - pr * b;
            }
        }
        for (int k = nv * Vector<float>.Count; k < n; k++)
        {
            float pr = sr[k], pim = si[k], a = zr[k], b = zi[k];
            float yr = pr * a + pim * b, yi = pim * a - pr * b;
            if (accumulate) { xr[k] += yr; xi[k] += yi; }
            else { xr[k] = yr; xi[k] = yi; }
        }
    }

    // The wanted correlations are the real parts of IFFT(X1) and IFFT(X2), i.e. the inverse
    // transforms of their Hermitian parts. Writes H = herm(X1) + i·herm(X2) (scaled by 1/N) over
    // X1, so that IFFT(H) = c1 + i c2. herm(X)[k] needs X[-k]: see PartnerPairs for where that is.
    private void CombineHermitian(int n)
    {
        float scale = 0.5f / n;
        ref float x1r = ref MemoryMarshal.GetArrayDataReference(_aRe);
        ref float x1i = ref MemoryMarshal.GetArrayDataReference(_aIm);
        ref float x2r = ref MemoryMarshal.GetArrayDataReference(_bRe);
        ref float x2i = ref MemoryMarshal.GetArrayDataReference(_bIm);
        if (!_ix.Fft.IsRadix3)
        {
            CombinePow2Block(ref x1r, ref x1i, ref x2r, ref x2i, 0, n, scale);
            return;
        }
        // N = 3M: position r·M + i holds frequency 3·rev(i) + r. Frequencies 3k' pair within the
        // first third like a size-M power-of-two spectrum; frequency 3k'+1 at M + i pairs with
        // 3(M-1-k')+2 at 2M + (M-1-i) (bit reversal commutes with complementing all bits).
        int m = n / 3;
        CombinePow2Block(ref x1r, ref x1i, ref x2r, ref x2i, 0, m, scale);
        for (int i = 0; i < m; i++) CombinePair(ref x1r, ref x1i, ref x2r, ref x2i, m + i, 3 * m - 1 - i, scale);
    }

    // Partners within a power-of-two bit-reversed spectrum of size len at offset base: positions
    // 0 and 1 hold frequencies 0 and len/2 (self-partnered); position k in the octave [2^j, 2^(j+1))
    // pairs with 3·2^j - 1 - k.
    private static void CombinePow2Block(ref float x1r, ref float x1i, ref float x2r, ref float x2i, int @base, int len, float scale)
    {
        for (int k = 0; k < Math.Min(len, 2); k++)
        {
            int p = @base + k;
            float a1 = Unsafe.Add(ref x1r, p), a2 = Unsafe.Add(ref x2r, p);
            Unsafe.Add(ref x1r, p) = 2 * a1 * scale;
            Unsafe.Add(ref x1i, p) = 2 * a2 * scale;
        }
        for (int lo = 2; lo < len; lo <<= 1)
        {
            int mirror = 3 * lo - 1;
            int half = lo + (lo >> 1);
            for (int k = lo; k < half; k++)
                CombinePair(ref x1r, ref x1i, ref x2r, ref x2i, @base + k, @base + mirror - k, scale);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CombinePair(ref float x1r, ref float x1i, ref float x2r, ref float x2i, int k, int q, float scale)
    {
        float a1 = Unsafe.Add(ref x1r, k), b1 = Unsafe.Add(ref x1i, k);
        float c1 = Unsafe.Add(ref x1r, q), d1 = Unsafe.Add(ref x1i, q);
        float a2 = Unsafe.Add(ref x2r, k), b2 = Unsafe.Add(ref x2i, k);
        float c2 = Unsafe.Add(ref x2r, q), d2 = Unsafe.Add(ref x2i, q);
        float sumR = a1 + c1, difI = b1 - d1, sum2 = a2 + c2, dif2 = d2 - b2;
        Unsafe.Add(ref x1r, k) = (sumR + dif2) * scale;
        Unsafe.Add(ref x1i, k) = (difI + sum2) * scale;
        Unsafe.Add(ref x1r, q) = (sumR - dif2) * scale;
        Unsafe.Add(ref x1i, q) = (sum2 - difI) * scale;
    }

    /// <summary>
    /// Locates <paramref name="block"/>: computes the correlations, takes the TopK largest entries
    /// of each, combines every pair by CRT into an old-file position x ∈ [-(B-1), n) aligned with
    /// the block's first byte, and writes the candidates ranked by the sum of the two z-scores
    /// (best first; ties by position) into <paramref name="results"/>. Returns the number written.
    /// </summary>
    public int Find(ReadOnlySpan<byte> block, Span<MismatchCandidate> results)
    {
        ComputeCorrelations(block);
        return RankCandidates(results);
    }

    /// <summary>Ranking step of <see cref="Find"/> on the correlations already computed.</summary>
    public int RankCandidates(Span<MismatchCandidate> results)
    {
        MismatchIndex ix = _ix;
        ReadOnlySpan<float> c1 = Correlation(0), c2 = Correlation(1);
        int k1 = TopIndices(c1, _heapIdx1);
        (double m1, double s1) = MeanStd(c1);
        int k2 = TopIndices(c2, _heapIdx2);
        (double m2, double s2) = MeanStd(c2);
        double inv1 = s1 > 0 ? 1 / s1 : 0, inv2 = s2 > 0 ? 1 / s2 : 0;

        long lo = -(long)(ix.BlockLength - 1);
        long mod = ix.CrtModulus;
        int n = ix.OldLength;
        _pairs.Clear();
        for (int a = 0; a < k1; a++)
        {
            int j1 = _heapIdx1[a];
            double z1 = (c1[j1] - m1) * inv1;
            for (int b = 0; b < k2; b++)
            {
                int j2 = _heapIdx2[b];
                long x0 = ix.Crt(j1, j2);
                long x = ((x0 - lo) % mod + mod) % mod + lo;
                if (x >= n) continue;
                float score = (float)(z1 + (c2[j2] - m2) * inv2);
                for (; x < n; x += mod)
                    _pairs.Add(new MismatchCandidate((int)x, score));
            }
        }
        _pairs.Sort(CompareCandidates);
        int count = Math.Min(results.Length, _pairs.Count);
        for (int i = 0; i < count; i++) results[i] = _pairs[i];
        return count;
    }

    private static int CompareCandidates(MismatchCandidate a, MismatchCandidate b)
    {
        int c = b.Score.CompareTo(a.Score);
        return c != 0 ? c : a.OldPos.CompareTo(b.OldPos);
    }

    private static (double mean, double std) MeanStd(ReadOnlySpan<float> c)
    {
        double sum = 0, sum2 = 0;
        for (int i = 0; i < c.Length; i++) { double v = c[i]; sum += v; sum2 += v * v; }
        double mean = sum / c.Length;
        double var = Math.Max(0, sum2 / c.Length - mean * mean);
        return (mean, Math.Sqrt(var));
    }

    /// <summary>Indices of the (up to) K largest values; ties favour the smaller index.</summary>
    private int TopIndices(ReadOnlySpan<float> c, int[] idx)
    {
        int k = Math.Min(idx.Length, c.Length);
        float[] val = _heapVal;
        int size = 0;
        for (int i = 0; i < c.Length; i++)
        {
            float v = c[i];
            if (size < k)
            {
                // sift up in a min-heap
                int pos = size++;
                while (pos > 0)
                {
                    int parent = (pos - 1) >> 1;
                    if (!Less(v, i, val[parent], idx[parent])) break;
                    val[pos] = val[parent]; idx[pos] = idx[parent];
                    pos = parent;
                }
                val[pos] = v; idx[pos] = i;
            }
            else if (Less(val[0], idx[0], v, i))
            {
                int pos = 0;
                while (true)
                {
                    int child = 2 * pos + 1;
                    if (child >= size) break;
                    if (child + 1 < size && Less(val[child + 1], idx[child + 1], val[child], idx[child])) child++;
                    if (!Less(val[child], idx[child], v, i)) break;
                    val[pos] = val[child]; idx[pos] = idx[child];
                    pos = child;
                }
                val[pos] = v; idx[pos] = i;
            }
        }
        return size;
    }

    // "a is worse than b": smaller value, or equal value and larger index.
    private static bool Less(float va, int ia, float vb, int ib) => va < vb || (va == vb && ia > ib);
}

#if MATCHING_PROFILE
internal static class Profile
{
    public static long Pack, Fft, Mul, Combine;
    public static string Report()
    {
        double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        return $"pack {Pack * f:F0}ms fft {Fft * f:F0}ms mul {Mul * f:F0}ms combine {Combine * f:F0}ms";
    }
}
#endif
