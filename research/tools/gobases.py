#!/usr/bin/env python3
"""Write VA map files that also list "relative bases" for the base-relative offset32 hypothesis.

  gobases.py <pair> sec   -> work/<pair>/old.sec.vamap, new.sec.vamap
        bases = starts of the (up to 8 largest) sections present in both files (container headers only)
  gobases.py <pair> go    -> work/<pair>/old.go.vamap, new.go.vamap
        bases = .text start, rodata start(s), and the Go pclntab sub-tables (funcnametab, pctab,
        functab) read from the pcHeader (Go >= 1.18).  This is PLATFORM-AWARE (Go runtime layout).
"""
import os, struct, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from common import *
import vamap


def go_tables(path, inf):
    b = open(path, "rb").read()
    sec = {nm: (off, size, va) for nm, off, size, va in inf["sections"]}
    pc = sec.get(".gopclntab") or sec.get("__DATA_CONST,__gopclntab") or sec.get("__TEXT,__gopclntab")
    if not pc:
        return None
    off = pc[0]
    magic, _, _, minlc, ptr = struct.unpack_from("<IBBBB", b, off)
    if magic not in (0xFFFFFFF1, 0xFFFFFFF0) or ptr != 8:
        return None
    nfunc, nfiles, textstart, funcname, cu, filetab, pctab, pcln = struct.unpack_from("<8Q", b, off + 8)
    regions = inf["regions"]
    def va2off(va):
        for lo, hi, v, _ in regions:
            if v <= va < v + (hi - lo):
                return va - v + lo
        return None
    out = {"text": va2off(textstart), "funcnametab": off + funcname, "pctab": off + pctab, "functab": off + pcln}
    for nm in (".rodata", "__TEXT,__rodata", "__DATA_CONST,__rodata"):
        if nm in sec:
            out[nm] = sec[nm][0]
    return out


def main(pair, mode):
    oldp, newp = paths(pair)
    io, inw = vamap.info(oldp), vamap.info(newp)
    wd = os.path.join(WORK, pair)
    vo, vn = os.path.join(wd, "old.%s.vamap" % mode), os.path.join(wd, "new.%s.vamap" % mode)
    vamap.write_vamap(io, vo)
    vamap.write_vamap(inw, vn)
    bases = []
    if mode == "go":
        a, b = go_tables(oldp, io), go_tables(newp, inw)
        if a and b:
            bases = [(k, a[k], b[k]) for k in a if k in b and a[k] is not None and b[k] is not None]
    else:
        so = {nm: (off, size) for nm, off, size, va in io["sections"]}
        sn = {nm: (off, size) for nm, off, size, va in inw["sections"]}
        common = sorted((nm for nm in so if nm in sn), key=lambda nm: -sn[nm][1])[:8]
        bases = [(nm, so[nm][0], sn[nm][0]) for nm in common]
    with open(vo, "a") as f:
        for nm, co, cn in bases[:8]:
            f.write("rbase %d %d\n" % (co, cn))
    return bases


if __name__ == "__main__":
    for nm, co, cn in main(sys.argv[1], sys.argv[2]):
        print("%-28s old %#x new %#x" % (nm, co, cn))
