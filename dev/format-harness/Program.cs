using System.Diagnostics;
using BsDiff6;
using BsDiff6.Format;
using BsDiff6.FormatHarness;

// Usage: FormatHarness <pairDir>...   (each dir holds files "old" and "new")
//        FormatHarness --max-bytes N <pairDir>...
// For each pair: greedy alignment, full PatchWriter breakdown, then the map-model study on the
// difference map of every distinct mode.

int maxBytes = 4 << 20;
var dirs = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--max-bytes") maxBytes = int.Parse(args[++i]);
    else dirs.Add(args[i]);
}

var totals = new Dictionary<string, long>();
void Add(string k, long v) { totals.TryGetValue(k, out long t); totals[k] = t + v; }

foreach (string dir in dirs)
{
    byte[] old = File.ReadAllBytes(Path.Combine(dir, "old"));
    byte[] nw = File.ReadAllBytes(Path.Combine(dir, "new"));
    string name = Path.GetFileName(dir.TrimEnd('/'));
    if (old.Length > maxBytes || nw.Length > maxBytes) { Console.WriteLine($"# skip {name} (too large)"); continue; }

    var sw = Stopwatch.StartNew();
    Segment[] align = GreedyAligner.Align(old, nw);
    long alignMs = sw.ElapsedMilliseconds;
    sw.Restart();
    EncodeResult r = PatchWriter.EncodeDetailed(old, nw, align);
    long encMs = sw.ElapsedMilliseconds;
    sw.Restart();
    byte[] back = PatchReader.Apply(old, r.Patch);
    long decMs = sw.ElapsedMilliseconds;
    if (!back.AsSpan().SequenceEqual(nw)) throw new Exception("round trip failed");

    PatchInfo h = r.Info;
    Console.WriteLine($"## {name}: old {old.Length}, new {nw.Length}, segments {align.Length}, records {r.ControlRecords}, D {r.DifferenceLength}");
    Console.WriteLine($"   align {alignMs} ms, encode {encMs} ms, apply {decMs} ms");
    Console.WriteLine($"   patch {r.Patch.Length}: header {h.HeaderLength}, control {h.ControlCompressedLength} ({h.ControlCodec}; none/zlib/bz2 = {string.Join("/", r.ControlBytesByCodec)}), " +
                      $"nz {h.NonZeroCompressedLength} ({h.NonZeroCodec}), map {h.MapCompressedLength}, extra {h.ExtraCompressedLength}/{h.ExtraLength} ({h.ExtraCodec}; {string.Join("/", r.ExtraBytesByCodec)}), mode {h.DiffMode}");
    foreach (DiffModeCandidate c in r.Candidates) Console.WriteLine("   " + c);
    Add("patch", r.Patch.Length);
    Add("control", h.ControlCompressedLength);
    Add("extra", h.ExtraCompressedLength);
    Add("nz", h.NonZeroCompressedLength);
    Add("map(model0, chosen mode)", h.MapCompressedLength);

    // Map study: the maps of modes 1 (= mode 0), 2 and 3.
    var matched = align.Where(s => s.IsMatched).ToList();
    int d = r.DifferenceLength;
    byte[] oldAligned = new byte[d];
    { int p = 0; foreach (Segment s in matched) { old.AsSpan(s.OldPos, s.Length).CopyTo(oldAligned.AsSpan(p)); p += s.Length; } }

    foreach (DiffMode mode in new[] { DiffMode.Bytewise, DiffMode.LittleEndian, DiffMode.BigEndian })
    {
        byte[] map = new byte[d];
        byte[] nz = DifferenceCoder.Split(mode, old, nw, matched, map);
        int k = nz.Length;
        if (MidpointCoder.IsTrivial(d, k)) continue;
        byte[] l = Bwt.Forward(map, out _);
        int[] q = new int[k];
        { int j = 0; for (int i = 0; i < d; i++) if (l[i] != 0) q[j++] = i; }

        var row = new List<(string, long)>
        {
            ("iid bound log2 C(D,K)", (long)Math.Ceiling(MapExperiments.Log2Binomial(d, k) / 8)),
            ("model0 uniform", MapExperiments.Midpoint(q, d, MapExperiments.Model0())),
            ("ext, interior uniform", MapExperiments.Midpoint(q, d, MapExperiments.Extremes(0, 5))),
            ("ext, no density ctx", MapExperiments.Midpoint(q, d, MapExperiments.Extremes(0, 5, useDensity: false))),
            ("ext + bisect2", MapExperiments.Midpoint(q, d, MapExperiments.Extremes(2, 5))),
            ("ext + bisect4", MapExperiments.Midpoint(q, d, MapExperiments.Extremes(4, 5))),
            ("ext + bisect4 s4", MapExperiments.Midpoint(q, d, MapExperiments.Extremes(4, 4))),
            ("ext + bisect6", MapExperiments.Midpoint(q, d, MapExperiments.Extremes(6, 5))),
            ("bisect4 (no ext)", MapExperiments.Midpoint(q, d, MapExperiments.Extremes(4, 5, extremes: false))),
            ("bisect8 (no ext)", MapExperiments.Midpoint(q, d, MapExperiments.Extremes(8, 5, extremes: false))),
            ("bisect3 side", MapExperiments.Midpoint2(q, d, MapExperiments.Bisect(3, 5, true, false))),
            ("bisect3 side+parent", MapExperiments.Midpoint2(q, d, MapExperiments.Bisect(3, 5, true, true))),
            ("bisect4 side+parent", MapExperiments.Midpoint2(q, d, MapExperiments.Bisect(4, 5, true, true))),
            ("bisect4 side+parent s4", MapExperiments.Midpoint2(q, d, MapExperiments.Bisect(4, 4, true, true))),
            ("bisect2 side+parent", MapExperiments.Midpoint2(q, d, MapExperiments.Bisect(2, 5, true, true))),
            ("bisect4 parent lc6", MapExperiments.Midpoint2(q, d, MapExperiments.Bisect(4, 5, false, true, 6))),
            ("REF zlib raw bits", MapExperiments.Zlib(MapExperiments.PackBits(map))),
            ("REF bzip2 raw bits", MapExperiments.Bzip2(MapExperiments.PackBits(map))),
            ("REF zlib BWT bits", MapExperiments.Zlib(MapExperiments.PackBits(l))),
            ("REF bzip2 BWT bits", MapExperiments.Bzip2(MapExperiments.PackBits(l))),
            ("REF order-8 raw", MapExperiments.OrderK(map, 8)),
            ("REF order-16 raw", MapExperiments.OrderK(map, 16)),
            ("REF order-22 raw", MapExperiments.OrderK(map, 22)),
            ("REF order-8 BWT", MapExperiments.OrderK(l, 8)),
            ("REF order-16 BWT", MapExperiments.OrderK(l, 16)),
            ("REF runlength BWT", MapExperiments.RunLength(l)),
            ("REF runlength raw", MapExperiments.RunLength(map)),
            ("REF oldbyte+8 raw", MapExperiments.OldByteContext(map, oldAligned, 8, false)),
            ("REF 2oldbytes+4 raw", MapExperiments.OldByteContext(map, oldAligned, 4, true)),
        };
        Console.WriteLine($"   map {mode}: D {d}, K {k}");
        foreach (var (label, size) in row)
        {
            Console.WriteLine($"      {label,-26} {size,9}");
            Add($"map {mode,-12} {label}", size);
        }
    }
    Console.WriteLine();
}

Console.WriteLine("## totals");
foreach (var kv in totals.OrderBy(kv => kv.Key, StringComparer.Ordinal)) Console.WriteLine($"   {kv.Key,-50} {kv.Value,10}");
