/* addr.h - alignment-driven address prediction (Q4).
 *
 * Given the alignment (segments), FWD[] maps an old file offset to the new file offset it is
 * aligned with (extrapolated across unmatched gaps).  An "address hypothesis" reads a field of the
 * OLD file as some kind of reference, computes the old target, maps the target through FWD, and
 * re-encodes the field as it should appear at the corresponding NEW location.  Everything here is
 * computable by the patcher from the old file and the control string alone.
 *
 * File offsets vs virtual addresses: a VaMap is a list of regions (off_lo, off_hi, va_lo) plus an
 * image base.  With no map given, a single identity region is used (va == file offset): that is
 * the fully format-agnostic mode.  tools/vamap.py produces maps from ELF/Mach-O/PE headers.
 *
 * Requires globals: OLD, NEW, N, M, SEGS, NSEG, AMASK.
 *
 * Hypothesis slots (AMASK bit -> slots):
 *   1  x86 rel32, field starting 0..3 bytes back                  -> slots 0..3
 *   2  abs64 pointer at 8-aligned / 4-mod-8 old offset            -> slots 4, 5
 *   4  abs32 / rva32 at 4-aligned old offset (value is VA; value+base is VA) -> slots 6, 7
 *   8  ARM64 B/BL imm26                                            -> slot 8
 *  16  ARM64 ADRP (paired with following ADD/LDR/STR, else page only), and the pair's low12 -> slots 9, 10
 *  32  ARM64 imm19 branches / LDR literal                          -> slot 11
 * 128  self-relative 32-bit offset (target = field address + value) at 4-aligned old offset  -> slot 12
 * 256  base-relative 32-bit offset at 4-aligned old offset, one slot per (old base, new base) pair
 *      listed in the old map file as "rbase <old_off> <new_off>"  -> slots 13..20
 *  64  x86 rel32 with trailing immediate (k = 1 or 4, chosen by a 2-byte opcode rule) folded into slots 0..3
 */
#ifndef ADDR_H
#define ADDR_H
typedef struct { i64 lo, hi, va; } Reg;
typedef struct { Reg r[64]; int n; i64 base; int last; } VaMap;
static VaMap VO, VN;
static i32 *FWD;
static u8 *COV;
#define A_NHYP 21
#define A_MAXRB 8
typedef struct { i64 co, cn; } RBase;
static RBase RB[A_MAXRB];
static int NRB;

static void vamap_load(VaMap *m, const char *path, i64 size)
{
    m->n = 0; m->base = 0; m->last = 0;
    if (!path || !strcmp(path, "-")) { m->r[0].lo = 0; m->r[0].hi = size; m->r[0].va = 0; m->n = 1; return; }
    FILE *f = fopen(path, "r");
    if (!f) { perror(path); exit(1); }
    char line[256];
    long long a, b, c;
    while (fgets(line, sizeof line, f)) {
        if (sscanf(line, "rbase %lld %lld", &a, &b) == 2) { if (NRB < A_MAXRB) { RB[NRB].co = a; RB[NRB].cn = b; NRB++; } }
        else if (sscanf(line, "base %lld", &a) == 1) m->base = a;
        else if (sscanf(line, "%lld %lld %lld", &a, &b, &c) == 3 && m->n < 64) { m->r[m->n].lo = a; m->r[m->n].hi = b; m->r[m->n].va = c; m->n++; }
    }
    fclose(f);
    if (!m->n) { m->r[0].lo = 0; m->r[0].hi = size; m->r[0].va = 0; m->n = 1; }
}
static inline int off2va(VaMap *m, i64 off, i64 *va)
{
    Reg *r = &m->r[m->last];
    if (off >= r->lo && off < r->hi) { *va = off - r->lo + r->va; return 1; }
    for (int k = 0; k < m->n; k++) { r = &m->r[k]; if (off >= r->lo && off < r->hi) { m->last = k; *va = off - r->lo + r->va; return 1; } }
    return 0;
}
static inline int va2off(const VaMap *m, i64 va, i64 *off)
{
    for (int k = 0; k < m->n; k++) { const Reg *r = &m->r[k]; if (va >= r->va && va < r->va + (r->hi - r->lo)) { *off = va - r->va + r->lo; return 1; } }
    return 0;
}
/* old VA -> new VA through the alignment; 0 if unknown */
static inline int map_va(i64 va, i64 *out)
{
    i64 off;
    if (va2off(&VO, va, &off)) {
        if (off >= N) return 0;
        i64 no = FWD[off];
        if (no < 0) return 0;
        return off2va(&VN, no, out);
    }
    /* not file-backed (e.g. .bss): shift with the end of the nearest preceding region, up to 8 MiB past it */
    const Reg *best = NULL;
    for (int k = 0; k < VO.n; k++) { const Reg *r = &VO.r[k]; i64 end = r->va + (r->hi - r->lo); if (end <= va && (!best || end > best->va + (best->hi - best->lo))) best = r; }
    if (!best) return 0;
    i64 end = best->va + (best->hi - best->lo);
    if (va - end > (8 << 20) || best->hi - 1 >= N || best->hi < 1) return 0;
    i64 no = FWD[best->hi - 1], nva;
    if (no < 0 || !off2va(&VN, no, &nva)) return 0;
    *out = nva + (va - (end - 1));
    return 1;
}

static int seg_len_cmp(const void *a, const void *b) { i32 la = SEGS[*(const i32 *)a].len, lb = SEGS[*(const i32 *)b].len; return la < lb ? 1 : la > lb ? -1 : (*(const i32 *)a - *(const i32 *)b); }
static void addr_setup(const char *vo, const char *vn)
{
    vamap_load(&VO, vo, N);
    vamap_load(&VN, vn, M);
    FWD = (i32 *)malloc(sizeof(i32) * (N + 1));
    COV = (u8 *)calloc(N + 1, 1);
    for (i64 i = 0; i <= N; i++) FWD[i] = -1;
    /* longest segments claim old positions first */
    i32 *ord = (i32 *)malloc(sizeof(i32) * (NSEG + 1));
    i32 c = 0;
    for (i32 s = 0; s < NSEG; s++) if (SEGS[s].opos >= 0) ord[c++] = s;
    qsort(ord, c, sizeof(i32), seg_len_cmp);
    for (i32 k = 0; k < c; k++) {
        Seg *s = &SEGS[ord[k]];
        for (i32 q = 0; q < s->len; q++) if (FWD[s->opos + q] < 0) { FWD[s->opos + q] = s->npos + q; COV[s->opos + q] = 1; }
    }
    free(ord);
    /* extrapolate into uncovered gaps from the preceding covered position (then from the following one) */
    i64 lastc = -1;
    for (i64 i = 0; i < N; i++) {
        if (COV[i]) lastc = i;
        else if (lastc >= 0) { i64 v = (i64)FWD[lastc] + (i - lastc); FWD[i] = v < M ? (i32)v : -1; }
    }
    i64 nextc = -1;
    for (i64 i = N - 1; i >= 0; i--) {
        if (COV[i]) nextc = i;
        else if (FWD[i] < 0 && nextc >= 0) { i64 v = (i64)FWD[nextc] - (nextc - i); FWD[i] = v >= 0 ? (i32)v : -1; }
    }
}
static void addr_cache_reset(void);
static void addr_begin(void) { addr_cache_reset(); }
static void addr_end(void) { free(FWD); free(COV); FWD = NULL; COV = NULL; NRB = 0; }

static inline u32 le32(const u8 *p) { u32 v; memcpy(&v, p, 4); return v; }
static inline u64 le64(const u8 *p) { u64 v; memcpy(&v, p, 8); return v; }
static inline i64 sext(u64 v, int bits) { return (i64)(v << (64 - bits)) >> (64 - bits); }

/* x86 rel32 at old offset f / new offset fn, with k trailing bytes between the field and the next instruction */
static inline int pred_rel32(i64 f, i64 fn, int k, u32 *pred)
{
    i64 va, van, t;
    if (f < 0 || f + 4 > N || fn < 0) return 0;
    if (!off2va(&VO, f, &va) || !off2va(&VN, fn, &van)) return 0;
    i32 rel = (i32)le32(OLD + f);
    if (!map_va(va + 4 + k + rel, &t)) return 0;
    i64 d = t - (van + 4 + k);
    if (d < INT32_MIN || d > INT32_MAX) return 0;
    *pred = (u32)(i32)d;
    return 1;
}
/* light x86 rule: number of immediate bytes following a RIP-relative disp32 at f, from the 2 bytes before it */
static inline int x86_trailing(i64 f)
{
    if (f < 2) return 0;
    int modrm = OLD[f - 1], op = OLD[f - 2];
    if ((modrm & 0xC7) != 0x05) return 0;
    if (op == 0x80 || op == 0x83 || op == 0xC6 || op == 0xC0 || op == 0xC1 || op == 0x6B) return 1;
    if (op == 0x81 || op == 0xC7 || op == 0x69) return 4;
    return 0;
}
static inline int pred_abs64(i64 f, u64 *pred)
{
    i64 t;
    if (f < 0 || f + 8 > N) return 0;
    u64 v = le64(OLD + f);
    if (v < 0x1000 || (v >> 48)) return 0;
    if (!map_va((i64)v, &t)) return 0;
    *pred = (u64)t;
    return 1;
}
static inline int pred_abs32(i64 f, int rva, u32 *pred)
{
    i64 t;
    if (f < 0 || f + 4 > N) return 0;
    u32 v = le32(OLD + f);
    if (v < 0x400) return 0;
    if (!map_va(rva ? VO.base + (i64)v : (i64)v, &t)) return 0;
    i64 r = rva ? t - VN.base : t;
    if (r < 0 || r > 0xFFFFFFFFLL) return 0;
    *pred = (u32)r;
    return 1;
}
/* 32-bit offset relative to a base that itself moves: v' = FWD[co + v] - cn */
static inline int pred_rbase(i64 f, int b, u32 *pred)
{
    if (f < 0 || f + 4 > N) return 0;
    u32 v = le32(OLD + f);
    if (v == 0) return 0;
    i64 x = RB[b].co + (i64)v;
    if (x >= N) return 0;
    i64 no = FWD[x];
    if (no < 0) return 0;
    i64 r = no - RB[b].cn;
    if (r <= 0 || r > 0xFFFFFFFFLL) return 0;
    *pred = (u32)r;
    return 1;
}
/* ARM64 instruction word at 4-aligned old offset f; cls: 0 B/BL, 1 ADRP paired, 2 ADRP alone, 3 low12 of a pair, 4 imm19 */
static inline int a64_lo12(u32 w2, int rd, int *scale)
{
    if ((w2 & 0xFFC00000) == 0x91000000 && ((w2 >> 5) & 31) == (u32)rd) { *scale = 0; return (int)((w2 >> 10) & 0xFFF); }
    if ((w2 & 0x3B000000) == 0x39000000 && ((w2 >> 5) & 31) == (u32)rd) {
        int sc = (int)(w2 >> 30);
        if (((w2 >> 26) & 1) && ((w2 >> 23) & 1)) sc = 4;
        *scale = sc;
        return (int)((w2 >> 10) & 0xFFF);
    }
    return -1;
}
static inline int pred_a64(i64 f, i64 fn, u32 *pred, int *cls)
{
    i64 va, van, t;
    if (f < 0 || f + 4 > N || fn < 0) return 0;
    u32 w = le32(OLD + f);
    int isb = (w & 0x7C000000) == 0x14000000, isadrp = (w & 0x9F000000) == 0x90000000;
    int is19 = (w & 0xFF000010) == 0x54000000 || (w & 0x7E000000) == 0x34000000 || (w & 0x3B000000) == 0x18000000;
    int islo = (w & 0xFFC00000) == 0x91000000 || (w & 0x3B000000) == 0x39000000;
    if (!(isb || isadrp || is19 || islo)) return 0;
    if (!off2va(&VO, f, &va) || !off2va(&VN, fn, &van)) return 0;
    if (isb) {
        if (!map_va(va + sext(w & 0x3FFFFFF, 26) * 4, &t)) return 0;
        i64 d = t - van;
        if (d & 3) return 0;
        d >>= 2;
        if (d < -(1 << 25) || d >= (1 << 25)) return 0;
        *pred = (w & 0xFC000000) | ((u32)d & 0x3FFFFFF); *cls = 0;
        return 1;
    }
    if (isadrp) {
        int rd = (int)(w & 31), scale = 0;
        i64 page = (va & ~0xFFFLL) + sext((u64)((w >> 5) & 0x7FFFF) << 2 | ((w >> 29) & 3), 21) * 4096;
        int lo = f + 8 <= N ? a64_lo12(le32(OLD + f + 4), rd, &scale) : -1;
        if (lo >= 0) { if (!map_va(page + ((i64)lo << scale), &t)) return 0; *cls = 1; }
        else { if (!map_va(page, &t)) return 0; *cls = 2; }
        i64 d = ((t & ~0xFFFLL) - (van & ~0xFFFLL)) >> 12;
        if (d < -(1 << 20) || d >= (1 << 20)) return 0;
        *pred = (w & 0x9F00001F) | (((u32)d & 3) << 29) | ((((u32)(d >> 2)) & 0x7FFFF) << 5);
        return 1;
    }
    if (islo && f >= 4) {
        u32 wp = le32(OLD + f - 4);
        if ((wp & 0x9F000000) == 0x90000000) {
            int scale = 0, lo = a64_lo12(w, (int)(wp & 31), &scale);
            if (lo >= 0) {
                i64 page = ((va - 4) & ~0xFFFLL) + sext((u64)((wp >> 5) & 0x7FFFF) << 2 | ((wp >> 29) & 3), 21) * 4096;
                if (!map_va(page + ((i64)lo << scale), &t)) return 0;
                i64 l = t & 0xFFF;
                if (l & ((1 << scale) - 1)) return 0;
                *pred = (w & ~(0xFFFu << 10)) | ((u32)(l >> scale) << 10); *cls = 3;
                return 1;
            }
        }
    }
    if (is19) {
        if (!map_va(va + sext((w >> 5) & 0x7FFFF, 19) * 4, &t)) return 0;
        i64 d = t - van;
        if (d & 3) return 0;
        d >>= 2;
        if (d < -(1 << 18) || d >= (1 << 18)) return 0;
        *pred = (w & ~(0x7FFFFu << 5)) | (((u32)d & 0x7FFFF) << 5); *cls = 4;
        return 1;
    }
    return 0;
}

/* verification state of a field: bytes fn..j-1 of NEW are known; pv[] holds the predicted field bytes.
 * 0 no byte known yet; 1 all known bytes agree and at least one differs from old; 2 all agree, none
 * differs; -1 a known byte disagrees (hypothesis dropped) */
static inline int verif(i64 f, i64 fn, int b, const u8 *pv)
{
    int st = b ? 2 : 0;
    for (int k = 0; k < b; k++) {
        if (NEW[fn + k] != pv[k]) return -1;
        if (pv[k] != OLD[f + k]) st = 1;
    }
    return st;
}
static inline u32 ah(u32 a, u32 b) { u64 x = (((u64)a << 32) | b) * 0x9E3779B97F4A7C15ULL; x ^= x >> 29; x *= 0xBF58476D1CE4E5B9ULL; return (u32)(x >> 32); }

/* per-field result cache: a field is consulted once for every byte it covers */
typedef struct { i64 f, fn; u64 p; int ok, aux; } FCache;
static FCache FC_rel[4], FC_a64[2], FC_a32[4], FC_arm, FC_self, FC_rb[A_MAXRB];
#define FC_HIT(c, f_, fn_) ((c)->f == (f_) && (c)->fn == (fn_))
static void addr_cache_reset(void)
{
    for (int k = 0; k < 4; k++) FC_rel[k].f = FC_a32[k].f = -1;
    FC_a64[0].f = FC_a64[1].f = FC_arm.f = FC_self.f = -1;
    for (int k = 0; k < A_MAXRB; k++) FC_rb[k].f = -1;
}

/* fills hp[k] (predicted NEW byte at position j, or -1) and hc[k] (context for its reliability) */
static inline int addr_predict(i64 i, i64 j, i32 q, i32 len, int *hp, u32 *hc)
{
    (void)len;
    if (!AMASK) return 0;
    for (int k = 0; k < A_NHYP; k++) hp[k] = -1;
    u8 pv[8];
    if (AMASK & 1) {
        for (int b = 0; b < 4 && b <= q; b++) {
            i64 f = i - b, fn = j - b;
            u32 p;
            int k;
            FCache *c = &FC_rel[f & 3];
            if (!FC_HIT(c, f, fn)) {
                c->f = f; c->fn = fn; c->aux = (AMASK & 64) ? x86_trailing(f) : 0;
                c->ok = pred_rel32(f, fn, c->aux, &p); c->p = p;
            }
            if (!c->ok) continue;
            p = (u32)c->p; k = c->aux;
            memcpy(pv, &p, 4);
            int v = verif(f, fn, b, pv);
            if (v < 0) continue;
            int same = p == le32(OLD + f);
            hp[b] = pv[b];
            hc[b] = ah(ah(1000 + b, (u32)(f > 0 ? OLD[f - 1] : 0) | (u32)k << 8), (u32)v | (u32)same << 2);
        }
    }
    if (AMASK & 2) {
        for (int s = 0; s < 2; s++) {
            i64 f = s == 0 ? (i & ~7LL) : (((i - 4) & ~7LL) + 4);
            int b = (int)(i - f);
            if (b < 0 || b > 7 || b > q) continue;
            u64 p;
            FCache *c = &FC_a64[s];
            if (!FC_HIT(c, f, j - b)) { c->f = f; c->fn = j - b; c->ok = pred_abs64(f, &p); c->p = p; }
            if (!c->ok) continue;
            p = c->p;
            memcpy(pv, &p, 8);
            int v = verif(f, j - b, b, pv);
            if (v < 0) continue;
            hp[4 + s] = pv[b];
            hc[4 + s] = ah(1010 + s, (u32)v | (u32)(p == le64(OLD + f)) << 2 | (u32)b << 3);
        }
    }
    if (AMASK & 4) {
        i64 f = i & ~3LL;
        int b = (int)(i - f);
        if (b <= q) {
            for (int s = 0; s < 2; s++) {
                if (s == 1 && VO.base == 0 && VN.base == 0) break;
                u32 p;
                FCache *c = &FC_a32[s];
                if (!FC_HIT(c, f, j - b)) { c->f = f; c->fn = j - b; c->ok = pred_abs32(f, s, &p); c->p = p; }
                if (!c->ok) continue;
                p = (u32)c->p;
                memcpy(pv, &p, 4);
                int v = verif(f, j - b, b, pv);
                if (v < 0) continue;
                u32 ov = le32(OLD + f);
                int mag = ov < (1u << 12) ? 0 : ov < (1u << 16) ? 1 : ov < (1u << 20) ? 2 : 3;
                hp[6 + s] = pv[b];
                hc[6 + s] = ah(1020 + s, (u32)v | (u32)(p == ov) << 2 | (u32)b << 3 | (u32)mag << 5);
            }
        }
    }
    if (AMASK & (8 | 16 | 32)) {
        i64 f = i & ~3LL;
        int b = (int)(i - f);
        u32 p = 0; int cls = 0;
        FCache *c = &FC_arm;
        if (b <= q && !FC_HIT(c, f, j - b)) { c->f = f; c->fn = j - b; c->ok = pred_a64(f, j - b, &p, &cls); c->p = p; c->aux = cls; }
        if (b <= q && FC_HIT(c, f, j - b) && c->ok) {
            p = (u32)c->p; cls = c->aux;
            int slot = cls == 0 ? 8 : cls == 1 || cls == 2 ? 9 : cls == 3 ? 10 : 11;
            int en = cls == 0 ? (AMASK & 8) : cls == 4 ? (AMASK & 32) : (AMASK & 16);
            if (en) {
                memcpy(pv, &p, 4);
                int v = verif(f, j - b, b, pv);
                if (v >= 0) {
                    hp[slot] = pv[b];
                    hc[slot] = ah(1030 + cls, (u32)v | (u32)(p == le32(OLD + f)) << 2 | (u32)b << 3 | (u32)(OLD[f + 3] >> 7) << 5);
                }
            }
        }
    }
    if ((AMASK & 256) && NRB) {
        i64 f = i & ~3LL;
        int b = (int)(i - f);
        if (b <= q) {
            for (int r = 0; r < NRB; r++) {
                u32 p;
                FCache *c = &FC_rb[r];
                if (!FC_HIT(c, f, j - b)) { c->f = f; c->fn = j - b; c->ok = pred_rbase(f, r, &p); c->p = p; }
                if (!c->ok) continue;
                p = (u32)c->p;
                memcpy(pv, &p, 4);
                int v = verif(f, j - b, b, pv);
                if (v < 0) continue;
                hp[13 + r] = pv[b];
                hc[13 + r] = ah(1050 + r, (u32)v | (u32)(p == le32(OLD + f)) << 2 | (u32)b << 3);
            }
        }
    }
    if (AMASK & 128) {
        i64 f = i & ~3LL;
        int b = (int)(i - f);
        if (b <= q) {
            u32 p;
            FCache *c = &FC_self;
            if (!FC_HIT(c, f, j - b)) { c->f = f; c->fn = j - b; c->ok = pred_rel32(f, j - b, -4, &p); c->p = p; }
            if (c->ok) {
                p = (u32)c->p;
                memcpy(pv, &p, 4);
                int v = verif(f, j - b, b, pv);
                if (v >= 0) {
                    u32 ov = le32(OLD + f);
                    hp[12] = pv[b];
                    hc[12] = ah(1040, (u32)v | (u32)(p == ov) << 2 | (u32)b << 3);
                }
            }
        }
    }
    return A_NHYP;
}
static inline void addr_update(i64 i, i64 j, int nb) { (void)i; (void)j; (void)nb; }
#endif
