namespace BsDiff6.Format;

/// <summary>
/// Header fields and section sizes of a patch (DESIGN 7.7). Returned by
/// <see cref="PatchReader.ReadInfo"/> and, for a freshly built patch, by
/// <see cref="PatchWriter.EncodeDetailed"/>.
/// </summary>
public sealed class PatchInfo
{
    public int Version { get; init; }
    public DiffMode DiffMode { get; init; }
    public Codec ControlCodec { get; init; }
    public Codec NonZeroCodec { get; init; }
    public Codec ExtraCodec { get; init; }
    public int MapModel { get; init; }
    public long OldSize { get; init; }
    public long NewSize { get; init; }
    public byte[] OldMd5 { get; init; } = Array.Empty<byte>();
    public byte[] NewMd5 { get; init; } = Array.Empty<byte>();
    public long ControlCompressedLength { get; init; }
    public long ControlLength { get; init; }
    public long NonZeroCompressedLength { get; init; }
    public long NonZeroLength { get; init; }
    public long ExtraCompressedLength { get; init; }
    public long ExtraLength { get; init; }
    public long MapCompressedLength { get; init; }
    public long BwtPrimary { get; init; }
    /// <summary>Bytes before the first section.</summary>
    public int HeaderLength { get; internal set; }
    public long TotalLength => HeaderLength + ControlCompressedLength + NonZeroCompressedLength
                               + MapCompressedLength + ExtraCompressedLength;

    public override string ToString() =>
        $"version {Version}, mode {DiffMode}, map model {MapModel}, old {OldSize}, new {NewSize}, " +
        $"header {HeaderLength}, control {ControlCompressedLength}/{ControlLength} ({ControlCodec}), " +
        $"non-zero {NonZeroCompressedLength}/{NonZeroLength} ({NonZeroCodec}), map {MapCompressedLength}, " +
        $"extra {ExtraCompressedLength}/{ExtraLength} ({ExtraCodec}), primary {BwtPrimary}, total {TotalLength}";
}

internal static class PatchHeader
{
    public static readonly byte[] Magic = { (byte)'B', (byte)'S', (byte)'D', (byte)'I', (byte)'F', (byte)'F', (byte)'6', (byte)'+' };
    public const int Version = 1;
    /// <summary>Bound on newSize, D and every uncompressed length (DESIGN 7.8).</summary>
    public const int MaxLength = int.MaxValue - 64;

    public static byte[] Write(PatchInfo h)
    {
        var o = new List<byte>(128);
        o.AddRange(Magic);
        o.Add((byte)h.Version);
        o.Add((byte)((int)h.DiffMode | ((int)h.ControlCodec << 2) | ((int)h.NonZeroCodec << 4) | ((int)h.ExtraCodec << 6)));
        o.Add((byte)h.MapModel);
        VarInt.Write(o, (ulong)h.OldSize);
        VarInt.Write(o, (ulong)h.NewSize);
        o.AddRange(h.OldMd5);
        o.AddRange(h.NewMd5);
        VarInt.Write(o, (ulong)h.ControlCompressedLength);
        VarInt.Write(o, (ulong)h.ControlLength);
        VarInt.Write(o, (ulong)h.NonZeroCompressedLength);
        VarInt.Write(o, (ulong)h.NonZeroLength);
        VarInt.Write(o, (ulong)h.ExtraCompressedLength);
        VarInt.Write(o, (ulong)h.ExtraLength);
        VarInt.Write(o, (ulong)h.MapCompressedLength);
        VarInt.Write(o, (ulong)h.BwtPrimary);
        return o.ToArray();
    }

    /// <summary>
    /// Parses and structurally validates the header: magic, version, ids, size bounds, and that the
    /// four sections exactly fill the rest of the patch. Throws <see cref="PatchFormatException"/>.
    /// </summary>
    public static PatchInfo Read(ReadOnlySpan<byte> patch)
    {
        if (patch.Length < Magic.Length + 3) throw new PatchFormatException("patch too short");
        if (!patch.Slice(0, Magic.Length).SequenceEqual(Magic)) throw new PatchFormatException("bad magic");
        int pos = Magic.Length;
        int version = patch[pos++];
        if (version != Version) throw new PatchFormatException("unsupported version " + version);
        int modes = patch[pos++];
        int mapModel = patch[pos++];
        int ctrlCodec = (modes >> 2) & 3, nzCodec = (modes >> 4) & 3, extraCodec = (modes >> 6) & 3;
        if (!StringCodec.IsKnown(ctrlCodec) || !StringCodec.IsKnown(nzCodec) || !StringCodec.IsKnown(extraCodec))
            throw new PatchFormatException("unknown codec id");
        if (!MidpointCoder.IsKnownModel(mapModel)) throw new PatchFormatException("unknown map model " + mapModel);

        long oldSize = ReadLength(patch, ref pos, "old size");
        long newSize = ReadLength(patch, ref pos, "new size");
        if (patch.Length - pos < 32) throw new PatchFormatException("patch truncated in MD5 fields");
        byte[] oldMd5 = patch.Slice(pos, 16).ToArray(); pos += 16;
        byte[] newMd5 = patch.Slice(pos, 16).ToArray(); pos += 16;
        long ctrlC = ReadLength(patch, ref pos, "control compressed length");
        long ctrl = ReadLength(patch, ref pos, "control length");
        long nzC = ReadLength(patch, ref pos, "non-zero compressed length");
        long nz = ReadLength(patch, ref pos, "non-zero length");
        long exC = ReadLength(patch, ref pos, "extra compressed length");
        long ex = ReadLength(patch, ref pos, "extra length");
        long mapC = ReadLength(patch, ref pos, "map length");
        long primary = ReadLength(patch, ref pos, "BWT primary index");

        long remaining = patch.Length - pos;
        if (ctrlC + nzC + exC + mapC != remaining)
            throw new PatchFormatException(ctrlC + nzC + exC + mapC < remaining
                ? "trailing data after the extra string" : "patch truncated");
        if (ex > newSize || nz > newSize) throw new PatchFormatException("string longer than the new file");

        return new PatchInfo
        {
            Version = version,
            DiffMode = (DiffMode)(modes & 3),
            ControlCodec = (Codec)ctrlCodec,
            NonZeroCodec = (Codec)nzCodec,
            ExtraCodec = (Codec)extraCodec,
            MapModel = mapModel,
            OldSize = oldSize,
            NewSize = newSize,
            OldMd5 = oldMd5,
            NewMd5 = newMd5,
            ControlCompressedLength = ctrlC,
            ControlLength = ctrl,
            NonZeroCompressedLength = nzC,
            NonZeroLength = nz,
            ExtraCompressedLength = exC,
            ExtraLength = ex,
            MapCompressedLength = mapC,
            BwtPrimary = primary,
            HeaderLength = pos,
        };
    }

    private static long ReadLength(ReadOnlySpan<byte> src, ref int pos, string what)
    {
        if (!VarInt.TryRead(src, ref pos, out ulong v)) throw new PatchFormatException("malformed or truncated varint: " + what);
        if (v > MaxLength) throw new PatchFormatException(what + " too large");
        return (long)v;
    }
}
