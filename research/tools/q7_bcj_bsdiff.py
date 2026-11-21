#!/usr/bin/env python3
""""Filter both files with a branch converter, then bsdiff 4.3" experiment.
Real patches; the filter's inverse is verified on both files, and bspatch43 output is verified
against the filtered new file.  Usage: python3 tools/q7_bcj_bsdiff.py [-j N] -> results/q7_bcj.json"""
import json, os, subprocess, sys, filecmp
from concurrent.futures import ThreadPoolExecutor
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from common import *
import vamap
BS = os.path.join(R, "..", "bench", "baseline", "bsdiff43"); BP = os.path.join(R, "..", "bench", "baseline", "bspatch43")
BCJ = os.path.join(R, "bin", "bcj")
def one(p):
    oldp, newp = paths(p)
    arch = vamap.info(newp)["arch"]
    if arch not in ("x86_64", "arm64"):
        arch = "arm64" if "arm64" in p else "x86_64" if "x86_64" in p else None
    if not arch:
        return p, None
    a = "x86" if arch == "x86_64" else "arm64"
    wd = os.path.join(WORK, p, "bcj"); os.makedirs(wd, exist_ok=True)
    fo, fn, back, patch, out = (os.path.join(wd, x) for x in ("old.f", "new.f", "back", "patch", "out"))
    for src, dst in ((oldp, fo), (newp, fn)):
        subprocess.run([BCJ, a, "e", src, dst], check=True)
        subprocess.run([BCJ, a, "d", dst, back], check=True)
        assert filecmp.cmp(src, back, shallow=False), "filter not invertible"
    subprocess.run([BS, fo, fn, patch], check=True)
    subprocess.run([BP, fo, out, patch], check=True)
    assert filecmp.cmp(out, fn, shallow=False)
    sz = os.path.getsize(patch)
    for f in (fo, fn, back, out):
        os.remove(f)
    return p, sz
j = int(sys.argv[sys.argv.index("-j") + 1]) if "-j" in sys.argv else 3
res = {}
with ThreadPoolExecutor(j) as ex:
    # --small: skip pairs over 7 MB (bsdiff 4.3 itself needs minutes on them on the shared machine)
    ps = [p for p in pairs() if "--small" not in sys.argv or newsize(p) <= 7_000_000]
    for p, s in ex.map(one, ps):
        res[p] = s
        print(p, s, flush=True)
json.dump(res, open(os.path.join(RESULTS, "q7_bcj.json"), "w"), indent=1)
