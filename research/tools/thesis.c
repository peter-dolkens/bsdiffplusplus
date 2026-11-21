/* thesis.c - cost of the thesis ("bsdiff 6", DESIGN.md section 7) encoding for a given alignment.
 *
 * usage: thesis <old> <new> <segs.bin | greedy> [outdir]
 *
 * For each of the four difference modes: map/non-zero split, BWT + uniform recursive midpoint code
 * length for the map (true code length: sum of log2(R) over the coded midpoints, ideal arithmetic
 * coder), and real zlib -9 / bzip2 -9 sizes of the non-zero string.  Also the DESIGN 7.2 control
 * string and the extra string under zlib -9 / bzip2 -9.  Prints one JSON object.
 * With an outdir, writes map<mode>.bin (one 0/1 byte per entry), nz<mode>.bin, ctrl6.bin, and
 * segs_greedy.bin when the alignment is "greedy" (a port of dev/format-harness/GreedyAligner.cs).
 */
#include "lab.h"
#include <zlib.h>
#include <bzlib.h>

static i64 zsize(const u8 *b, i64 n)
{
    if (!n) return 0;
    uLongf cap = compressBound(n);
    u8 *o = (u8 *)malloc(cap);
    if (compress2(o, &cap, b, n, 9) != Z_OK) die("zlib");
    free(o);
    return (i64)cap;
}
static i64 bzsize(const u8 *b, i64 n)
{
    if (!n) return 0;
    unsigned cap = (unsigned)(n + n / 100 + 700);
    u8 *o = (u8 *)malloc(cap);
    if (BZ2_bzBuffToBuffCompress((char *)o, &cap, (char *)b, (unsigned)n, 9, 0, 0) != BZ_OK) die("bz2");
    free(o);
    return (i64)cap;
}
static i64 min3(i64 a, i64 b, i64 c) { i64 m = a < b ? a : b; return m < c ? m : c; }

int main(int argc, char **argv)
{
    if (argc < 4) die("usage: thesis old new segs.bin|greedy [outdir]");
    i64 n, m;
    u8 *old = load_file(argv[1], &n), *nw = load_file(argv[2], &m);
    i32 nseg;
    Seg *segs;
    const char *outdir = argc > 4 ? argv[4] : NULL;
    char path[4096];
    if (!strcmp(argv[3], "greedy")) {
        segs = greedy_align(old, n, nw, m, &nseg);
        if (outdir) { snprintf(path, sizeof path, "%s/segs_greedy.bin", outdir); save_segs(path, segs, nseg); }
    } else segs = load_segs(argv[3], &nseg);

    i64 D = 0, E = 0;
    for (i32 i = 0; i < nseg; i++) { if (segs[i].opos >= 0) D += segs[i].len; else E += segs[i].len; }
    u8 *extra = (u8 *)malloc(E + 1);
    i64 e = 0;
    for (i32 i = 0; i < nseg; i++) if (segs[i].opos < 0) { memcpy(extra + e, nw + segs[i].npos, segs[i].len); e += segs[i].len; }
    u8 *ctrl = (u8 *)malloc((i64)nseg * 30 + 64);
    i64 clen = build_control(segs, nseg, ctrl);
    if (outdir) { snprintf(path, sizeof path, "%s/ctrl6.bin", outdir); save_file(path, ctrl, clen); }

    i64 cz = zsize(ctrl, clen), cb = bzsize(ctrl, clen), ez = zsize(extra, E), eb = bzsize(extra, E);
    printf("{\"D\":%lld,\"E\":%lld,\"nseg\":%d,\"ctrl_raw\":%lld,\"ctrl_z\":%lld,\"ctrl_bz\":%lld,\"extra_z\":%lld,\"extra_bz\":%lld,\"modes\":[",
           (long long)D, (long long)E, nseg, (long long)clen, (long long)cz, (long long)cb, (long long)ez, (long long)eb);
    u8 *map = (u8 *)malloc(D + 1), *nz = (u8 *)malloc(D + 1);
    i64 best = -1; int bestMode = -1;
    for (int mode = 0; mode < 4; mode++) {
        i64 K = diff_split(mode, old, nw, segs, nseg, map, nz);
        double bits = bwt_midpoint_bits(map, D, K, NULL, NULL);
        i64 mapBytes = (K && D) ? (i64)ceil(bits / 8.0) + 4 : 0;   /* +4: range coder flush */
        i64 nzz = zsize(nz, K), nzb = bzsize(nz, K);
        i64 tot = mapBytes + min3(nzz, nzb, K);
        if (best < 0 || tot < best) { best = tot; bestMode = mode; }
        printf("%s{\"mode\":%d,\"K\":%lld,\"map\":%lld,\"nz_z\":%lld,\"nz_bz\":%lld}", mode ? "," : "",
               mode, (long long)K, (long long)mapBytes, (long long)nzz, (long long)nzb);
        if (outdir) {
            snprintf(path, sizeof path, "%s/map%d.bin", outdir, mode); save_file(path, map, D);
            snprintf(path, sizeof path, "%s/nz%d.bin", outdir, mode); save_file(path, nz, K);
        }
    }
    /* header: magic 8 + version/modes/mapModel 3 + sizes ~8 + md5 32 + 8 varints ~20 */
    i64 header = 8 + 3 + 8 + 32 + 20;
    i64 total = header + min3(cz, cb, clen) + min3(ez, eb, E) + best;
    printf("],\"best_mode\":%d,\"diff_best\":%lld,\"header\":%lld,\"total\":%lld}\n", bestMode, (long long)best, (long long)header, (long long)total);
    return 0;
}
