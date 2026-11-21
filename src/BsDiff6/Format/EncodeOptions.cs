namespace BsDiff6.Format;

/// <summary>
/// Overrides for <see cref="PatchWriter"/>. Every null field means "try all, keep the smallest".
/// </summary>
public sealed class EncodeOptions
{
    public DiffMode? DiffMode { get; set; }
    public Codec? ControlCodec { get; set; }
    public Codec? NonZeroCodec { get; set; }
    public Codec? ExtraCodec { get; set; }
    public MapModel? MapModel { get; set; }
}

/// <summary>Sizes measured for one difference mode while encoding.</summary>
public sealed class DiffModeCandidate
{
    public DiffMode Mode { get; init; }
    /// <summary>Number of 1s in the difference map (= non-zero string length).</summary>
    public int Ones { get; init; }
    /// <summary>Map section size per model id (-1 = not tried).</summary>
    public long[] MapBytesByModel { get; init; } = Array.Empty<long>();
    /// <summary>Compressed non-zero string size per codec id (-1 = not tried).</summary>
    public long[] NonZeroBytesByCodec { get; init; } = Array.Empty<long>();
    public MapModel BestModel { get; init; }
    public Codec BestCodec { get; init; }
    public long Total { get; init; }

    public override string ToString() =>
        $"{Mode}: K={Ones} map[{string.Join(",", MapBytesByModel)}] nz[{string.Join(",", NonZeroBytesByCodec)}] total={Total}";
}

/// <summary>Result of <see cref="PatchWriter.EncodeDetailed"/>: the patch and its size breakdown.</summary>
public sealed class EncodeResult
{
    public byte[] Patch { get; init; } = Array.Empty<byte>();
    public PatchInfo Info { get; init; } = new PatchInfo();
    /// <summary>Σ copyLen: length of the difference string / map.</summary>
    public int DifferenceLength { get; init; }
    public int ControlRecords { get; init; }
    public long[] ControlBytesByCodec { get; init; } = Array.Empty<long>();
    public long[] ExtraBytesByCodec { get; init; } = Array.Empty<long>();
    /// <summary>One entry per difference mode tried, in id order.</summary>
    public IReadOnlyList<DiffModeCandidate> Candidates { get; init; } = Array.Empty<DiffModeCandidate>();
}
