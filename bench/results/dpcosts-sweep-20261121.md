# DP cost sweep (2026-11-21)

Pruning on (the new default). Costs = mismatch,unmatched,switch; the thesis's values are 2,1,20. Raw runs: `matrix-20261121-054337-sweep1`, `matrix-20261121-055320-sweep2` (18 pairs, lazygit excluded) and `matrix-20261121-060015-confirm20` (all 20) — `.md` there has the per-pair tables. avg = patch/new weighted by sqrt(new); gmean = geometric mean of size / bsdiff 4.3; sections are stored bytes; matched = share of new bytes in matched segments; differ = share of matched bytes that differ. Diff CPU is user+sys with 3 jobs on a loaded machine (±5%).

## 18 pairs (lazygit excluded), sorted by M/U then S/U

| costs | M/U | S/U | avg all | avg other | avg patch | avg upgrade | gmean vs 4.3 | total bytes | vs 2,1,20 | diff CPU s | control | non-zero | map | extra | matched | differ |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 1,1,10 | 1.00 | 10.0 | 0.1371 | 0.0038 | 0.0382 | 0.2054 | 0.820x | 6,624,560 | +9.94% | 101.7 | 769,763 | 4,638,544 | 1,215,053 | 0 | 100.00% | 15.95% |
| 1,1,20 | 1.00 | 20.0 | 0.1394 | 0.0040 | 0.0398 | 0.2084 | 0.860x | 6,716,065 | +11.46% | 107.8 | 376,999 | 5,256,929 | 1,080,936 | 0 | 100.00% | 19.74% |
| 5,4,80 | 1.25 | 20.0 | 0.1309 | 0.0039 | 0.0388 | 0.1953 | 0.827x | 6,290,971 | +4.40% | 100.2 | 386,552 | 2,733,107 | 834,464 | 2,335,612 | 89.21% | 10.96% |
| 4,3,40 | 1.33 | 13.3 | 0.1297 | 0.0038 | 0.0376 | 0.1937 | 0.804x | 6,241,309 | +3.58% | 96.7 | 595,196 | 2,586,711 | 903,523 | 2,154,644 | 90.62% | 9.92% |
| 4,3,60 | 1.33 | 20.0 | 0.1289 | 0.0039 | 0.0385 | 0.1923 | 0.818x | 6,192,980 | +2.78% | 98.3 | 369,251 | 2,321,496 | 760,360 | 2,740,636 | 87.05% | 9.47% |
| 3,2,20 | 1.50 | 10.0 | 0.1270 | 0.0037 | 0.0371 | 0.1897 | 0.790x | 6,119,672 | +1.56% | 98.9 | 817,645 | 2,177,757 | 885,831 | 2,237,199 | 90.49% | 8.35% |
| 3,2,24 | 1.50 | 12.0 | 0.1264 | 0.0037 | 0.0373 | 0.1886 | 0.793x | 6,079,890 | +0.90% | 97.7 | 631,367 | 2,102,615 | 823,039 | 2,521,630 | 88.82% | 8.31% |
| 3,2,30 | 1.50 | 15.0 | 0.1257 | 0.0038 | 0.0376 | 0.1874 | 0.796x | 6,042,179 | +0.28% | 97.8 | 484,612 | 1,987,984 | 751,790 | 2,816,551 | 86.97% | 8.15% |
| 3,2,40 | 1.50 | 20.0 | 0.1257 | 0.0038 | 0.0382 | 0.1872 | 0.806x | 6,032,497 | +0.12% | 98.0 | 344,116 | 1,836,267 | 662,854 | 3,188,019 | 84.52% | 7.83% |
| 2,1,6 | 2.00 | 6.0 | 0.1294 | 0.0037 | 0.0376 | 0.1933 | 0.799x | 6,240,644 | +3.57% | 98.5 | 1,467,631 | 1,626,878 | 832,268 | 2,312,627 | 90.77% | 6.22% |
| 2,1,8 | 2.00 | 8.0 | 0.1254 | 0.0037 | 0.0375 | 0.1870 | 0.791x | 6,032,539 | +0.12% | 96.2 | 927,427 | 1,489,365 | 733,651 | 2,880,853 | 87.57% | 6.10% |
| 2,1,10 | 2.00 | 10.0 | 0.1241 | 0.0037 | 0.0374 | 0.1850 | 0.788x | 5,964,818 | -1.01% | 93.9 | 680,607 | 1,391,892 | 670,263 | 3,220,814 | 85.53% | 5.97% |
| 2,1,12 | 2.00 | 12.0 | 0.1239 | 0.0037 | 0.0376 | 0.1846 | 0.790x | 5,951,620 | -1.23% | 98.7 | 540,692 | 1,321,696 | 624,474 | 3,463,514 | 84.04% | 5.86% |
| 2,1,14 | 2.00 | 14.0 | 0.1241 | 0.0038 | 0.0378 | 0.1849 | 0.793x | 5,959,369 | -1.10% | 91.1 | 450,090 | 1,264,934 | 588,155 | 3,654,946 | 82.84% | 5.75% |
| 2,1,17 | 2.00 | 17.0 | 0.1247 | 0.0038 | 0.0381 | 0.1858 | 0.800x | 5,986,315 | -0.65% | 97.0 | 358,759 | 1,200,915 | 546,251 | 3,879,145 | 81.43% | 5.66% |
| **2,1,20** | **2.00** | **20.0** | **0.1256** | **0.0038** | **0.0387** | **0.1869** | **0.808x** | **6,025,547** | **+0.00%** | **94.5** | **295,318** | **1,154,415** | **513,650** | **4,060,919** | **80.28%** | **5.57%** |
| 2,1,30 | 2.00 | 30.0 | 0.1276 | 0.0039 | 0.0397 | 0.1897 | 0.826x | 6,113,308 | +1.46% | 91.1 | 189,056 | 1,037,688 | 439,185 | 4,446,135 | 77.57% | 5.30% |
| 2,1,40 | 2.00 | 40.0 | 0.1289 | 0.0040 | 0.0407 | 0.1915 | 0.847x | 6,172,411 | +2.44% | 93.5 | 137,194 | 958,460 | 390,568 | 4,684,944 | 75.68% | 5.09% |
| 5,2,30 | 2.50 | 15.0 | 0.1253 | 0.0038 | 0.0386 | 0.1866 | 0.811x | 6,002,046 | -0.39% | 99.0 | 370,727 | 926,977 | 473,103 | 4,229,993 | 79.73% | 4.63% |
| 5,2,36 | 2.50 | 18.0 | 0.1261 | 0.0038 | 0.0389 | 0.1877 | 0.816x | 6,039,370 | +0.23% | 99.2 | 298,715 | 884,105 | 443,992 | 4,411,312 | 78.49% | 4.55% |
| 3,1,14 | 3.00 | 14.0 | 0.1266 | 0.0038 | 0.0394 | 0.1884 | 0.823x | 6,054,949 | +0.49% | 99.4 | 374,418 | 758,975 | 415,528 | 4,504,784 | 78.29% | 3.98% |
| 3,1,20 | 3.00 | 20.0 | 0.1279 | 0.0038 | 0.0403 | 0.1901 | 0.835x | 6,108,765 | +1.38% | 100.5 | 241,610 | 706,007 | 373,199 | 4,786,705 | 76.13% | 3.91% |

## All 20 pairs: baseline and the best settings

| costs | M/U | S/U | avg all | avg other | avg patch | avg upgrade | gmean vs 4.3 | total bytes | vs 2,1,20 | diff CPU s | control | non-zero | map | extra | matched | differ |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 2,1,10 | 2.00 | 10.0 | 0.1318 | 0.0037 | 0.0374 | 0.1725 | 0.792x | 11,677,632 | -0.90% | 193.5 | 1,429,483 | 3,515,032 | 1,499,217 | 5,232,504 | 87.39% | 8.42% |
| 2,1,12 | 2.00 | 12.0 | 0.1316 | 0.0037 | 0.0376 | 0.1722 | 0.793x | 11,658,415 | -1.06% | 198.6 | 1,140,549 | 3,438,961 | 1,422,911 | 5,654,595 | 86.07% | 8.46% |
| 2,1,14 | 2.00 | 14.0 | 0.1318 | 0.0038 | 0.0378 | 0.1724 | 0.796x | 11,669,946 | -0.97% | 181.3 | 957,394 | 3,372,453 | 1,361,285 | 5,977,415 | 85.01% | 8.47% |
| **2,1,20** | **2.00** | **20.0** | **0.1332** | **0.0038** | **0.0387** | **0.1742** | **0.809x** | **11,783,847** | **+0.00%** | **194.9** | **644,747** | **3,233,671** | **1,236,682** | **6,667,348** | **82.72%** | **8.53%** |

## Reading

- **Ratio mismatch:unmatched.** 2:1 (the thesis's) is the best ratio at every switch cost tried. Making matching
  more attractive (3:2, 4:3, 1:1) matches more of the new file (up to 100%) but moves bytes from the extra
  section into non-zero + map at a worse rate. 3,2,30 vs 2,1,20 on 18 pairs: extra −1.24 MB stored for 2.80 M
  fewer unmatched bytes (0.44 B/byte), non-zero + map +1.07 MB for 1.10 M more differing bytes (≈0.97 B/byte), control +0.19 MB.
  So in our format a differing byte costs about twice an unmatched one, and the thesis's ratio fits the
  measurements; the "break-even above 50% mismatches" hypothesis does not hold. 5:2 and 3:1 lose in the other direction.
- **Switch cost.** This is the parameter that matters, with a flat optimum at S/U ≈ 10–14 (−1.0 to −1.2% total bytes,
  gmean 0.808x → 0.788–0.793x on 18 pairs). Lower values (6–8) pay too much control; higher values (30–40) leave too much unmatched.
- **Size of the effect.** About 1% total bytes and 1.6–2% gmean, with no change in CPU. It is consistent on the small
  `patch`/`other` pairs (0 to −7% at 2,1,14; up to −11% at 2,1,10); on the big upgrade pairs it is within ±3%.

Pairs more than 1% worse than 2,1,20 (all 20 pairs): 2,1,14 — lazygit-0.43.1-0.44.0 +1.2%; 2,1,12 — lazygit-0.43.1-0.44.0
+1.7%, ripgrep-13.0.0-14.1.0 +1.4%; 2,1,10 — jq-1.7-1.7.1 linux +1.4% and windows +2.2%, lazygit-0.43.1 +2.7%, ripgrep-13 +2.3%.

## Peak memory (upgrade-lazygit-0.43.1-0.44.0-linux-amd64, 18.8 + 18.9 MB)

Peak *footprint* from `/usr/bin/time -l` and `footprint` sampled every 0.3 s. Maximum RSS is misleading on this machine:
under memory pressure macOS compresses idle pages, so RSS ranged from 483 to 635 MB between identical default runs.

| mode | before | after the LocalAligner change | stage that sets the peak |
|---|---:|---:|---|
| default | 668 MB | 669–671 MB | end of encode (3 mode groups coded concurrently) |
| `--no-block` | 670 MB | 667–669 MB | end of encode |
| `--no-parallel` | 592 MB | 549–554 MB | encode (local-alignment plateau ~494 MB) |

Local alignment sits at a ~495 MB plateau (inputs 2×18.8, SA 151, PLCP 151, result arrays 151 MB, alive together in
the scans) and stays there through the DP. Encode then climbs to the peak. Each of the three concurrent mode groups holds
a map (D = 18.7 MB), a BWT suffix array (int[D] = 75 MB) and the BWT output (18.7 MB). Compacting the LOH before encode was
tried and reverted: the footprint fell to ~250 MB before encode, but encode climbed back to the same 670 MB.
Lowering the default peak needs encode changes (code fewer modes at once, or share/free the BWT buffers).

## Fixed overhead (patch-zlib-minigzip-1.3-1.3.1-macos-x86_64, ~100 KB)

| what | CPU s |
|---|---:|
| `bsdiff6 --version` (runtime start) | 0.08 |
| `bsdiff6 old new patch` (whole process) | 0.60–0.64 |
| `bsdiff6 --no-parallel` | 0.43–0.48 |
| stages inside the process, first call (align / DP / encode) | 0.52 (0.26 / 0.04 / 0.23) |
| same call repeated in one process, steady state | 0.17–0.22 parallel, 0.10–0.14 sequential |
| `bspatch6` | 0.11 |

Of the ~0.6 s, ~0.08 s is runtime start and ~0.3–0.35 s is JIT and tier-0 code on the first call. About 0.1 s is real work,
plus ~0.08 s of thread-pool overhead in parallel mode. ReadyToRun/AOT would remove most of the 0.3 s; not started.
