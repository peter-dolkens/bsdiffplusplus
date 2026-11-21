#!/usr/bin/env python3
"""Run bin/thesis (thesis/DESIGN-7 encoding cost) over the corpus for an alignment:
  b43    work/<pair>/segs.bin      (recovered from the bsdiff 4.3 patch)     -> work/<pair>/t43/
  greedy port of dev/format-harness/GreedyAligner.cs                         -> work/<pair>/tgr/
  dp     work/<pair>/segs_dp.bin   (project DP, local matches only)
  dpb    work/<pair>/segs_dpb.bin  (project DP, local + FFT block alignment)
  dpo    work/<pair>/segs_dpo.bin  (project DP, block alignment only)
Usage: python3 tools/run_thesis.py [b43|greedy|dp|dpb|dpo] [-j N]   -> results/thesis_<align>.json"""
import json, os, subprocess, sys
from concurrent.futures import ThreadPoolExecutor
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from common import *
which = sys.argv[1] if len(sys.argv) > 1 and not sys.argv[1].startswith("-") else "b43"
j = min(3, int(sys.argv[sys.argv.index("-j") + 1]) if "-j" in sys.argv else 3)
def one(p):
    oldp, newp = paths(p)
    wd = os.path.join(WORK, p)
    if which in ("b43", "greedy"):
        out = os.path.join(wd, "t43" if which == "b43" else "tgr")
        os.makedirs(out, exist_ok=True)
        seg = os.path.join(wd, "segs.bin") if which == "b43" else "greedy"
        cmd = [os.path.join(R, "bin", "thesis"), oldp, newp, seg, out]
    else:
        seg = os.path.join(wd, "segs_%s.bin" % which)
        if not os.path.exists(seg):
            return p, None
        cmd = [os.path.join(R, "bin", "thesis"), oldp, newp, seg]
    r = subprocess.run(cmd, capture_output=True, text=True, check=True)
    return p, json.loads(r.stdout)
res = {}
with ThreadPoolExecutor(j) as ex:
    for p, r in ex.map(one, sorted(pairs(), key=newsize)):
        if r is None:
            continue
        res[p] = r
        print(p, r["total"], r["best_mode"], flush=True)
        json.dump(res, open(os.path.join(RESULTS, "thesis_%s.json" % which), "w"), indent=1)
json.dump(res, open(os.path.join(RESULTS, "thesis_%s.json" % which), "w"), indent=1)
