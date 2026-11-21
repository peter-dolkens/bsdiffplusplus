/* bcj.c - minimal invertible branch-converter filters (relative -> absolute targets), for the
 * "filter both files, then diff" experiment.  Not xz's BCJ: unconditional, so trivially invertible.
 *   x86  : every E8/E9 opcode: the following rel32 becomes rel32 + (offset of next instruction)
 *   arm64: every 4-aligned BL (opcode 100101): imm26 becomes imm26 + (offset / 4)   (mod 2^26)
 * usage: bcj <x86|arm64> <e|d> <in> <out>
 */
#include "lab.h"
int main(int argc, char **argv)
{
    if (argc != 5) die("usage: bcj x86|arm64 e|d in out");
    i64 n;
    u8 *b = load_file(argv[3], &n);
    int enc = argv[2][0] == 'e';
    if (!strcmp(argv[1], "x86")) {
        for (i64 i = 0; i + 5 <= n;) {
            if (b[i] == 0xE8 || b[i] == 0xE9) {
                u32 v; memcpy(&v, b + i + 1, 4);
                v = enc ? v + (u32)(i + 5) : v - (u32)(i + 5);
                memcpy(b + i + 1, &v, 4);
                i += 5;
            } else i++;
        }
    } else {
        for (i64 i = 0; i + 4 <= n; i += 4) {
            u32 w; memcpy(&w, b + i, 4);
            if ((w & 0xFC000000) == 0x94000000) {
                u32 imm = w & 0x3FFFFFF;
                imm = (enc ? imm + (u32)(i >> 2) : imm - (u32)(i >> 2)) & 0x3FFFFFF;
                w = 0x94000000 | imm;
                memcpy(b + i, &w, 4);
            }
        }
    }
    save_file(argv[4], b, n);
    return 0;
}
