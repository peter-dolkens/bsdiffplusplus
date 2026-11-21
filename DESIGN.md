# bsdiff6 — design

An implementation, in C# 10, of the delta-compression algorithm from Chapter 2 of
Colin Percival's doctoral thesis *Matching with Mismatches and Assorted Applications*
(`ref/thesis/thesis.pdf`, text in `ref/thesis/thesis.txt`), which he called "bsdiff 6"
and never released. The matching-with-mismatches algorithm it depends on is Chapter 1.

This is an independent implementation. The thesis leaves many details open; where it
does, this document decides them. Where this document and the thesis disagree, this
document wins, and the deviation is listed in section 9.

Notation: `S` = old file, length `n`. `T` = new file, length `m`. An *offset* is
`oldPos - newPos`. `ln` is the natural log.

## 1. Pipeline

```
old, new
  ├─ block alignment   (Matching/)   FFT matching-with-mismatches, thesis §1, §2.4
  ├─ local alignment   (Alignment/)  suffix array longest matches, thesis §2.5
  └─ pruned-graph DP   (Alignment/)  combines both, thesis §2.6        → Segment[]
Segment[] ─ delta encoding (Format/) thesis §2.7                         → patch bytes
patch + old ─ PatchReader.Apply (Format/)                                → new
```

## 2. Toolchain and layout

- Language: **C# 10** (`<LangVersion>10.0</LangVersion>`). Target framework `net10.0`
  (the only SDK installed here). Do not use BCL APIs newer than .NET 6, so that
  retargeting to `net6.0` is a one-line change. No `unsafe` unless a measured hot loop
  needs it. `Nullable` enabled. File-scoped namespaces.
- Dependencies: BCL only, plus `SharpZipLib` (NuGet, bzip2). zlib comes from
  `System.IO.Compression.ZLibStream`. MD5 from `System.Security.Cryptography`.
- All positions and lengths are `int`. Files are limited to < 2 GiB each.

```
DESIGN.md
Directory.Build.props         LangVersion etc. Applies to every project.
src/BsDiff6/Common/           shared types (frozen; ask before changing)
src/BsDiff6/Matching/         FFT, mismatch index, block alignment
src/BsDiff6/Alignment/        suffix array, local alignment, DP
src/BsDiff6/Format/           delta coding, map codec, patch writer/reader
src/BsDiff6/BsDiff6.csproj    the library (assembly BsDiff6.Core, see below); top level
                              in src/BsDiff6/BsDiff.cs
src/cli/bsdiff6/, src/cli/bspatch6/, src/cli/Shared/   CLIs
tests/BsDiff6.Tests/<Area>/   xunit tests, one folder per area, plus EndToEnd/
dev/<area>-harness/           dev console harnesses (project reference to the library)
BsDiff6.sln                   library, CLIs, tests
bench/                        baseline bsdiff 4.3 (C), corpus, benchmark script
ref/                          thesis and original bsdiff 4.3 source (read-only)
```

The file system here is case-insensitive: `src/bsdiff6/` would be `src/BsDiff6/`, and
`bsdiff6.dll` (CLI) would overwrite `BsDiff6.dll` (library) in every output folder. Hence the
CLIs live under `src/cli/` and the library assembly is named `BsDiff6.Core`.

Namespaces: `BsDiff6` (Common, top level), `BsDiff6.Matching`, `BsDiff6.Alignment`, `BsDiff6.Format`.

## 3. Shared types (`Common/`)

`Segment(int NewPos, int Length, int OldPos)`; `OldPos == -1` means unmatched.
An *alignment* is a `Segment[]` that is sorted by `NewPos`, covers `[0, m)` exactly
with no gaps or overlaps, has `Length > 0` everywhere, and for matched segments has
`0 <= OldPos` and `OldPos + Length <= n`. `AlignmentUtil.Validate` checks this;
`AlignmentUtil.Canonicalize` merges adjacent segments that continue each other.

Block alignment also returns a `Segment[]` with the same invariants, where an
unmatched segment means "no prediction here".

## 4. Block alignment (`Matching/`) — thesis §1 (esp. Alg. 1.1–1.3, §1.6) and §2.4

Public API:

```csharp
public sealed class BlockAlignOptions { ... }   // every constant below is a property
public static class BlockAligner {
    public static Segment[] Align(ReadOnlySpan<byte> old, ReadOnlySpan<byte> @new,
                                  BlockAlignOptions? options = null);
}
```

### 4.1 Index of the old file

- Block length `B = round(sqrt(n ln n))`. `L = 4 B` (`LFactor = 4`).
- `k = 2` primes `p1 != p2` chosen at random from `[L, L (1 + 2 / ln L))`.
- For each prime, `R` random maps `φ: byte → ℝ` (`MapsPerPrime`, default 2; §1.6
  note 8). `φ(c) = ±w(c)`, sign uniformly random, `w(c) = 1 / sqrt(freq(c))` where
  `freq` is the frequency of byte `c` in `S ‖ T` (§1.6 note 4); normalise so the
  mean of `w(c)^2` over the data is 1.
- `A[j] = Σ_λ φ(S[j + λ p])` for `0 <= j < p`.
- The PRNG is a seeded, self-contained generator (e.g. xoshiro256**, SplitMix64
  seeding) with a fixed default seed, so that output is identical across runs and
  platforms. Do not use `System.Random`.

If `n` is too small for this to make sense (`n < 1024`, or `L >= n`), skip block
alignment and return a single unmatched segment.

### 4.2 Query, per block of the new file

The new file is cut into consecutive blocks of length `B` (the last one may be
short; if it is shorter than `B/4`, give it its predecessor's offset as its only
candidate).

For block `b = T[t0 .. t0+len)`, per prime and per map, the cyclic correlation

`C[j] = Σ_{r < len} A[(r + j) mod p] · φ(T[t0 + r])`, `0 <= j < p`

is computed with a power-of-two FFT of size `N >= p + B`: extend `A` periodically
to length `p + B`, zero-pad, transform once at index time; per block transform the
zero-padded block, multiply by the conjugate, inverse transform, read the first `p`
entries (§1.6 note 1). Sum `C` over the `R` maps of the same prime. Write the FFT
yourself (iterative radix-2 or split-radix on `float`; a real-input transform is a
later optimisation). Blocks are independent, so the query loop may be parallelised
with `Parallel.For` provided the output stays deterministic.

Candidate offsets: take the `TopK` (default 32) largest entries of each of the two
summed vectors, combine every pair by CRT into the unique `x` with
`x ≡ j1 (mod p1)`, `x ≡ j2 (mod p2)` in a window of length `p1·p2` that starts at
`-(B-1)` (so a block that overhangs the start of the old file can be found), keep
those with `x < n`. `x` is the old-file position aligned with the start of the
block. Rank the surviving pairs by the sum of their two z-scores
(`(C[j] - mean) / stddev` per vector). Add the previous block's chosen `x` shifted
by one block (continuity) as an extra candidate.

Verification: for the best `VerifyCount` (default 8) candidates, count actual byte
matches between the block and the old file at that position (positions outside the
old file count as mismatches). The block's offset is the candidate with the most
matches. If even the best one matches fewer than `MinMatchFraction` (default 0.25)
of its bytes, the block has no prediction.

### 4.3 Tweaking (§2.4)

Scan forwards, then backwards, over every boundary between two adjacent runs with
different offsets (a run is a maximal sequence of blocks with the same offset, or
with no prediction). For runs `a = [sa, e)` at offset `oa` and `b = [e, eb)` at
offset `ob`, move the boundary to the `q ∈ [sa, eb]` minimising
`mismatches(T[sa..q) at oa) + mismatches(T[q..eb) at ob)`. In a run with no
prediction every byte scores 0.5 mismatches (work in integer half-units), so that
such a run yields to a neighbour that matches more than half the time. Positions
whose old-file position is out of range count as mismatches. Among all minimising
`q`, choose the one divisible by the largest power of two (in new-file
coordinates); among those, the one nearest the original boundary.

A run that shrinks below `MinRunLength` (default `B / 8`, at least 32) is removed:
its range becomes no-prediction, and the two boundaries it had are re-tweaked as a
single boundary between its neighbours.

Do **not** apply the thesis's final 50% filter here; the DP does that job.

Output: canonical `Segment[]`.

## 5. Local alignment (`Alignment/`) — thesis §2.5, first half

```csharp
public static class SaIs {           // suffix array, O(n), ints, no sentinel in the text
    public static void Build(ReadOnlySpan<byte> text, Span<int> sa);
}
public sealed class LocalMatches {   // index = position in the new file
    public int[] OldPos;             // start in old of the longest match, or -1
    public int[] Length;             // its length (0 if none)
}
public static class LocalAligner {
    public static LocalMatches Compute(ReadOnlySpan<byte> old, ReadOnlySpan<byte> @new);
}
```

- `SaIs.Build` sorts the suffixes of `text` lexicographically, a suffix that is a
  proper prefix of another sorting first (i.e. an implicit sentinel smaller than
  every byte). Must be linear or near-linear time with a small constant; it is the
  dominant cost of the whole tool. SA-IS is the expected choice. It must behave well
  on highly repetitive input (e.g. 100 MB of zeros, or a 0/1 string with long runs).
- `LocalAligner.Compute`: suffix-sort `S ‖ # ‖ T`, where `#` is a unique virtual
  symbol smaller than every byte (alphabet of 257; `SaIs.BuildSeparated`), compute
  the LCP array (Φ/PLCP variant, in place), then scan the suffix array forwards and
  backwards so that every `T` suffix learns its nearest `S` suffix on each side and
  the minimum LCP in between; keep the longer. The separator is required: without
  it the nearest `S` suffix maximises a common prefix that may run from `S` into
  `T`, and after capping a farther suffix can be the longer match. Ties: prefer the
  offset equal to that of position `t-1`'s match if either side offers it, else the
  preceding `S` suffix in suffix-array order.
- Requires `n + m <= int.MaxValue - 64`; otherwise throw `NotSupportedException`
  (the caller then falls back to block-only mode).
- Watch memory: at most three `int[n+m]` alive at once besides the text.

## 6. Pruned-graph shortest path (`Alignment/`) — thesis §2.6

```csharp
public sealed class DpOptions {
    public int LocalCandidates = 31;   // window of following positions
    public int BestCandidates  = 31;   // survivors from the previous position
    public int MismatchCost = 2, UnmatchedCost = 1, SwitchCost = 14;
    public int MinLocalMatchLength = 1; // ignore shorter longest-matches
}
public static class DpAligner {
    public static Segment[] Align(ReadOnlySpan<byte> old, ReadOnlySpan<byte> @new,
        LocalMatches? local, IReadOnlyList<Segment>? block, DpOptions? options = null);
}
```

State at new-file position `pos`: an offset `o` with `0 <= pos + o < n`, or the
special state *unmatched*. The candidate set at `pos` is the union of

1. the offsets of the longest matches starting at `pos, pos+1, …, pos+30`
   (`local`, skipped when null — that is "block alignment only" mode),
2. the `BestCandidates` offsets with the smallest distance at `pos - 1`,
3. the block-alignment prediction covering `pos` (`block`, skipped when null or
   when that segment is unmatched),
4. *unmatched*,

deduplicated; at most 64 states.

Costs: stepping `(pos-1, o) → (pos, o)` costs 0 if `T[pos] == S[pos+o]`, else
`MismatchCost`; *unmatched → unmatched* costs `UnmatchedCost`; every other
transition costs `SwitchCost` plus the cost of the byte at the destination
(0 / `MismatchCost` for an offset state, `UnmatchedCost` for unmatched). All states
start at distance 0 at `pos = 0` (so the first byte costs only its own byte cost).

Because the switch cost does not depend on the source or destination, the best
predecessor for a switch is always the minimum-distance state at `pos - 1`:

`dist[pos][s] = byteCost(pos, s) + min(dist[pos-1][s] if s was live, best[pos-1] + SwitchCost)`

Ties are broken in favour of staying on the same offset. States are ordered by
(distance, offset), unmatched after offsets at equal distance; the carried states
are the first `BestCandidates` offsets in that order that are still inside the old
file at the new position.

`DpOptions.PruneHopeless` (not in the thesis): a state whose distance is at least
`best + SwitchCost` is not carried, since a fresh switch reaches the same offset
as cheaply. It changes only which offsets remain candidates; measured path-cost
penalty ≤ 0.2%, DP 3–7× faster. It is **on by default**: over the 20-pair corpus it
cost +0.17% patch bytes (geometric mean vs bsdiff 4.3 unchanged at 0.809x) and cut
total diff CPU from 320 s to 185 s (`bench/results/matrix-20261121-050431.md`).
`--no-prune` (`PruneHopeless = false`) restores the thesis's exact candidate handling.

**Cost defaults.** The thesis's values are mismatch 2, unmatched 1, switch 20. Here the default
switch cost is **14** (`DpOptions.SwitchCost`); `--dp-costs 2,1,20 --no-prune` reproduces the
thesis-exact DP. A sweep of 22 settings (`bench/results/dpcosts-sweep-20261121.md`) found that the
thesis's 2:1 mismatch:unmatched ratio is the best one at every switch cost tried, and that the switch
cost has a flat optimum at about 10-14. Over the 20-pair corpus `2,1,14` gives 11,669,946 bytes against
11,783,847 for `2,1,20` (-0.97%; geometric mean vs bsdiff 4.3 0.809x -> 0.796x) at the same CPU. The
value was tuned on this project's corpus, so it is an empirical default, as the thesis's own values were.

**Traceback without O(64·m) memory.** Do not store a back-pointer per state per
position. Each live state holds a reference to a *run record*
`{offset, startPos, parent, refCount}`. A state that continues keeps its record. A
state that is entered by a switch gets a new record whose parent is the record of
the best state at `pos - 1`. When a state leaves the candidate set its record's
`refCount` is decremented, and records reaching zero are returned to a free list,
cascading to parents. Records live in pooled struct arrays (indices, not objects).
At the end, follow the parents of the best final state to produce the segments.
Live records stay small because surviving paths share ancestors.

Output: canonical `Segment[]` satisfying the invariants of section 3.

Performance target: 10 MB new file in a few seconds. The inner loop over ≤ 64
states must not allocate.

## 7. Delta encoding and patch format (`Format/`) — thesis §2.7

```csharp
public static class PatchWriter {
    public static byte[] Encode(ReadOnlySpan<byte> old, ReadOnlySpan<byte> @new,
        IReadOnlyList<Segment> alignment, EncodeOptions? options = null);
}
public static class PatchReader {
    public static byte[] Apply(ReadOnlySpan<byte> old, ReadOnlySpan<byte> patch);
    // throws PatchFormatException on any malformed, truncated or mismatching input
}
```

### 7.1 Integers

`varint`: unsigned, little-endian base 128, low group first; the **last** byte has
its top bit set (thesis: the top bit "identifies the most significant byte"). So
0 → `80`, 127 → `FF`, 128 → `00 81`. Signed values are zigzag-mapped first.
Encodings must be minimal (no redundant zero groups) and fit in 63 bits, so the
longest valid varint is 9 bytes; anything else is malformed.

### 7.2 Control string

Sequence of records `(seek: zigzag varint, copyLen: varint, extraLen: varint)`.
The decoder keeps an old-file cursor starting at 0. Per record: `cursor += seek`;
produce `copyLen` bytes from old at the cursor combined with the next `copyLen`
difference bytes, `cursor += copyLen`; then produce `extraLen` bytes from the extra
string. Records continue until the control string is exhausted; the total produced
must equal the new size. The encoder builds records from the alignment (a matched
segment optionally followed by an unmatched one; a leading unmatched segment is a
record with `copyLen = 0`). The encoder canonicalises the alignment first, so one
record's copy is one maximal run at a single offset. A record with
`copyLen = extraLen = 0` is malformed, as is a seek outside ±`int.MaxValue`.

### 7.3 Extra string

Concatenation of all unmatched bytes of the new file, in order.

### 7.4 Difference string

Let `D = Σ copyLen`. The difference string has `D` bytes, the matched segments
concatenated in new-file order. Four modes; the encoder computes all four and keeps
the one with the smallest encoded size (map + non-zero string):

| id | mode | definition |
|---|---|---|
| 0 | correction | `d = new` where `new != old`, marked in the map; else 0 (see below) |
| 1 | bytewise | `d = (new - old) mod 256` |
| 2 | little-endian multi-precision | per segment, `i` ascending |
| 3 | big-endian multi-precision | per segment, `i` descending |

Mode 0 is not a difference: the map marks `new != old` and the non-zero string
holds the new bytes at those positions (which may themselves be zero).

Multi-precision, balanced digits: carry `c = 0` at the start of each control
record's copy (called a segment below); per
byte `v = new_i - old_i + c`; `d = ((v + 128) mod 256) - 128` (so `d ∈ [-128, 127]`,
`mod` non-negative); `c = (v - d) / 256` (so `c ∈ {-1, 0, 1}`); store `d & 0xFF`.
The final carry of a segment is discarded. Decoder: `new_i = (old_i + d - c) mod 256`
with `d` sign-extended, then recompute `c` the same way. Check against the thesis's
examples: `1200 - 11FC` and `1210 - 120C` both give `(00)(04)`; `1280 - 11C8` and
`12B8 - 1200` both give `(01)(-48)` (hex, most significant digit first).

Split: the *difference map* has `D` entries in {0, 1}, 1 where the byte is non-zero
(mode 0: where `new != old`); the *non-zero string* holds those bytes in order
(mode 0: the new bytes at marked positions).

### 7.5 Difference map coding

1. Burrows–Wheeler transform of the `D`-symbol map. Definition: let `SA` be the
   suffix array of the map (convention of section 5). Output `L[0] = map[D-1]`,
   then for `i = 0 … D-1` in order: if `SA[i] == 0`, set `primary = i + 1` and emit
   nothing; else emit `map[SA[i] - 1]`. `L` has `D` symbols; `primary ∈ [1, D]` is
   "the position of the EOF character".
2. Let `q[0] < q[1] < … < q[K-1]` be the positions of the 1s in `L`. `K` equals the
   length of the non-zero string, so the decoder knows `D` and `K`.
3. Recursive midpoint coding. `enc(iLo, iHi, vLo, vHi)` codes `q[iLo..iHi)`, all
   known to lie in `[vLo, vHi)`: let `cnt = iHi - iLo`; if `cnt == 0` or
   `cnt == vHi - vLo`, return; `mid = iLo + cnt/2`; `a = mid - iLo`;
   `b = iHi - mid - 1`; code `x = q[mid] - (vLo + a)` which lies in `[0, R)` with
   `R = (vHi - vLo) - cnt + 1`; recurse on `(iLo, mid, vLo, q[mid])` then
   `(mid+1, iHi, q[mid]+1, vHi)`. Start with `enc(0, K, 0, D)`.
4. `x` is coded with a range coder. Map model id 0: uniform over `[0, R)` (when
   `R > 2^16`, code `x >> 16` uniformly over `((R-1) >> 16) + 1`, then the low 16
   bits over `2^16`; a decoded `x >= R` is malformed). Model 0 is the only model:
   adaptive midpoint models were measured at ~1% smaller maps (~0.2% of patch
   size) and rejected as not worth a second id.

Range coder (LZMA-style, carry-propagating; byte-exact spec in the header comment
of `Format/RangeCoder.cs`): 32-bit range starting at `0xFFFFFFFF`, 33-bit low,
normalise while `range < 2^24`, flush with five shifts; the always-zero first
output byte is not stored; no symbols coded means zero bytes. A symbol
`[start, start+size)` of `total <= 2^16`: `r = range / total`, `low += r·start`,
`range = r·size`. The decoder starts from 4 bytes big-endian, rejects
`code / r >= total`, and must consume the map section exactly.

Suffix sorting uses `SuffixArray.Build` from `Common/`. The inverse BWT is an
LF-mapping walk over the bit-packed `L` with a rank directory (about `D/5` bytes
of working memory); it rejects an `L`/`primary` pair that is not the transform of
any string. If `D == 0`, `K == 0` or `K == D` the map is implied, the map section
is empty, and `bwtPrimary` must be 0.

### 7.6 Compression of the three strings

Control, non-zero and extra strings are each compressed independently with
whichever of {0: none, 1: zlib level 9, 2: bzip2 level 9} is smallest.

### 7.7 File layout

```
magic        8 bytes   "BSDIFF6+"
version      1 byte    1
modes        1 byte    bits 0-1 diff mode; 2-3 control codec; 4-5 non-zero codec;
                       6-7 extra codec
mapModel     1 byte    0 = uniform midpoint (7.5)
oldSize      varint
newSize      varint
oldMd5       16 bytes
newMd5       16 bytes
ctrlCLen, ctrlLen, nzCLen, nzLen, extraCLen, extraLen, mapCLen, bwtPrimary   varints
control string (ctrlCLen bytes) ‖ non-zero string (nzCLen) ‖ map (mapCLen) ‖ extra (extraCLen)
```

Nothing may follow the extra string. MD5 is there to catch mistakes (wrong old
file, bug), exactly as in the thesis; it is not a security feature.

### 7.8 Reader hardening

`PatchReader.Apply` treats the patch as hostile. Every length is checked against
the remaining input before use; decompressed sizes must equal the declared sizes
(stop decompressing at the declared size + 1); `newSize` and `D` are bounded by
`int.MaxValue - 64`; `seek` arithmetic is checked for overflow and the cursor range
`[0, n - copyLen]` is checked per record; the range decoder never reads past the
map section; the old MD5 is verified before any work and the new MD5 before
returning. Any violation throws `PatchFormatException`; no other exception type
may escape for any input bytes (tests fuzz this).

Order of work: the control string (at most `27 × newSize` bytes) is decompressed
and fully validated — cursor ranges, total output equal to `newSize`, extra total
equal to `extraLen`, `K <= D` — before the output buffer or the map is allocated.
Decompression grows its buffer as data arrives and stops at the declared size
plus one. A compressed section must be consumed exactly by its codec.

zlib output can differ between .NET runtimes (the BCL changed zlib
implementation), so patch *bytes* are reproducible only for a given runtime;
every runtime reads every patch.

## 8. Top level

`BsDiff.Create(old, new, BsDiffOptions)` / `CreateDetailed` (patch, alignment,
`EncodeResult`, stage timings, block/DP diagnostics, coverage): block alignment and local
alignment (concurrently when `Parallel`), DP, encode. `UseLocalAlignment = false` is
"block alignment only" (thesis: far less memory, patches ~8% larger; measured here: 1.37x bsdiff 4.3);
`UseBlockAlignment = false` is local alignment only; both false encodes every byte as
unmatched (a valid patch). If local alignment throws `NotSupportedException` (inputs too
large) the run falls back to block-only. Local matches and block segments are released
(and a GC forced for inputs ≥ 1 MB) before encoding. With `Parallel` the encoder codes the
difference-mode groups {0,1}, {2}, {3} and the control and extra strings concurrently; the
choice is made afterwards in id order, so the patch does not depend on `Parallel`.
`BsPatch.Apply` = `PatchReader.Apply`.

CLIs keep the original argument order: `bsdiff6 [options] oldfile newfile patchfile`,
`bspatch6 oldfile newfile patchfile`, plus `bspatch6 --info patchfile`. Exit codes 0 / 1
(runtime failure, one-line message) / 2 (usage). Output files are written to a temporary
name in the same directory and renamed.

## 9. Deviations from, and additions to, the thesis

- Control string uses relative seeks rather than absolute offsets.
- Tie-breaking, thresholds, `TopK`, verification by direct comparison, the
  continuity candidate, the half-mismatch score for no-prediction runs: unspecified
  in the thesis, chosen here.
- The DP's local candidates use the window `pos … pos+30` (the thesis says
  "positions immediately following pos").
- The midpoint coder's probability model is unspecified in the thesis.
- Local alignment sorts the two files with a separator between them (the thesis
  writes `S#T#`; an earlier draft of this document wrongly dropped it).
- Block alignment as built differs from section 4 in these defaults, each
  switchable through `BlockAlignOptions`: a no-prediction byte scores 0.75 of a
  mismatch in tweaking (0.5 swallowed runs matching 25–50%); the FFT size `N` is
  chosen first (sizes `2^k` and `3·2^k`) and the primes are drawn just below
  `N - B`, never below `L`; continuity candidates come from the nearest predicted
  block in both directions; a tweak moves a boundary at most two blocks; a run
  removed for being too short becomes a no-prediction run.
- `DpOptions.PruneHopeless` (section 6), on by default (+0.17% bytes for ~40% less
  diff CPU); `--no-prune` gives the thesis's behaviour.
- An empty zlib section is the empty string (.NET's `ZLibStream` writes no bytes for empty
  input); a non-empty zlib or bzip2 section must be consumed exactly (zlib is read with
  SharpZipLib's `Inflater`, which reports unconsumed input).
- Default DP costs are 2,1,14 rather than the thesis's 2,1,20 (section 6): -0.97% total patch bytes
  and geometric mean 0.809x -> 0.796x vs bsdiff 4.3 on the 20-pair corpus, same CPU. Empirical, tuned
  on that corpus; `--dp-costs 2,1,20 --no-prune` is the thesis-exact DP.
- (append here as they arise)
