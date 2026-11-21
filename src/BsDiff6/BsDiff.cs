using System.Diagnostics;
using BsDiff6.Alignment;
using BsDiff6.Format;
using BsDiff6.Matching;

namespace BsDiff6;

/// <summary>Options of <see cref="BsDiff.Create"/> (DESIGN §8).</summary>
public sealed class BsDiffOptions
{
    /// <summary>Run block alignment (thesis §2.4). False = local alignment only.</summary>
    public bool UseBlockAlignment { get; set; } = true;

    /// <summary>
    /// Run local alignment (thesis §2.5). False = "block alignment only" mode (thesis §2.8): far
    /// less memory, larger patches. With both alignments off every byte of the new file is
    /// unmatched: a valid patch that is just the compressed new file.
    /// </summary>
    public bool UseLocalAlignment { get; set; } = true;

    public BlockAlignOptions Block { get; set; } = new BlockAlignOptions();
    public DpOptions Dp { get; set; } = new DpOptions();
    public EncodeOptions Encode { get; set; } = new EncodeOptions();

    /// <summary>
    /// Use more than one thread: block and local alignment run concurrently, block queries run in
    /// parallel, and the difference modes are coded concurrently. The patch does not depend on this.
    /// When false, block alignment is sequential whatever <see cref="BlockAlignOptions.Parallel"/> says.
    /// </summary>
    public bool Parallel { get; set; } = true;
}

/// <summary>Wall-clock and process CPU time of the stages of one <see cref="BsDiff.CreateDetailed"/> run.</summary>
public sealed class StageTimings
{
    /// <summary>Wall time of block alignment alone.</summary>
    public TimeSpan BlockWall { get; internal set; }
    /// <summary>Wall time of local alignment alone.</summary>
    public TimeSpan LocalWall { get; internal set; }
    /// <summary>Wall time of the alignment phase (block and local, concurrently when parallel).</summary>
    public TimeSpan AlignWall { get; internal set; }
    /// <summary>
    /// Process CPU time during the alignment phase. Block and local alignment overlap, so their
    /// CPU time is only available combined.
    /// </summary>
    public TimeSpan AlignCpu { get; internal set; }
    public TimeSpan DpWall { get; internal set; }
    public TimeSpan DpCpu { get; internal set; }
    public TimeSpan EncodeWall { get; internal set; }
    public TimeSpan EncodeCpu { get; internal set; }
    public TimeSpan TotalWall { get; internal set; }
    public TimeSpan TotalCpu { get; internal set; }
}

/// <summary>Everything <see cref="BsDiff.CreateDetailed"/> knows about the patch it built.</summary>
public sealed class BsDiffResult
{
    public byte[] Patch { get; internal set; } = Array.Empty<byte>();
    /// <summary>The final canonical alignment that was encoded.</summary>
    public Segment[] Alignment { get; internal set; } = Array.Empty<Segment>();
    /// <summary>Size breakdown of the patch and of the alternatives the encoder tried.</summary>
    public EncodeResult Encode { get; internal set; } = new EncodeResult();
    public StageTimings Timings { get; } = new StageTimings();

    /// <summary>Whether block alignment ran (it is skipped inside when the old file is small; see <see cref="BlockStats"/>).</summary>
    public bool UsedBlockAlignment { get; internal set; }
    /// <summary>Whether local alignment ran.</summary>
    public bool UsedLocalAlignment { get; internal set; }
    /// <summary>Local alignment was requested but the inputs were too large; block-only mode was used.</summary>
    public bool LocalAlignmentFellBack { get; internal set; }

    public BlockAlignStats? BlockStats { get; internal set; }
    /// <summary>Coverage of the block alignment on its own (before the DP).</summary>
    public AlignmentCoverage? BlockCoverage { get; internal set; }
    public DpDiagnostics? Dp { get; internal set; }
    /// <summary>Coverage of the final alignment.</summary>
    public AlignmentCoverage Coverage { get; internal set; }

    public int Segments => Alignment.Length;
    public int MatchedSegments => Alignment.Count(s => s.IsMatched);
}

/// <summary>Builds bsdiff 6 patches (DESIGN §8).</summary>
public static class BsDiff
{
    /// <summary>Builds a patch that turns <paramref name="old"/> into <paramref name="new"/>.</summary>
    public static byte[] Create(ReadOnlyMemory<byte> old, ReadOnlyMemory<byte> @new, BsDiffOptions? options = null)
        => CreateDetailed(old, @new, options).Patch;

    /// <summary>As <see cref="Create"/>, also returning the alignment, diagnostics and timings.</summary>
    public static BsDiffResult CreateDetailed(ReadOnlyMemory<byte> old, ReadOnlyMemory<byte> @new, BsDiffOptions? options = null)
    {
        options ??= new BsDiffOptions();
        if (old.Length > int.MaxValue - 64 || @new.Length > int.MaxValue - 64)
            throw new ArgumentException("file too large");
        var result = new BsDiffResult();
        var total = Stopwatch.StartNew();
        TimeSpan cpu0 = Cpu();

        // ---- Alignment phase: block and local alignment are independent. ----
        BlockAlignOptions blockOpts = options.Block.Clone();
        if (!options.Parallel) blockOpts.Parallel = false;
        Segment[]? block = null;
        BlockAlignStats? blockStats = null;
        LocalMatches? local = null;
        bool localFellBack = false;
        TimeSpan blockWall = TimeSpan.Zero, localWall = TimeSpan.Zero;

        Action runBlock = () =>
        {
            var sw = Stopwatch.StartNew();
            block = BlockAligner.Align(old, @new, blockOpts, out BlockAlignStats st);
            blockStats = st;
            blockWall = sw.Elapsed;
        };
        Action runLocal = () =>
        {
            var sw = Stopwatch.StartNew();
            try { local = LocalAligner.Compute(old.Span, @new.Span); }
            catch (NotSupportedException) { localFellBack = true; }
            localWall = sw.Elapsed;
        };

        var phase = Stopwatch.StartNew();
        if (options.UseBlockAlignment && options.UseLocalAlignment && options.Parallel)
            System.Threading.Tasks.Parallel.Invoke(runBlock, runLocal);
        else
        {
            if (options.UseBlockAlignment) runBlock();
            if (options.UseLocalAlignment) runLocal();
        }
        // Falling back to block-only mode needs a block alignment; build it now if it was not wanted.
        if (localFellBack && block == null) runBlock();
        result.Timings.AlignWall = phase.Elapsed;
        result.Timings.BlockWall = blockWall;
        result.Timings.LocalWall = localWall;
        TimeSpan cpu1 = Cpu();
        result.Timings.AlignCpu = cpu1 - cpu0;

        result.UsedBlockAlignment = block != null;
        result.UsedLocalAlignment = local != null;
        result.LocalAlignmentFellBack = localFellBack;
        result.BlockStats = blockStats;
        if (block != null) result.BlockCoverage = AlignmentCoverage.Measure(old.Span, @new.Span, block);

        // ---- DP. ----
        phase.Restart();
        Segment[] alignment;
        if (block == null && local == null)
        {
            alignment = @new.Length == 0 ? Array.Empty<Segment>() : new[] { new Segment(0, @new.Length, Segment.Unmatched) };
        }
        else
        {
            var diag = new DpDiagnostics();
            alignment = DpAligner.Align(old.Span, @new.Span, local, block, options.Dp, diag);
            result.Dp = diag;
        }
        // Release the large intermediates (two int[m] of local matches, the block segments)
        // before encoding, so that peak memory is not the sum of all stages.
        local = null;
        block = null;
        result.Timings.DpWall = phase.Elapsed;
        TimeSpan cpu2 = Cpu();
        result.Timings.DpCpu = cpu2 - cpu1;
        if ((long)old.Length + @new.Length >= 1 << 20) GC.Collect();

        result.Alignment = alignment;
        result.Coverage = AlignmentCoverage.Measure(old.Span, @new.Span, alignment);

        // ---- Encode. ----
        phase.Restart();
        result.Encode = PatchWriter.EncodeDetailed(old, @new, alignment, options.Encode, options.Parallel);
        result.Patch = result.Encode.Patch;
        result.Timings.EncodeWall = phase.Elapsed;
        TimeSpan cpu3 = Cpu();
        result.Timings.EncodeCpu = cpu3 - cpu2;
        result.Timings.TotalWall = total.Elapsed;
        result.Timings.TotalCpu = cpu3 - cpu0;
        return result;
    }

    private static TimeSpan Cpu()
    {
        try { return Process.GetCurrentProcess().TotalProcessorTime; }
        catch (PlatformNotSupportedException) { return TimeSpan.Zero; }
    }
}

/// <summary>Applies bsdiff 6 patches.</summary>
public static class BsPatch
{
    /// <summary>
    /// Reconstructs the new file from <paramref name="old"/> and <paramref name="patch"/>. Throws
    /// <see cref="PatchFormatException"/> on any malformed, truncated or mismatching input.
    /// </summary>
    public static byte[] Apply(ReadOnlySpan<byte> old, ReadOnlySpan<byte> patch) => PatchReader.Apply(old, patch);
}
