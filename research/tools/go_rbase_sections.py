#!/usr/bin/env python3
"""Per-section matched-data cost on the Go pair with and without base-relative offset hypotheses.
Needs the per-byte cost files written by tools/chain2.sh (cost_full_va_b43.f32, cost_rb_sec.f32,
cost_rb_go.f32 in the pair's work directory).  -> results/go_rbase_sections.json"""
import array, json, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from common import *
import vamap, b43
p = "upgrade-lazygit-0.43.1-0.44.0-linux-amd64"
oldp, newp = paths(p)
wd = os.path.join(WORK, p)
inf = vamap.info(newp)
m = inf["size"]
matched = bytearray(m)
for npos, ln, opos in b43.read_segs(os.path.join(wd, "segs.bin")):
    if opos >= 0:
        matched[npos:npos + ln] = b"\x01" * ln
def load(f):
    a = array.array("f")
    a.frombytes(open(os.path.join(wd, f), "rb").read())
    return a
base, sec, go = load("cost_full_va_b43.f32"), load("cost_rb_sec.f32"), load("cost_rb_go.f32")
rows = []
for nm, off, size, va in sorted(inf["sections"], key=lambda s: s[1]):
    if off + size > m or size < 2048:
        continue
    idx = [i for i in range(off, off + size) if matched[i]]
    rows.append([nm, size] + [round(sum(c[i] for i in idx) / 8) for c in (base, sec, go)])
    print(*rows[-1])
json.dump(rows, open(os.path.join(RESULTS, "go_rbase_sections.json"), "w"))
