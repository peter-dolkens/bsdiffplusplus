/* q4.c - oracle for alignment-driven address prediction.
 *
 * usage: q4 <old> <new> <segs.bin|greedy> [oldmap newmap]
 *
 * For every matched position where old and new bytes differ, asks: is there an address-field
 * hypothesis (addr.h) and a field placement containing this byte such that the field value
 * predicted from the OLD field and the alignment map equals the NEW field exactly (and differs
 * from the old one)?  This is an ORACLE (it looks at the new bytes to pick hypothesis and
 * placement), i.e. an upper bound on what is computable; the real cost of telling the decoder is
 * measured by cm -a.  Also reports, for hypotheses with a structural trigger that the decoder can
 * see in the old file, their precision when they predict a change / no change.
 */
#include "lab.h"
static u8 *OLD, *NEW;
static i64 N, M;
static Seg *SEGS;
static i32 NSEG;
static u32 AMASK = 0;
#include "addr.h"

enum { C_BL, C_ADRP_PAIR, C_ADRP_ALONE, C_LO12, C_IMM19, C_REL32, C_REL32K, C_ABS64_8, C_ABS64_4, C_ABS64_U,
       C_ABS32, C_RVA32, C_A32_U, C_SELFREL, C_RBASE, NCLS };
static const char *CN_[NCLS] = { "a64_bl", "a64_adrp_pair", "a64_adrp_alone", "a64_lo12", "a64_imm19", "x86_rel32", "x86_rel32_imm",
                                 "abs64_al8", "abs64_al4", "abs64_unal", "abs32_al4", "rva32_al4", "abs32_rva32_unal", "selfrel32", "rbase32_al4" };
enum { T_E8E9, T_JCC, T_RIPREL, T_BL, T_ADRP_PAIR, T_ADRP_ALONE, T_LO12, T_IMM19, T_ABS64, T_ABS32, T_RVA32, T_SELFREL, NTRIG };
static const char *TN_[NTRIG] = { "x86_e8e9", "x86_jcc", "x86_riprel", "a64_bl", "a64_adrp_pair", "a64_adrp_alone", "a64_lo12", "a64_imm19",
                                  "abs64_al8", "abs32_al4", "rva32_al4", "selfrel32_al4" };
static i64 tr[NTRIG][5];   /* n, predicts change, of which correct, predicts no change, of which correct */

/* ---- learned targets: old target VA -> actual new target VA, taught by earlier reference fields (in
 * new-file order) whose target the alignment map got wrong or could not map.  This is the adaptive
 * analogue of Zucchini's label association / "extra targets". ---- */
#define LT_BITS 22
static u64 *LT_K, *LT_V;
static void lt_put(u64 k, u64 v)
{
    u64 h = (k * 0x9E3779B97F4A7C15ULL) >> (64 - LT_BITS);
    for (int n = 0; n < 64; n++, h = (h + 1) & ((1u << LT_BITS) - 1)) if (LT_K[h] == 0 || LT_K[h] == k) { LT_K[h] = k; LT_V[h] = v; return; }
}
static int lt_get(u64 k, u64 *v)
{
    u64 h = (k * 0x9E3779B97F4A7C15ULL) >> (64 - LT_BITS);
    for (int n = 0; n < 64; n++, h = (h + 1) & ((1u << LT_BITS) - 1)) { if (LT_K[h] == 0) return 0; if (LT_K[h] == k) { *v = LT_V[h]; return 1; } }
    return 0;
}
static i64 lt_bytes[4], lt_fields[4];      /* 0 x86 e8/e9 rel32, 1 a64 b/bl, 2 a64 adrp pair, 3 abs64 */
static u8 *EX2;
static void lt_field(int type, i64 f, i64 fn, int size, i64 To, i64 Tn, int map_ok)
{
    /* map_ok: the alignment-mapped prediction equals the new field */
    int changed = memcmp(OLD + f, NEW + fn, size) != 0;
    u64 v;
    if (changed && !map_ok && lt_get((u64)To, &v) && (i64)v == Tn) {
        lt_fields[type]++;
        for (int k = 0; k < size; k++) if (OLD[f + k] != NEW[fn + k] && !EX2[fn + k]) { EX2[fn + k] = 1; lt_bytes[type]++; }
    }
    lt_put((u64)To, (u64)Tn);
}
static void lt_scan(i64 lo, i64 hi, i64 off)
{
    for (i64 f = lo; f + 4 <= hi; f++) {
        i64 va, van; u32 p; u64 p8; int cls;
        if (!off2va(&VO, f, &va) || !off2va(&VN, f + off, &van)) continue;
        if (f - 1 >= lo && (OLD[f - 1] == 0xE8 || OLD[f - 1] == 0xE9) && NEW[f + off - 1] == OLD[f - 1]) {
            i64 To = va + 4 + (i32)le32(OLD + f), Tn = van + 4 + (i32)le32(NEW + f + off);
            i64 o1;
            if (va2off(&VO, To, &o1)) {
                int ok = pred_rel32(f, f + off, 0, &p) && p == le32(NEW + f + off);
                lt_field(0, f, f + off, 4, To, Tn, ok);
            }
        }
        if ((f & 3) == 0) {
            u32 w = le32(OLD + f), wn = le32(NEW + f + off);
            if ((w & 0x7C000000) == 0x14000000 && (wn & 0xFC000000) == (w & 0xFC000000)) {
                i64 To = va + sext(w & 0x3FFFFFF, 26) * 4, Tn = van + sext(wn & 0x3FFFFFF, 26) * 4, o1;
                if (va2off(&VO, To, &o1)) {
                    int ok = pred_a64(f, f + off, &p, &cls) && p == wn;
                    lt_field(1, f, f + off, 4, To, Tn, ok);
                }
            }
            if ((w & 0x9F000000) == 0x90000000 && (wn & 0x9F00001F) == (w & 0x9F00001F) && f + 8 <= hi) {
                int sc = 0, sc2 = 0;
                u32 w2 = le32(OLD + f + 4), wn2 = le32(NEW + f + off + 4);
                int lo12 = a64_lo12(w2, (int)(w & 31), &sc), lon = a64_lo12(wn2, (int)(wn & 31), &sc2);
                if (lo12 >= 0 && lon >= 0 && sc == sc2 && (w2 & ~(0xFFFu << 10)) == (wn2 & ~(0xFFFu << 10))) {
                    i64 To = (va & ~0xFFFLL) + sext((u64)((w >> 5) & 0x7FFFF) << 2 | ((w >> 29) & 3), 21) * 4096 + ((i64)lo12 << sc);
                    i64 Tn = (van & ~0xFFFLL) + sext((u64)((wn >> 5) & 0x7FFFF) << 2 | ((wn >> 29) & 3), 21) * 4096 + ((i64)lon << sc);
                    u32 pa, pb; int c1, c2;
                    int ok = pred_a64(f, f + off, &pa, &c1) && pa == wn && pred_a64(f + 4, f + off + 4, &pb, &c2) && pb == wn2;
                    lt_field(2, f, f + off, 8, To, Tn, ok);
                }
            }
        }
        if ((f & 7) == 0 && f + 8 <= hi) {
            u64 v = le64(OLD + f), vn = le64(NEW + f + off);
            i64 o1;
            if (v >= 0x1000 && !(v >> 48) && va2off(&VO, (i64)v, &o1)) {
                int ok = pred_abs64(f, &p8) && p8 == vn;
                lt_field(3, f, f + off, 8, (i64)v, (i64)vn, ok);
            }
        }
    }
}

static void trig(int t, const u8 *pv, i64 f, i64 fn, int size)
{
    int pc = memcmp(pv, OLD + f, size) != 0;
    int ok = memcmp(pv, NEW + fn, size) == 0;
    tr[t][0]++;
    if (pc) { tr[t][1]++; tr[t][2] += ok; } else { tr[t][3]++; tr[t][4] += ok; }
}

int main(int argc, char **argv)
{
    if (argc < 4) die("usage: q4 old new segs.bin|greedy [oldmap newmap]");
    OLD = load_file(argv[1], &N);
    NEW = load_file(argv[2], &M);
    if (!strcmp(argv[3], "greedy")) SEGS = greedy_align(OLD, N, NEW, M, &NSEG);
    else SEGS = load_segs(argv[3], &NSEG);
    addr_setup(argc > 5 ? argv[4] : NULL, argc > 5 ? argv[5] : NULL);
    i64 first[NCLS] = { 0 }, any[NCLS] = { 0 }, D = 0, diff = 0, expl = 0, cov = 0, rbhit[A_MAXRB] = { 0 };
    for (i64 i = 0; i < N; i++) cov += COV[i];
    u8 *ex = (u8 *)calloc(M + 8, 1);      /* per new byte: 1 + class of first explanation */
    LT_K = (u64 *)calloc(1u << LT_BITS, 8); LT_V = (u64 *)calloc(1u << LT_BITS, 8);
    for (i32 s = 0; s < NSEG; s++) {
        Seg sg = SEGS[s];
        if (sg.opos < 0) continue;
        D += sg.len;
        i64 off = (i64)sg.npos - sg.opos;
        i64 lo = sg.opos, hi = (i64)sg.opos + sg.len;
        /* structural-trigger precision, over all fields fully inside the segment */
        for (i64 f = lo; f + 4 <= hi; f++) {
            u32 p; u64 p8; int cls; u8 pv[8];
            if (f - 1 >= lo && (OLD[f - 1] == 0xE8 || OLD[f - 1] == 0xE9) && pred_rel32(f, f + off, 0, &p)) { memcpy(pv, &p, 4); trig(T_E8E9, pv, f, f + off, 4); }
            if (f - 2 >= lo && OLD[f - 2] == 0x0F && (OLD[f - 1] & 0xF0) == 0x80 && pred_rel32(f, f + off, 0, &p)) { memcpy(pv, &p, 4); trig(T_JCC, pv, f, f + off, 4); }
            if (f - 2 >= lo && (OLD[f - 1] & 0xC7) == 0x05 && pred_rel32(f, f + off, x86_trailing(f), &p)) { memcpy(pv, &p, 4); trig(T_RIPREL, pv, f, f + off, 4); }
            if ((f & 3) == 0) {
                if (pred_a64(f, f + off, &p, &cls)) { memcpy(pv, &p, 4); trig(T_BL + cls, pv, f, f + off, 4); }
                if (pred_abs32(f, 0, &p)) { memcpy(pv, &p, 4); trig(T_ABS32, pv, f, f + off, 4); }
                if (VO.base && pred_abs32(f, 1, &p)) { memcpy(pv, &p, 4); trig(T_RVA32, pv, f, f + off, 4); }
                if (pred_rel32(f, f + off, -4, &p)) { memcpy(pv, &p, 4); trig(T_SELFREL, pv, f, f + off, 4); }
            }
            if ((f & 7) == 0 && f + 8 <= hi && pred_abs64(f, &p8)) { memcpy(pv, &p8, 8); trig(T_ABS64, pv, f, f + off, 8); }
        }
        /* oracle explanation of differing bytes */
        for (i64 i = lo; i < hi; i++) {
            i64 j = i + off;
            if (OLD[i] == NEW[j]) continue;
            diff++;
            int got[NCLS] = { 0 };
            u32 p; u64 p8; int cls;
            /* ARM64: the aligned word containing i */
            { i64 f = i & ~3LL;
              if (f >= lo && f + 4 <= hi && pred_a64(f, f + off, &p, &cls) && p != le32(OLD + f) && p == le32(NEW + f + off)) got[C_BL + cls] = 1; }
            for (int b = 0; b < 4; b++) {
                i64 f = i - b;
                if (f < lo || f + 4 > hi) continue;
                u32 ov = le32(OLD + f), nv = le32(NEW + f + off);
                if (pred_rel32(f, f + off, 0, &p) && p != ov && p == nv) got[C_REL32] = 1;
                for (int k = 1; k <= 4; k++) if (k != 3 && pred_rel32(f, f + off, k, &p) && p != ov && p == nv) got[C_REL32K] = 1;
                if (pred_rel32(f, f + off, -4, &p) && p != ov && p == nv) got[C_SELFREL] = 1;
                for (int r = 0; r < 2; r++)
                    if (pred_abs32(f, r, &p) && p != ov && p == nv) got[(f & 3) ? C_A32_U : (r ? C_RVA32 : C_ABS32)] = 1;
            }
            { i64 f = i & ~3LL;
              if (f >= lo && f + 4 <= hi)
                  for (int r = 0; r < NRB; r++)
                      if (pred_rbase(f, r, &p) && p != le32(OLD + f) && p == le32(NEW + f + off)) { got[C_RBASE] = 1; rbhit[r]++; } }
            for (int b = 0; b < 8; b++) {
                i64 f = i - b;
                if (f < lo || f + 8 > hi) continue;
                if (pred_abs64(f, &p8) && p8 != le64(OLD + f) && p8 == le64(NEW + f + off)) got[(f & 7) == 0 ? C_ABS64_8 : (f & 3) == 0 ? C_ABS64_4 : C_ABS64_U] = 1;
            }
            int f1 = -1;
            for (int c = 0; c < NCLS; c++) if (got[c]) { any[c]++; if (f1 < 0) f1 = c; }
            if (f1 >= 0) { first[f1]++; expl++; ex[j] = (u8)(1 + f1); }
        }
        EX2 = ex;
        lt_scan(lo, hi, off);
    }
    printf("{\"D\":%lld,\"diff\":%lld,\"explained\":%lld,\"old_covered\":%.4f,\"first\":{", (long long)D, (long long)diff, (long long)expl, (double)cov / (N ? N : 1));
    for (int c = 0; c < NCLS; c++) printf("%s\"%s\":%lld", c ? "," : "", CN_[c], (long long)first[c]);
    printf("},\"any\":{");
    for (int c = 0; c < NCLS; c++) printf("%s\"%s\":%lld", c ? "," : "", CN_[c], (long long)any[c]);
    printf("},\"trig\":{");
    for (int t = 0; t < NTRIG; t++) printf("%s\"%s\":[%lld,%lld,%lld,%lld,%lld]", t ? "," : "", TN_[t], (long long)tr[t][0], (long long)tr[t][1], (long long)tr[t][2], (long long)tr[t][3], (long long)tr[t][4]);
    printf("},\"learned_bytes\":{\"x86_e8e9\":%lld,\"a64_bl\":%lld,\"a64_adrp_pair\":%lld,\"abs64\":%lld}", (long long)lt_bytes[0], (long long)lt_bytes[1], (long long)lt_bytes[2], (long long)lt_bytes[3]);
    printf(",\"rbase_hits\":[");
    for (int r = 0; r < NRB; r++) printf("%s%lld", r ? "," : "", (long long)rbhit[r]);
    printf("]}\n");
    if (argc > 6) save_file(argv[6], ex, M);
    return 0;
}
