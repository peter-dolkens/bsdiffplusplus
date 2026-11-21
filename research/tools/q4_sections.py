#!/usr/bin/env python3
"""Per-section breakdown of where the patch bytes come from (new-file sections).

For each section of the new file: size, bytes left unmatched by the alignment (extra), matched
bytes, matched-but-differing bytes, how many of those the address oracle explains, and - when a
per-byte cost file from `cm -c` exists - the exact code length the CM coder charged to that
section (split into matched and extra bytes).

Usage: python3 tools/q4_sections.py [pair-substring] [--align b43|dp] [--cost full_va]
"""
import array, json, os, re, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from common import *
import b43, vamap

align = sys.argv[sys.argv.index("--align") + 1] if "--align" in sys.argv else "b43"
costcfg = sys.argv[sys.argv.index("--cost") + 1] if "--cost" in sys.argv else "full_va"
only = sys.argv[1] if len(sys.argv) > 1 and not sys.argv[1].startswith("-") else None
SEG = {"b43": "segs.bin", "dp": "segs_dp.bin"}[align]
allres = {}
for p in pairs():
    if only and only not in p:
        continue
    oldp, newp = paths(p)
    wd = os.path.join(WORK, p)
    old, new = open(oldp, "rb").read(), open(newp, "rb").read()
    inf = vamap.info(newp)
    segs = b43.read_segs(os.path.join(wd, SEG))
    m = len(new)
    state = bytearray(m)        # 0 extra, 1 matched equal, 2 matched differing
    for npos, ln, opos in segs:
        if opos < 0:
            continue
        a, b = new[npos:npos + ln], old[opos:opos + ln]
        if a == b:
            state[npos:npos + ln] = b"\x01" * ln
        else:
            state[npos:npos + ln] = bytes(1 if x == y else 2 for x, y in zip(a, b))
    ex = open(os.path.join(wd, "expl_%s.bin" % align), "rb").read() if os.path.exists(os.path.join(wd, "expl_%s.bin" % align)) else bytes(m)
    cpath = os.path.join(wd, "cost_%s_%s.f32" % (costcfg, align))
    cost = None
    if os.path.exists(cpath):
        cost = array.array("f")
        cost.frombytes(open(cpath, "rb").read())
    secs = sorted((s for s in inf["sections"] if s[1] + s[2] <= m), key=lambda s: s[1])
    # add gaps as pseudo sections
    rows, pos = [], 0
    for nm, off, size, va in secs:
        if off > pos:
            rows.append(("(gap/headers)", pos, off - pos))
        if off + size > pos:
            rows.append((nm, max(off, pos), off + size - max(off, pos)))
            pos = off + size
    if pos < m:
        rows.append(("(tail)" if secs else "(whole file)", pos, m - pos))
    out = []
    for nm, off, size in rows:
        st = state[off:off + size]
        nextra, ndiff = st.count(0), st.count(2)
        nexp = size - ex[off:off + size].count(0)
        cm_m = cm_x = None
        if cost is not None:
            c = cost[off:off + size]
            cm_x = sum(v for v, s in zip(c, st) if s == 0) / 8
            cm_m = sum(c) / 8 - cm_x
        out.append(dict(name=nm, off=off, size=size, extra=nextra, differ=ndiff, explained=nexp, cm_matched=cm_m, cm_extra=cm_x))
    allres[p] = out
    print("==", p, inf["fmt"], inf["arch"])
    print("%-28s %10s %10s %8s %10s %8s %8s %10s %10s" % ("section", "size", "extra", "extra%", "differ", "diff%", "expl%", "cm_match", "cm_extra"))
    for r in out:
        if r["size"] < 256 and r["extra"] + r["differ"] < 64:
            continue
        mt = r["size"] - r["extra"]
        print("%-28s %10d %10d %7.1f%% %10d %7.2f%% %7.1f%% %10s %10s" % (
            r["name"][:28], r["size"], r["extra"], 100 * r["extra"] / r["size"], r["differ"], 100 * r["differ"] / max(1, mt),
            100 * r["explained"] / max(1, r["differ"]),
            "-" if r["cm_matched"] is None else "%.0f" % r["cm_matched"], "-" if r["cm_extra"] is None else "%.0f" % r["cm_extra"]))
    # compiler version hints (strings only)
    for tag, blob in (("old", old), ("new", new)):
        hints = set(x.decode() for x in re.findall(rb"go1\.\d+(?:\.\d+)?", blob)[:50]) | set(x.decode() for x in re.findall(rb"rustc version [0-9.]+", blob)[:5]) \
            | set(x.decode() for x in re.findall(rb"/rustc/[0-9a-f]{40}", blob)[:200]) | set(x.decode() for x in re.findall(rb"GCC: \([^)]*\) [0-9.]+", blob)[:5])
        if hints:
            print("  toolchain hints (%s): %s" % (tag, ", ".join(sorted(hints))[:300]))
json.dump(allres, open(os.path.join(RESULTS, "q4_sections_%s.json" % align), "w"), indent=1)
