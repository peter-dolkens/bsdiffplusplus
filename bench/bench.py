#!/usr/bin/env python3
"""Benchmark binary-diff tools against bsdiff 4.3 over bench/corpus.

Usage:
  bench.py [--only SUBSTR] [--timeout SEC] [--out DIR] [--refresh-baseline]
           [--tool NAME "DIFFCMD" "PATCHCMD"] ...

Command templates may use {old} {new} {patch} {out}; values are shell-quoted.
Example:
  bench.py --tool bsdiff6 "dotnet /p/bsdiff6.dll {old} {new} {patch}" \
                          "dotnet /p/bspatch6.dll {old} {out} {patch}"
Standard library only. Peak RSS comes from `/usr/bin/time -l` (macOS).
"""
import argparse, csv, datetime, json, math, os, re, shlex, shutil, subprocess, sys, tempfile, time

BENCH = os.path.dirname(os.path.abspath(__file__))
CORPUS = os.path.join(BENCH, "corpus")
BSDIFF43 = os.path.join(BENCH, "baseline", "bsdiff43")
BSPATCH43 = os.path.join(BENCH, "baseline", "bspatch43")
TIME = "/usr/bin/time"


def run(cmd, timeout, tmpdir):
    """Run shell command under /usr/bin/time -l. Returns dict(ok, secs, rss, err)."""
    tf = os.path.join(tmpdir, "time.txt")
    t0 = time.perf_counter()
    try:
        p = subprocess.run([TIME, "-l", "-o", tf, "sh", "-c", cmd], stdout=subprocess.DEVNULL,
                           stderr=subprocess.PIPE, timeout=timeout)
    except subprocess.TimeoutExpired:
        return dict(ok=False, secs=timeout, rss=None, err="timeout after %ss" % timeout)
    secs = time.perf_counter() - t0
    rss = None
    try:
        m = re.search(r"^\s*(\d+)\s+maximum resident set size", open(tf).read(), re.M)
        if m:
            rss = int(m.group(1))
    except OSError:
        pass
    err = p.stderr.decode("utf8", "replace").strip()[-300:]
    return dict(ok=p.returncode == 0, secs=secs, rss=rss, err=("exit %d: %s" % (p.returncode, err)) if p.returncode else "")


def files_equal(a, b):
    if not (os.path.exists(a) and os.path.exists(b)) or os.path.getsize(a) != os.path.getsize(b):
        return False
    with open(a, "rb") as fa, open(b, "rb") as fb:
        while True:
            x, y = fa.read(1 << 20), fb.read(1 << 20)
            if x != y:
                return False
            if not x:
                return True


def fill(tpl, **kw):
    return tpl.format(**{k: shlex.quote(v) for k, v in kw.items()})


def diff_and_verify(difftpl, patchtpl, old, new, timeout, tmp):
    patch, out = os.path.join(tmp, "patch"), os.path.join(tmp, "out")
    for f in (patch, out):
        if os.path.exists(f):
            os.remove(f)
    r = dict(size=None, dsecs=None, drss=None, psecs=None, prss=None, ok=False, err="")
    d = run(fill(difftpl, old=old, new=new, patch=patch, out=out), timeout, tmp)
    r.update(dsecs=d["secs"], drss=d["rss"])
    if not d["ok"]:
        r["err"] = "diff: " + d["err"]
        return r
    if not os.path.exists(patch):
        r["err"] = "diff produced no patch file"
        return r
    r["size"] = os.path.getsize(patch)
    p = run(fill(patchtpl, old=old, new=new, patch=patch, out=out), timeout, tmp)
    r.update(psecs=p["secs"], prss=p["rss"])
    if not p["ok"]:
        r["err"] = "patch: " + p["err"]
    elif not files_equal(out, new):
        r["err"] = "round-trip mismatch"
    else:
        r["ok"] = True
    for f in (patch, out):
        if os.path.exists(f):
            os.remove(f)
    return r


def bzip2_size(path):
    p = subprocess.Popen(["bzip2", "-9", "-c", path], stdout=subprocess.PIPE)
    n = 0
    while True:
        b = p.stdout.read(1 << 20)
        if not b:
            break
        n += len(b)
    if p.wait() != 0:
        raise RuntimeError("bzip2 failed on " + path)
    return n


def sig(old, new):
    so, sn = os.stat(old), os.stat(new)
    sb = os.stat(BSDIFF43)
    return [so.st_size, int(so.st_mtime), sn.st_size, int(sn.st_mtime), int(sb.st_mtime)]


# ---- formatting helpers ----
def kb(n):
    return "-" if n is None else "{:,}".format(n)

def secs(x):
    return "-" if x is None else ("%.2f" % x)

def mb(x):
    return "-" if x is None else "%.0f" % (x / 1048576)

def ratio(a, b):
    return None if a is None or not b else a / b

def rfmt(x):
    return "-" if x is None else "%.2fx" % x


def wavg(items):
    """items: [(size, newsize)] -> sqrt(new)-weighted mean of size/new"""
    items = [(s, n) for s, n in items if s is not None]
    if not items:
        return None
    w = sum(math.sqrt(n) for _, n in items)
    return sum(math.sqrt(n) * (s / n) for s, n in items) / w


def gmean(xs):
    xs = [x for x in xs if x]
    return math.exp(sum(math.log(x) for x in xs) / len(xs)) if xs else None


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--tool", nargs=3, action="append", default=[], metavar=("NAME", "DIFFCMD", "PATCHCMD"))
    ap.add_argument("--only", help="only pairs whose name contains this substring")
    ap.add_argument("--timeout", type=float, default=1800, help="seconds per command (default 1800)")
    ap.add_argument("--out", default=os.path.join(BENCH, "results"))
    ap.add_argument("--refresh-baseline", action="store_true")
    a = ap.parse_args()

    if not os.path.exists(BSDIFF43):
        sys.exit("baseline missing: run %s/baseline/build.sh" % BENCH)
    names = sorted(d for d in os.listdir(CORPUS) if os.path.isfile(os.path.join(CORPUS, d, "old"))
                   and os.path.isfile(os.path.join(CORPUS, d, "new")))
    if a.only:
        names = [n for n in names if a.only in n]
    if not names:
        sys.exit("no corpus pairs matched")
    tools = [t[0] for t in a.tool]
    os.makedirs(a.out, exist_ok=True)
    cache_path = os.path.join(a.out, "baseline.json")
    cache = {}
    if os.path.exists(cache_path) and not a.refresh_baseline:
        try:
            cache = json.load(open(cache_path))
        except ValueError:
            cache = {}
    tmp_root = os.path.join(a.out, "tmp")
    os.makedirs(tmp_root, exist_ok=True)
    tmp = tempfile.mkdtemp(prefix="run.", dir=tmp_root)

    rows, failures = [], []
    try:
        for n in names:
            old, new = (os.path.join(CORPUS, n, x) for x in ("old", "new"))
            row = dict(pair=n, cat=n.split("-")[0], new=os.path.getsize(new))
            s = sig(old, new)
            c = cache.get(n)
            if c and c.get("sig") == s:
                print("[%s] baseline cached" % n, file=sys.stderr)
            else:
                print("[%s] baseline ..." % n, file=sys.stderr)
                c = dict(sig=s, bzip2=bzip2_size(new),
                         b43=diff_and_verify(shlex.quote(BSDIFF43) + " {old} {new} {patch}",
                                             shlex.quote(BSPATCH43) + " {old} {out} {patch}", old, new, a.timeout, tmp))
                cache[n] = c
                json.dump(cache, open(cache_path, "w"), indent=1)
            row["bzip2"], row["b43"] = c["bzip2"], c["b43"]
            if not c["b43"]["ok"]:
                failures.append("%s: bsdiff43: %s" % (n, c["b43"]["err"]))
            row["tools"] = {}
            for name, dcmd, pcmd in a.tool:
                print("[%s] %s ..." % (n, name), file=sys.stderr)
                r = diff_and_verify(dcmd, pcmd, old, new, a.timeout, tmp)
                row["tools"][name] = r
                if not r["ok"]:
                    failures.append("%s: %s: %s" % (n, name, r["err"]))
            rows.append(row)
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
        try:
            os.rmdir(tmp_root)
        except OSError:
            pass

    # ---- render ----
    hdr = ["pair", "new", "bzip2 -9", "bsdiff43", "b43 diff s", "b43 RSS MB", "b43 patch s"]
    for t in tools:
        hdr += [t, t + " /b43", t + " diff s", t + " RSS MB", t + " patch s"]
    lines = ["| " + " | ".join(hdr) + " |", "|" + "|".join(["---"] + ["---:"] * (len(hdr) - 1)) + "|"]

    def b43size(r):
        return r["b43"]["size"] if r["b43"]["ok"] else None

    def tsize(r, t):
        x = r["tools"][t]
        return x["size"] if x["ok"] else None

    for r in rows:
        b = r["b43"]
        cells = [r["pair"], kb(r["new"]), kb(r["bzip2"]), kb(b["size"]) if b["ok"] else "**FAIL**",
                 secs(b["dsecs"]), mb(b["drss"]), secs(b["psecs"])]
        for t in tools:
            x = r["tools"][t]
            if x["ok"]:
                cells += [kb(x["size"]), rfmt(ratio(x["size"], b43size(r))), secs(x["dsecs"]), mb(x["drss"]), secs(x["psecs"])]
            else:
                cells += ["**FAIL**", "-", secs(x["dsecs"]), mb(x["drss"]), "-"]
        lines.append("| " + " | ".join(cells) + " |")

    def footer(label, rs):
        cells = [label, kb(sum(r["new"] for r in rs)), kb(sum(r["bzip2"] for r in rs)),
                 kb(sum(b43size(r) or 0 for r in rs)),
                 secs(sum(r["b43"]["dsecs"] or 0 for r in rs)), "", secs(sum(r["b43"]["psecs"] or 0 for r in rs))]
        for t in tools:
            cells += [kb(sum(tsize(r, t) or 0 for r in rs)), "", secs(sum(r["tools"][t]["dsecs"] or 0 for r in rs)), "",
                      secs(sum(r["tools"][t]["psecs"] or 0 for r in rs))]
        lines.append("| " + " | ".join("**%s**" % c if c else "" for c in cells) + " |")

    def avgrow(label, rs):
        # thesis "average compression": mean of size/new weighted by sqrt(new); lower is better
        cells = [label, "", "%.4f" % wavg([(r["bzip2"], r["new"]) for r in rs]),
                 _f(wavg([(b43size(r), r["new"]) for r in rs])), "", "", ""]
        for t in tools:
            cells += [_f(wavg([(tsize(r, t), r["new"]) for r in rs])),
                      rfmt(gmean([ratio(tsize(r, t), b43size(r)) for r in rs])) , "", "", ""]
        lines.append("| " + " | ".join(cells) + " |")

    def _f(x):
        return "-" if x is None else "%.4f" % x

    footer("TOTAL", rows)
    avgrow("avg compression (sqrt-weighted), all", rows)
    for cat in sorted({r["cat"] for r in rows}):
        avgrow("avg compression, %s" % cat, [r for r in rows if r["cat"] == cat])
    lines.append("")
    lines.append("Avg-compression rows: mean of patch/new weighted by sqrt(new size); lower is better. "
                 "In the tool `/b43` columns of those rows the value is the geometric mean of per-pair tool/bsdiff43 size ratios. "
                 "Failed pairs are excluded from averages.")
    if failures:
        lines += ["", "**FAILURES:**"] + ["- " + f for f in failures]
    md = "\n".join(lines) + "\n"
    print(md)

    stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    open(os.path.join(a.out, stamp + ".md"), "w").write(md)
    with open(os.path.join(a.out, stamp + ".csv"), "w", newline="") as f:
        w = csv.writer(f)
        h = ["pair", "category", "new", "bzip2", "bsdiff43", "b43_diff_s", "b43_rss", "b43_patch_s", "b43_ok"]
        for t in tools:
            h += [t + "_size", t + "_ratio_b43", t + "_diff_s", t + "_rss", t + "_patch_s", t + "_ok"]
        w.writerow(h)
        for r in rows:
            b = r["b43"]
            row = [r["pair"], r["cat"], r["new"], r["bzip2"], b["size"], b["dsecs"], b["drss"], b["psecs"], int(b["ok"])]
            for t in tools:
                x = r["tools"][t]
                row += [x["size"], ratio(x["size"], b["size"]) if x["ok"] and b["ok"] else None, x["dsecs"], x["drss"], x["psecs"], int(x["ok"])]
            w.writerow(row)
    if failures:
        print("FAIL: %d failure(s)" % len(failures), file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    main()
