#!/usr/bin/env python3
"""Q2: back-end codecs on the patch sections.

For every pair and for each of: extra.bin (bsdiff 4.3 extra string), diff.bin (4.3 bytewise diff
string), ctrl.bin (4.3 control, 24-byte triples), t43/ctrl6.bin (DESIGN 7.2 varint control string),
t43/nz2.bin (little-endian-mode non-zero string) and the whole new file (no delta, for reference),
measure the real compressed size under each codec.  Every size is the length of an actual
compressed stream produced by the library/CLI (so decodable); "dict" variants use the old file as
a preset dictionary / prefix.

Usage: python3 tools/q2_codecs.py [-j N] [--fast]    -> results/q2.json
Resumable: (pair, section) results already in results/q2.json are kept.  --fast skips the slow
or uninformative variants (lp/pb tuning, BCJ filters, large-window brotli) and, for whole files
over 10 MB, brotli with a dictionary; it was used for the four largest pairs after the first run
hit its time limit on the shared machine.
"""
import bz2, json, lzma, os, subprocess, sys, zlib
from concurrent.futures import ThreadPoolExecutor
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from common import *

LZDICT = os.path.join(R, "bin", "lzdict")


BUDGET = 600      # hard time budget per external compressor run, seconds


def cli(args):
    try:
        p = subprocess.run(args, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=BUDGET)
    except subprocess.TimeoutExpired:
        return None       # recorded as "did not finish within the budget"
    if p.returncode:
        raise RuntimeError("%s: %s" % (args[:2], p.stderr.decode()[-200:]))
    return len(p.stdout)


def dict_size(n):
    d = 1 << 16
    while d < n and d < (1 << 30):
        d <<= 1
    return d


def lz(data, bcj=None, lc=3, lp=0, pb=2):
    f = []
    if bcj:
        f.append({"id": bcj})
    f.append({"id": lzma.FILTER_LZMA2, "preset": 9 | lzma.PRESET_EXTREME, "dict_size": dict_size(len(data)),
              "lc": lc, "lp": lp, "pb": pb})
    return len(lzma.compress(data, format=lzma.FORMAT_RAW, filters=f))


FAST = "--fast" in sys.argv


def codecs(path, oldp, kind):
    data = open(path, "rb").read()
    n = len(data)
    r = {"raw": n}
    if n == 0:
        return r
    r["zlib"] = len(zlib.compress(data, 9))
    r["bzip2"] = len(bz2.compress(data, 9))
    r["lzma"] = lz(data)
    r["lzma_pb0"] = lz(data, lc=3, lp=0, pb=0)
    if not FAST:
        r["lzma_lp2"] = min(lz(data, lc=2, lp=2, pb=2), lz(data, lc=0, lp=2, pb=2))
    lw = "--lgwin=24"
    if not (FAST and kind == "new"):
        r["brotli"] = cli(["brotli", "-q", "11", lw, "-c", path])
    if n > (1 << 24) and not FAST:
        r["brotli_lw"] = cli(["brotli", "-q", "11", "--large_window=30", "-c", path])
    r["zstd"] = cli(["zstd", "--ultra", "-22", "--long=31", "--no-check", "-c", "-q", path])
    if kind in ("extra", "new"):
        if not FAST:
            r["lzma_x86"] = lz(data, bcj=lzma.FILTER_X86)
            ds = dict_size(n)
            r["lzma_arm64"] = cli(["xz", "--format=raw", "--arm64", "--lzma2=preset=9e,dict=%d,lc=2,lp=2,pb=2" % ds, "-c", path])
        # old file as dictionary / prefix
        if FAST and kind == "new" and n > 10_000_000:
            # one level down for the two 18 MB pairs (the -22 run did not finish in 50 minutes on the shared machine)
            r["zstd_pf"] = cli(["zstd", "-19", "--long=27", "--no-check", "--patch-from=" + oldp, "-c", "-q", path])
            r["zstd_pf_level"] = 19
        else:
            r["zstd_pf"] = cli(["zstd", "--ultra", "-22", "--long=31", "--no-check", "--patch-from=" + oldp, "-c", "-q", path])
        if not (FAST and kind == "new" and n > 10_000_000):
            r["brotli_dict"] = cli(["brotli", "-q", "11", "--large_window=30", "-D", oldp, "-c", path])
        try:
            r["lzma_dict"] = int(subprocess.run([LZDICT, oldp, path], capture_output=True, text=True, check=True, timeout=BUDGET).stdout)
        except subprocess.TimeoutExpired:
            r["lzma_dict"] = None
    return {k: v for k, v in r.items() if v is not None}


def one(job):
    p, kind, path = job
    oldp, newp = paths(p)
    return p, kind, codecs(path, oldp, kind)


jobs = []
for p in pairs():
    wd = os.path.join(WORK, p)
    oldp, newp = paths(p)
    for kind, path in (("extra", os.path.join(wd, "extra.bin")), ("diff", os.path.join(wd, "diff.bin")),
                       ("ctrl", os.path.join(wd, "ctrl.bin")), ("ctrl6", os.path.join(wd, "t43", "ctrl6.bin")),
                       ("nz2", os.path.join(wd, "t43", "nz2.bin")), ("new", newp)):
        if os.path.exists(path):
            jobs.append((p, kind, path))
j = min(3, int(sys.argv[sys.argv.index("-j") + 1]) if "-j" in sys.argv else 3)     # never more than 3 concurrent jobs
outp = os.path.join(RESULTS, "q2.json")
res = json.load(open(outp)) if os.path.exists(outp) else {}
jobs = [jb for jb in jobs if jb[1] not in res.get(jb[0], {})]
# cheap sections first, whole files last
order = {"ctrl6": 0, "nz2": 1, "ctrl": 2, "extra": 3, "diff": 4, "new": 5}
jobs.sort(key=lambda jb: (order[jb[1]], os.path.getsize(jb[2])))
from concurrent.futures import as_completed
with ThreadPoolExecutor(j) as ex:
    futs = [ex.submit(one, jb) for jb in jobs]
    for f in as_completed(futs):
        p, kind, r = f.result()
        res.setdefault(p, {})[kind] = r
        print(p, kind, r, flush=True)
        json.dump(res, open(outp, "w"), indent=1)
