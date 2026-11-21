#!/usr/bin/env python3
"""Control-string coding: DESIGN 7.2 varints + zlib/bzip2 (from results/thesis_<align>.json) versus
the diagonal/rep-list adaptive coder in cm (-K; encoded, decoded and compared).
Usage: python3 tools/run_ctrl.py   -> results/ctrl.json   {align: {pair: {...}}}"""
import json, os, subprocess, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from common import *
res = {}
for align, segf in (("b43", "segs.bin"), ("dp", "segs_dp.bin"), ("dpb", "segs_dpb.bin"), ("greedy", None)):
    for p in pairs():
        oldp, newp = paths(p)
        seg = os.path.join(WORK, p, segf) if segf else "greedy"
        if segf and not os.path.exists(seg):
            continue
        r = subprocess.run([os.path.join(R, "bin", "cm"), oldp, newp, seg, "-K"], capture_output=True, text=True)
        if r.returncode:
            print("FAILED", align, p, r.stderr[-200:]); continue
        res.setdefault(align, {})[p] = json.loads(r.stdout)
        print(align, p, r.stdout.strip(), flush=True)
json.dump(res, open(os.path.join(RESULTS, "ctrl.json"), "w"), indent=1)
