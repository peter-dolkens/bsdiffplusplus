#!/usr/bin/env python3
"""Q1: where do the bytes go?  Decompose bsdiff 4.3 patches (control / diff / extra).

Usage: python3 tools/q1_decompose.py     (after tools/make_patches.py)
Writes work/<pair>/{ctrl,diff,extra}.bin, segs.bin and results/q1.{json,md}.
All sizes are real: compressed sizes are the actual bzip2 streams inside the patch.
"""
import json, os, subprocess, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import b43
from common import *

rows = {}
for p in pairs():
    wd = os.path.join(WORK, p)
    oldp, newp = paths(p)
    pt = b43.parse(os.path.join(wd, "b43.patch"))
    old = open(oldp, "rb").read()
    new = open(newp, "rb").read()
    assert b43.apply(pt, old) == new, "parser round trip failed: " + p
    open(os.path.join(wd, "ctrl.bin"), "wb").write(pt.ctrl)
    open(os.path.join(wd, "diff.bin"), "wb").write(pt.diff)
    open(os.path.join(wd, "extra.bin"), "wb").write(pt.extra)
    b43.write_segs(os.path.join(wd, "segs.bin"), pt.segs)
    nz = len(pt.diff) - pt.diff.count(0)
    # runs of non-zero bytes ("fields")
    runs = 0
    prev = 0
    for b in pt.diff:
        if b and not prev:
            runs += 1
        prev = b
    rows[p] = dict(new=len(new), old=len(old), patch=pt.size, ctrl_c=pt.ctrl_c, diff_c=pt.diff_c,
                   extra_c=pt.extra_c, ctrl_raw=len(pt.ctrl), diff_raw=len(pt.diff), extra_raw=len(pt.extra),
                   triples=len(pt.triples), nz=nz, nzruns=runs,
                   nseg_matched=sum(1 for s in pt.segs if s[2] >= 0))
    print(p, rows[p], flush=True)

os.makedirs(RESULTS, exist_ok=True)
json.dump(rows, open(os.path.join(RESULTS, "q1.json"), "w"), indent=1)

hdr = ["pair", "new", "patch", "patch/new", "ctrl", "diff", "extra", "ctrl %", "diff %", "extra %",
       "matched % of new", "non-zero % of diff", "diff bits/matched byte", "extra bits/byte"]
tab = []
for p, r in rows.items():
    tab.append([short(p), f"{r['new']:,}", f"{r['patch']:,}", f"{r['patch']/r['new']:.4f}",
                f"{r['ctrl_c']:,}", f"{r['diff_c']:,}", f"{r['extra_c']:,}",
                f"{100*r['ctrl_c']/r['patch']:.1f}", f"{100*r['diff_c']/r['patch']:.1f}", f"{100*r['extra_c']/r['patch']:.1f}",
                f"{100*r['diff_raw']/r['new']:.1f}", f"{100*r['nz']/max(1,r['diff_raw']):.2f}",
                f"{8*r['diff_c']/max(1,r['diff_raw']):.3f}", f"{8*r['extra_c']/max(1,r['extra_raw']):.2f}" if r['extra_raw'] else "-"])
out = [md_table(hdr, tab, "lrrrrrrrrrrrrr"), ""]
# category summary: sqrt-weighted share of new size taken by each section
hdr2 = ["category", "patch/new", "ctrl/new", "diff/new", "extra/new", "ctrl share", "diff share", "extra share"]
tab2 = []
for c in ("all", "upgrade", "patch", "other"):
    sel = {p: r for p, r in rows.items() if c == "all" or cat(p) == c}
    w = {k: wavg([(r[k], r["new"]) for r in sel.values()]) for k in ("patch", "ctrl_c", "diff_c", "extra_c")}
    tab2.append([c, f"{w['patch']:.4f}", f"{w['ctrl_c']:.4f}", f"{w['diff_c']:.4f}", f"{w['extra_c']:.4f}",
                 f"{100*w['ctrl_c']/w['patch']:.1f}%", f"{100*w['diff_c']/w['patch']:.1f}%", f"{100*w['extra_c']/w['patch']:.1f}%"])
out.append(md_table(hdr2, tab2, "lrrrrrrr"))
open(os.path.join(RESULTS, "q1.md"), "w").write("\n".join(out) + "\n")
print("\n".join(out))
