#!/usr/bin/env python3
"""Run bin/q4 (address-prediction oracle) over the corpus, with identity and container VA maps.
Usage: python3 tools/run_q4.py [--align b43|dp] -> results/q4_oracle_<align>.json ; work/<pair>/expl_<align>.bin"""
import json, os, subprocess, sys
from concurrent.futures import ThreadPoolExecutor
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from common import *
import vamap
align = sys.argv[sys.argv.index("--align") + 1] if "--align" in sys.argv else "b43"
SEG = {"b43": "segs.bin", "dp": "segs_dp.bin"}[align]
def one(p):
    oldp, newp = paths(p)
    wd = os.path.join(WORK, p)
    io, inw = vamap.info(oldp), vamap.info(newp)
    vo, vn = os.path.join(wd, "old.vamap"), os.path.join(wd, "new.vamap")
    vamap.write_vamap(io, vo); vamap.write_vamap(inw, vn)
    q4 = os.path.join(R, "bin", "q4")
    seg = os.path.join(wd, SEG)
    a = json.loads(subprocess.run([q4, oldp, newp, seg], capture_output=True, text=True, check=True).stdout)
    b = json.loads(subprocess.run([q4, oldp, newp, seg, vo, vn, os.path.join(wd, "expl_%s.bin" % align)], capture_output=True, text=True, check=True).stdout)
    return p, dict(fmt=inw["fmt"], arch=inw["arch"], identity=a, vamap=b)
res = {}
with ThreadPoolExecutor(4) as ex:
    for p, r in ex.map(one, pairs()):
        res[p] = r
        print(p, r["fmt"], r["arch"], "diff", r["vamap"]["diff"], "expl id %.3f va %.3f" % (
            r["identity"]["explained"] / max(1, r["identity"]["diff"]), r["vamap"]["explained"] / max(1, r["vamap"]["diff"])), flush=True)
json.dump(res, open(os.path.join(RESULTS, "q4_oracle_%s.json" % align), "w"), indent=1)
