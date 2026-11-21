using System.Security.Cryptography;

namespace BsDiff6.Format;

/// <summary>Builds bsdiff 6 patches from an alignment (DESIGN 7).</summary>
public static class PatchWriter
{
    /// <summary>
    /// Encodes <paramref name="new"/> against <paramref name="old"/> using
    /// <paramref name="alignment"/>, which must satisfy <see cref="AlignmentUtil.Validate"/>
    /// (an <see cref="ArgumentException"/> is thrown otherwise).
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<byte> old, ReadOnlySpan<byte> @new,
        IReadOnlyList<Segment> alignment, EncodeOptions? options = null)
        => EncodeDetailed(old, @new, alignment, options).Patch;

    /// <summary>As <see cref="Encode"/>, also returning the size breakdown of every choice made.</summary>
    public static EncodeResult EncodeDetailed(ReadOnlySpan<byte> old, ReadOnlySpan<byte> @new,
        IReadOnlyList<Segment> alignment, EncodeOptions? options = null)
    {
        options ??= new EncodeOptions();
        Layout lay = BuildLayout(old, @new, alignment);
        var groups = ModeGroups(options);
        var results = new ModeResult[groups.Count][];
        for (int g = 0; g < groups.Count; g++) results[g] = EvaluateModes(old, @new, lay, groups[g], options);
        long[] ctrlSizes = new long[3], extraSizes = new long[3];
        byte[] ctrlC = StringCodec.CompressBest(lay.Control, options.ControlCodec, out Codec ctrlCodec, ctrlSizes);
        byte[] extraC = StringCodec.CompressBest(lay.Extra, options.ExtraCodec, out Codec extraCodec, extraSizes);
        return Assemble(old, @new, lay, results, ctrlC, ctrlCodec, ctrlSizes, extraC, extraCodec, extraSizes);
    }

    /// <summary>
    /// As <see cref="EncodeDetailed(ReadOnlySpan{byte}, ReadOnlySpan{byte}, IReadOnlyList{Segment}, EncodeOptions?)"/>;
    /// with <paramref name="parallel"/> the difference modes and the control and extra strings are
    /// coded concurrently. The output does not depend on <paramref name="parallel"/>.
    /// </summary>
    public static EncodeResult EncodeDetailed(ReadOnlyMemory<byte> old, ReadOnlyMemory<byte> @new,
        IReadOnlyList<Segment> alignment, EncodeOptions? options, bool parallel)
    {
        if (!parallel) return EncodeDetailed(old.Span, @new.Span, alignment, options);
        options ??= new EncodeOptions();
        EncodeOptions opts = options;
        Layout lay = BuildLayout(old.Span, @new.Span, alignment);
        var groups = ModeGroups(opts);
        var results = new ModeResult[groups.Count][];
        long[] ctrlSizes = new long[3], extraSizes = new long[3];
        byte[]? ctrlC = null, extraC = null;
        Codec ctrlCodec = Codec.None, extraCodec = Codec.None;
        var work = new List<Action>
        {
            () => ctrlC = StringCodec.CompressBest(lay.Control, opts.ControlCodec, out ctrlCodec, ctrlSizes),
            () => extraC = StringCodec.CompressBest(lay.Extra, opts.ExtraCodec, out extraCodec, extraSizes),
        };
        for (int g = 0; g < groups.Count; g++)
        {
            int gi = g;
            work.Add(() => results[gi] = EvaluateModes(old.Span, @new.Span, lay, groups[gi], opts));
        }
        System.Threading.Tasks.Parallel.Invoke(work.ToArray());
        return Assemble(old.Span, @new.Span, lay, results, ctrlC!, ctrlCodec, ctrlSizes, extraC!, extraCodec, extraSizes);
    }

    /// <summary>Control string, extra string and matched segments derived from the alignment (7.2, 7.3).</summary>
    private sealed class Layout
    {
        public byte[] Control = Array.Empty<byte>();
        public byte[] Extra = Array.Empty<byte>();
        public List<Segment> Matched = new List<Segment>();
        public int Records;
        public int DLen;
    }

    /// <summary>Result of coding one difference mode.</summary>
    private sealed class ModeResult
    {
        public DiffModeCandidate Candidate = null!;
        public byte[] Map = Array.Empty<byte>();
        public byte[] NonZero = Array.Empty<byte>();
        public int Primary;
    }

    private static Layout BuildLayout(ReadOnlySpan<byte> old, ReadOnlySpan<byte> @new, IReadOnlyList<Segment> alignment)
    {
        if (alignment == null) throw new ArgumentNullException(nameof(alignment));
        if (@new.Length > PatchHeader.MaxLength || old.Length > PatchHeader.MaxLength)
            throw new ArgumentException("file too large");
        AlignmentUtil.Validate(alignment, old.Length, @new.Length);
        Segment[] segs = AlignmentUtil.Canonicalize(alignment);

        var control = new List<byte>();
        long extraTotal = 0;
        foreach (Segment s in segs) if (!s.IsMatched) extraTotal += s.Length;
        byte[] extra = new byte[extraTotal];
        int extraPos = 0;
        var lay = new Layout();
        long cursor = 0;
        int i = 0;
        if (segs.Length > 0 && !segs[0].IsMatched)
        {
            VarInt.WriteSigned(control, 0);
            VarInt.Write(control, 0);
            VarInt.Write(control, (ulong)segs[0].Length);
            @new.Slice(segs[0].NewPos, segs[0].Length).CopyTo(extra.AsSpan(extraPos));
            extraPos += segs[0].Length;
            lay.Records++;
            i = 1;
        }
        long d = 0;
        while (i < segs.Length)
        {
            Segment s = segs[i++];
            int extraLen = 0;
            if (i < segs.Length && !segs[i].IsMatched)
            {
                extraLen = segs[i].Length;
                @new.Slice(segs[i].NewPos, extraLen).CopyTo(extra.AsSpan(extraPos));
                extraPos += extraLen;
                i++;
            }
            VarInt.WriteSigned(control, s.OldPos - cursor);
            VarInt.Write(control, (ulong)s.Length);
            VarInt.Write(control, (ulong)extraLen);
            cursor = (long)s.OldPos + s.Length;
            lay.Matched.Add(s);
            d += s.Length;
            lay.Records++;
        }
        lay.Control = control.ToArray();
        lay.Extra = extra;
        lay.DLen = (int)d;
        return lay;
    }

    /// <summary>
    /// The difference modes to try, grouped so that modes 0 and 1 (which share their map) are
    /// coded together.
    /// </summary>
    private static List<int[]> ModeGroups(EncodeOptions options)
    {
        var groups = new List<int[]>();
        if (options.DiffMode.HasValue) { groups.Add(new[] { (int)options.DiffMode.Value }); return groups; }
        groups.Add(new[] { 0, 1 });
        groups.Add(new[] { 2 });
        groups.Add(new[] { 3 });
        return groups;
    }

    private static ModeResult[] EvaluateModes(ReadOnlySpan<byte> old, ReadOnlySpan<byte> @new, Layout lay,
        int[] modes, EncodeOptions options)
    {
        var results = new ModeResult[modes.Length];
        (byte[] coded, int primary, long[] sizes, MapModel model)? sharedCoded01 = null; // modes 0 and 1 share the map
        for (int j = 0; j < modes.Length; j++)
        {
            int mode = modes[j];
            byte[] map = new byte[lay.DLen];
            byte[] nz = DifferenceCoder.Split((DiffMode)mode, old, @new, lay.Matched, map);

            (byte[] coded, int primary, long[] sizes, MapModel model) mc;
            if (mode <= 1 && sharedCoded01.HasValue)
                mc = sharedCoded01.Value;
            else
            {
                mc = CodeMap(map, nz.Length, options.MapModel);
                if (mode <= 1) sharedCoded01 = mc;
            }
            map = Array.Empty<byte>();

            long[] nzSizes = new long[3];
            byte[] nzC = StringCodec.CompressBest(nz, options.NonZeroCodec, out Codec nzCodec, nzSizes);
            results[j] = new ModeResult
            {
                Candidate = new DiffModeCandidate
                {
                    Mode = (DiffMode)mode,
                    Ones = nz.Length,
                    MapBytesByModel = mc.sizes,
                    NonZeroBytesByCodec = nzSizes,
                    BestModel = mc.model,
                    BestCodec = nzCodec,
                    Total = mc.coded.Length + nzC.Length,
                },
                Map = mc.coded,
                NonZero = nzC,
                Primary = mc.primary,
            };
        }
        return results;
    }

    private static EncodeResult Assemble(ReadOnlySpan<byte> old, ReadOnlySpan<byte> @new, Layout lay,
        ModeResult[][] groups, byte[] ctrlC, Codec ctrlCodec, long[] ctrlSizes,
        byte[] extraC, Codec extraCodec, long[] extraSizes)
    {
        // Candidates in mode-id order; the first strictly smallest wins (deterministic).
        var all = groups.SelectMany(g => g).OrderBy(r => (int)r.Candidate.Mode).ToList();
        ModeResult best = all[0];
        foreach (ModeResult r in all) if (r.Candidate.Total < best.Candidate.Total) best = r;

        var info = new PatchInfo
        {
            Version = PatchHeader.Version,
            DiffMode = best.Candidate.Mode,
            ControlCodec = ctrlCodec,
            NonZeroCodec = best.Candidate.BestCodec,
            ExtraCodec = extraCodec,
            MapModel = (int)best.Candidate.BestModel,
            OldSize = old.Length,
            NewSize = @new.Length,
            OldMd5 = MD5.HashData(old),
            NewMd5 = MD5.HashData(@new),
            ControlCompressedLength = ctrlC.Length,
            ControlLength = lay.Control.Length,
            NonZeroCompressedLength = best.NonZero.Length,
            NonZeroLength = best.Candidate.Ones,
            ExtraCompressedLength = extraC.Length,
            ExtraLength = lay.Extra.Length,
            MapCompressedLength = best.Map.Length,
            BwtPrimary = best.Primary,
        };
        byte[] header = PatchHeader.Write(info);
        info.HeaderLength = header.Length;

        byte[] patch = new byte[header.Length + ctrlC.Length + best.NonZero.Length + best.Map.Length + extraC.Length];
        int p = 0;
        foreach (byte[] part in new[] { header, ctrlC, best.NonZero, best.Map, extraC })
        {
            Buffer.BlockCopy(part, 0, patch, p, part.Length);
            p += part.Length;
        }

        return new EncodeResult
        {
            Patch = patch,
            Info = info,
            DifferenceLength = lay.DLen,
            ControlRecords = lay.Records,
            ControlBytesByCodec = ctrlSizes,
            ExtraBytesByCodec = extraSizes,
            Candidates = all.Select(r => r.Candidate).ToList(),
        };
    }

    /// <summary>
    /// BWT + midpoint coding of a map with <paramref name="k"/> ones under each requested model.
    /// The primary index is written as 0 when the map section is empty (D = 0, K = 0 or K = D).
    /// </summary>
    internal static (byte[] coded, int primary, long[] sizes, MapModel model) CodeMap(byte[] map, int k, MapModel? forced)
    {
        long[] sizes = new long[MidpointCoder.ModelCount];
        for (int i = 0; i < sizes.Length; i++) sizes[i] = -1;
        if (MidpointCoder.IsTrivial(map.Length, k))
        {
            MapModel m = forced ?? MapModel.Uniform;
            sizes[(int)m] = 0;
            return (Array.Empty<byte>(), 0, sizes, m);
        }
        byte[] l = Bwt.Forward(map, out int primary);
        int[] q = new int[k];
        int j = 0;
        for (int i = 0; i < l.Length; i++) if (l[i] != 0) q[j++] = i;

        byte[]? best = null;
        MapModel bestModel = MapModel.Uniform;
        for (int id = 0; id < MidpointCoder.ModelCount; id++)
        {
            if (forced.HasValue && (int)forced.Value != id) continue;
            byte[] coded = MidpointCoder.Encode(q, map.Length, (MapModel)id);
            sizes[id] = coded.Length;
            if (best == null || coded.Length < best.Length) { best = coded; bestModel = (MapModel)id; }
        }
        return (best!, primary, sizes, bestModel);
    }
}
