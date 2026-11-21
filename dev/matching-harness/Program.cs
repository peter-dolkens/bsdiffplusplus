using System.Diagnostics;
using BsDiff6;
using BsDiff6.Matching;
using BsDiff6.Tests.Matching;

// Usage:
//   locate <random|binary|file:PATH> <sizeBytes> <mismatch> <trials> [key=value options]
//   align <oldFile> <newFile> [key=value options]
//   synth <sizeMB> [mismatch] [key=value options]      synthetic shuffled-regions pair, timing + quality
//   corpus <dir> [key=value options]                     runs align on every <dir>/<name>/{old,new}
static class Program
{
    static int Main(string[] args)
    {
        if (args.Length == 0) { Console.WriteLine("see source for usage"); return 1; }
        switch (args[0])
        {
            case "locate": Locate(args); break;
            case "align": AlignFiles(args[1], args[2], ParseOptions(args, 3)); break;
            case "synth": Synth(args); break;
            case "qbench":
            {
                byte[] old = File.ReadAllBytes(args[1]);
                int b = BlockAlignOptions.DefaultBlockLength(old.Length);
                var ix = new MismatchIndex(old, b, ByteWeights.InverseSqrtFrequency(old, default));
                var q = ix.CreateQuery();
                var cands = new MismatchCandidate[8];
                byte[] block = old.AsSpan(old.Length / 2, b).ToArray();
                for (int w = 0; w < 50; w++) q.Find(block, cands);
                double best1 = double.MaxValue, best2 = double.MaxValue, best3 = double.MaxValue;
                for (int trial = 0; trial < 20; trial++)
                {
                    var sw = Stopwatch.StartNew();
                    for (int r = 0; r < 10; r++) q.ComputeCorrelations(block);
                    best1 = Math.Min(best1, sw.Elapsed.TotalMilliseconds / 10);
                    sw.Restart();
                    for (int r = 0; r < 10; r++) q.RankCandidates(cands);
                    best2 = Math.Min(best2, sw.Elapsed.TotalMilliseconds / 10);
                    sw.Restart();
                    for (int r = 0; r < 10; r++) ix.Fft.ForwardScrambled(new float[ix.FftSize], new float[ix.FftSize]);
                    best3 = Math.Min(best3, sw.Elapsed.TotalMilliseconds / 10);
                }
#if MATCHING_PROFILE
                // Needs the library built with MATCHING_PROFILE (dotnet build -p:DefineConstants=MATCHING_PROFILE).
                Profile.Pack = Profile.Fft = Profile.Mul = Profile.Combine = 0;
                for (int r = 0; r < 100; r++) q.ComputeCorrelations(block);
                Console.WriteLine("100 queries: " + Profile.Report());
#endif
                Console.WriteLine($"N={ix.FftSize} correlations {best1:F3} ms, ranking {best2:F3} ms, one FFT(+alloc) {best3:F3} ms");
                break;
            }
            case "oracle": Oracle(args[1], args[2], ParseOptions(args, 3)); break;
            case "ibench":
            {
                byte[] old = TestData.BinaryLike((int)(double.Parse(args[1]) * 1_000_000), 5);
                int b = BlockAlignOptions.DefaultBlockLength(old.Length);
                for (int rep = 0; rep < 3; rep++)
                {
                    var proc = Process.GetCurrentProcess();
                    TimeSpan c0 = proc.TotalProcessorTime; var sw = Stopwatch.StartNew();
                    float[] w = ByteWeights.InverseSqrtFrequency(old, default);
                    proc.Refresh(); TimeSpan c1 = proc.TotalProcessorTime; double t1 = sw.Elapsed.TotalMilliseconds;
                    var ix = new MismatchIndex(old, b, w);
                    proc.Refresh(); TimeSpan c2 = proc.TotalProcessorTime;
                    Console.WriteLine($"weights cpu {(c1 - c0).TotalMilliseconds:F0}ms wall {t1:F0}ms; index cpu {(c2 - c1).TotalMilliseconds:F0}ms wall {sw.Elapsed.TotalMilliseconds - t1:F0}ms N={ix.FftSize}");
                }
                break;
            }
            case "fftbench":
            {
                int n = int.Parse(args[1]);
                var fft = new Fft(n);
                var re = new float[n]; var im = new float[n];
                for (int i = 0; i < n; i++) re[i] = i % 7;
                for (int w = 0; w < 20; w++) fft.ForwardScrambled(re, im);
                double bestMs = double.MaxValue;
                for (int trial = 0; trial < 30; trial++)
                {
                    var sw = Stopwatch.StartNew();
                    int reps = 10;
                    for (int r = 0; r < reps; r++) { fft.ForwardScrambled(re, im); fft.InverseFromScrambled(re, im); }
                    bestMs = Math.Min(bestMs, sw.Elapsed.TotalMilliseconds / (2 * reps));
                }
                Console.WriteLine($"N={n}: {bestMs:F3} ms per transform (best of 30), Vector<float>.Count={System.Numerics.Vector<float>.Count}, accel={System.Numerics.Vector.IsHardwareAccelerated}");
                break;
            }
            case "corpus":
                foreach (string dir in Directory.GetDirectories(args[1]).OrderBy(d => d))
                {
                    if (!File.Exists(Path.Combine(dir, "old")) || !File.Exists(Path.Combine(dir, "new"))) continue;
                    Console.Write($"{Path.GetFileName(dir),-28} ");
                    AlignFiles(Path.Combine(dir, "old"), Path.Combine(dir, "new"), ParseOptions(args, 2));
                }
                break;
            default: Console.WriteLine("unknown command"); return 1;
        }
        return 0;
    }

    static BlockAlignOptions ParseOptions(string[] args, int from)
    {
        var o = new BlockAlignOptions();
        for (int i = from; i < args.Length; i++)
        {
            string[] kv = args[i].Split('=', 2);
            if (kv.Length != 2) continue;
            var prop = typeof(BlockAlignOptions).GetProperty(kv[0]) ?? throw new ArgumentException("no option " + kv[0]);
            object v = Convert.ChangeType(kv[1], prop.PropertyType, System.Globalization.CultureInfo.InvariantCulture);
            prop.SetValue(o, v);
        }
        return o;
    }

    static byte[] Source(string kind, int size)
    {
        if (kind == "random") return TestData.Random(size, 1);
        if (kind == "binary") return TestData.BinaryLike(size, 1);
        if (kind.StartsWith("file:"))
        {
            byte[] f = File.ReadAllBytes(kind.Substring(5));
            return size > 0 && size < f.Length ? f.AsSpan(0, size).ToArray() : f;
        }
        throw new ArgumentException(kind);
    }

    static void Locate(string[] args)
    {
        byte[] old = Source(args[1], int.Parse(args[2]));
        double mismatch = double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
        bool fromData = mismatch < 0; // negative: substitutes drawn from the file's own bytes
        mismatch = Math.Abs(mismatch);
        int trials = int.Parse(args[4]);
        BlockAlignOptions opt = ParseOptions(args, 5);
        int b = opt.BlockLength > 0 ? opt.BlockLength : BlockAlignOptions.DefaultBlockLength(old.Length);
        float[] w = opt.FrequencyWeighting ? ByteWeights.InverseSqrtFrequency(old, default, opt.WeightFrequencyFloor) : ByteWeights.Uniform();
        var ix = new MismatchIndex(old, b, w, opt);
        Console.WriteLine($"n={old.Length} B={b} p=({ix.P1},{ix.P2}) N={ix.FftSize}");
        MismatchQuery q = ix.CreateQuery();
        var rng = new Xoshiro256(12345);
        var block = new byte[b];
        int hits = 0, asGood = 0, in1 = 0, in2 = 0, inVerify = 0;
        var zs1 = new List<double>(); var zs2 = new List<double>();
        var cands = new MismatchCandidate[opt.TopK * opt.TopK];
        for (int t = 0; t < trials; t++)
        {
            int x = rng.NextInt(old.Length - b);
            old.AsSpan(x, b).CopyTo(block);
            if (fromData)
            {
                int k = (int)Math.Round(mismatch * b), done = 0;
                var used = new HashSet<int>();
                while (done < k)
                {
                    int p = rng.NextInt(b);
                    if (used.Contains(p)) continue;
                    byte v = old[rng.NextInt(old.Length)];
                    if (v == block[p]) continue;
                    block[p] = v; used.Add(p); done++;
                }
            }
            else TestData.Substitute(block, mismatch, rng);
            int trueMatches = MatchCounter.Count(old, x, block);
            int count = q.Find(block, cands);
            int best = -1, bestPos = 0;
            for (int i = 0; i < Math.Min(count, opt.VerifyCount); i++)
            {
                int m = MatchCounter.Count(old, cands[i].OldPos, block);
                if (m > best) { best = m; bestPos = cands[i].OldPos; }
            }
            if (bestPos == x) hits++;
            if (best >= trueMatches) asGood++;
            int r1 = Rank(q.Correlation(0), x % ix.P1, out double z1);
            int r2 = Rank(q.Correlation(1), x % ix.P2, out double z2);
            zs1.Add(z1); zs2.Add(z2);
            if (r1 < opt.TopK) in1++;
            if (r2 < opt.TopK) in2++;
            for (int i = 0; i < Math.Min(count, opt.VerifyCount); i++) if (cands[i].OldPos == x) { inVerify++; break; }
        }
        Console.WriteLine($"mismatch {mismatch:P0}: located {hits}/{trials}, as good {asGood}/{trials}; " +
                          $"true j in top{opt.TopK}: p1 {in1}, p2 {in2}; true x in verify set {inVerify}; " +
                          $"median z {Median(zs1):F2} {Median(zs2):F2}");
    }

    static int Rank(ReadOnlySpan<float> c, int j, out double z)
    {
        double s = 0, s2 = 0;
        int above = 0;
        for (int i = 0; i < c.Length; i++) { s += c[i]; s2 += (double)c[i] * c[i]; if (c[i] > c[j]) above++; }
        double mean = s / c.Length, sd = Math.Sqrt(s2 / c.Length - mean * mean);
        z = (c[j] - mean) / sd;
        return above;
    }

    static double Median(List<double> v) { v.Sort(); return v[v.Count / 2]; }

    static void AlignFiles(string oldPath, string newPath, BlockAlignOptions opt)
    {
        byte[] old = File.ReadAllBytes(oldPath), nw = File.ReadAllBytes(newPath);
        var sw = Stopwatch.StartNew();
        TimeSpan cpu0 = Process.GetCurrentProcess().TotalProcessorTime;
        Segment[] segs = BlockAligner.Align(old, nw, opt, out BlockAlignStats st);
        double ms = sw.Elapsed.TotalMilliseconds;
        double cpuMs = (Process.GetCurrentProcess().TotalProcessorTime - cpu0).TotalMilliseconds;
        AlignmentUtil.Validate(segs, old.Length, nw.Length);
        var cov = AlignmentCoverage.Measure(old, nw, segs);
        Console.WriteLine($"n={old.Length} m={nw.Length} total={ms:F0}ms cpu={cpuMs:F0}ms covered={cov.CoveredFraction:P1} matching={cov.MatchFraction:P1} " +
                          $"(matched bytes {(double)cov.MatchingBytes / Math.Max(1, nw.Length):P1} of new) segs={segs.Length} | {st}");
    }

    /// <summary>
    /// Per block: best verified offset from the FFT index (top VerifyCount, as BlockAligner does
    /// before continuity) versus an offset found by exact 12-gram voting, verified the same way.
    /// Tells whether the index misses alignments that exist.
    /// </summary>
    static void Oracle(string oldPath, string newPath, BlockAlignOptions opt)
    {
        byte[] old = File.ReadAllBytes(oldPath), nw = File.ReadAllBytes(newPath);
        int b = opt.BlockLength > 0 ? opt.BlockLength : BlockAlignOptions.DefaultBlockLength(old.Length);
        const int K = 12;
        // Sorted (hash, pos) of all old K-grams.
        int cnt = Math.Max(0, old.Length - K + 1);
        var keys = new ulong[cnt]; var pos = new int[cnt];
        for (int i = 0; i < cnt; i++) { keys[i] = Hash(old, i, K); pos[i] = i; }
        Array.Sort(keys, pos);
        float[] w = opt.FrequencyWeighting ? ByteWeights.InverseSqrtFrequency(old, nw, opt.WeightFrequencyFloor) : ByteWeights.Uniform();
        var ix = new MismatchIndex(old, b, w, opt);
        var q = ix.CreateQuery();
        var cands = new MismatchCandidate[opt.VerifyCount];
        int blocks = (nw.Length + b - 1) / b;
        long fftTotal = 0, oracleTotal = 0, both25 = 0, fftOnly25 = 0, oracleOnly25 = 0, sameOffset = 0;
        var votes = new Dictionary<long, int>();
        var missed = new List<(int r1, int r2, double z1, double z2, double frac)>();
        for (int bi = 0; bi < blocks; bi++)
        {
            int t0 = bi * b, len = Math.Min(b, nw.Length - t0);
            var block = nw.AsSpan(t0, len);
            int c = q.Find(block, cands);
            int fBest = 0; long fx = long.MinValue;
            for (int i = 0; i < c; i++) { int m = MatchCounter.Count(old, cands[i].OldPos, block); if (m > fBest) { fBest = m; fx = cands[i].OldPos; } }
            votes.Clear();
            for (int r = 0; r + K <= len; r++)
            {
                ulong h = Hash(nw, t0 + r, K);
                int lo = LowerBound(keys, h);
                int hi = lo; while (hi < keys.Length && keys[hi] == h) hi++;
                if (hi - lo > 8) continue; // too common
                for (int k = lo; k < hi; k++) { long x = pos[k] - r; votes[x] = votes.TryGetValue(x, out int v) ? v + 1 : 1; }
            }
            int oBest = 0; long ox = long.MinValue;
            foreach (var kv in votes.OrderByDescending(kv => kv.Value).Take(16))
            {
                int m = MatchCounter.Count(old, kv.Key, block);
                if (m > oBest) { oBest = m; ox = kv.Key; }
            }
            if (fBest > oBest) { oBest = fBest; ox = fx; }
            fftTotal += fBest; oracleTotal += oBest;
            bool f25 = fBest >= 0.25 * len, o25 = oBest >= 0.25 * len;
            if (f25 && o25) both25++; else if (f25) fftOnly25++; else if (o25) oracleOnly25++;
            if (fx == ox) sameOffset++;
            if (o25 && !f25 && ox != long.MinValue)
            {
                int j1 = (int)(((ox % ix.P1) + ix.P1) % ix.P1), j2 = (int)(((ox % ix.P2) + ix.P2) % ix.P2);
                int r1 = Rank(q.Correlation(0), j1, out double z1), r2 = Rank(q.Correlation(1), j2, out double z2);
                missed.Add((r1, r2, z1, z2, (double)oBest / len));
                if (missed.Count <= 6)
                {
                    // Direct signal for prime 0: Σ_maps Σ_r φ(S[x+r]) φ(T[r]) over the overlap.
                    double sig = 0; int zerosMatched = 0, matched = 0, zerosBlock = 0;
                    for (int r = 0; r < len; r++)
                    {
                        if (block[r] == 0) zerosBlock++;
                        long op = ox + r;
                        if (op < 0 || op >= old.Length) continue;
                        for (int m = 0; m < opt.MapsPerPrime; m++) sig += ix.Phi(0, m)[old[op]] * ix.Phi(0, m)[block[r]];
                        if (old[op] == block[r]) { matched++; if (block[r] == 0) zerosMatched++; }
                    }
                    var c0 = q.Correlation(0);
                    double mean = 0, sd = 0; foreach (float v in c0) mean += v; mean /= c0.Length;
                    foreach (float v in c0) sd += (v - mean) * (v - mean); sd = Math.Sqrt(sd / c0.Length);
                    float max = float.MinValue; foreach (float v in c0) max = Math.Max(max, v);
                    Console.WriteLine($"    block {bi}: match {(double)oBest / len:P0}, zeros in block {(double)zerosBlock / len:P0}, zeros among matched {(double)zerosMatched / Math.Max(1, matched):P0}; " +
                                      $"direct signal {sig:F0}, C[j]={c0[j1]:F0}, mean {mean:F0}, sd {sd:F0}, max {max:F0}, theory sd {Math.Sqrt(opt.MapsPerPrime * len * (double)old.Length / ix.P1):F0}");
                }
            }
        }
        if (missed.Count > 0)
        {
            var ranks = missed.Select(m => Math.Max(m.r1, m.r2)).OrderBy(x => x).ToList();
            var zs = missed.Select(m => m.z1 + m.z2).OrderBy(x => x).ToList();
            var fr = missed.Select(m => m.frac).OrderBy(x => x).ToList();
            Console.WriteLine($"  missed: n={missed.Count} max-rank quartiles {ranks[ranks.Count / 4]} {ranks[ranks.Count / 2]} {ranks[3 * ranks.Count / 4]}; " +
                              $"zsum quartiles {zs[zs.Count / 4]:F1} {zs[zs.Count / 2]:F1} {zs[3 * zs.Count / 4]:F1}; oracle match frac quartiles {fr[fr.Count / 4]:P0} {fr[fr.Count / 2]:P0} {fr[3 * fr.Count / 4]:P0}; " +
                              $"both ranks < 32: {missed.Count(m => m.r1 < 32 && m.r2 < 32)}, < 256: {missed.Count(m => m.r1 < 256 && m.r2 < 256)}");
        }
        Console.WriteLine($"blocks={blocks} B={b}: matched bytes fft={fftTotal} ({(double)fftTotal / nw.Length:P1}) oracle={oracleTotal} ({(double)oracleTotal / nw.Length:P1}); " +
                          $">=25%: both {both25}, fft only {fftOnly25}, oracle only {oracleOnly25}; same offset {sameOffset}");
    }

    static ulong Hash(byte[] d, int p, int k)
    {
        ulong h = 1469598103934665603UL;
        for (int i = 0; i < k; i++) { h ^= d[p + i]; h *= 1099511628211UL; }
        return h;
    }

    static int LowerBound(ulong[] a, ulong v)
    {
        int lo = 0, hi = a.Length;
        while (lo < hi) { int mid = (lo + hi) >> 1; if (a[mid] < v) lo = mid + 1; else hi = mid; }
        return lo;
    }

    /// <summary>
    /// Synthetic pair: new = shuffled copies of old regions (each with substitutions) separated by
    /// random insertions. Reports timing and how many copied bytes got their true offset.
    /// </summary>
    static void Synth(string[] args)
    {
        int size = (int)(double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) * 1_000_000);
        double mismatch = args.Length > 2 && !args[2].Contains('=') ? double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 0.3;
        BlockAlignOptions opt = ParseOptions(args, 2);
        var rng = new Xoshiro256(99);
        byte[] old = TestData.BinaryLike(size, 5);
        var nw = new List<byte>(size + size / 8);
        var truth = new List<(int newPos, int len, int offset)>();
        int regions = 40;
        var order = Enumerable.Range(0, regions).ToArray();
        for (int i = regions - 1; i > 0; i--) { int j = rng.NextInt(i + 1); (order[i], order[j]) = (order[j], order[i]); }
        int regionLen = size / regions;
        foreach (int r in order)
        {
            int ins = rng.NextInt(regionLen / 10);
            nw.AddRange(TestData.Random(ins, rng.NextUInt64()));
            int start = r * regionLen;
            byte[] chunk = old.AsSpan(start, regionLen).ToArray();
            TestData.Substitute(chunk, mismatch, rng);
            truth.Add((nw.Count, regionLen, start - nw.Count));
            nw.AddRange(chunk);
        }
        byte[] newArr = nw.ToArray();
        var sw = Stopwatch.StartNew();
        TimeSpan cpu0 = Process.GetCurrentProcess().TotalProcessorTime;
        Segment[] segs = BlockAligner.Align(old, newArr, opt, out BlockAlignStats st);
        double ms = sw.Elapsed.TotalMilliseconds;
        double cpuMs = (Process.GetCurrentProcess().TotalProcessorTime - cpu0).TotalMilliseconds;
        AlignmentUtil.Validate(segs, old.Length, newArr.Length);
        var cov = AlignmentCoverage.Measure(old, newArr, segs);
        // Fraction of copied bytes whose segment has the true offset.
        var offsetAt = new int[newArr.Length];
        Array.Fill(offsetAt, int.MinValue);
        foreach (var s in segs) if (s.IsMatched) for (int i = s.NewPos; i < s.NewEnd; i++) offsetAt[i] = s.Offset;
        long copied = 0, right = 0;
        foreach (var (p, len, off) in truth) { copied += len; for (int i = p; i < p + len; i++) if (offsetAt[i] == off) right++; }
        long insertedClaimed = 0, inserted = newArr.Length - copied;
        var isCopy = new bool[newArr.Length];
        foreach (var (p, len, _) in truth) for (int i = p; i < p + len; i++) isCopy[i] = true;
        for (int i = 0; i < newArr.Length; i++) if (!isCopy[i] && offsetAt[i] != int.MinValue) insertedClaimed++;
        Console.WriteLine($"n={old.Length} m={newArr.Length} mismatch={mismatch:P0} total={ms:F0}ms cpu={cpuMs:F0}ms right-offset={(double)right / copied:P2} " +
                          $"inserted-claimed={(double)insertedClaimed / Math.Max(1, inserted):P1} covered={cov.CoveredFraction:P1} matching={cov.MatchFraction:P1}");
        Console.WriteLine($"  {st}");
    }
}
