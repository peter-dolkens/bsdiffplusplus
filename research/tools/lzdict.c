/* lzdict.c - raw LZMA2 (liblzma) with an optional preset dictionary; prints the compressed size after
 * verifying that decoding with the same dictionary reproduces the input (true round trip).
 * usage: lzdict <dictfile | -> <input> [lc lp pb]
 * build: clang -O2 -I/opt/homebrew/include -L/opt/homebrew/lib -o bin/lzdict tools/lzdict.c -llzma
 */
#include "lab.h"
#include <lzma.h>

int main(int argc, char **argv)
{
    if (argc < 3) die("usage: lzdict dict|- input [lc lp pb]");
    i64 dn = 0, n;
    u8 *dict = strcmp(argv[1], "-") ? load_file(argv[1], &dn) : NULL;
    u8 *in = load_file(argv[2], &n);
    lzma_options_lzma opt;
    lzma_lzma_preset(&opt, 9 | LZMA_PRESET_EXTREME);
    u64 need = (u64)dn + (u64)n + 4096;
    u32 ds = 1u << 20;
    while (ds < need && ds < (1u << 30)) ds <<= 1;
    opt.dict_size = ds;
    if (argc >= 6) { opt.lc = atoi(argv[3]); opt.lp = atoi(argv[4]); opt.pb = atoi(argv[5]); }
    if (dict) { opt.preset_dict = dict; opt.preset_dict_size = (u32)dn; }
    lzma_filter f[2] = { { LZMA_FILTER_LZMA2, &opt }, { LZMA_VLI_UNKNOWN, NULL } };
    size_t cap = n + n / 3 + 65536, outpos = 0;
    u8 *out = (u8 *)malloc(cap);
    if (lzma_raw_buffer_encode(f, NULL, in, n, out, &outpos, cap) != LZMA_OK) die("encode failed");
    u8 *back = (u8 *)malloc(n + 1);
    size_t inpos = 0, bpos = 0;
    if (lzma_raw_buffer_decode(f, NULL, out, &inpos, outpos, back, &bpos, n + 1) != LZMA_OK) die("decode failed");
    if (bpos != (size_t)n || memcmp(back, in, n)) die("round trip mismatch");
    printf("%zu\n", outpos);
    return 0;
}
