# Benchmarks

Everything runs from the project root. macOS: timings and peak RSS come from `/usr/bin/time -l`.
The machine may be shared; treat wall times as unreliable unless the load average was low.

## Build

```sh
bench/baseline/build.sh                          # bsdiff 4.3 baseline (bench/baseline/bsdiff43, bspatch43)
bench/fetch_corpus.sh                            # corpus (bench/corpus/<pair>/{old,new}); see corpus/MANIFEST.md
dotnet build -c Release BsDiff6.sln              # bsdiff6 / bspatch6
dotnet test -c Release BsDiff6.sln               # quick suite (long fuzz excluded; add --filter Category=LongFuzz for it)
```

The CLIs land in `src/cli/bsdiff6/bin/Release/net10.0/bsdiff6.dll` and
`src/cli/bspatch6/bin/Release/net10.0/bspatch6.dll`; run them with `dotnet <dll>`.

## bsdiff 4.3 vs bsdiff6 over the whole corpus (round trips verified)

```sh
B=src/cli
python3 bench/bench.py --tool bsdiff6 \
  "dotnet $B/bsdiff6/bin/Release/net10.0/bsdiff6.dll {old} {new} {patch}" \
  "dotnet $B/bspatch6/bin/Release/net10.0/bspatch6.dll {old} {out} {patch}"
```

Writes `bench/results/<stamp>.{md,csv}`. bsdiff 4.3 results are cached in
`bench/results/baseline.json` (`--refresh-baseline` to redo them). `--only SUBSTR` limits pairs.

## Configuration matrix

```sh
python3 bench/matrix.py                     # all configurations; the two >10 MB pairs only default + no-prune
python3 bench/matrix.py --big-configs all   # every configuration on every pair (slow)
python3 bench/matrix.py --configs default,no-prune --only upgrade
python3 bench/matrix.py --resume bench/results/matrix-<stamp>.json   # reuse finished runs
# configurations from the command line (replace the built-in list; run on every pair by default):
python3 bench/matrix.py --exclude lazygit --tag sweep \
  --config base= --config c3-2-40="--dp-costs 3,2,40" --config c2-1-30="--dp-costs 2,1,30"
```

Built-in configurations (`CONFIGS` in `matrix.py`): `default`, `no-prune` (`--no-prune`, the thesis's
exact DP; pruning has been the default since the tuning pass), `block-only`, `local-only`
(`--no-block`), `mm4`, `mm8` (`--min-match N`). `--config NAME=FLAGS` (repeatable) replaces that list;
`--exclude SUBSTR` skips pairs; `--compare-to NAME` (default: the first configuration) adds a table of
per-pair size ratios to that configuration and lists every pair more than 1% worse; `--detail a,b`
picks the configurations that get per-pair section/alignment tables; `--tag TEXT` is appended to the
output names. `--resume` reuses a finished run only if its flags are unchanged. Each run calls `bsdiff6
--stats-json`, verifies the patch with `bspatch6`, and the results go to
`bench/results/matrix-<stamp>.md` (tables: default per pair, summary per configuration with the
sqrt-weighted average compression overall and per category and the geometric-mean ratio to bsdiff
4.3, stored section totals per configuration, per-pair ratios to the reference configuration,
size/time/RSS matrices, section breakdown and alignment statistics for the detail configurations),
`.csv` (one row per pair × configuration) and `.json` (raw stats, usable with `--resume`).
`--jobs N` (at most 3) runs pairs concurrently at the cost of timing accuracy.

For one pair by hand: `dotnet .../bsdiff6.dll -v old new patch` prints stage timings, alignment
statistics and the section/codec/difference-mode breakdown; `dotnet .../bspatch6.dll --info patch`
prints a patch's header.
