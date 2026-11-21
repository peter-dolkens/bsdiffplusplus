/* cm.c - conditional context-mixing delta coder (research prototype, Q3/Q4/Q5).
 *
 * Codes the new file given the old file and an alignment (segments), as ONE adaptive binary
 * arithmetic-coded stream, in new-file order (so it can be applied in a streaming fashion):
 *   matched byte : a flag "difference digit is zero" (this is the thesis's difference map bit),
 *                  then, if non-zero, the 8 bits of the digit (this is the non-zero string);
 *   extra byte   : 8 bits, order-1..6 + sparse contexts + a match model over old ++ new-so-far.
 * Each binary decision is predicted by a set of context-indexed adaptive probabilities mixed by a
 * small gated logistic mixer (lpaq-style), contexts being built from data the decoder already has:
 * the whole old file (including bytes AFTER the aligned position), the already-decoded new bytes,
 * and optionally "address hypotheses" (addr.h): predictions of the new value of an address field
 * computed from the old field and the alignment map.
 *
 * Every size printed is the length of a real stream that is decoded again in-process by a decoder
 * that has no access to the new file; the tool aborts if the round trip is not exact.
 *
 * usage: cm <old> <new> <segs.bin|greedy> [-d domain] [-x 0|1] [-a hypmask] [-v oldmap newmap]
 *           [-A ablation-mask] [-C] [-o out.cm] [-c costfile] [-q]
 *   -T  log2 of the flag/value/indirect table sizes (default 22: 16 MB + 16 MB + 4 x 8 MB); extra table is 4x
 *   -K  code only the control records, decode them again and compare (prints ctrl_bytes)
 *   -n  encode only: report the length of the (real) stream without decoding it back
 *   -C  also code the control records (the alignment) in the stream, so that old + stream -> new
 *       with no side information (requires -x 1); ctrl_bytes reports their share
 *   -M / -V  bitmask of enabled base flag / value contexts (default all 17), for ablations
 *   -c  write a float32 per new byte: the ideal code length in bits charged to that byte
 *   -d  0 correction (flag: new!=old, value: new byte)   1 bytewise   2 little-endian digits (default)
 *   -x  1 (default) code extra bytes in the same stream; 0 leave them out (diff part only)
 *   -a  bitmask of address hypotheses (see addr.h); 0 = none (default)
 *   -A  ablation bitmask: 1 no old-lookahead ctxs, 2 no indirect (old-window -> digit) ctxs,
 *       4 no match model for extra, 8 no old-continuation ctx for extra
 */
#include "lab.h"
#include <time.h>

/* ---------------- arithmetic coder: 64-bit carry-less binary coder, 16-bit probabilities -------- */
static int DEC;
static u64 X1, X2, XD;
static u8 *CB;
static i64 CP, CN, CCAP;
static inline int ac(int bit, u32 p)   /* p = P(bit==1) * 65536, in 1..65535 */
{
    u64 xmid = X1 + ((X2 - X1) >> 16) * p;
    if (DEC) bit = XD <= xmid;
    if (bit) X2 = xmid; else X1 = xmid + 1;
    while (((X1 ^ X2) >> 56) == 0) {
        if (DEC) { XD = (XD << 8) | (CP < CN ? CB[CP] : 0); CP++; }
        else { if (CP >= CCAP) { CCAP = CCAP * 2 + 65536; CB = (u8 *)realloc(CB, CCAP); } CB[CP++] = (u8)(X2 >> 56); }
        X1 <<= 8; X2 = (X2 << 8) | 255;
    }
    return bit;
}
static void ac_start(int dec)
{
    DEC = dec; X1 = 0; X2 = ~0ULL; XD = 0;
    if (dec) { CP = 0; for (int i = 0; i < 8; i++) { XD = (XD << 8) | (CP < CN ? CB[CP] : 0); CP++; } }
    else { CP = 0; }
}
static void ac_flush(void)
{
    if (CP >= CCAP) { CCAP += 16; CB = (u8 *)realloc(CB, CCAP); }
    CB[CP++] = (u8)(X2 >> 56);   /* then implicit zeros: X1 <= (X2>>56)<<56 <= X2 */
    CN = CP;
}

/* ---------------- adaptive probabilities, mixers ---------------- */
static float STR[65536], RECIP[1024];
static inline float squashf(float x)
{
    if (x > 20) x = 20;
    if (x < -20) x = -20;
    return 1.0f / (1.0f + expf(-x));
}
/* slot: (p22 << 10 | n) ^ 0x80000000 so that calloc'd memory means p = 0.5, n = 0 */
#define SLOT_P16(s) ((((s) ^ 0x80000000u)) >> 16)
static inline void slot_update(u32 *s, int bit, u32 limit)
{
    u32 v = *s ^ 0x80000000u;
    u32 n = v & 1023;
    i32 p = (i32)(v >> 10);
    i32 target = bit ? (1 << 22) - 1 : 0;
    p += (i32)((float)(target - p) * RECIP[n]);
    if (n < limit) n++;
    *s = (((u32)p << 10) | n) ^ 0x80000000u;
}
static inline u32 H2(u32 a, u32 b)
{
    u64 x = (((u64)a << 32) | b) * 0x9E3779B97F4A7C15ULL;
    x ^= x >> 29; x *= 0xBF58476D1CE4E5B9ULL;
    return (u32)(x >> 32);
}
#define H3(a, b, c) H2(H2(a, b), c)

#define MAXIN 64
#define MAXK 4
typedef struct {
    int n, K, nsel[MAXK], nsel2;
    float *w[MAXK], *w2;
    float lr, lr2;
    u32 limit;
    /* per-decision scratch */
    u32 *sl[MAXIN];
    signed char ex[MAXIN];     /* -1: slot predicts the bit; 0/1: slot predicts "bit == ex" */
} Net;
static void net_init(Net *t, int n, int K, const int *nsel, int nsel2, float lr, u32 limit)
{
    t->n = n; t->K = K; t->nsel2 = nsel2; t->lr = lr; t->lr2 = lr * 0.3f; t->limit = limit;
    for (int k = 0; k < K; k++) {
        t->nsel[k] = nsel[k];
        t->w[k] = (float *)malloc(sizeof(float) * n * nsel[k]);
        for (int i = 0; i < n * nsel[k]; i++) t->w[k][i] = 0.25f;
    }
    t->w2 = (float *)malloc(sizeof(float) * K * nsel2);
    for (int i = 0; i < K * nsel2; i++) t->w2[i] = 1.0f / K;
    for (int i = 0; i < MAXIN; i++) { t->sl[i] = NULL; t->ex[i] = -1; }
}
static void net_free(Net *t) { for (int k = 0; k < t->K; k++) free(t->w[k]); free(t->w2); }
/* codes one bit; returns it.  acc accumulates the ideal code length in bits (encoder only). */
static inline int net_code(Net *t, const int *sel, int sel2, int bit, double *acc)
{
    int n = t->n, K = t->K, na = 0;
    int act[MAXIN];
    float x[MAXIN], s[MAXK], pk[MAXK];
    for (int i = 0; i < n; i++) {
        if (!t->sl[i]) continue;
        float st = STR[SLOT_P16(*t->sl[i])];
        x[na] = t->ex[i] < 0 ? st : (t->ex[i] ? st : -st);
        act[na++] = i;
    }
    float dot2 = 0;
    float *w2 = t->w2 + sel2 * K;
    for (int k = 0; k < K; k++) {
        const float *w = t->w[k] + sel[k] * n;
        float dot = 0;
        for (int a = 0; a < na; a++) dot += w[act[a]] * x[a];
        if (dot > 20) dot = 20;
        if (dot < -20) dot = -20;
        s[k] = dot; pk[k] = squashf(dot);
        dot2 += w2[k] * dot;
    }
    float p = squashf(dot2);
    i32 p16 = (i32)(p * 65536.0f);
    if (p16 < 1) p16 = 1;
    if (p16 > 65535) p16 = 65535;
    bit = ac(bit, (u32)p16);
    if (acc) *acc += -log2(bit ? p16 / 65536.0 : 1.0 - p16 / 65536.0);
    float e2 = ((float)bit - p) * t->lr2;
    for (int k = 0; k < K; k++) {
        w2[k] += e2 * s[k];
        float *w = t->w[k] + sel[k] * n;
        float e = ((float)bit - pk[k]) * t->lr;
        for (int a = 0; a < na; a++) w[act[a]] += e * x[a];
    }
    for (int a = 0; a < na; a++) {
        int i = act[a];
        slot_update(t->sl[i], t->ex[i] < 0 ? bit : (bit == t->ex[i]), t->limit);
    }
    return bit;
}

/* ---------------- globals: data, options ---------------- */
static u8 *OLD, *NEW;      /* NEW is the true new file when encoding, the output buffer when decoding */
static i64 N, M;
static Seg *SEGS;
static i32 NSEG;
static int DOM = 2, XMODE = 1, ABL = 0, QUIET = 0;
static u32 FMASK = 0xFFFFFFFFu, VMASK = 0xFFFFFFFFu;   /* which base flag / value contexts are enabled */
static u32 AMASK = 0;
static double BITS_FLAG, BITS_VAL, BITS_EXTRA;
static int CTRL = 0;       /* -C: control records are part of the stream */
static int CTRLONLY = 0;   /* -K: code only the control records (and verify them) */
static int NOVERIFY = 0;   /* -n: encode only (the stream is real, but is not decoded back) */
static i64 CTRL_BYTES;
static const char *VAO, *VAN;
static float *COST;      /* optional: ideal code length in bits charged to each new byte */
static i64 CNT_FLAG, CNT_VAL, CNT_EXTRA;

#include "addr.h"

static int TFB = 22, TVB = 22, TEB = 24, INDB = 22;   /* log2 table sizes (-T sets TFB=TVB=INDB=t, TEB=t+2) */
#define MMB 22
static u32 *TF, *TV, *TE;
static u16 *IND[4];
static i32 *MMT;
#define NHYP_MAX 24
#define NF (17 + NHYP_MAX)
#define NV (17 + NHYP_MAX)
#define NE 13
static Net netF, netV, netE;

static void model_init(void)
{
    /* TV and TE are used as 64-byte buckets (15 nodes of a nibble's bit tree), so align them to cache lines */
    TF = (u32 *)calloc(1u << TFB, 4);
    TV = (u32 *)aligned_alloc(64, (size_t)4 << TVB); memset(TV, 0, (size_t)4 << TVB);
    TE = (u32 *)aligned_alloc(64, (size_t)4 << TEB); memset(TE, 0, (size_t)4 << TEB);
    for (int k = 0; k < 4; k++) IND[k] = (u16 *)calloc(1u << INDB, 2);
    MMT = (i32 *)malloc(sizeof(i32) << MMB);
    for (u32 i = 0; i < (1u << MMB); i++) MMT[i] = -1;
    int sf[3] = { 256, 64, 256 }, sv[3] = { 256, 256, 64 }, se[3] = { 256, 256, 256 };
    net_init(&netF, NF, 3, sf, 4, 0.004f, 255);
    net_init(&netV, NV, 3, sv, 4, 0.004f, 127);
    net_init(&netE, NE, 3, se, 4, 0.003f, 255);
}
static void model_free(void)
{
    free(TF); free(TV); free(TE); free(MMT);
    for (int k = 0; k < 4; k++) free(IND[k]);
    net_free(&netF); net_free(&netV); net_free(&netE);
}

static inline u32 mm_hash(const u8 *p)   /* hash of the 6 bytes p[-6..-1] */
{
    u64 v = 0;
    memcpy(&v, p - 6, 6);
    return (u32)((v * 0x9E3779B97F4A7C15ULL) >> (64 - MMB));
}
/* virtual concatenation old ++ new */
static inline int vbyte(i64 v) { return v < N ? OLD[v] : NEW[v - N]; }


/* ---------------- control records in the same arithmetic stream (-C) ----------------
 * A record is (diagonal, copyLen, extraLen) as in DESIGN 7.2, but the old position is coded as a
 * "diagonal" (oldPos - newPos) relative to a small move-to-front list of recently used diagonals:
 * returning to the diagonal used two or three segments ago (an excursion and back) costs about a
 * bit instead of two full seeks.  Numbers use an adaptive Elias-gamma code: the bit length through
 * a context-coded bit tree, the top two mantissa bits adaptively, the rest verbatim. */
static u16 CP_[16][4096];
static inline int cbit(int bit, u16 *p)
{
    bit = ac(bit, *p < 64 ? 64 : *p > 65472 ? 65472 : *p);
    if (bit) *p += (65536 - *p) >> 5; else *p -= *p >> 5;
    return bit;
}
static u32 cnum(u32 v, int set, int ctx)       /* v >= 0, coded as v + 1 */
{
    u16 *P = CP_[set] + ctx * 256;
    u32 x = v + 1;
    int nb = 0;
    if (!DEC) { nb = 32 - __builtin_clz(x); }
    int t = 1;                                  /* bit tree over nb - 1 in 0..31 (5 bits) */
    for (int b = 4; b >= 0; b--) t = t << 1 | cbit(DEC ? 0 : ((nb - 1) >> b) & 1, &P[t]);
    nb = (t & 31) + 1;
    u32 r = 1;
    for (int b = nb - 2; b >= 0; b--) {
        int bit = DEC ? 0 : (x >> b) & 1;
        if (b >= nb - 3) bit = cbit(bit, &P[64 + nb * 4 + (nb - 2 - b) * 2 + (b == nb - 3 ? (int)(r & 1) : 0)]);
        else bit = ac(bit, 32768);
        r = r << 1 | (u32)bit;
    }
    return r - 1;
}
static Seg *code_control(Seg *in, i32 *nseg)
{
    for (int a = 0; a < 16; a++) for (int b = 0; b < 4096; b++) CP_[a][b] = 32768;
    i64 rep[4] = { 0, 0, 0, 0 };
    i32 cap = DEC ? 1024 : *nseg + 2, cnt = 0, i = 0;
    Seg *out = DEC ? (Seg *)malloc(sizeof(Seg) * cap) : in;
    i64 npos = 0;
    int prevsym = 0, prevcb = 0;
    while (npos < M) {
        i64 copy = 0, extra = 0, diag = 0;
        if (!DEC) {
            if (i < *nseg && in[i].opos >= 0) { copy = in[i].len; diag = (i64)in[i].opos - in[i].npos; i++; }
            if (i < *nseg && in[i].opos < 0) { extra = in[i].len; i++; }
        }
        copy = cnum((u32)copy, 0, prevsym);
        if (copy) {
            int sym = 4;
            if (!DEC) for (int r = 0; r < 4; r++) if (rep[r] == diag) { sym = r; break; }
            int cb = copy < 16 ? 0 : copy < 64 ? 1 : copy < 1024 ? 2 : 3;
            u16 *P = CP_[1] + (prevsym * 4 + cb) * 8;
            int s0 = cbit(sym == 4, &P[0]);
            if (s0) {
                sym = 4;
                i64 d = diag - rep[0];
                int neg = cbit(d < 0, &CP_[2][prevsym]);
                u32 mag = cnum(DEC ? 0 : (u32)((d < 0 ? -d : d) - 1), 3, 0);
                d = (i64)mag + 1;
                diag = rep[0] + (neg ? -d : d);
            } else {
                int hi = cbit(sym >> 1, &P[1]);
                int lo = cbit(sym & 1, &P[2 + hi]);
                sym = hi << 1 | lo;
                diag = rep[sym];
            }
            int at = sym < 4 ? sym : 3;       /* move to front */
            for (int r = at; r > 0; r--) rep[r] = rep[r - 1];
            rep[0] = diag;
            prevsym = sym; prevcb = cb;
        }
        extra = cnum((u32)extra, 4, (copy ? 1 : 0) + 2 * prevcb);
        if (DEC) {
            if (cnt + 2 > cap) { cap *= 2; out = (Seg *)realloc(out, sizeof(Seg) * cap); }
            if (copy) {
                if (npos + diag < 0 || npos + diag + copy > N || npos + copy > M) die("corrupt control");
                out[cnt].npos = (i32)npos; out[cnt].len = (i32)copy; out[cnt].opos = (i32)(npos + diag); cnt++;
            }
            if (extra) { if (npos + copy + extra > M) die("corrupt control"); out[cnt].npos = (i32)(npos + copy); out[cnt].len = (i32)extra; out[cnt].opos = -1; cnt++; }
            if (!copy && !extra) die("corrupt control");
        }
        npos += copy + extra;
    }
    if (DEC) *nseg = cnt;
    return out;
}

/* ---------------- the coder (same code path for encoding and decoding) ---------------- */
static void run(int dec)
{
    ac_start(dec);
    if (CTRL) {
        SEGS = code_control(SEGS, &NSEG);
        if (!dec) CTRL_BYTES = CP;
        if (CTRLONLY) { if (!dec) { ac_flush(); CTRL_BYTES = CN; } return; }
    }
    addr_setup(VAO, VAN);       /* needs the segments: built here by encoder and decoder alike */
    u32 fh = 0; u64 dh = 0;
    int lastnz[3] = { 0, 0, 0 }, lnzp[4] = { 0, 0, 0, 0 }, zrun = 0;
    /* nz-string history for its match model */
    i64 D = 0;
    for (i32 s = 0; s < NSEG; s++) if (SEGS[s].opos >= 0) D += SEGS[s].len;
    u8 *nzh = (u8 *)malloc(D + 8);
    i64 nzn = 0;
    i32 *nzt = (i32 *)malloc(sizeof(i32) << 18);
    for (int i = 0; i < (1 << 18); i++) nzt[i] = -1;
    i64 nzmp = -1; int nzml = 0;
    /* match model for extra bytes: index all of old first */
    if (XMODE && !(ABL & 4))
        for (i64 i = 6; i <= N; i++) MMT[mm_hash(OLD + i)] = (i32)i;
    i64 mmp = -1; int mml = 0;
    i64 cont = -1;     /* old position continuing the previous matched segment */
    int selF[3], selV[3], selE[3];
    addr_begin();

    for (i32 sgi = 0; sgi < NSEG; sgi++) {
        Seg sg = SEGS[sgi];
        if (sg.opos >= 0) {
            int carry = 0;
            for (i32 q = 0; q < sg.len; q++) {
                i64 i = (i64)sg.opos + q, j = (i64)sg.npos + q;
                int ph = (int)(j & 3);
                int o = OLD[i], o1 = i > 0 ? OLD[i - 1] : 0, o2 = i > 1 ? OLD[i - 2] : 0, o3 = i > 2 ? OLD[i - 3] : 0;
                int a1 = OLD[i + 1], a2 = OLD[i + 2], a3 = OLD[i + 3];
                if (ABL & 1) a1 = a2 = a3 = 0;
                u32 win[4];
                win[0] = (u32)o | (u32)a1 << 8 | (u32)a2 << 16 | (u32)a3 << 24;
                win[1] = (u32)o1 | (u32)o << 8 | (u32)a1 << 16 | (u32)a2 << 24;
                win[2] = (u32)o2 | (u32)o1 << 8 | (u32)o << 16 | (u32)a1 << 24;
                win[3] = (u32)o3 | (u32)o2 << 8 | (u32)o1 << 16 | (u32)o << 24;
                u16 *indp[4]; int iv[4];
                for (int k = 0; k < 4; k++) {
                    indp[k] = &IND[k][H2(win[k], 77 + k) >> (32 - INDB)];
                    iv[k] = (ABL & 2) ? 0 : *indp[k];
                }
                /* address hypotheses: predicted new byte per hypothesis (or -1) */
                int hp[NHYP_MAX]; u32 hc[NHYP_MAX];
                int nh = addr_predict(i, j, q, sg.len, hp, hc);
                int hd[NHYP_MAX];   /* predicted digit */
                for (int k = 0; k < nh; k++) {
                    if (hp[k] < 0) { hd[k] = -1; continue; }
                    if (DOM == 2) { int v = hp[k] - o + carry; hd[k] = (((v + 128) & 0xFF) - 128) & 0xFF; }
                    else hd[k] = (hp[k] - o) & 0xFF;
                }

                /* ---- flag ---- */
                Net *t = &netF;
                int qs = q < 4 ? q : q < 8 ? 4 : q < 16 ? 5 : q < 64 ? 6 : 7;
                int rem = sg.len - 1 - q;
                int qe = rem < 4 ? rem : rem < 8 ? 4 : rem < 16 ? 5 : rem < 64 ? 6 : 7;
                int zr = zrun < 31 ? zrun : 31;
                u32 sparse = ((fh >> 3) & 1) | ((fh >> 7) & 1) << 1 | ((fh >> 11) & 1) << 2 | ((fh >> 15) & 1) << 3 | (fh & 7) << 4 | (u32)ph << 7;
                u32 cf[17];
                cf[0] = H2(1, fh & 0xFFFF);
                cf[1] = H3(2, (fh & 0xFF) | (u32)ph << 8, o);
                cf[2] = H3(3, (u32)o1 << 8 | o, fh & 15);
                cf[3] = H3(4, (u32)o | (u32)a1 << 8 | (u32)ph << 16, fh & 3);
                cf[4] = H3(5, win[2], fh & 1);
                cf[5] = H2(6, sparse);
                cf[6] = H3(7, (u32)zr | (u32)ph << 5, lastnz[0]);
                for (int k = 0; k < 4; k++) cf[7 + k] = H3(8 + k, (u32)(iv[k] >> 8) | (u32)((iv[k] & 255) != 0) << 1 | (fh & 3) << 2, 0);
                cf[11] = H3(12, (u32)(dh & 0xFF) | (u32)((dh >> 24) & 0xFF) << 8, (u32)ph | (u32)(carry + 1) << 2);
                cf[12] = H3(13, win[0], fh & 1);
                cf[13] = H2(14, (u32)ph | (u32)a3 << 2 | (fh & 7) << 10);
                cf[14] = H2(15, (u32)(i & 3) | (u32)((ABL & 1) ? 0 : OLD[i | 3]) << 2 | (fh & 3) << 10);
                cf[15] = H2(16, (u32)qs | (u32)qe << 3);
                cf[16] = H2(17, (u32)ph);
                for (int k = 0; k < 17; k++) { t->sl[k] = ((FMASK >> k) & 1) ? &TF[cf[k] >> (32 - TFB)] : NULL; t->ex[k] = -1; }
                for (int k = 0; k < NHYP_MAX; k++) {
                    if (k < nh && hd[k] >= 0) {
                        t->sl[17 + k] = &TF[H3(40 + k, hc[k], fh & 1) >> (32 - TFB)];
                        t->ex[17 + k] = hd[k] != 0;
                    } else t->sl[17 + k] = NULL;
                }
                selF[0] = fh & 0xFF; selF[1] = ph | (zr > 15 ? 15 : zr) << 2; selF[2] = o;
                int truth = 0, flag;
                int nb = 0, d = 0;
                if (!dec) {
                    nb = NEW[j];
                    if (DOM == 2) { int c = carry; d = mp_digit((u8)nb, (u8)o, &c); }
                    else d = (nb - o) & 0xFF;
                    truth = d != 0;
                }
                double b0 = BITS_FLAG + BITS_VAL;
                flag = net_code(t, selF, ph, truth, dec ? NULL : &BITS_FLAG);
                CNT_FLAG++;

                /* ---- value ---- */
                int sym = 0;
                if (flag) {
                    t = &netV;
                    /* nz-history match model */
                    int mpred = -1;
                    if (nzml > 0 && nzmp >= 0 && nzmp < nzn) mpred = nzh[nzmp];
                    u32 cv[17];
                    cv[0] = 1;
                    cv[1] = H2(2, o);
                    cv[2] = H2(3, lastnz[0]);
                    cv[3] = H2(4, (u32)lastnz[0] | (u32)lastnz[1] << 8);
                    cv[4] = H2(5, (u32)lastnz[0] | (u32)lastnz[1] << 8 | (u32)lastnz[2] << 16);
                    cv[5] = H2(6, (u32)(dh & 0xFFFF));
                    cv[6] = H2(7, (u32)((dh >> 24) & 0xFF) | (u32)ph << 8);
                    cv[7] = H2(8, (u32)lnzp[ph] | (u32)ph << 8);
                    cv[8] = H2(9, (u32)o | (u32)a1 << 8);
                    cv[9] = H2(10, (u32)o1 << 8 | o);
                    for (int k = 0; k < 4; k++) cv[10 + k] = H2(11 + k, iv[k]);
                    cv[14] = H2(15, (u32)(zr > 15 ? 15 : zr) | (u32)ph << 4 | (u32)lastnz[0] << 6);
                    cv[15] = H2(16, win[0]);
                    cv[16] = H2(17, (u32)(mpred + 1) | (u32)(nzml > 15 ? 15 : nzml) << 9);
                    int target = 0;
                    if (!dec) target = DOM == 0 ? nb : d;
                    int c0 = 1, ni = 1;
                    u32 bk[17];
                    for (int k = 0; k < 17; k++) bk[k] = (H2(cv[k], 0) >> (32 - TVB)) & ~15u;
                    selV[1] = lastnz[0];
                    for (int b = 7; b >= 0; b--) {
                        if (b == 3) { ni = 1; for (int k = 0; k < 17; k++) bk[k] = (H2(cv[k], c0) >> (32 - TVB)) & ~15u; }
                        for (int k = 0; k < 17; k++) { t->sl[k] = ((VMASK >> k) & 1) ? &TV[bk[k] + ni] : NULL; t->ex[k] = -1; }
                        for (int k = 0; k < NHYP_MAX; k++) {
                            t->sl[17 + k] = NULL;
                            if (k < nh && hd[k] > 0) {
                                int pv = DOM == 0 ? hp[k] : hd[k];
                                if (((pv | 256) >> (b + 1)) == c0) {      /* all bits so far agree with the prediction */
                                    t->sl[17 + k] = &TV[H3(60 + k, hc[k], b) >> (32 - TVB)];
                                    t->ex[17 + k] = (pv >> b) & 1;
                                }
                            }
                        }
                        selV[0] = c0; selV[2] = ph | (zr > 3 ? 3 : zr) << 2 | (b > 3 ? 4 : 0) << 2;
                        int bit = net_code(t, selV, ph, (target >> b) & 1, dec ? NULL : &BITS_VAL);
                        c0 = c0 << 1 | bit; ni = ni << 1 | bit;
                    }
                    sym = c0 & 255;
                    CNT_VAL++;
                }
                /* reconstruct */
                if (!flag) { d = 0; if (DOM == 2) nb = mp_undigit((u8)o, 0, &carry); else nb = o; }
                else if (DOM == 2) { d = sym; nb = mp_undigit((u8)o, (u8)d, &carry); }
                else if (DOM == 1) { d = sym; nb = (o + d) & 0xFF; }
                else { nb = sym; d = (nb - o) & 0xFF; }
                if (dec) NEW[j] = (u8)nb;
                else if (COST) COST[j] = (float)(BITS_FLAG + BITS_VAL - b0);
                addr_update(i, j, nb);
                /* ---- state update ---- */
                if (!(ABL & 2)) for (int k = 0; k < 4; k++) *indp[k] = (u16)(0x100 | d);
                fh = fh << 1 | (u32)(d != 0); dh = dh << 8 | (u64)d;
                if (d) {
                    /* nz match model */
                    if (nzml > 0 && nzmp >= 0 && nzmp < nzn && nzh[nzmp] == d) { nzml++; nzmp++; } else nzml = 0;
                    nzh[nzn++] = (u8)d;
                    if (nzn >= 3) {
                        u32 hh = H2(99, (u32)nzh[nzn - 1] | (u32)nzh[nzn - 2] << 8 | (u32)nzh[nzn - 3] << 16) >> (32 - 18);
                        if (nzml == 0 && nzt[hh] >= 0) { nzmp = nzt[hh]; nzml = 1; }
                        nzt[hh] = (i32)nzn;
                    }
                    lastnz[2] = lastnz[1]; lastnz[1] = lastnz[0]; lastnz[0] = d; lnzp[ph] = d; zrun = 0;
                } else if (zrun < 1 << 20) zrun++;
                if (XMODE && !(ABL & 4) && j >= 5) MMT[mm_hash(NEW + j + 1)] = (i32)(N + j + 1);
            }
            cont = (i64)sg.opos + sg.len;
            mml = 0; mmp = -1;
        } else {
            if (!XMODE) continue;
            Net *t = &netE;
            for (i32 q = 0; q < sg.len; q++) {
                i64 j = (i64)sg.npos + q;
                int ph = (int)(j & 3);
                u32 c4 = 0, c8 = 0;
                for (int k = 1; k <= 4; k++) c4 |= (u32)(j - k >= 0 ? NEW[j - k] : 0) << (8 * (k - 1));
                for (int k = 5; k <= 8; k++) c8 |= (u32)(j - k >= 0 ? NEW[j - k] : 0) << (8 * (k - 5));
                /* match model */
                if (!(ABL & 4)) {
                    if (mml == 0 && j >= 6) {
                        i32 cand = MMT[mm_hash(NEW + j)];
                        if (cand >= 0) {
                            int l = 0;
                            while (l < 32 && cand - 1 - l >= 0 && j - 1 - l >= 0 && vbyte(cand - 1 - l) == NEW[j - 1 - l]) l++;
                            if (l >= 6) { mml = l; mmp = cand; }
                        }
                    }
                }
                int mpred = -1;
                if (mml > 0 && mmp < N + j) mpred = vbyte(mmp); else mml = 0;
                int ml = mml > 31 ? 15 : mml > 15 ? 14 : mml > 11 ? 13 : mml;
                int oc = -1;
                if (!(ABL & 8) && cont >= 0 && cont + q < N) oc = OLD[cont + q];
                u32 ce[12];
                ce[0] = 1;
                ce[1] = H2(2, c4 & 0xFF);
                ce[2] = H2(3, c4 & 0xFFFF);
                ce[3] = H2(4, c4 & 0xFFFFFF);
                ce[4] = H2(5, c4);
                ce[5] = H3(6, c4, c8 & 0xFFFF);
                ce[6] = H3(7, (u32)ph | ((c4 >> 24) & 0xFF) << 2, (c8 >> 24) & 0xFF);
                ce[7] = H3(8, (u32)ph | (c4 & 0xFF) << 2, (c4 >> 24) & 0xFF);
                ce[8] = H2(9, (c4 >> 8) & 0xFFFF);
                ce[9] = H2(10, (u32)(mpred + 1) | (u32)ml << 9);
                ce[10] = H2(11, (u32)(oc + 1));
                ce[11] = H2(12, (u32)(oc + 1) | (c4 & 0xFF) << 9);
                int target = dec ? 0 : NEW[j];
                int c0 = 1, ni = 1;
                double e0 = BITS_EXTRA;
                u32 bk[12];
                for (int k = 0; k < 12; k++) bk[k] = (H2(ce[k], 0) >> (32 - TEB)) & ~15u;
                selE[2] = c4 & 0xFF;
                for (int b = 7; b >= 0; b--) {
                    if (b == 3) { ni = 1; for (int k = 0; k < 12; k++) bk[k] = (H2(ce[k], c0) >> (32 - TEB)) & ~15u; }
                    for (int k = 0; k < 12; k++) { t->sl[k] = &TE[bk[k] + ni]; t->ex[k] = -1; }
                    t->sl[12] = NULL;
                    if (mpred >= 0 && ((mpred | 256) >> (b + 1)) == c0) {
                        t->sl[12] = &TE[H2(200, (u32)ml | (u32)b << 4) >> (32 - TEB)];
                        t->ex[12] = (mpred >> b) & 1;
                    }
                    selE[0] = c0;
                    selE[1] = (t->sl[12] ? 1 + ((mpred >> b) & 1) : 0) | ml << 2 | ph << 6;
                    int bit = net_code(t, selE, ph, (target >> b) & 1, dec ? NULL : &BITS_EXTRA);
                    c0 = c0 << 1 | bit; ni = ni << 1 | bit;
                }
                int nb = c0 & 255;
                if (dec) NEW[j] = (u8)nb;
                else if (COST) COST[j] = (float)(BITS_EXTRA - e0);
                CNT_EXTRA++;
                if (mml > 0) { if (mpred == nb) { if (mml < 65535) mml++; mmp++; } else mml = 0; }
                if (!(ABL & 4) && j >= 5) MMT[mm_hash(NEW + j + 1)] = (i32)(N + j + 1);
            }
        }
    }
    if (!dec) ac_flush();
    free(nzh); free(nzt);
    addr_end();
}

/* CPU time of this process (the machine is shared, wall time is not meaningful) */
static double now(void) { struct timespec ts; clock_gettime(CLOCK_PROCESS_CPUTIME_ID, &ts); return ts.tv_sec + ts.tv_nsec * 1e-9; }

int main(int argc, char **argv)
{
    if (argc < 4) die("usage: cm old new segs.bin|greedy [-d domain] [-x 0|1] [-a mask] [-v oldmap newmap] [-A abl] [-o out] [-q]");
    u8 *nw;
    OLD = load_file(argv[1], &N);
    nw = load_file(argv[2], &M);
    if (!strcmp(argv[3], "greedy")) SEGS = greedy_align(OLD, N, nw, M, &NSEG);
    else SEGS = load_segs(argv[3], &NSEG);
    const char *outp = NULL, *vo = NULL, *vn = NULL, *costp = NULL;
    for (int a = 4; a < argc; a++) {
        if (!strcmp(argv[a], "-d")) DOM = atoi(argv[++a]);
        else if (!strcmp(argv[a], "-x")) XMODE = atoi(argv[++a]);
        else if (!strcmp(argv[a], "-a")) AMASK = (u32)strtoul(argv[++a], NULL, 0);
        else if (!strcmp(argv[a], "-A")) ABL = atoi(argv[++a]);
        else if (!strcmp(argv[a], "-o")) outp = argv[++a];
        else if (!strcmp(argv[a], "-v")) { vo = argv[++a]; vn = argv[++a]; }
        else if (!strcmp(argv[a], "-C")) CTRL = 1;
        else if (!strcmp(argv[a], "-n")) NOVERIFY = 1;
        else if (!strcmp(argv[a], "-T")) { TFB = TVB = INDB = atoi(argv[++a]); TEB = TFB + 2; }
        else if (!strcmp(argv[a], "-K")) { CTRL = 1; CTRLONLY = 1; }
        else if (!strcmp(argv[a], "-q")) QUIET = 1;
        else if (!strcmp(argv[a], "-c")) costp = argv[++a];
        else if (!strcmp(argv[a], "-M")) FMASK = (u32)strtoul(argv[++a], NULL, 0);
        else if (!strcmp(argv[a], "-V")) VMASK = (u32)strtoul(argv[++a], NULL, 0);
        else die("bad option");
    }
    for (int i = 0; i < 65536; i++) { double p = (i + 0.5) / 65536.0; STR[i] = (float)log(p / (1 - p)); }
    for (int i = 0; i < 1024; i++) RECIP[i] = (float)(1.0 / (i + 1.6));
    VAO = vo; VAN = vn;

    if (CTRLONLY) {
        /* control records only: encode, decode, compare the segment lists */
        Seg *orig = SEGS; i32 on = NSEG;
        NEW = nw;
        run(0);
        SEGS = NULL; NSEG = 0;
        run(1);
        int same = NSEG == on && memcmp(SEGS, orig, sizeof(Seg) * (size_t)on) == 0;
        printf("{\"ctrl_bytes\":%lld,\"segments\":%d,\"ok\":%s}\n", (long long)CTRL_BYTES, on, same ? "true" : "false");
        if (!same) die("CONTROL ROUND TRIP FAILED");
        return 0;
    }
    /* encode */
    if (costp) COST = (float *)calloc(M + 1, sizeof(float));
    NEW = nw;
    model_init();
    double t0 = now();
    run(0);
    double t1 = now();
    model_free();
    i64 csize = CN;
    if (outp) save_file(outp, CB, CN);
    if (costp) save_file(costp, (const u8 *)COST, M * (i64)sizeof(float));
    double bf = BITS_FLAG, bv = BITS_VAL, be = BITS_EXTRA;
    i64 cf = CNT_FLAG, cv = CNT_VAL, ce = CNT_EXTRA;
    double t2 = 0, t3 = 0;
    int ok = 1;
    if (!NOVERIFY) {
        /* decode into a fresh buffer; extra bytes are part of the stream only when XMODE */
        u8 *out = (u8 *)calloc(M + 64, 1);
        if (!XMODE) for (i32 s = 0; s < NSEG; s++) if (SEGS[s].opos < 0) memcpy(out + SEGS[s].npos, nw + SEGS[s].npos, SEGS[s].len);
        NEW = out;
        if (CTRL) { SEGS = NULL; NSEG = 0; }      /* the decoder gets the alignment from the stream only */
        CNT_FLAG = CNT_VAL = CNT_EXTRA = 0;
        model_init();
        t2 = now();
        run(1);
        t3 = now();
        model_free();
        ok = memcmp(out, nw, M) == 0;
    }
    printf("{\"bytes\":%lld,\"ctrl_bytes\":%lld,\"flag_bytes\":%.0f,\"val_bytes\":%.0f,\"extra_bytes\":%.0f,\"D\":%lld,\"K\":%lld,\"E\":%lld,"
           "\"enc_s\":%.2f,\"dec_s\":%.2f,\"verified\":%s,\"ok\":%s}\n",
           (long long)csize, (long long)CTRL_BYTES, bf / 8, bv / 8, be / 8, (long long)cf, (long long)cv, (long long)ce, t1 - t0, t3 - t2, NOVERIFY ? "false" : "true", ok ? "true" : "false");
    if (!ok) die("ROUND TRIP FAILED");
    return 0;
}
