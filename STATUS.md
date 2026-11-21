# bsdiff6 — working status (handoff file)

Updated at each check-in. If a session is lost, a new one can resume from this file
plus `DESIGN.md`. Last update: 2026-11-21 09:50. **The thesis-faithful build is complete and verified by Fable**: 308 tests pass; final benchmark `bench/results/20261121-094010.md` — 20/20 round trips, bsdiff6 smaller than bsdiff 4.3 on every pair, geometric mean 0.796x, sqrt-weighted avg 0.1318 vs 0.1518; `README.md` written. No agents running. The 15-minute check-in (cron 104e4d7d) was cancelled at 09:50 because nothing is in progress; re-create it when new work starts. Waiting on the user's decision about the research extensions (Brotli back end; conditional coder + address prediction — see `research/REPORT.md`).

## Standing instructions from the user

- Build the thesis algorithm ("bsdiff 6", `ref/thesis/`) in **C# 10** (`LangVersion` 10.0, net10.0).
- Build work is done by agents on **Opus 5.5 at medium effort, or cheaper**. Fable is used
  only for design, steering, review, and unsticking agents. Exception granted once: a
  Fable xhigh agent for the research investigation below.
- Generic, format-agnostic tool first. Container-aware "decompress then diff"
  (Star Citizen `.p4k`) is a later investigation — do not build it now.
- Do not commit to git unless asked (the project is not a git repository).

## Done

| Piece | Where | State |
|---|---|---|
| Design | `DESIGN.md` | Written; §2 layout, §8 top level and §9 updated at integration |
| Baseline bsdiff 4.3, corpus (20 pairs), benchmark | `bench/` | Done. `python3 bench/bench.py --tool NAME "DIFFCMD" "PATCHCMD"`. Baseline weighted avg patch/new: 0.152 overall, 0.198 upgrade, 0.048 patch |
| Patch format, writer, hardened reader | `src/BsDiff6/Format/`, tests in `tests/BsDiff6.Tests/Format/` | Done, reviewed. Integration: trailing bytes after a zlib/bzip2 stream now rejected (zlib read via SharpZipLib `Inflater`); encoder can code the difference modes in parallel (deterministic) |
| Block alignment (FFT matching with mismatches) | `src/BsDiff6/Matching/` | Done, reviewed |
| SA-IS, local alignment, pruned-graph DP | `src/BsDiff6/Alignment/` | Done, reviewed. `SuffixArray.Build` now calls `SaIs.Build` |
| Integration: library, CLIs, tests, solution | `BsDiff6.sln`; `src/BsDiff6/` (assembly `BsDiff6.Core`, top level `BsDiff.cs`); `src/cli/bsdiff6`, `src/cli/bspatch6`; `tests/BsDiff6.Tests/` | Done. 297 tests pass in ~30 s (308 after the tuning pass) (long fuzz excluded via `default.runsettings`). CLIs under `src/cli/` because the file system is case-insensitive (`src/bsdiff6` = `src/BsDiff6`). Dev test projects deleted; harnesses build via project reference |
| First benchmark + configuration matrix | `bench/results/20261121-045706.md`, `bench/results/matrix-20261121-050431.{md,csv,json}`, `bench/matrix.py`, `bench/README.md` | All 20 round trips pass. Default: weighted avg 0.1330 (b43 0.1518), gmean 0.809x b43, smaller on every pair. `--prune`: same size (+0.17% bytes), ~40% less CPU. Block-only: 1.37x b43 (much worse than the thesis's "~8%"); local-only ≈ default. Wall times of the matrix unreliable (a VM loaded the machine to load average 30); use the CPU columns |
| Tuning pass: pruning default, `--dp-costs`, DP cost sweep, memory / overhead measurements | `DpOptions.PruneHopeless = true` (CLI `--no-prune`; `--prune` a no-op); `bsdiff6 --dp-costs M,U,S` (in `-v` and `--stats-json`); `bench/matrix.py --config NAME=FLAGS --exclude --compare-to --tag`; summary `bench/results/dpcosts-sweep-20261121.{md,csv}`, raw `matrix-20261121-{054337-sweep1,055320-sweep2,060015-confirm20}.*`; DESIGN §6/§9 | Done, 308 tests pass. Sweep (pruning on, 22 cost settings on 18 pairs, best 3 confirmed on 20): 2:1 mismatch:unmatched is the best ratio (a differing byte costs ~0.97 B stored vs ~0.44 B for an unmatched one); switch cost is the lever, flat optimum 10–14. All 20 pairs: 2,1,20 = 11,783,847 B, gmean 0.809x; 2,1,14 = −0.97%, 0.796x (worst pair lazygit-0.43 +1.2%); 2,1,12 = −1.06%, 0.793x (lazygit-0.43 +1.7%, ripgrep-13 +1.4%); CPU unchanged. **Decision (2026-11-21): default switch cost changed 20 -> 14** (`--dp-costs 2,1,20 --no-prune` is the thesis-exact DP). Peak memory on lazygit (footprint, not RSS) ~670 MB is set by the parallel encode (3 concurrent BWTs), not local alignment (~495 MB plateau); LocalAligner now frees the joined text before allocating its results (`--no-parallel` 592 → 550 MB; default unchanged). Fixed overhead on 100 KB: ~0.08 s runtime start + ~0.3 s JIT + ~0.1–0.2 s work |
| Final defaults, final benchmark, README | `DpOptions.SwitchCost = 14`; `bench/results/20261121-094010.{md,csv}`; `README.md`; `.gitignore` | Done. 308 tests pass (+2 LongFuzz pass). 20/20 round trips; sqrt-weighted avg 0.1318 (b43 0.1518; upgrade 0.1724 vs 0.1978, patch 0.0378 vs 0.0483, other 0.0038 vs 0.0040), gmean 0.796x b43, total 11,669,946 B vs 13,447,294; smaller on every pair. DESIGN §6/§9 updated; CLI help updated; CliTests assertion 20 -> 14 |

## In progress

| Agent | ID | Scope | Notes |
|---|---|---|---|
| (none) | | | Finishing agent `a4a64c1bbbde9bd07` (Sonnet) completed at 09:45: switch-cost default 14, final benchmark, `README.md`, `.gitignore` |

Tuning (agent `aaed06beccef6c93d`, done): sweep in `bench/results/dpcosts-sweep-20261121.md`. The thesis's 2:1 mismatch:unmatched ratio is right (a differing byte costs ~0.97 stored bytes, an unmatched one ~0.44); switch cost has a flat optimum at 10-14; `2,1,14` = -1% total bytes, gmean 0.809x -> 0.796x. Peak memory (macOS peak footprint; RSS is noisy here) ~670 MB on an 18 MB pair, set by the encode stage (three difference-mode groups coded concurrently, each with a BWT suffix array), not by local alignment; `--no-parallel` ~550 MB. Fixed overhead on a 100 KB pair: ~0.6 s CPU, of which ~0.35 s JIT, 0.08 s runtime start, ~0.1 s real work.

## Next steps, in order

1. (Fable, in parallel with integration) review `SaIs.cs`, `LocalAligner.cs`, `DpAligner.cs`.
2. Integration (one Opus medium agent):
   - `src/BsDiff6/BsDiff6.csproj` (single library, all areas + SharpZipLib), `BsDiff.Create` /
     `BsPatch.Apply` top level per DESIGN §8 (block + local alignment in parallel, DP, encode;
     `BlockOnly` option), `SuffixArray.Build` body replaced by a call to `SaIs.Build`.
   - CLIs `src/bsdiff6/`, `src/bspatch6/` (argument order of the original), a `--info` /
     verbose size breakdown using `PatchWriter.EncodeDetailed`.
   - `tests/BsDiff6.Tests/BsDiff6.Tests.csproj` (one test project for everything), solution file,
     end-to-end tests; then retire `dev/<area>/` dev projects (keep harnesses).
   - Format fix: a compressed section must be consumed exactly by its codec (trailing junk
     after a zlib/bzip2 stream is currently accepted).
3. Benchmark against bsdiff 4.3 on the whole corpus with `bench/bench.py`; every round trip must pass. Matrix: default, prune, block-only, local-only, prune + min-match 4 / 8. Decide the `PruneHopeless` default from it.
4. Tuning agent running (see In progress). When it reports: Fable decides DP cost defaults from the sweep, then re-benchmark all 20 pairs.
5. Research proposal put to the user at 05:20 (P3 Brotli back end; P1+P2 conditional coder with address hypotheses as a new format version). Implement only what they approve; Fable reviews `research/REPORT.md` before any of it is specified in DESIGN.md.
6. ~~README~~ done (`README.md`).

## Open decisions and known issues

- Block alignment is deterministic per machine but not guaranteed bit-identical across
  platforms (FFT twiddles from `Math.Sin`/`Math.Cos`). Decide at integration whether to pin.
- Block-alignment recall is weak on big version jumps (10–29% of bytes on ripgrep 13→14,
  fd, bat, lazygit 0.40→0.44); local alignment + DP are expected to cover those.
- zlib output differs between .NET runtimes, so patch bytes are reproducible only per runtime.
- The machine has been heavily loaded (other sessions plus research experiments); wall-clock
  timings taken so far are unreliable.
