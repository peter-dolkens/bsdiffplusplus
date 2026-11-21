#!/usr/bin/env python3
"""Run the snapshot copy of the project's C# aligners (research/csalign) over the corpus.
Writes work/<pair>/segs_dp.bin (DP over local matches) and segs_dpb.bin (DP over local + FFT block).
Usage: python3 tools/run_csalign.py [-j N] [modes]   -> results/csalign.json (stdout lines per pair)"""
import json, os, subprocess, sys
from concurrent.futures import ThreadPoolExecutor
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from common import *
modes = [a for a in sys.argv[1:] if not a.startswith("-") and not a.isdigit()]
modes = modes[0] if modes else "local,both"
j = min(2, int(sys.argv[sys.argv.index("-j") + 1]) if "-j" in sys.argv else 2)
DLL = os.path.join(R, "csalign", "bin", "Release", "net10.0", "csalign.dll")
def one(p):
    oldp, newp = paths(p)
    try:
        r = subprocess.run(["dotnet", DLL, oldp, newp, os.path.join(WORK, p), modes], capture_output=True, text=True, timeout=900)
    except subprocess.TimeoutExpired:
        return p, ["TIMEOUT after 900 s"]
    return p, (r.stdout.strip().split("\n") if r.returncode == 0 else ["FAILED: " + r.stderr[-400:]])
out = os.path.join(RESULTS, "csalign.json")
res = json.load(open(out)) if os.path.exists(out) else {}
ps = sorted(pairs(), key=newsize)
if "--missing" in sys.argv:
    ps = [p for p in ps if p not in res or not os.path.exists(os.path.join(WORK, p, "segs_dp.bin"))]
with ThreadPoolExecutor(j) as ex:
    for p, lines in ex.map(one, ps):
        res[p] = lines
        print(p, lines, flush=True)
        json.dump(res, open(out, "w"), indent=1)
