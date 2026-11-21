using System.Diagnostics;
using BsDiff6;
using BsDiff6.Alignment;

// Dev harness: timings and real-data runs for the Alignment area.
//   sais <zeros|random|runs|fib|ab|file:PATH> <MB>
//   local <old> <new>
//   dp <old> <new> [mutate-seed]        (if mutate-seed given, new is a mutated copy of <old>; <new> ignored)
//   corpus <dir>                        (runs local + DP on every <dir>/<name>/{old,new})

static byte[] Synthetic(string kind, long size)
{
    byte[] b = new byte[size];
    var rng = new Random(1);
    switch (kind)
    {
        case "zeros": break;
        case "random": rng.NextBytes(b); break;
        case "ab": for (long i = 0; i < size; i++) b[i] = (byte)(i & 1); break;
        case "runs":
            for (long i = 0; i < size;)
            {
                int len = rng.Next(1, 100_000);
                byte v = (byte)rng.Next(2);
                for (int j = 0; j < len && i < size; j++) b[i++] = v;
            }
            break;
        case "fib":
        {
            // Fibonacci word via the morphism a -> ab, b -> a, generated directly.
            double phi = (1 + Math.Sqrt(5)) / 2;
            for (long i = 0; i < size; i++)
                b[i] = (byte)(Math.Floor((i + 2) / phi) - Math.Floor((i + 1) / phi));
            break;
        }
        default:
            if (kind.StartsWith("file:"))
            {
                byte[] f = File.ReadAllBytes(kind.Substring(5));
                for (long i = 0; i < size; i += f.Length)
                    Array.Copy(f, 0, b, i, Math.Min(f.Length, size - i));
                // perturb each copy a little so it is not a pure repetition
                for (long i = f.Length; i < size; i += 997) b[i] ^= (byte)rng.Next(256);
            }
            else throw new ArgumentException(kind);
            break;
    }
    return b;
}

static byte[] Mutate(byte[] old, int seed)
{
    // Rough imitation of a rebuilt binary: blocks copied in order with occasional inserted and
    // deleted chunks, and within copied blocks some 4-byte "addresses" changed in their low bytes.
    var rng = new Random(seed);
    var res = new MemoryStream(old.Length + old.Length / 10);
    int pos = 0;
    while (pos < old.Length)
    {
        int len = Math.Min(old.Length - pos, rng.Next(500, 20_000));
        byte[] chunk = old.AsSpan(pos, len).ToArray();
        int addrRate = rng.Next(4) == 0 ? 8 : 200;
        for (int i = 0; i + 4 <= chunk.Length; i += 4)
            if (rng.Next(addrRate) == 0) { chunk[i] += (byte)rng.Next(1, 64); }
        res.Write(chunk);
        pos += len;
        int r = rng.Next(10);
        if (r == 0) { byte[] ins = new byte[rng.Next(1, 3000)]; rng.NextBytes(ins); res.Write(ins); }
        else if (r == 1) pos += rng.Next(1, 3000);
    }
    return res.ToArray();
}

static string Mb(long bytes) => $"{bytes / 1048576.0:F1} MB";

static void Report(string label, byte[] old, byte[] nw, Segment[] segs, DpDiagnostics d, double localSec, double dpSec)
{
    long matched = 0, differ = 0;
    foreach (Segment s in segs)
    {
        if (!s.IsMatched) continue;
        matched += s.Length;
        for (int i = 0; i < s.Length; i++) if (old[s.OldPos + i] != nw[s.NewPos + i]) differ++;
    }
    Console.WriteLine($"{label}: old {Mb(old.Length)}, new {Mb(nw.Length)}; local {localSec:F2}s, dp {dpSec:F2}s; " +
        $"segments {segs.Length}, matched {100.0 * matched / Math.Max(1, nw.Length):F2}% of new, " +
        $"differing {100.0 * differ / Math.Max(1, matched):F2}% of matched; cost {d.Cost}; " +
        $"states avg {(double)d.TotalStates / Math.Max(1, nw.Length):F1} peak {d.PeakStates}; " +
        $"records peak {d.PeakLiveRecords} final {d.FinalLiveRecords}");
}

static (Segment[] segs, DpDiagnostics d, double localSec, double dpSec) RunBoth(byte[] old, byte[] nw, DpOptions? opt = null)
{
    var proc = Process.GetCurrentProcess();
    proc.Refresh();
    double cpu0 = proc.TotalProcessorTime.TotalSeconds;
    var sw = Stopwatch.StartNew();
    LocalMatches lm = LocalAligner.Compute(old, nw);
    double localSec = sw.Elapsed.TotalSeconds;
    proc.Refresh();
    double cpu1 = proc.TotalProcessorTime.TotalSeconds;
    GC.Collect();
    proc.Refresh();
    double cpu2 = proc.TotalProcessorTime.TotalSeconds;
    sw.Restart();
    var d = new DpDiagnostics();
    long alloc0 = GC.GetAllocatedBytesForCurrentThread();
    if (Environment.GetEnvironmentVariable("DP_PRUNE") == "1") { opt ??= new DpOptions(); opt.PruneHopeless = true; }
    Segment[] segs = DpAligner.Align(old, nw, lm, null, opt, d);
    int.TryParse(Environment.GetEnvironmentVariable("DP_REPEAT"), out int rep);
    for (int k = 0; k < rep; k++)
    {
        proc.Refresh();
        double r0 = proc.TotalProcessorTime.TotalSeconds;
        DpAligner.Align(old, nw, lm, null, opt, d);
        proc.Refresh();
        Console.WriteLine($"  repeat dp cpu {proc.TotalProcessorTime.TotalSeconds - r0:F2}s");
    }
    double dpSec = sw.Elapsed.TotalSeconds;
    long alloc = GC.GetAllocatedBytesForCurrentThread() - alloc0;
    proc.Refresh();
    double cpu3 = proc.TotalProcessorTime.TotalSeconds;
    Console.WriteLine($"  local cpu {cpu1 - cpu0:F2}s, dp cpu {cpu3 - cpu2:F2}s; dp allocated {alloc / 1024.0:F0} KB");
    return (segs, d, localSec, dpSec);
}

string cmd = args.Length > 0 ? args[0] : "";
{
    // CPU-speed calibration: the machine this was developed on was heavily oversubscribed, so
    // wall times are meaningless; report CPU time of a fixed dependent loop to normalise against.
    var pr = Process.GetCurrentProcess();
    double c0 = pr.TotalProcessorTime.TotalSeconds;
    ulong x = 1;
    for (int i = 0; i < 200_000_000; i++) x = x * 6364136223846793005UL + 1442695040888963407UL;
    pr.Refresh();
    Console.WriteLine($"calibration: {(pr.TotalProcessorTime.TotalSeconds - c0) / 0.2:F2} ns CPU per dependent mul-add (~1.1 on an idle P-core) [{x & 1}]");
}
if (Environment.GetEnvironmentVariable("SAIS_KEEPCOUNTS") == "0") SaIs.KeepCounts = false;
switch (cmd)
{
    case "sais":
    {
        long size = (long)(double.Parse(args[2]) * 1048576);
        byte[] text = Synthetic(args[1], size);
        int[] sa = new int[text.Length];
        var sw = Stopwatch.StartNew();
        SaIs.Build(text, sa);
        Console.WriteLine($"sais {args[1]} {Mb(size)}: {sw.Elapsed.TotalSeconds:F2}s wall, {Process.GetCurrentProcess().UserProcessorTime.TotalSeconds:F2}s user CPU (incl. input generation)");
        if (args.Length > 3 && args[3] == "check")
        {
            int[] rank = new int[text.Length + 1];
            rank[text.Length] = -1;
            for (int i = 0; i < sa.Length; i++) rank[sa[i]] = i;
            for (int i = 1; i < sa.Length; i++)
            {
                int a = sa[i - 1], b = sa[i];
                if (text[a] > text[b] || (text[a] == text[b] && rank[a + 1] > rank[b + 1]))
                    throw new Exception($"bad order at {i}");
            }
            Console.WriteLine("  order verified");
        }
        break;
    }
    case "calib":
    {
        // CPU calibration: a dependent integer loop, ~1 ns per iteration on a modern core.
        var sw = Stopwatch.StartNew();
        ulong x = 1;
        for (int i = 0; i < 1_000_000_000; i++) x = x * 6364136223846793005UL + 1442695040888963407UL;
        Console.WriteLine($"calib: {sw.Elapsed.TotalSeconds:F2}s ({x & 1})");
        break;
    }
    case "local":
    {
        byte[] old = File.ReadAllBytes(args[1]), nw = File.ReadAllBytes(args[2]);
        var sw = Stopwatch.StartNew();
        LocalMatches lm = LocalAligner.Compute(old, nw);
        Console.WriteLine($"local {Mb(old.Length)} + {Mb(nw.Length)}: {sw.Elapsed.TotalSeconds:F2}s");
        break;
    }
    case "dp":
    {
        byte[] old = File.ReadAllBytes(args[1]);
        byte[] nw = args.Length > 3 ? Mutate(old, int.Parse(args[3])) : File.ReadAllBytes(args[2]);
        var (segs, d, ls, ds) = RunBoth(old, nw);
        Report(Path.GetFileName(args[1]), old, nw, segs, d, ls, ds);
        break;
    }
    case "corpus":
    {
        foreach (string dir in Directory.GetDirectories(args[1]).OrderBy(x => x))
        {
            string o = Path.Combine(dir, "old"), n = Path.Combine(dir, "new");
            if (!File.Exists(o) || !File.Exists(n)) continue;
            byte[] old = File.ReadAllBytes(o), nw = File.ReadAllBytes(n);
            var (segs, d, ls, ds) = RunBoth(old, nw);
            Report(Path.GetFileName(dir), old, nw, segs, d, ls, ds);
        }
        break;
    }
    default:
        Console.Error.WriteLine("usage: sais|local|dp|corpus ...");
        return 1;
}
return 0;
