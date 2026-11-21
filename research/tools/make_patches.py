#!/usr/bin/env python3
"""Generate bsdiff 4.3 patches for every corpus pair into research/work/<pair>/b43.patch.
Usage: python3 tools/make_patches.py [-j N]"""
import os, subprocess, sys
from concurrent.futures import ThreadPoolExecutor
R = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
C = os.path.join(R, "..", "bench", "corpus")
B = os.path.join(R, "..", "bench", "baseline", "bsdiff43")
def one(p):
    d = os.path.join(R, "work", p); os.makedirs(d, exist_ok=True)
    out = os.path.join(d, "b43.patch")
    if not (os.path.exists(out) and os.path.getsize(out) > 0):
        subprocess.run([B, os.path.join(C, p, "old"), os.path.join(C, p, "new"), out], check=True)
    return p, os.path.getsize(out)
pairs = sorted(x for x in os.listdir(C) if os.path.isdir(os.path.join(C, x)))
j = int(sys.argv[sys.argv.index("-j") + 1]) if "-j" in sys.argv else 6
with ThreadPoolExecutor(j) as ex:
    for p, s in ex.map(one, pairs):
        print(s, p, flush=True)
