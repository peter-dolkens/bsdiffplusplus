namespace BsDiff6.Matching;

/// <summary>
/// Tuning constants of block alignment (DESIGN §4). Defaults are the values stated there unless
/// a property's documentation says otherwise.
/// </summary>
public sealed class BlockAlignOptions
{
    /// <summary>Seed of the xoshiro256** generator that picks the primes and the maps φ.</summary>
    public ulong Seed { get; set; } = 0x6273646966663600UL;

    /// <summary>Block length B; 0 = round(sqrt(n ln n)).</summary>
    public int BlockLength { get; set; }

    /// <summary>L = LFactor · B; the primes are drawn from [L, L (1 + 2 / ln L)).</summary>
    public double LFactor { get; set; } = 4.0;

    /// <summary>
    /// FFT size policy. False: primes from [L, L (1 + 2 / ln L)) and N = the power of two
    /// ≥ max(p) + B (DESIGN §4.1–4.2 as written). True (default): N = the smallest power of two
    /// ≥ L + B whose prime interval [max(L, U / (1 + 2 / ln U)), U), U = N - B + 1, holds at least
    /// <see cref="MinPrimeChoices"/> primes, and the primes are drawn from that interval. Every
    /// prime is still ≥ L, N is never larger than under the other policy, and the primes use
    /// whatever room the power-of-two FFT has anyway (larger p = better signal-to-noise).
    /// </summary>
    public bool FillFftSize { get; set; } = true;

    /// <summary>
    /// Allow FFT sizes 3·2^k as well as 2^k (one radix-3 stage). Caps the padding waste of the
    /// transform at 4/3 instead of 2. False = powers of two only, as DESIGN §4.2 says.
    /// </summary>
    public bool AllowRadix3Fft { get; set; } = true;

    /// <summary>Minimum number of primes to choose from under <see cref="FillFftSize"/>.</summary>
    public int MinPrimeChoices { get; set; } = 16;

    /// <summary>Number of random maps φ per prime (§1.6 note 8); their correlations are summed.</summary>
    public int MapsPerPrime { get; set; } = 2;

    /// <summary>
    /// Weight φ by the inverse square root of byte frequency in old ‖ new (§1.6 note 4). When
    /// false every byte has weight 1, as in the thesis's Algorithm 1.1.
    /// </summary>
    public bool FrequencyWeighting { get; set; } = true;

    /// <summary>
    /// With <see cref="FrequencyWeighting"/>: byte frequencies are floored at
    /// WeightFrequencyFloor / 256 before weighting, so bytes rarer than that are not up-weighted
    /// (rare-byte coincidences otherwise produce large spurious correlation peaks). 0 = no floor.
    /// </summary>
    public double WeightFrequencyFloor { get; set; }

    /// <summary>Number of largest entries taken from each prime's correlation vector.</summary>
    public int TopK { get; set; } = 32;

    /// <summary>Number of best-ranked candidates whose matches are counted directly.</summary>
    public int VerifyCount { get; set; } = 8;

    /// <summary>A block whose best candidate matches fewer than this fraction of its bytes has no prediction.</summary>
    public double MinMatchFraction { get; set; } = 0.25;

    /// <summary>Add the previous block's offset as an extra candidate.</summary>
    public bool ContinuityCandidate { get; set; } = true;

    /// <summary>
    /// With <see cref="ContinuityCandidate"/>: the continuity candidate is the offset of the
    /// nearest block before that has a prediction (not just the immediately preceding block), and
    /// a second, backward pass also tries the offset of the nearest predicted block after. Off =
    /// DESIGN §4.2 as written (previous block only).
    /// </summary>
    public bool BidirectionalContinuity { get; set; } = true;

    /// <summary>
    /// A final block shorter than this fraction of B is not queried; its only candidate is its
    /// predecessor's offset.
    /// </summary>
    public double ShortBlockFraction { get; set; } = 0.25;

    /// <summary>Block alignment is skipped (one unmatched segment) when the old file is shorter.</summary>
    public int MinOldLength { get; set; } = 1024;

    /// <summary>Run the boundary-tweaking passes (§2.4).</summary>
    public bool Tweak { get; set; } = true;

    /// <summary>
    /// A tweak moves a boundary at most this many block lengths (in either direction). 0 =
    /// anywhere within the two runs (DESIGN §4.3 as written). Blocks have already been verified,
    /// so a boundary is rarely more than a block from where it belongs; the limit keeps tweaking
    /// linear-time when runs are very long.
    /// </summary>
    public int TweakWindowBlocks { get; set; } = 2;

    /// <summary>Minimum run length after tweaking; 0 = max(B / 8, 32).</summary>
    public int MinRunLength { get; set; }

    /// <summary>Divisor and floor used for the default <see cref="MinRunLength"/>.</summary>
    public int MinRunLengthDivisor { get; set; } = 8;
    public int MinRunLengthFloor { get; set; } = 32;

    /// <summary>
    /// Tweaking score of a byte in a run with no prediction, as a fraction of one mismatch. A run
    /// with a prediction yields bytes to a neighbouring no-prediction run where it matches less
    /// often than 1 - UnmatchedCost. Default 0.75 = 1 - MinMatchFraction, i.e. consistent with the
    /// block acceptance threshold. DESIGN §4.3 says 0.5, which in practice deletes whole verified
    /// runs that match 25–50% (the thesis's final 50% filter, which §4.3 says not to apply).
    /// </summary>
    public double UnmatchedCost { get; set; } = 0.75;

    /// <summary>Query blocks in parallel. Output does not depend on this.</summary>
    public bool Parallel { get; set; } = true;

    /// <summary>Upper bound on worker threads when <see cref="Parallel"/> is set; 0 = runtime default.</summary>
    public int MaxDegreeOfParallelism { get; set; }

    internal BlockAlignOptions Validated()
    {
        if (LFactor <= 0 || double.IsNaN(LFactor)) throw new ArgumentException("LFactor must be positive");
        if (MapsPerPrime < 1) throw new ArgumentException("MapsPerPrime must be at least 1");
        if (TopK < 1) throw new ArgumentException("TopK must be at least 1");
        if (VerifyCount < 1) throw new ArgumentException("VerifyCount must be at least 1");
        if (BlockLength < 0) throw new ArgumentException("BlockLength must be non-negative");
        if (TweakWindowBlocks < 0) throw new ArgumentException("TweakWindowBlocks must be non-negative");
        if (MinRunLength < 0) throw new ArgumentException("MinRunLength must be non-negative");
        if (MinRunLengthDivisor < 1) throw new ArgumentException("MinRunLengthDivisor must be at least 1");
        if (!(UnmatchedCost >= 0 && UnmatchedCost <= 1)) throw new ArgumentException("UnmatchedCost must be in [0, 1]");
        return this;
    }

    public static int DefaultBlockLength(int oldLength)
    {
        if (oldLength < 2) return 1;
        return Math.Max(1, (int)Math.Round(Math.Sqrt(oldLength * Math.Log(oldLength))));
    }

    internal int ResolveBlockLength(int oldLength) => BlockLength > 0 ? BlockLength : DefaultBlockLength(oldLength);

    internal int ResolveMinRunLength(int blockLength) =>
        MinRunLength > 0 ? MinRunLength : Math.Max(blockLength / MinRunLengthDivisor, MinRunLengthFloor);
}
