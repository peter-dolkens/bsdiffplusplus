/* lab.h - shared helpers for the research prototypes (throwaway measurement code).
 *
 * - file / segment loading
 * - a C port of dev/format-harness/GreedyAligner.cs (to reproduce the format agent's inputs)
 * - int32 qsufsort (Larsson-Sadakane, adapted from ref/bsdiff-4.3/bsdiff.c, BSD licence, C. Percival)
 * - the four difference modes of DESIGN.md 7.4
 * - BWT of a 0/1 map (DESIGN 7.5 definition) and the uniform recursive-midpoint code length
 */
#ifndef LAB_H
#define LAB_H
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>

typedef uint8_t u8;
typedef uint16_t u16;
typedef uint32_t u32;
typedef uint64_t u64;
typedef int32_t i32;
typedef int64_t i64;

typedef struct { i32 npos, len, opos; } Seg;   /* opos == -1: unmatched */

static void die(const char *m) { fprintf(stderr, "fatal: %s\n", m); exit(1); }

static u8 *load_file(const char *path, i64 *len)
{
    FILE *f = fopen(path, "rb");
    if (!f) { perror(path); exit(1); }
    fseek(f, 0, SEEK_END);
    i64 n = ftell(f);
    fseek(f, 0, SEEK_SET);
    u8 *b = (u8 *)malloc(n + 64);
    if (!b) die("oom");
    if (n && fread(b, 1, n, f) != (size_t)n) die("short read");
    memset(b + n, 0, 64);
    fclose(f);
    *len = n;
    return b;
}

static void save_file(const char *path, const u8 *b, i64 n)
{
    FILE *f = fopen(path, "wb");
    if (!f) { perror(path); exit(1); }
    if (n) fwrite(b, 1, n, f);
    fclose(f);
}

static Seg *load_segs(const char *path, i32 *count)
{
    i64 n;
    u8 *b = load_file(path, &n);
    i32 c;
    memcpy(&c, b, 4);
    if (4 + 12 * (i64)c != n) die("bad segs file");
    Seg *s = (Seg *)malloc(sizeof(Seg) * (c + 1));
    memcpy(s, b + 4, 12 * (size_t)c);
    free(b);
    *count = c;
    return s;
}

static void save_segs(const char *path, const Seg *s, i32 c)
{
    FILE *f = fopen(path, "wb");
    fwrite(&c, 4, 1, f);
    fwrite(s, 12, c, f);
    fclose(f);
}

/* ---- port of GreedyAligner.cs (seed 8, 2^22 table, extension while 2*matches-length improves) ---- */
static i32 ga_exact(const u8 *old, i64 n, i64 o, const u8 *nw, i64 m, i64 p)
{
    i32 l = 0;
    while (o + l < n && p + l < m && old[o + l] == nw[p + l] && l < (1 << 16)) l++;
    return l;
}
static u32 ga_hash(const u8 *s, i64 i)
{
    u64 v;
    memcpy(&v, s + i, 8);
    return (u32)((v * 0x9E3779B97F4A7C15ULL) >> (64 - 22));
}
static Seg *greedy_align(const u8 *old, i64 n, const u8 *nw, i64 m, i32 *count)
{
    i32 cap = 1024, cnt = 0;
    Seg *segs = (Seg *)malloc(sizeof(Seg) * cap);
    i32 *table = (i32 *)malloc(sizeof(i32) << 22);
    for (i32 i = 0; i < (1 << 22); i++) table[i] = -1;
    for (i64 i = 0; i + 8 <= n; i++) table[ga_hash(old, i)] = (i32)i;
    i64 p = 0, prevEnd = 0, lastOff = 0;
#define GA_PUSH(a, b, c) do { if (cnt + 2 > cap) { cap *= 2; segs = (Seg *)realloc(segs, sizeof(Seg) * cap); } \
        segs[cnt].npos = (i32)(a); segs[cnt].len = (i32)(b); segs[cnt].opos = (i32)(c); cnt++; } while (0)
    while (p + 8 <= m) {
        i64 bestPos = -1; i32 bestLen = 0;
        i64 c1 = p + lastOff;
        if (c1 >= 0 && c1 < n) {
            i32 l = ga_exact(old, n, c1, nw, m, p);
            if (l >= 4) { bestPos = c1; bestLen = l + 12; }
        }
        i32 c2 = table[ga_hash(nw, p)];
        if (c2 >= 0) {
            i32 l = ga_exact(old, n, c2, nw, m, p);
            if (l >= 16 && l > bestLen) { bestPos = c2; bestLen = l; }
        }
        if (bestPos < 0) { p++; continue; }
        i64 off = bestPos - p;
        i32 lenb = 0, s = 0, bestScore = 0;
        for (i32 j = 1; p - j >= prevEnd && p - j + off >= 0; j++) {
            if (old[p - j + off] == nw[p - j]) s++;
            i32 score = 2 * s - j;
            if (score > bestScore) { bestScore = score; lenb = j; }
            if (score < bestScore - 128) break;
        }
        i32 lenf = 0; s = 0; bestScore = 0;
        for (i32 i = 0; p + i < m && p + i + off < n; i++) {
            if (old[p + i + off] == nw[p + i]) s++;
            i32 score = 2 * s - (i + 1);
            if (score > bestScore) { bestScore = score; lenf = i + 1; }
            if (score < bestScore - 128) break;
        }
        if (lenf == 0) { p++; continue; }
        i64 start = p - lenb, end = p + lenf;
        if (start > prevEnd) GA_PUSH(prevEnd, start - prevEnd, -1);
        GA_PUSH(start, end - start, start + off);
        prevEnd = end; p = end; lastOff = off;
    }
    if (prevEnd < m) GA_PUSH(prevEnd, m - prevEnd, -1);
    free(table);
    /* Canonicalize: merge adjacent segments that continue each other */
    i32 w = 0;
    for (i32 i = 0; i < cnt; i++) {
        if (w > 0) {
            Seg *a = &segs[w - 1], *b = &segs[i];
            int cont = (a->opos < 0 && b->opos < 0) ||
                       (a->opos >= 0 && b->opos >= 0 && a->opos + a->len == b->opos);
            if (cont && a->npos + a->len == b->npos) { a->len += b->len; continue; }
        }
        segs[w++] = segs[i];
    }
    *count = w;
    return segs;
}

/* ---- qsufsort, int32 version of bsdiff 4.3's (suffix that is a proper prefix sorts first) ---- */
static void qs_split(i32 *I, i32 *V, i32 start, i32 len, i32 h)
{
    i32 i, j, k, x, tmp, jj, kk;
    if (len < 16) {
        for (k = start; k < start + len; k += j) {
            j = 1; x = V[I[k] + h];
            for (i = 1; k + i < start + len; i++) {
                if (V[I[k + i] + h] < x) { x = V[I[k + i] + h]; j = 0; }
                if (V[I[k + i] + h] == x) { tmp = I[k + j]; I[k + j] = I[k + i]; I[k + i] = tmp; j++; }
            }
            for (i = 0; i < j; i++) V[I[k + i]] = k + j - 1;
            if (j == 1) I[k] = -1;
        }
        return;
    }
    x = V[I[start + len / 2] + h];
    jj = 0; kk = 0;
    for (i = start; i < start + len; i++) {
        if (V[I[i] + h] < x) jj++;
        if (V[I[i] + h] == x) kk++;
    }
    jj += start; kk += jj;
    i = start; j = 0; k = 0;
    while (i < jj) {
        if (V[I[i] + h] < x) i++;
        else if (V[I[i] + h] == x) { tmp = I[i]; I[i] = I[jj + j]; I[jj + j] = tmp; j++; }
        else { tmp = I[i]; I[i] = I[kk + k]; I[kk + k] = tmp; k++; }
    }
    while (jj + j < kk) {
        if (V[I[jj + j] + h] == x) j++;
        else { tmp = I[jj + j]; I[jj + j] = I[kk + k]; I[kk + k] = tmp; k++; }
    }
    if (jj > start) qs_split(I, V, start, jj - start, h);
    for (i = 0; i < kk - jj; i++) V[I[jj + i]] = kk - 1;
    if (jj == kk - 1) I[jj] = -1;
    if (start + len > kk) qs_split(I, V, kk, start + len - kk, h);
}
/* I, V: arrays of n+1 ints.  On return I[0] == n and I[1..n] is the suffix array. */
static void qsufsort32(i32 *I, i32 *V, const u8 *old, i32 n)
{
    i32 buckets[256];
    i32 i, h, len;
    for (i = 0; i < 256; i++) buckets[i] = 0;
    for (i = 0; i < n; i++) buckets[old[i]]++;
    for (i = 1; i < 256; i++) buckets[i] += buckets[i - 1];
    for (i = 255; i > 0; i--) buckets[i] = buckets[i - 1];
    buckets[0] = 0;
    for (i = 0; i < n; i++) I[++buckets[old[i]]] = i;
    I[0] = n;
    for (i = 0; i < n; i++) V[i] = buckets[old[i]];
    V[n] = 0;
    for (i = 1; i < 256; i++) if (buckets[i] == buckets[i - 1] + 1) I[buckets[i]] = -1;
    I[0] = -1;
    for (h = 1; I[0] != -(n + 1); h += h) {
        len = 0;
        for (i = 0; i < n + 1;) {
            if (I[i] < 0) { len -= I[i]; i -= I[i]; }
            else {
                if (len) I[i - len] = -len;
                len = V[I[i]] + 1 - i;
                qs_split(I, V, i, len, h);
                i += len;
                len = 0;
            }
        }
        if (len) I[i - len] = -len;
    }
    for (i = 0; i < n + 1; i++) I[V[i]] = i;
}

/* ---- difference modes (DESIGN 7.4); mirrors src/BsDiff6/Format/DifferenceCoder.cs ---- */
static inline u8 mp_digit(u8 nb, u8 ob, int *carry)
{
    int v = (int)nb - (int)ob + *carry;
    int d = ((v + 128) & 0xFF) - 128;
    *carry = (v - d) >> 8;
    return (u8)d;
}
static inline u8 mp_undigit(u8 ob, u8 digit, int *carry)
{
    int d = (int8_t)digit;
    u8 n = (u8)(ob + d - *carry);
    int v = (int)n - (int)ob + *carry;
    *carry = (v - d) >> 8;
    return n;
}
/* Fills map[D] (0/1) and nz[]; returns K = number of non-zero entries. `ob` is the byte source for
 * old (normally the old file; may be a transformed copy indexed by old position). */
static i64 diff_split(int mode, const u8 *old, const u8 *nw, const Seg *segs, i32 nseg, u8 *map, u8 *nz)
{
    i64 p = 0, k = 0;
    for (i32 s = 0; s < nseg; s++) {
        if (segs[s].opos < 0) continue;
        const u8 *o = old + segs[s].opos, *n = nw + segs[s].npos;
        i32 len = segs[s].len;
        u8 *m = map + p;
        if (mode == 0) { for (i32 i = 0; i < len; i++) { if (n[i] != o[i]) { m[i] = 1; nz[k++] = n[i]; } else m[i] = 0; } }
        else if (mode == 1) { for (i32 i = 0; i < len; i++) { u8 d = (u8)(n[i] - o[i]); if (d) { m[i] = 1; nz[k++] = d; } else m[i] = 0; } }
        else {
            int c = 0;
            if (mode == 2) for (i32 i = 0; i < len; i++) m[i] = mp_digit(n[i], o[i], &c);
            else for (i32 i = len - 1; i >= 0; i--) m[i] = mp_digit(n[i], o[i], &c);
            for (i32 i = 0; i < len; i++) if (m[i]) { nz[k++] = m[i]; m[i] = 1; }
        }
        p += len;
    }
    return k;
}

/* ---- BWT of the map + uniform recursive midpoint code length (DESIGN 7.5) ---- */
static double mid_bits;
static void mid_rec(const i32 *q, i64 iLo, i64 iHi, i64 vLo, i64 vHi)
{
    while (1) {
        i64 cnt = iHi - iLo;
        if (cnt == 0 || cnt == vHi - vLo) return;
        i64 mid = iLo + cnt / 2;
        i64 R = (vHi - vLo) - cnt + 1;
        mid_bits += log2((double)R);
        mid_rec(q, iLo, mid, vLo, q[mid]);
        iLo = mid + 1; vLo = (i64)q[mid] + 1;
    }
}
/* Returns the exact code length in bits of the uniform midpoint model for the BWT of map[0..D).
 * (Ideal arithmetic coder; a real range coder adds < 0.1% + ~4 bytes of flush.)
 * If Lout != NULL it receives the BWT column (D symbols). */
static double bwt_midpoint_bits(const u8 *map, i64 D, i64 K, u8 *Lout, i32 *primary)
{
    if (D == 0 || K == 0) return 0;
    i32 *I = (i32 *)malloc(sizeof(i32) * (D + 1)), *V = (i32 *)malloc(sizeof(i32) * (D + 1));
    if (!I || !V) die("oom");
    qsufsort32(I, V, map, (i32)D);
    free(V);
    i32 *q = (i32 *)malloc(sizeof(i32) * (K + 1));
    i64 k = 0, li = 0;
    /* L[0] = map[D-1]; then for each SA[i]: skip SA[i]==0 (primary), else map[SA[i]-1] */
    if (map[D - 1]) q[k++] = (i32)li;
    if (Lout) Lout[li] = map[D - 1];
    li++;
    for (i64 i = 1; i <= D; i++) {
        i32 sa = I[i];
        if (sa == 0) { if (primary) *primary = (i32)i; continue; }
        u8 c = map[sa - 1];
        if (c) q[k++] = (i32)li;
        if (Lout) Lout[li] = c;
        li++;
    }
    if (k != K || li != D) die("bwt count mismatch");
    free(I);
    mid_bits = 0;
    mid_rec(q, 0, K, 0, D);
    free(q);
    return mid_bits;
}

/* varint per DESIGN 7.1 (last byte has the top bit set) */
static i64 put_varint(u8 *out, u64 v)
{
    i64 n = 0;
    while (v >= 128) { out[n++] = (u8)(v & 127); v >>= 7; }
    out[n++] = (u8)(v | 128);
    return n;
}
static inline u64 zigzag(i64 v) { return ((u64)v << 1) ^ (u64)(v >> 63); }

/* control string per DESIGN 7.2; returns its length */
static i64 build_control(const Seg *segs, i32 nseg, u8 *out)
{
    i64 n = 0, cursor = 0;
    i32 i = 0;
    while (i < nseg) {
        i64 seek = 0, copy = 0, extra = 0;
        if (segs[i].opos >= 0) { seek = segs[i].opos - cursor; copy = segs[i].len; cursor = segs[i].opos + copy; i++; }
        if (i < nseg && segs[i].opos < 0) { extra = segs[i].len; i++; }
        n += put_varint(out + n, zigzag(seek));
        n += put_varint(out + n, (u64)copy);
        n += put_varint(out + n, (u64)extra);
    }
    return n;
}
#endif
