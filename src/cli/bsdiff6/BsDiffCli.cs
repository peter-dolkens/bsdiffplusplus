using System.Globalization;
using System.Text.Json;
using BsDiff6.Format;

namespace BsDiff6.Cli;

/// <summary>The bsdiff6 command line: <c>bsdiff6 [options] oldfile newfile patchfile</c>.</summary>
public static class BsDiffCli
{
    private const string Tool = "bsdiff6";

    private const string HelpText =
@"usage: bsdiff6 [options] oldfile newfile patchfile

Builds a bsdiff 6 patch that turns oldfile into newfile.

options:
  --block-only       block alignment only (no local alignment: far less memory,
                     larger patches)
  --no-block         local alignment only (no block alignment)
  --no-prune         DP: keep states that cannot beat a fresh switch (the
                     thesis's exact candidate handling; slower, patches ~0.2%
                     smaller). By default they are dropped; --prune is accepted
                     and changes nothing
  --dp-costs M,U,S   DP costs: mismatched byte, unmatched byte, switch (positive
                     integers; default 2,1,14, tuned on this
                     project's corpus; the thesis's values are 2,1,20, and
                     --dp-costs 2,1,20 --no-prune reproduces its DP exactly)
  --min-match N      DP: ignore longest matches shorter than N bytes (default 1)
  --no-parallel      use one thread (the patch is the same either way)
  --seed N           seed of the block-alignment random choices (decimal or 0x hex)
  -v, --verbose      print timings, alignment statistics and a size breakdown
                     to stderr
  --stats-json FILE  write the same statistics as JSON to FILE
  -h, --help         show this help
  --version          show the version

exit status: 0 success, 1 failure, 2 usage error";

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr) =>
        CliSupport.Guard(Tool, stderr, () => RunCore(args, stdout, stderr));

    private sealed class Args
    {
        public bool BlockOnly, NoBlock, Prune = true, NoParallel, Verbose, Help, Version;
        public int[]? DpCosts;
        public int? MinMatch;
        public ulong? Seed;
        public string? StatsJson;
        public readonly List<string> Files = new List<string>();
    }

    private static Args Parse(string[] argv)
    {
        var a = new Args();
        bool optionsDone = false;
        for (int i = 0; i < argv.Length; i++)
        {
            string s = argv[i];
            if (optionsDone || s == "-" || !s.StartsWith("-", StringComparison.Ordinal)) { a.Files.Add(s); continue; }
            string NextValue()
            {
                if (i + 1 >= argv.Length) throw new UsageException($"option {s} needs a value");
                return argv[++i];
            }
            switch (s)
            {
                case "--": optionsDone = true; break;
                case "--block-only": a.BlockOnly = true; break;
                case "--no-block": a.NoBlock = true; break;
                case "--prune": a.Prune = true; break;
                case "--no-prune": a.Prune = false; break;
                case "--no-parallel": a.NoParallel = true; break;
                case "-v": case "--verbose": a.Verbose = true; break;
                case "-h": case "--help": a.Help = true; break;
                case "--version": a.Version = true; break;
                case "--stats-json": a.StatsJson = NextValue(); break;
                case "--dp-costs":
                {
                    string v = NextValue();
                    string[] parts = v.Split(',');
                    var costs = new int[parts.Length];
                    bool ok = parts.Length == 3;
                    for (int k = 0; ok && k < 3; k++)
                        ok = int.TryParse(parts[k], NumberStyles.None, CultureInfo.InvariantCulture, out costs[k]) && costs[k] >= 1
                             && costs[k] <= 1_000_000;
                    if (!ok) throw new UsageException($"--dp-costs needs three positive integers MISMATCH,UNMATCHED,SWITCH (at most 1000000), not '{v}'");
                    a.DpCosts = costs;
                    break;
                }
                case "--min-match":
                {
                    string v = NextValue();
                    if (!int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out int mm) || mm < 1)
                        throw new UsageException($"--min-match needs a positive integer, not '{v}'");
                    a.MinMatch = mm;
                    break;
                }
                case "--seed":
                {
                    string v = NextValue();
                    bool ok = v.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                        ? ulong.TryParse(v.Substring(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong seed)
                        : ulong.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out seed);
                    if (!ok) throw new UsageException($"--seed needs a non-negative integer, not '{v}'");
                    a.Seed = seed;
                    break;
                }
                default: throw new UsageException($"unknown option '{s}'");
            }
        }
        return a;
    }

    private static int RunCore(string[] argv, TextWriter stdout, TextWriter stderr)
    {
        Args a = Parse(argv);
        if (a.Help) { stdout.WriteLine(HelpText); return CliSupport.Success; }
        if (a.Version) { stdout.WriteLine($"{Tool} {CliSupport.Version} (patch format version 1)"); return CliSupport.Success; }
        if (a.Files.Count != 3) throw new UsageException(a.Files.Count < 3 ? "missing operand" : "too many operands");
        if (a.BlockOnly && a.NoBlock) throw new UsageException("--block-only and --no-block exclude each other");

        var options = new BsDiffOptions
        {
            UseBlockAlignment = !a.NoBlock,
            UseLocalAlignment = !a.BlockOnly,
            Parallel = !a.NoParallel,
        };
        options.Dp.PruneHopeless = a.Prune;
        if (a.MinMatch.HasValue) options.Dp.MinLocalMatchLength = a.MinMatch.Value;
        if (a.DpCosts != null)
        {
            options.Dp.MismatchCost = a.DpCosts[0];
            options.Dp.UnmatchedCost = a.DpCosts[1];
            options.Dp.SwitchCost = a.DpCosts[2];
        }
        if (a.Seed.HasValue) options.Block.Seed = a.Seed.Value;

        string oldPath = a.Files[0], newPath = a.Files[1], patchPath = a.Files[2];
        var readWatch = System.Diagnostics.Stopwatch.StartNew();
        byte[] old = CliSupport.ReadInput(oldPath, "old file");
        byte[] @new = CliSupport.ReadInput(newPath, "new file");
        TimeSpan readTime = readWatch.Elapsed;

        BsDiffResult r = BsDiff.CreateDetailed(old, @new, options);

        var writeWatch = System.Diagnostics.Stopwatch.StartNew();
        CliSupport.WriteAtomically(patchPath, r.Patch);
        TimeSpan writeTime = writeWatch.Elapsed;

        if (a.Verbose || a.StatsJson != null)
        {
            Dictionary<string, object?> stats = BuildStats(r, options, a, old.Length, @new.Length, oldPath, newPath, patchPath, readTime, writeTime);
            if (a.Verbose) WriteVerbose(stderr, r, options, old.Length, @new.Length, readTime, writeTime);
            if (a.StatsJson != null)
            {
                byte[] json = JsonSerializer.SerializeToUtf8Bytes(stats, new JsonSerializerOptions { WriteIndented = true });
                CliSupport.WriteAtomically(a.StatsJson, json);
            }
        }
        return CliSupport.Success;
    }

    // ---- statistics ---------------------------------------------------------------------------

    private static double Ms(TimeSpan t) => Math.Round(t.TotalMilliseconds, 1);

    private static Dictionary<string, object?> CodecSizes(long[] sizes) => new Dictionary<string, object?>
    {
        ["none"] = sizes.Length > 0 ? sizes[0] : -1,
        ["zlib"] = sizes.Length > 1 ? sizes[1] : -1,
        ["bzip2"] = sizes.Length > 2 ? sizes[2] : -1,
    };

    private static Dictionary<string, object?> BuildStats(BsDiffResult r, BsDiffOptions o, Args a, int oldLen, int newLen,
        string oldPath, string newPath, string patchPath, TimeSpan read, TimeSpan write)
    {
        PatchInfo h = r.Encode.Info;
        var t = r.Timings;
        var stats = new Dictionary<string, object?>
        {
            ["tool"] = Tool,
            ["version"] = CliSupport.Version,
            ["old"] = new Dictionary<string, object?> { ["path"] = oldPath, ["size"] = oldLen },
            ["new"] = new Dictionary<string, object?> { ["path"] = newPath, ["size"] = newLen },
            ["patch"] = new Dictionary<string, object?> { ["path"] = patchPath, ["size"] = r.Patch.Length },
            ["options"] = new Dictionary<string, object?>
            {
                ["block_alignment"] = o.UseBlockAlignment,
                ["local_alignment"] = o.UseLocalAlignment,
                ["prune"] = o.Dp.PruneHopeless,
                ["min_match"] = o.Dp.MinLocalMatchLength,
                ["dp_costs"] = new Dictionary<string, object?>
                {
                    ["mismatch"] = o.Dp.MismatchCost, ["unmatched"] = o.Dp.UnmatchedCost, ["switch"] = o.Dp.SwitchCost,
                },
                ["parallel"] = o.Parallel,
                ["seed"] = o.Block.Seed,
            },
            ["timings_ms"] = new Dictionary<string, object?>
            {
                ["read"] = Ms(read),
                ["block_wall"] = Ms(t.BlockWall),
                ["local_wall"] = Ms(t.LocalWall),
                ["align_wall"] = Ms(t.AlignWall),
                ["align_cpu"] = Ms(t.AlignCpu),
                ["dp_wall"] = Ms(t.DpWall),
                ["dp_cpu"] = Ms(t.DpCpu),
                ["encode_wall"] = Ms(t.EncodeWall),
                ["encode_cpu"] = Ms(t.EncodeCpu),
                ["total_wall"] = Ms(t.TotalWall),
                ["total_cpu"] = Ms(t.TotalCpu),
                ["write"] = Ms(write),
            },
            ["used_block_alignment"] = r.UsedBlockAlignment,
            ["used_local_alignment"] = r.UsedLocalAlignment,
            ["local_fell_back"] = r.LocalAlignmentFellBack,
        };
        if (r.BlockStats != null)
        {
            var b = r.BlockStats;
            var bd = new Dictionary<string, object?>
            {
                ["skipped"] = b.Skipped,
                ["block_length"] = b.BlockLength,
                ["p1"] = b.P1,
                ["p2"] = b.P2,
                ["fft_size"] = b.FftSize,
                ["blocks"] = b.Blocks,
                ["queried_blocks"] = b.QueriedBlocks,
                ["predicted_blocks"] = b.PredictedBlocks,
                ["continuity_chosen"] = b.ContinuityChosen,
                ["runs_before_tweak"] = b.RunsBeforeTweak,
                ["boundaries_moved"] = b.BoundariesMoved,
                ["runs_removed"] = b.RunsRemoved,
                ["index_ms"] = Ms(b.IndexTime),
                ["query_ms"] = Ms(b.QueryTime),
                ["tweak_ms"] = Ms(b.TweakTime),
            };
            if (r.BlockCoverage.HasValue)
            {
                bd["covered_fraction"] = r.BlockCoverage.Value.CoveredFraction;
                bd["match_fraction"] = r.BlockCoverage.Value.MatchFraction;
            }
            stats["block"] = bd;
        }
        if (r.Dp != null)
        {
            stats["dp"] = new Dictionary<string, object?>
            {
                ["cost"] = r.Dp.Cost,
                ["peak_states"] = r.Dp.PeakStates,
                ["avg_states"] = newLen == 0 ? 0.0 : Math.Round((double)r.Dp.TotalStates / newLen, 3),
                ["peak_live_records"] = r.Dp.PeakLiveRecords,
                ["final_live_records"] = r.Dp.FinalLiveRecords,
                ["record_pool_capacity"] = r.Dp.RecordPoolCapacity,
            };
        }
        var c = r.Coverage;
        stats["alignment"] = new Dictionary<string, object?>
        {
            ["segments"] = r.Segments,
            ["matched_segments"] = r.MatchedSegments,
            ["control_records"] = r.Encode.ControlRecords,
            ["new_bytes"] = c.NewLength,
            ["matched_bytes"] = c.CoveredBytes,
            ["equal_bytes"] = c.MatchingBytes,
            ["matched_fraction"] = c.CoveredFraction,
            ["differing_fraction_of_matched"] = c.CoveredBytes == 0 ? 0.0 : 1.0 - c.MatchFraction,
        };
        stats["sections"] = new Dictionary<string, object?>
        {
            ["header"] = h.HeaderLength,
            ["diff_mode"] = CliSupport.ModeName(h.DiffMode),
            ["control"] = new Dictionary<string, object?>
            {
                ["raw"] = h.ControlLength, ["compressed"] = h.ControlCompressedLength,
                ["codec"] = CliSupport.CodecName(h.ControlCodec), ["by_codec"] = CodecSizes(r.Encode.ControlBytesByCodec),
            },
            ["nonzero"] = new Dictionary<string, object?>
            {
                ["raw"] = h.NonZeroLength, ["compressed"] = h.NonZeroCompressedLength,
                ["codec"] = CliSupport.CodecName(h.NonZeroCodec),
            },
            ["map"] = new Dictionary<string, object?>
            {
                ["raw"] = r.Encode.DifferenceLength, ["ones"] = h.NonZeroLength,
                ["compressed"] = h.MapCompressedLength, ["model"] = h.MapModel,
            },
            ["extra"] = new Dictionary<string, object?>
            {
                ["raw"] = h.ExtraLength, ["compressed"] = h.ExtraCompressedLength,
                ["codec"] = CliSupport.CodecName(h.ExtraCodec), ["by_codec"] = CodecSizes(r.Encode.ExtraBytesByCodec),
            },
            ["total"] = h.TotalLength,
        };
        stats["diff_mode_candidates"] = r.Encode.Candidates.Select(m => new Dictionary<string, object?>
        {
            ["mode"] = CliSupport.ModeName(m.Mode),
            ["ones"] = m.Ones,
            ["map"] = m.MapBytesByModel.Length > 0 ? m.MapBytesByModel.Where(x => x >= 0).DefaultIfEmpty(-1).Min() : -1,
            ["nonzero_by_codec"] = CodecSizes(m.NonZeroBytesByCodec),
            ["nonzero_codec"] = CliSupport.CodecName(m.BestCodec),
            ["total"] = m.Total,
            ["chosen"] = m.Mode == h.DiffMode,
        }).ToList();
        return stats;
    }

    private static void WriteVerbose(TextWriter e, BsDiffResult r, BsDiffOptions o, int oldLen, int newLen, TimeSpan read, TimeSpan write)
    {
        var inv = CultureInfo.InvariantCulture;
        string N(long v) => v.ToString("N0", inv);
        string P(double f) => (100 * f).ToString("F2", inv) + "%";
        string T(TimeSpan w) => w.TotalSeconds.ToString("F3", inv) + "s";
        PatchInfo h = r.Encode.Info;
        var t = r.Timings;

        e.WriteLine($"old {N(oldLen)} bytes, new {N(newLen)} bytes, patch {N(r.Patch.Length)} bytes" +
                    (newLen > 0 ? $" ({P((double)r.Patch.Length / newLen)} of new)" : ""));
        string mode = r.UsedBlockAlignment && r.UsedLocalAlignment ? "block + local"
            : r.UsedBlockAlignment ? "block only" : r.UsedLocalAlignment ? "local only" : "none (all unmatched)";
        e.WriteLine($"alignment: {mode}{(r.LocalAlignmentFellBack ? " (local alignment fell back: inputs too large)" : "")}; " +
                    $"DP prune={o.Dp.PruneHopeless} min-match={o.Dp.MinLocalMatchLength} " +
                    $"costs={o.Dp.MismatchCost},{o.Dp.UnmatchedCost},{o.Dp.SwitchCost}; parallel={o.Parallel}");
        e.WriteLine("timings (wall / process CPU):");
        e.WriteLine($"  read      {T(read)}");
        e.WriteLine($"  align     {T(t.AlignWall)} / {T(t.AlignCpu)}   (block {T(t.BlockWall)}, local {T(t.LocalWall)})");
        e.WriteLine($"  dp        {T(t.DpWall)} / {T(t.DpCpu)}");
        e.WriteLine($"  encode    {T(t.EncodeWall)} / {T(t.EncodeCpu)}");
        e.WriteLine($"  total     {T(t.TotalWall)} / {T(t.TotalCpu)}");
        e.WriteLine($"  write     {T(write)}");
        if (r.BlockStats != null)
        {
            e.WriteLine("block alignment: " + r.BlockStats);
            if (r.BlockCoverage.HasValue)
                e.WriteLine($"  covers {P(r.BlockCoverage.Value.CoveredFraction)} of new, {P(r.BlockCoverage.Value.MatchFraction)} of covered bytes equal");
        }
        if (r.Dp != null)
            e.WriteLine($"DP: cost {N(r.Dp.Cost)}, states peak {r.Dp.PeakStates} avg {(newLen == 0 ? 0 : (double)r.Dp.TotalStates / newLen).ToString("F1", inv)}, " +
                        $"live records peak {N(r.Dp.PeakLiveRecords)} final {N(r.Dp.FinalLiveRecords)}");
        var c = r.Coverage;
        e.WriteLine($"result: {N(r.Segments)} segments ({N(r.MatchedSegments)} matched, {N(r.Encode.ControlRecords)} control records); " +
                    $"{P(c.CoveredFraction)} of new matched; {P(c.CoveredBytes == 0 ? 0 : 1 - c.MatchFraction)} of matched bytes differ");
        e.WriteLine($"sections (difference mode {CliSupport.ModeName(h.DiffMode)}):");
        e.WriteLine($"  {"section",-9} {"raw",14} {"stored",12}  codec   alternatives (none / zlib / bzip2)");
        e.WriteLine($"  {"header",-9} {"",14} {N(h.HeaderLength),12}");
        string Alt(long[] s) => string.Join(" / ", s.Select(x => x < 0 ? "-" : N(x)));
        e.WriteLine($"  {"control",-9} {N(h.ControlLength),14} {N(h.ControlCompressedLength),12}  {CliSupport.CodecName(h.ControlCodec),-6}  {Alt(r.Encode.ControlBytesByCodec)}");
        DiffModeCandidate? chosen = r.Encode.Candidates.FirstOrDefault(m => m.Mode == h.DiffMode);
        e.WriteLine($"  {"non-zero",-9} {N(h.NonZeroLength),14} {N(h.NonZeroCompressedLength),12}  {CliSupport.CodecName(h.NonZeroCodec),-6}  {(chosen == null ? "" : Alt(chosen.NonZeroBytesByCodec))}");
        e.WriteLine($"  {"map",-9} {N(r.Encode.DifferenceLength),14} {N(h.MapCompressedLength),12}  model {h.MapModel}");
        e.WriteLine($"  {"extra",-9} {N(h.ExtraLength),14} {N(h.ExtraCompressedLength),12}  {CliSupport.CodecName(h.ExtraCodec),-6}  {Alt(r.Encode.ExtraBytesByCodec)}");
        e.WriteLine($"  {"total",-9} {"",14} {N(h.TotalLength),12}");
        e.WriteLine("difference modes (map + non-zero):");
        foreach (DiffModeCandidate m in r.Encode.Candidates)
        {
            long map = m.MapBytesByModel.Where(x => x >= 0).DefaultIfEmpty(-1).Min();
            e.WriteLine($"  {(m.Mode == h.DiffMode ? "*" : " ")} {CliSupport.ModeName(m.Mode),-14} K={N(m.Ones),-12} map {N(map),10}  " +
                        $"non-zero {Alt(m.NonZeroBytesByCodec)} ({CliSupport.CodecName(m.BestCodec)})  total {N(m.Total)}");
        }
    }
}
