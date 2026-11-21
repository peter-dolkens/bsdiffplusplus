# bsdiff++

An independent C# implementation of the binary delta algorithm in Chapter 2 of Colin Percival's doctoral thesis, ["Matching with Mismatches and Assorted Applications"](https://www.daemonology.net/papers/thesis.pdf). He called the algorithm "bsdiff 6" and never released an implementation; he is also the author of [bsdiff](https://www.daemonology.net/bsdiff/) 4.x. Given an old and a new file it produces a patch that rebuilds the new file from the old one.

This project was not written by, and is not affiliated with, Colin Percival. The patch format (magic `BSDIFF6+`) is this project's own. It is not compatible with bsdiff 4.x or with any other implementation of the thesis.

## Results

Whole corpus, default settings, final defaults (DP costs 2,1,14), sizes in bytes. Source: `bench/results/20261121-094010.md`. All 20 round trips verified (old file + patch gives a byte-identical new file).

| pair | new size | bzip2 -9 | bsdiff 4.3 | bsdiff6 | bsdiff6 / 4.3 |
|---|---:|---:|---:|---:|---:|
| other-lua-src-tar-5.4.6-5.4.7 | 1,537,536 | 303,002 | 20,096 | 18,854 | 0.94x |
| other-sqlite3.c-3.45.1-3.45.2 | 9,022,640 | 1,735,372 | 2,827 | 2,281 | 0.81x |
| patch-bzip2-1.0.6-1.0.8-macos-x86_64 | 113,072 | 51,794 | 3,831 | 2,924 | 0.76x |
| patch-liblua.a-5.4.6-5.4.7-macos-arm64 | 400,216 | 131,776 | 15,189 | 9,459 | 0.62x |
| patch-libz.a-1.3-1.3.1-macos-x86_64 | 116,536 | 56,651 | 971 | 565 | 0.58x |
| patch-lua-5.4.6-5.4.7-macos-arm64 | 298,128 | 117,204 | 20,144 | 15,737 | 0.78x |
| patch-lua-5.4.6-5.4.7-macos-x86_64 | 266,696 | 122,070 | 22,040 | 17,688 | 0.80x |
| patch-sqlite-shell-3.45.1-3.45.2-macos-arm64 | 1,330,960 | 621,399 | 68,383 | 61,151 | 0.89x |
| patch-zlib-minigzip-1.3-1.3.1-macos-arm64 | 108,112 | 45,094 | 4,487 | 3,079 | 0.69x |
| patch-zlib-minigzip-1.3-1.3.1-macos-x86_64 | 102,928 | 55,427 | 3,462 | 2,289 | 0.66x |
| upgrade-bat-0.25.0-0.26.0-macos-arm64 | 5,842,592 | 3,046,096 | 1,663,630 | 1,512,950 | 0.91x |
| upgrade-fd-10.2.0-10.3.0-linux-arm64 | 3,597,840 | 1,476,719 | 1,198,553 | 1,099,046 | 0.92x |
| upgrade-fzf-0.56.0-0.56.1-linux-arm64 | 3,735,552 | 1,339,607 | 67,909 | 59,786 | 0.88x |
| upgrade-jq-1.7-1.7.1-linux-arm64 | 1,709,616 | 767,969 | 34,675 | 31,397 | 0.91x |
| upgrade-jq-1.7-1.7.1-windows-amd64 | 985,088 | 387,016 | 41,259 | 29,553 | 0.72x |
| upgrade-jq-1.7.1-1.8.0-macos-arm64 | 841,376 | 302,585 | 142,965 | 122,309 | 0.86x |
| upgrade-lazygit-0.40.2-0.44.0-macos-arm64 | 18,794,242 | 6,038,673 | 5,594,541 | 4,912,232 | 0.88x |
| upgrade-lazygit-0.43.1-0.44.0-linux-amd64 | 18,854,040 | 6,211,644 | 1,038,396 | 798,345 | 0.77x |
| upgrade-ripgrep-13.0.0-14.1.0-windows-amd64 | 5,332,480 | 1,908,239 | 1,810,554 | 1,631,197 | 0.90x |
| upgrade-ripgrep-14.0.3-14.1.0-linux-amd64 | 6,590,000 | 2,341,634 | 1,693,382 | 1,339,104 | 0.79x |

Summary rows (patch size divided by new size, averaged with weights of sqrt(new size), as in the thesis; lower is better):

| category | bzip2 -9 | bsdiff 4.3 | bsdiff6 | bsdiff6 / 4.3 (geometric mean) |
|---|---:|---:|---:|---:|
| all 20 pairs | 0.3579 | 0.1518 | 0.1318 | 0.796x |
| upgrade (10) | 0.3737 | 0.1978 | 0.1724 | 0.85x |
| patch (8) | 0.4377 | 0.0483 | 0.0378 | 0.72x |
| other (2) | 0.1937 | 0.0040 | 0.0038 | 0.87x |

Total over the 20 pairs: bsdiff 4.3 13,447,294 bytes, bsdiff6 11,669,946 bytes. The overall geometric mean of per-pair size ratios is 0.796x (the benchmark table prints it rounded to 0.80x). bsdiff6 is smaller on every pair.

How to read it: bsdiff6 / 4.3 is the size of the bsdiff6 patch relative to the bsdiff 4.3 patch for the same pair, so 0.81x means 19% smaller. The gain is largest on small source-change pairs (0.58x to 0.89x) and smaller on big upgrades (0.77x to 0.92x). The corpus is 20 pairs: 10 real release binaries (C, Rust and Go; ELF, PE and Mach-O; from jq, fzf, fd, bat, ripgrep, lazygit), 8 small-change pairs compiled locally on macOS (zlib, Lua, SQLite, bzip2, with two static libraries), and two text pairs (a Lua source tar and the SQLite amalgamation). See `bench/corpus/MANIFEST.md`. Every number here comes from this corpus on one shared machine (Apple M5, 10 cores, load average about 3 to 5 during the run); none of it is a general claim.

Costs:

- CPU time. The wall-clock column of the benchmark file sums to 133 s for bsdiff6 and 209 s for bsdiff 4.3 over the 20 pairs. bsdiff6 uses several threads, so wall time understates its CPU time: the same run summed as user plus system CPU in the DP sweep (`bench/results/dpcosts-sweep-20261121.md`) is about 181 s, about the same as bsdiff 4.3's single-threaded time. The machine was quieter than for earlier runs, but not idle, so treat timings as rough. Applying a patch takes about 0.9 to 2.2 s for the 18 MB pairs and 0.2 to 0.3 s for small files.
- Memory. On the 18 MB lazygit pair the peak memory footprint (not RSS) of bsdiff6 is about 670 MB, against about 300 MB for bsdiff 4.3 (sweep report). Maximum RSS on the benchmark machine was noisy (macOS compresses idle pages; identical runs ranged 483 to 635 MB), so the RSS columns of the benchmark file should not be read closely. The peak is set by encoding the three difference-mode groups concurrently.
- Start-up. On small files a fixed .NET cost dominates: about 0.08 s of runtime start and about 0.3 s of JIT, so a 100 KB pair takes about 0.6 s of CPU in total against about 0.04 s for bsdiff 4.3.

## Requirements and build

- .NET SDK 10. The projects target `net10.0` with `LangVersion` 10.0; the code avoids APIs newer than .NET 6.
- Build: `dotnet build -c Release BsDiff6.sln`
- Tests (308, about 30 s): `dotnet test -c Release BsDiff6.sln`. The default run excludes long fuzz tests (`tests/BsDiff6.Tests/default.runsettings`); run them with `dotnet test tests/BsDiff6.Tests/BsDiff6.Tests.csproj -c Release --filter Category=LongFuzz -p:RunSettingsFilePath=` (the empty `RunSettingsFilePath` is needed because a `--filter` is combined with, not substituted for, the filter in `default.runsettings`).

The CLIs land in `src/cli/bsdiff6/bin/Release/net10.0/bsdiff6.dll` and `src/cli/bspatch6/bin/Release/net10.0/bspatch6.dll`; run them with `dotnet <dll>`.

## Usage

```
dotnet bsdiff6.dll [options] oldfile newfile patchfile
dotnet bspatch6.dll oldfile newfile patchfile
dotnet bspatch6.dll --info patchfile
```

`bspatch6` checks the old file against the MD5 stored in the patch before writing anything, and the result afterwards. `--info` prints the header (format, sizes, MD5s, difference mode) and the stored size of each section without applying the patch.

Main `bsdiff6` options (`--help` lists all):

| option | meaning |
|---|---|
| `--block-only` | block alignment only: far less memory, larger patches |
| `--no-block` | local alignment only |
| `--no-prune` | keep DP states that cannot beat a fresh switch (the thesis's exact candidate handling; slower, patches about 0.2% smaller) |
| `--dp-costs M,U,S` | DP costs for a mismatched byte, an unmatched byte and a switch; default 2,1,14. The thesis's values are 2,1,20; `--dp-costs 2,1,20 --no-prune` reproduces its DP exactly |
| `--min-match N` | ignore longest matches shorter than N bytes (default 1) |
| `--no-parallel` | one thread (same patch) |
| `--seed N` | seed of the block-alignment random choices |
| `-v`, `--stats-json FILE` | timings, alignment statistics and a size breakdown (stderr, or JSON) |

Exit status of both tools: 0 success, 1 failure (I/O error, malformed patch, wrong old file), 2 usage error.

Library (`src/BsDiff6`, assembly `BsDiff6.Core`):

```csharp
byte[] patch = BsDiff.Create(oldBytes, newBytes);          // optional BsDiffOptions
byte[] again = BsPatch.Apply(oldBytes, patch);             // throws PatchFormatException on bad input
// again is byte-for-byte equal to newBytes
```

## How it works

1. Block alignment (thesis section 2.4): the old file is indexed and the new file is processed in blocks; an FFT-based computation of matching with mismatches finds, for each block, the offset in the old file at which it agrees best. This tolerates scattered differing bytes, which is what recompiled code looks like. Block boundaries are then tweaked.
2. Local alignment (section 2.5): both files are suffix-sorted together (SA-IS), and for every position of the new file the longest exact match in the old file is found.
3. Pruned shortest path (section 2.6): a dynamic program over the new file chooses, per position, an offset in the old file (or "unmatched") from the candidates of steps 1 and 2, minimising mismatch, unmatched and switch costs. States that cannot win are pruned.
4. Delta encoding (section 2.7): the result is stored as a control string (segment lengths and seeks), an extra string (unmatched bytes), and the byte differences over matched segments. Differences are written either as a non-zero string with a difference map (the map is Burrows-Wheeler transformed and range-coded with a midpoint coder) or as multi-precision differences; the encoder picks the smaller mode per patch. Sections use zlib, bzip2 or none, whichever is smaller.

`DESIGN.md` has the byte-exact format and algorithm details. Its section 9 lists where this implementation departs from or adds to the thesis (including the pruning, the default switch cost 14 instead of 20, and the window of local candidates).

## Findings and limits

- Block alignment adds almost nothing on this corpus once local alignment is on: `--no-block` (local only) is within 0.1% of the default (`bench/results/matrix-20261121-050431.md`).
- `--block-only` is a low-memory mode, but on this corpus it produces patches about 37% larger than bsdiff 4.3 (1.37x), unlike the thesis's report of about 8% larger.
- The default switch cost of 14 was tuned on this project's own 20-pair corpus (about 1% fewer total bytes than the thesis's 20). It is an empirical default.
- Files must be smaller than 2 GiB. Local alignment needs several int arrays of the combined size, so memory use is a few times the file sizes, and about 2.2x bsdiff 4.3's on the 18 MB pair.
- Patch bytes are reproducible only for a given .NET runtime (zlib output differs between runtimes); every runtime reads every patch. Block alignment uses FFTs with `Math.Sin`/`Math.Cos` twiddles and is not guaranteed bit-identical across platforms. Patches from any runtime apply correctly.
- The MD5 values in the header are an error check, not security. Patches from untrusted sources must be authenticated before use. The reader is hardened against malformed input (size limits checked before allocation, exact section consumption) and fuzzed, but that is not a substitute.
- No container awareness: compressed archives (zip, gzip, `.p4k` and similar) diff poorly unless decompressed first.

## Further work

`research/REPORT.md` collects measurements on post-2006 methods using this corpus. Prototypes suggest that a Brotli or LZMA back end and a conditional context-mixing coder with alignment-driven address prediction could take patches from about 0.81x to about 0.70x of bsdiff 4.3. None of this is implemented.

## Repository layout

```
src/BsDiff6/          library: Matching/ (block alignment), Alignment/ (SA-IS, local alignment, DP), Format/ (patch writer/reader), BsDiff.cs (top level)
src/cli/bsdiff6/      diff command line tool
src/cli/bspatch6/     patch command line tool
tests/BsDiff6.Tests/  xUnit tests (unit, end-to-end, fuzz)
dev/                  development harnesses
bench/                corpus, bsdiff 4.3 baseline build, bench.py, matrix.py, results/
research/             post-2006 methods study (REPORT.md)
ref/                  bsdiff 4.3 sources; instructions for fetching the thesis
DESIGN.md             design and byte-exact patch format
STATUS.md             working status
```

To rerun the benchmarks see `bench/README.md` (corpus download, baseline build, `bench/bench.py`, and `bench/matrix.py` for configuration comparisons).

## Credits and licences

The algorithm is Colin Percival's. `ref/bsdiff-4.3/` holds his BSD-licensed bsdiff 4.3 sources, used only as the benchmark baseline; the thesis itself is not redistributed here (`ref/thesis/README.md` says how to fetch it). [SharpZipLib](https://github.com/icsharpcode/SharpZipLib) (MIT) is the only dependency. No licence has been chosen for this project's own code yet.
