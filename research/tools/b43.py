#!/usr/bin/env python3
"""bsdiff 4.3 patch parser.

Recovers, from a BSDIFF40 patch, the three raw sections and the exact alignment
(which new bytes are copied-with-diff from which old offsets, which are extra).

As a module:  parse(path) -> Patch
As a script:  b43.py <pair-work-dir> <old> <new>
              writes ctrl.bin diff.bin extra.bin segs.bin into the work dir and verifies that
              old + sections reproduces new (so the recovered alignment is exact).

segs.bin: little-endian int32 count, then count triples of int32 (newPos, length, oldPos),
          oldPos == -1 for an unmatched ("extra") segment.  Sorted by newPos, covers [0, m).
"""
import bz2, os, struct, sys


def offtin(b):
    y = int.from_bytes(b[:8], "little")
    if y & (1 << 63):
        y = -(y & ((1 << 63) - 1))
    return y


class Patch:
    pass


def parse(path):
    data = open(path, "rb").read()
    if data[:8] != b"BSDIFF40":
        raise ValueError("not a BSDIFF40 patch")
    clen, dlen, newsize = offtin(data[8:16]), offtin(data[16:24]), offtin(data[24:32])
    p = Patch()
    p.size = len(data)
    p.newsize = newsize
    p.ctrl_c, p.diff_c, p.extra_c = clen, dlen, len(data) - 32 - clen - dlen
    p.ctrl = bz2.decompress(data[32:32 + clen])
    p.diff = bz2.decompress(data[32 + clen:32 + clen + dlen])
    p.extra = bz2.decompress(data[32 + clen + dlen:])
    n = len(p.ctrl) // 24
    p.triples = [(offtin(p.ctrl[24 * i:24 * i + 8]), offtin(p.ctrl[24 * i + 8:24 * i + 16]),
                  offtin(p.ctrl[24 * i + 16:24 * i + 24])) for i in range(n)]
    segs = []
    newpos = oldpos = 0
    for x, y, z in p.triples:
        if x:
            segs.append((newpos, x, oldpos))
        newpos += x
        oldpos += x
        if y:
            segs.append((newpos, y, -1))
        newpos += y
        oldpos += z
    assert newpos == newsize, (newpos, newsize)
    p.segs = segs
    return p


def apply(p, old):
    out = bytearray(p.newsize)
    dp = ep = 0
    for npos, ln, opos in p.segs:
        if opos < 0:
            out[npos:npos + ln] = p.extra[ep:ep + ln]
            ep += ln
        else:
            o = old[opos:opos + ln]
            d = p.diff[dp:dp + ln]
            # bytewise add mod 256 (old bytes beyond EOF count as 0, as in bspatch)
            if len(o) < ln:
                o = o + bytes(ln - len(o))
            out[npos:npos + ln] = bytes((a + b) & 255 for a, b in zip(o, d))
            dp += ln
    return bytes(out)


def write_segs(path, segs):
    with open(path, "wb") as f:
        f.write(struct.pack("<i", len(segs)))
        for s in segs:
            f.write(struct.pack("<iii", *s))


def read_segs(path):
    b = open(path, "rb").read()
    n = struct.unpack_from("<i", b, 0)[0]
    return [struct.unpack_from("<iii", b, 4 + 12 * i) for i in range(n)]


if __name__ == "__main__":
    wd, oldp, newp = sys.argv[1:4]
    p = parse(os.path.join(wd, "b43.patch"))
    old = open(oldp, "rb").read()
    new = open(newp, "rb").read()
    assert apply(p, old) == new, "parser round-trip failed"
    # bsdiff 4.3 can emit matched segments reaching outside old (negative/over-range): check
    for npos, ln, opos in p.segs:
        if opos >= 0:
            assert opos + ln <= len(old), "segment exceeds old"
    open(os.path.join(wd, "ctrl.bin"), "wb").write(p.ctrl)
    open(os.path.join(wd, "diff.bin"), "wb").write(p.diff)
    open(os.path.join(wd, "extra.bin"), "wb").write(p.extra)
    write_segs(os.path.join(wd, "segs.bin"), p.segs)
    print("ok", wd, len(p.segs), "segments")
