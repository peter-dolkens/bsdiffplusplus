using System.Diagnostics;
using BsDiff6;
using BsDiff6.Alignment;
using BsDiff6.Matching;

// csalign <old> <new> <outdir> [modes]
//   modes: comma list of  local   (DP over suffix-array local matches only)        -> segs_dp.bin
//                         both    (DP over local matches + FFT block alignment)    -> segs_dpb.bin
//                         block   (FFT block alignment alone, as returned)         -> segs_blk.bin
//                         blockdp (DP with block alignment only, "block-only mode")-> segs_dpo.bin
// Output format: int32 count, then (newPos, length, oldPos) int32 triples; oldPos = -1 for unmatched.
static void Save(string path, Segment[] segs)
{
    using var w = new BinaryWriter(File.Create(path));
    w.Write(segs.Length);
    foreach (Segment s in segs) { w.Write(s.NewPos); w.Write(s.Length); w.Write(s.OldPos); }
}
static string Stat(byte[] old, byte[] nw, Segment[] segs)
{
    long matched = 0, differ = 0; int nm = 0;
    foreach (Segment s in segs)
    {
        if (!s.IsMatched) continue;
        nm++; matched += s.Length;
        for (int i = 0; i < s.Length; i++) if (old[s.OldPos + i] != nw[s.NewPos + i]) differ++;
    }
    return $"segs {segs.Length} matched_segs {nm} matched {matched} differ {differ}";
}

byte[] old = File.ReadAllBytes(args[0]);
byte[] nw = File.ReadAllBytes(args[1]);
string outdir = args[2];
var modes = new HashSet<string>((args.Length > 3 ? args[3] : "local").Split(','));
var proc = Process.GetCurrentProcess();
double Cpu() { proc.Refresh(); return proc.TotalProcessorTime.TotalSeconds; }

LocalMatches? lm = null;
Segment[]? block = null;
double t0 = Cpu();
if (modes.Contains("local") || modes.Contains("both")) lm = LocalAligner.Compute(old, nw);
double t1 = Cpu();
if (modes.Contains("block") || modes.Contains("both") || modes.Contains("blockdp")) block = BlockAligner.Align(old, nw);
double t2 = Cpu();
Console.WriteLine($"local_cpu {t1 - t0:F2} block_cpu {t2 - t1:F2}");
if (modes.Contains("block")) { Save(Path.Combine(outdir, "segs_blk.bin"), block!); Console.WriteLine("block: " + Stat(old, nw, block!)); }
if (modes.Contains("local"))
{
    double a = Cpu();
    Segment[] s = DpAligner.Align(old, nw, lm, null);
    Console.WriteLine($"dp_local: {Stat(old, nw, s)} dp_cpu {Cpu() - a:F2}");
    Save(Path.Combine(outdir, "segs_dp.bin"), s);
}
if (modes.Contains("both"))
{
    double a = Cpu();
    Segment[] s = DpAligner.Align(old, nw, lm, block);
    Console.WriteLine($"dp_both: {Stat(old, nw, s)} dp_cpu {Cpu() - a:F2}");
    Save(Path.Combine(outdir, "segs_dpb.bin"), s);
}
if (modes.Contains("blockdp"))
{
    double a = Cpu();
    Segment[] s = DpAligner.Align(old, nw, null, block);
    Console.WriteLine($"dp_blockonly: {Stat(old, nw, s)} dp_cpu {Cpu() - a:F2}");
    Save(Path.Combine(outdir, "segs_dpo.bin"), s);
}
Console.WriteLine($"peak_rss_mb {proc.PeakWorkingSet64 / 1048576}");
