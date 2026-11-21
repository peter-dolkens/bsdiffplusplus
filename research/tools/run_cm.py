#!/usr/bin/env python3
"""Run bin/cm (conditional context-mixing coder; every size is a verified round trip) over the corpus.

Usage: python3 tools/run_cm.py <config> [-j N] [--align b43|greedy|dp|dpb] [--only SUBSTR] [--small]
                               [--timeout SEC] [--max-size BYTES] [--no-verify-above BYTES]
Configs (see CONFIGS): diff, diff_id, diff_va, full_va, plus ablations.
Results: results/cm_<config>_<align>.json  (per pair: the JSON printed by cm).
"""
import json, os, subprocess, sys
from concurrent.futures import ThreadPoolExecutor
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from common import *
import vamap

CONFIGS = {
    # name: (cm options, use vamap, dump per-byte cost)
    "diff":      (["-x", "0"], False, False),                 # matched bytes only, no address hypotheses
    "diff_id":   (["-x", "0", "-a", "127"], False, False),    # + address hypotheses, identity VA map (format-agnostic)
    "diff_va":   (["-x", "0", "-a", "127"], True, False),     # + VA map from container headers
    "full_va":   (["-x", "1", "-a", "127"], True, True),      # whole new file in one stream (extra bytes included)
    "full_id":   (["-x", "1", "-a", "127"], False, False),
    "fullc_va":  (["-x", "1", "-a", "127", "-C"], True, False),   # complete patch: control records in the stream too
    "full":      (["-x", "1"], False, False),
    # smaller models (diff part only): 2^18-entry tables (1 MB + 1 MB + 4 x 0.5 MB); and a reduced context set
    "t18_va":    (["-x", "0", "-a", "127", "-T", "18"], True, False),
    "t16_va":    (["-x", "0", "-a", "127", "-T", "16"], True, False),
    "lite_va":   (["-x", "0", "-a", "127", "-T", "18", "-M", "0x384f", "-V", "0x818f"], True, False),
    "full_t18":  (["-x", "1", "-a", "127", "-T", "18"], True, False),
    # ablations (diff part only)
    "abl_nolook": (["-x", "0", "-A", "1"], False, False),
    "abl_noind":  (["-x", "0", "-A", "2"], False, False),
    "abl_both":   (["-x", "0", "-A", "3"], False, False),
    "a_rel32":   (["-x", "0", "-a", "1"], True, False),
    "a_abs":     (["-x", "0", "-a", "6"], True, False),
    "a_arm":     (["-x", "0", "-a", "56"], True, False),
    "x_nomm":    (["-x", "1", "-A", "4"], False, False),
    "x_nocont":  (["-x", "1", "-A", "8"], False, False),
}

cfg = sys.argv[1]
opts, use_va, dump = CONFIGS[cfg]
j = min(3, int(sys.argv[sys.argv.index("-j") + 1]) if "-j" in sys.argv else 3)   # never more than 3 concurrent jobs
align = sys.argv[sys.argv.index("--align") + 1] if "--align" in sys.argv else "b43"
only = sys.argv[sys.argv.index("--only") + 1] if "--only" in sys.argv else None
SEGFILE = {"b43": "segs.bin", "greedy": None, "dp": "segs_dp.bin", "dpb": "segs_dpb.bin"}[align]


def one(p):
    oldp, newp = paths(p)
    wd = os.path.join(WORK, p)
    seg = os.path.join(wd, SEGFILE) if SEGFILE else "greedy"
    cmd = [os.path.join(R, "bin", "cm"), oldp, newp, seg] + opts
    if use_va:
        vo, vn = os.path.join(wd, "old.vamap"), os.path.join(wd, "new.vamap")
        vamap.write_vamap(vamap.info(oldp), vo)
        vamap.write_vamap(vamap.info(newp), vn)
        cmd += ["-v", vo, vn]
    if dump:
        cmd += ["-c", os.path.join(wd, "cost_%s_%s.f32" % (cfg, align))]
    if NOVERIFY_ABOVE and newsize(p) > NOVERIFY_ABOVE:
        cmd += ["-n"]        # encode only: real stream length, not decoded back in this run
    try:
        r = subprocess.run(cmd, capture_output=True, text=True, timeout=TIMEOUT)
    except subprocess.TimeoutExpired:
        return p, {"timeout_s": TIMEOUT}
    if r.returncode:
        return p, {"error": r.stderr[-300:]}
    return p, json.loads(r.stdout)


small = "--small" in sys.argv      # the 14 pairs with new size <= 3.8 MB (the format agent's map-experiment set)
ps = [p for p in pairs() if (not only or only in p) and (not small or newsize(p) <= 3_800_000)]
def optval(name, default):
    return int(sys.argv[sys.argv.index(name) + 1]) if name in sys.argv else default
TIMEOUT = optval("--timeout", 900)                # hard per-pair time budget in seconds
NOVERIFY_ABOVE = optval("--no-verify-above", 0)   # new-file size above which the decode pass is skipped
MAXSIZE = optval("--max-size", 0)
if MAXSIZE:
    ps = [p for p in ps if newsize(p) <= MAXSIZE]
out = os.path.join(RESULTS, "cm_%s_%s%s.json" % (cfg, align, "_small" if small else ""))
res = json.load(open(out)) if (only and os.path.exists(out)) else {}
# biggest first so the pool stays busy
ps.sort(key=lambda p: -newsize(p))
from concurrent.futures import as_completed
with ThreadPoolExecutor(j) as ex:
    for f in as_completed([ex.submit(one, p) for p in ps]):
        p, r = f.result()
        res[p] = r
        print(cfg, p, r, flush=True)
        json.dump(res, open(out, "w"), indent=1)
