#!/usr/bin/env python3
"""Configuration matrix for bsdiff6 over bench/corpus.

Runs the bsdiff6 CLI once per (pair, configuration) with --stats-json, verifies every patch with
bspatch6, and writes bench/results/matrix-<stamp>.{md,csv,json}. bsdiff 4.3 numbers come from
bench/results/baseline.json (run bench.py once to fill it).

Usage:
  matrix.py [--only SUBSTR] [--exclude SUBSTR] [--configs a,b,...] [--config NAME=FLAGS ...]
            [--big-configs a,b,...] [--compare-to NAME] [--detail a,b,...] [--jobs N]
            [--timeout SEC] [--resume FILE.json] [--bin DIR] [--tag TEXT]

Configurations are the built-in CONFIGS unless --config is given (repeatable), e.g.
  --config base= --config c324="--dp-costs 3,2,40"
FLAGS is split like a shell command line. With --config, --configs defaults to those names in
order and --big-configs defaults to all of them. --compare-to NAME (default: the first
configuration) adds per-pair ratios to that configuration and lists pairs more than 1% worse.

Pairs whose new file is over --big-mb (default 10) MB run only --big-configs (default
"default,prune"); pass --big-configs all to run every configuration on them.
--jobs > 1 runs pairs concurrently (at most 3); timings are then less reliable.
Standard library only. Wall, CPU (user+sys) and peak RSS come from `/usr/bin/time -l` (macOS).
"""
import argparse, concurrent.futures, csv, datetime, json, math, os, re, shlex, subprocess, sys, tempfile, threading

BENCH = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(BENCH)
CORPUS = os.path.join(BENCH, "corpus")
TIME = "/usr/bin/time"

# The DP prunes hopeless states by default (DESIGN §6); "no-prune" is the thesis's exact DP.
CONFIGS = [
    ("default", []),
    ("no-prune", ["--no-prune"]),
    ("block-only", ["--block-only"]),
    ("local-only", ["--no-block"]),
    ("mm4", ["--min-match", "4"]),
    ("mm8", ["--min-match", "8"]),
]
DETAIL_CONFIGS = ("default", "no-prune")


def timed(cmd, timeout, tmpdir):
    """Runs cmd (a list) under /usr/bin/time -l. Returns dict(ok, wall, cpu, rss, err)."""
    tf = tempfile.NamedTemporaryFile(dir=tmpdir, delete=False, suffix=".time")
    tf.close()
    try:
        p = subprocess.run([TIME, "-l", "-o", tf.name] + cmd, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE,
                           timeout=timeout)
    except subprocess.TimeoutExpired:
        os.unlink(tf.name)
        return dict(ok=False, wall=timeout, cpu=None, rss=None, err="timeout after %ss" % timeout)
    txt = open(tf.name).read()
    os.unlink(tf.name)
    m = re.search(r"([\d.]+) real\s+([\d.]+) user\s+([\d.]+) sys", txt)
    r = re.search(r"^\s*(\d+)\s+maximum resident set size", txt, re.M)
    err = p.stderr.decode("utf8", "replace").strip()[-300:]
    return dict(ok=p.returncode == 0, wall=float(m.group(1)) if m else None,
                cpu=float(m.group(2)) + float(m.group(3)) if m else None,
                rss=int(r.group(1)) if r else None, err=("exit %d: %s" % (p.returncode, err)) if p.returncode else "")


def same_file(a, b):
    if os.path.getsize(a) != os.path.getsize(b):
        return False
    with open(a, "rb") as fa, open(b, "rb") as fb:
        while True:
            x, y = fa.read(1 << 20), fb.read(1 << 20)
            if x != y:
                return False
            if not x:
                return True


# ---- summaries ----
def wavg(items):
    items = [(s, n) for s, n in items if s is not None]
    if not items:
        return None
    w = sum(math.sqrt(n) for _, n in items)
    return sum(math.sqrt(n) * (s / n) for s, n in items) / w


def gmean(xs):
    xs = [x for x in xs if x]
    return math.exp(sum(math.log(x) for x in xs) / len(xs)) if xs else None


def n0(x):
    return "-" if x is None else "{:,}".format(int(x))


def f2(x):
    return "-" if x is None else "%.2f" % x


def f4(x):
    return "-" if x is None else "%.4f" % x


def pct(x):
    return "-" if x is None else "%.2f%%" % (100 * x)


def mb(x):
    return "-" if x is None else "%.0f" % (x / 1048576)


def rx(x):
    return "-" if x is None else "%.3fx" % x


def table(header, rows, align=None):
    align = align or (["---"] + ["---:"] * (len(header) - 1))
    out = ["| " + " | ".join(header) + " |", "|" + "|".join(align) + "|"]
    out += ["| " + " | ".join(str(c) for c in r) + " |" for r in rows]
    return out


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--only")
    ap.add_argument("--exclude", action="append", default=[], help="skip pairs containing SUBSTR (repeatable)")
    ap.add_argument("--config", action="append", default=[], metavar="NAME=FLAGS",
                    help="a configuration given on the command line (repeatable); replaces the built-in list")
    ap.add_argument("--configs")
    ap.add_argument("--big-configs")
    ap.add_argument("--compare-to", help="reference configuration for the per-pair comparison (default: first)")
    ap.add_argument("--detail", help="configurations that get the per-pair section/alignment tables")
    ap.add_argument("--tag", default="", help="appended to the output file names")
    ap.add_argument("--big-mb", type=float, default=10)
    ap.add_argument("--jobs", type=int, default=1)
    ap.add_argument("--timeout", type=float, default=3600)
    ap.add_argument("--resume", help="raw JSON of an earlier run; finished (pair, config) entries are reused")
    ap.add_argument("--bin", default=os.path.join(ROOT, "src", "cli", "%s", "bin", "Release", "net10.0"))
    ap.add_argument("--out", default=os.path.join(BENCH, "results"))
    a = ap.parse_args()
    jobs = max(1, min(3, a.jobs))

    # Each CLI has its own build folder; --bin may contain %s for the tool name.
    diffdir = a.bin % "bsdiff6" if "%s" in a.bin else a.bin
    patchdir = a.bin % "bspatch6" if "%s" in a.bin else a.bin
    for d, n in ((diffdir, "bsdiff6.dll"), (patchdir, "bspatch6.dll")):
        if not os.path.exists(os.path.join(d, n)):
            sys.exit("missing %s: dotnet build -c Release %s/BsDiff6.sln" % (os.path.join(d, n), ROOT))

    configs = CONFIGS
    if a.config:
        configs = []
        for spec in a.config:
            name, eq, flags = spec.partition("=")
            if not eq or not name or "," in name:
                sys.exit("--config needs NAME=FLAGS, not " + spec)
            configs.append((name, shlex.split(flags)))
        if len({c for c, _ in configs}) != len(configs):
            sys.exit("duplicate --config name")
    all_names = [c for c, _ in configs]
    if a.configs is None:
        a.configs = ",".join(all_names)
    if a.big_configs is None:
        a.big_configs = "all" if a.config else "default,no-prune"
    wanted = [c for c in a.configs.split(",") if c]
    flags_of = dict(configs)
    for c in wanted:
        if c not in flags_of:
            sys.exit("unknown config " + c)
    big_cfgs = all_names if a.big_configs == "all" else a.big_configs.split(",")
    a.flags_of = flags_of
    a.compare_to = a.compare_to or wanted[0]
    if a.compare_to not in wanted:
        sys.exit("--compare-to must be one of the configurations run")
    a.detail = [c for c in a.detail.split(",") if c] if a.detail is not None else [c for c in DETAIL_CONFIGS if c in wanted]

    pairs = sorted(d for d in os.listdir(CORPUS) if os.path.isfile(os.path.join(CORPUS, d, "new")))
    if a.only:
        pairs = [p for p in pairs if a.only in p]
    pairs = [p for p in pairs if not any(x in p for x in a.exclude)]
    base = json.load(open(os.path.join(a.out, "baseline.json")))
    newsize = {p: os.path.getsize(os.path.join(CORPUS, p, "new")) for p in pairs}
    oldsize = {p: os.path.getsize(os.path.join(CORPUS, p, "old")) for p in pairs}
    big = {p for p in pairs if newsize[p] > a.big_mb * 1e6}

    stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S") + ("-" + a.tag if a.tag else "")
    raw_path = os.path.join(a.out, "matrix-%s.json" % stamp)
    results = {}
    if a.resume:
        for r in json.load(open(a.resume)):
            # Reused only when the configuration's flags are unchanged (older files have no flags).
            if r.get("ok") and r["pair"] in newsize and r["config"] in flags_of and \
                    r.get("flags", flags_of[r["config"]]) == flags_of[r["config"]]:
                results[(r["pair"], r["config"])] = r

    todo = []
    for p in pairs:
        for c in wanted:
            if p in big and c not in big_cfgs:
                continue
            if (p, c) not in results:
                todo.append((p, c))
    # Big pairs first so that long runs do not end up alone at the tail.
    todo.sort(key=lambda pc: -newsize[pc[0]])

    tmproot = tempfile.mkdtemp(prefix="matrix.", dir=a.out)
    lock = threading.Lock()

    def save():
        json.dump(list(results.values()), open(raw_path, "w"), indent=1)

    def work(pc):
        p, c = pc
        print("[%s] %s ..." % (p, c), file=sys.stderr, flush=True)
        r = run_one(p, c, flags_of[c], diffdir, patchdir, a.timeout, tmproot)
        with lock:
            results[(p, c)] = r
            save()
        print("[%s] %s: %s %s wall %.1fs cpu %s" % (p, c, "ok" if r["ok"] else "FAIL " + r["err"], n0(r.get("size")),
              r.get("diff_wall") or 0, f2(r.get("diff_cpu"))), file=sys.stderr, flush=True)

    try:
        with concurrent.futures.ThreadPoolExecutor(max_workers=jobs) as ex:
            list(ex.map(work, todo))
    finally:
        try:
            os.rmdir(tmproot)
        except OSError:
            pass
    save()
    render(a, pairs, wanted, results, base, newsize, oldsize, big, big_cfgs, stamp, jobs)


def run_one(pair, cfg, flags, diffdir, patchdir, timeout, tmproot):
    """Diff with bsdiff6 (timed, with --stats-json), then verify with bspatch6."""
    old, new = os.path.join(CORPUS, pair, "old"), os.path.join(CORPUS, pair, "new")
    tmp = tempfile.mkdtemp(prefix="m.", dir=tmproot)
    patch, out, stats = (os.path.join(tmp, x) for x in ("patch", "out", "stats.json"))
    rec = dict(pair=pair, config=cfg, flags=flags, ok=False, err="")
    try:
        d = timed(["dotnet", os.path.join(diffdir, "bsdiff6.dll")] + flags + ["--stats-json", stats, old, new, patch],
                  timeout, tmp)
        rec.update(diff_wall=d["wall"], diff_cpu=d["cpu"], diff_rss=d["rss"])
        if not d["ok"]:
            rec["err"] = "diff: " + d["err"]
            return rec
        rec["size"] = os.path.getsize(patch)
        rec["stats"] = json.load(open(stats))
        p = timed(["dotnet", os.path.join(patchdir, "bspatch6.dll"), old, out, patch], timeout, tmp)
        rec.update(patch_wall=p["wall"], patch_cpu=p["cpu"], patch_rss=p["rss"])
        if not p["ok"]:
            rec["err"] = "patch: " + p["err"]
        elif not same_file(out, new):
            rec["err"] = "round-trip mismatch"
        else:
            rec["ok"] = True
        return rec
    finally:
        for f in os.listdir(tmp):
            os.remove(os.path.join(tmp, f))
        os.rmdir(tmp)


def render(a, pairs, wanted, results, base, newsize, oldsize, big, big_cfgs, stamp, jobs):
    def b43(p):
        b = base.get(p, {}).get("b43", {})
        return b.get("size") if b.get("ok") else None

    def get(p, c):
        r = results.get((p, c))
        return r if r and r.get("ok") else None

    def size(p, c):
        r = get(p, c)
        return r["size"] if r else None

    def ratio(p, c):
        s, b = size(p, c), b43(p)
        return s / b if s and b else None

    L = ["# bsdiff6 configuration matrix (%s)" % stamp, ""]
    L.append("Corpus: %d pairs. Sizes in bytes; ratio = bsdiff6 / bsdiff 4.3 (lower is better). "
             "Wall and CPU (user+sys) seconds and peak RSS from `/usr/bin/time -l`, whole process including "
             "the .NET runtime start (~0.05-0.1 s). Jobs: %d. Pairs over %g MB ran only: %s." %
             (len(pairs), jobs, a.big_mb, ", ".join(big_cfgs)))
    L.append("")
    fails = [r for r in results.values() if not r.get("ok")]

    # 1. default table
    if "default" in wanted:
        L += ["## Default configuration", ""]
        rows = []
        for p in pairs:
            r = get(p, "default")
            bb = base.get(p, {}).get("b43", {})
            rows.append([p, n0(newsize[p]), n0(b43(p)), n0(size(p, "default")) if r else "**FAIL**", rx(ratio(p, "default")),
                         f2(r and r["diff_wall"]), f2(r and r["diff_cpu"]), mb(r and r["diff_rss"]),
                         f2(bb.get("dsecs")), mb(bb.get("drss")), f2(r and r.get("patch_wall"))])
        L += table(["pair", "new", "bsdiff 4.3", "bsdiff6", "ratio", "diff wall s", "diff CPU s", "RSS MB",
                    "b43 diff s", "b43 RSS MB", "patch wall s"], rows)
        L.append("")

    # 2. summary per configuration
    L += ["## Summary per configuration", "",
          "avg = thesis average compression: mean of patch/new weighted by sqrt(new); lower is better. "
          "gmean = geometric mean of bsdiff6/bsdiff 4.3 per pair. Pairs where a configuration did not run "
          "are left out of its rows (n = pairs counted).", ""]
    cats = sorted({p.split("-")[0] for p in pairs})
    rows = [["bsdiff 4.3", str(len(pairs)), n0(sum(b43(p) or 0 for p in pairs)),
             f4(wavg([(b43(p), newsize[p]) for p in pairs]))] +
            [f4(wavg([(b43(p), newsize[p]) for p in pairs if p.startswith(c + "-")])) for c in cats] +
            ["1.000x", f2(sum(base[p]["b43"]["dsecs"] for p in pairs)), "-", "-"]]
    for c in wanted:
        ps = [p for p in pairs if get(p, c)]
        rows.append([c, str(len(ps)), n0(sum(size(p, c) for p in ps)),
                     f4(wavg([(size(p, c), newsize[p]) for p in ps]))] +
                    [f4(wavg([(size(p, c), newsize[p]) for p in ps if p.startswith(k + "-")])) for k in cats] +
                    [rx(gmean([ratio(p, c) for p in ps])), f2(sum(get(p, c)["diff_wall"] for p in ps)),
                     f2(sum(get(p, c)["diff_cpu"] for p in ps)), mb(max([get(p, c)["diff_rss"] or 0 for p in ps] or [0]))])
    L += table(["config", "n", "total bytes", "avg all"] + ["avg " + k for k in cats] +
               ["gmean vs 4.3", "diff wall s", "diff CPU s", "max RSS MB"], rows)
    L.append("")
    # Same summary restricted to the pairs every configuration ran on, so that rows compare like with like.
    common = [p for p in pairs if all(get(p, c) for c in wanted)]
    if len(common) != len(pairs):
        L += ["Same, restricted to the %d pairs every configuration ran on:" % len(common), ""]
        rows = [["bsdiff 4.3", n0(sum(b43(p) or 0 for p in common)), f4(wavg([(b43(p), newsize[p]) for p in common]))] +
                [f4(wavg([(b43(p), newsize[p]) for p in common if p.startswith(k + "-")])) for k in cats] + ["1.000x", "-", "-"]]
        for c in wanted:
            rows.append([c, n0(sum(size(p, c) for p in common)), f4(wavg([(size(p, c), newsize[p]) for p in common]))] +
                        [f4(wavg([(size(p, c), newsize[p]) for p in common if p.startswith(k + "-")])) for k in cats] +
                        [rx(gmean([ratio(p, c) for p in common])), f2(sum(get(p, c)["diff_wall"] for p in common)),
                         f2(sum(get(p, c)["diff_cpu"] for p in common))])
        L += table(["config", "total bytes", "avg all"] + ["avg " + k for k in cats] + ["gmean vs 4.3", "diff wall s", "diff CPU s"], rows)
        L.append("")

    # 2b. section totals and DP statistics per configuration (over the common pairs)
    L += ["## Section totals per configuration (stored bytes, %d common pairs)" % len(common), "",
          "matched = share of all new bytes in matched segments; differ = share of matched bytes that differ; "
          "extra raw = unmatched bytes; non-zero raw = differing matched bytes.", ""]
    rows = []
    for c in wanted:
        tot = dict(control=0, nonzero=0, map=0, extra=0, extra_raw=0, nz_raw=0, header=0, newb=0, matched=0, equal=0)
        for p in common:
            st = get(p, c)["stats"]
            sec, al = st["sections"], st["alignment"]
            for k in ("control", "nonzero", "map", "extra"):
                tot[k] += sec[k]["compressed"]
            tot["header"] += sec["header"]
            tot["extra_raw"] += sec["extra"]["raw"]
            tot["nz_raw"] += sec["nonzero"]["raw"]
            tot["newb"] += al["new_bytes"]
            tot["matched"] += al["matched_bytes"]
            tot["equal"] += al["equal_bytes"]
        opts = get(common[0], c)["stats"]["options"] if common else {}
        dc = opts.get("dp_costs")
        rows.append([c, "%s,%s,%s" % (dc["mismatch"], dc["unmatched"], dc["switch"]) if dc else "-",
                     n0(tot["control"]), n0(tot["nonzero"]), n0(tot["map"]), n0(tot["extra"]), n0(tot["header"]),
                     n0(tot["control"] + tot["nonzero"] + tot["map"] + tot["extra"] + tot["header"]),
                     n0(tot["nz_raw"]), n0(tot["extra_raw"]),
                     pct(tot["matched"] / tot["newb"]) if tot["newb"] else "-",
                     pct(1 - tot["equal"] / tot["matched"]) if tot["matched"] else "-"])
    L += table(["config", "costs", "control", "non-zero", "map", "extra", "header", "total", "non-zero raw",
                "extra raw", "matched", "differ"], rows)
    L.append("")

    # 2c. per-pair comparison with the reference configuration
    ref = a.compare_to
    others = [c for c in wanted if c != ref]
    if others:
        L += ["## Per-pair ratio to `%s`" % ref, "",
              "size(config) / size(%s); pairs more than 1%% worse are listed below the table." % ref, ""]
        rows = []
        for p in pairs:
            rs = size(p, ref)
            rows.append([p] + [rx(size(p, c) / rs) if rs and size(p, c) else "-" for c in others])
        L += table(["pair"] + others, rows)
        L.append("")
        for c in others:
            bad = [(p, size(p, c) / size(p, ref)) for p in pairs if size(p, ref) and size(p, c) and size(p, c) > 1.01 * size(p, ref)]
            L.append("- `%s`: %s" % (c, "; ".join("%s %s" % (p, rx(x)) for p, x in bad) if bad else "no pair more than 1% worse"))
        L.append("")

    # 3. size / ratio matrix
    L += ["## Patch size and ratio to bsdiff 4.3", ""]
    rows = [[p, n0(b43(p))] + ["%s (%s)" % (n0(size(p, c)), rx(ratio(p, c))) if get(p, c) else "-" for c in wanted] for p in pairs]
    L += table(["pair", "bsdiff 4.3"] + wanted, rows)
    L.append("")

    # 4. time / memory matrix
    L += ["## Diff wall s / CPU s / peak RSS MB", ""]
    rows = [[p] + ["%s / %s / %s" % (f2(get(p, c)["diff_wall"]), f2(get(p, c)["diff_cpu"]), mb(get(p, c)["diff_rss"]))
                   if get(p, c) else "-" for c in wanted] for p in pairs]
    L += table(["pair"] + wanted, rows)
    L.append("")

    # 5. section breakdown and alignment statistics
    for c in a.detail:
        if c not in wanted:
            continue
        L += ["## Sections: %s" % c, "",
              "raw / stored bytes; codec in brackets. map raw = D (difference-string length).", ""]
        rows, tot = [], [0] * 9
        for p in pairs:
            r = get(p, c)
            if not r:
                rows.append([p] + ["-"] * 7)
                continue
            s = r["stats"]["sections"]
            ct, nz, mp, ex = s["control"], s["nonzero"], s["map"], s["extra"]
            vals = [ct["raw"], ct["compressed"], nz["raw"], nz["compressed"], mp["raw"], mp["compressed"], ex["raw"], ex["compressed"], s["header"]]
            tot = [x + y for x, y in zip(tot, vals)]
            rows.append([p, s["diff_mode"],
                         "%s / %s (%s)" % (n0(ct["raw"]), n0(ct["compressed"]), ct["codec"]),
                         "%s / %s (%s)" % (n0(nz["raw"]), n0(nz["compressed"]), nz["codec"]),
                         "%s / %s" % (n0(mp["raw"]), n0(mp["compressed"])),
                         "%s / %s (%s)" % (n0(ex["raw"]), n0(ex["compressed"]), ex["codec"]),
                         n0(s["total"])])
        stored = tot[1] + tot[3] + tot[5] + tot[7] + tot[8]
        rows.append(["**TOTAL**", "",
                     "%s / %s" % (n0(tot[0]), n0(tot[1])), "%s / %s" % (n0(tot[2]), n0(tot[3])),
                     "%s / %s" % (n0(tot[4]), n0(tot[5])), "%s / %s" % (n0(tot[6]), n0(tot[7])), n0(stored)])
        if stored:
            rows.append(["**share of stored**", "", pct(tot[1] / stored), pct(tot[3] / stored), pct(tot[5] / stored),
                         pct(tot[7] / stored), "header %s" % pct(tot[8] / stored)])
        L += table(["pair", "mode", "control", "non-zero", "map", "extra", "total"], rows)
        L.append("")

        L += ["## Alignment: %s" % c, "",
              "block cover / eq = share of new covered by block alignment alone / share of those bytes equal. "
              "matched = share of new in matched segments after the DP; differ = share of matched bytes that differ.", ""]
        rows = []
        for p in pairs:
            r = get(p, c)
            if not r:
                rows.append([p] + ["-"] * 9)
                continue
            s = r["stats"]
            al, b, dp, t = s["alignment"], s.get("block", {}), s.get("dp", {}), s["timings_ms"]
            rows.append([p, n0(al["segments"]), pct(al["matched_fraction"]), pct(al["differing_fraction_of_matched"]),
                         "%s / %s" % (pct(b.get("covered_fraction")), pct(b.get("match_fraction"))),
                         f2(dp.get("avg_states")),
                         "%.2f / %.2f" % (t["block_wall"] / 1000, t["local_wall"] / 1000),
                         "%.2f / %.2f" % (t["align_wall"] / 1000, t["align_cpu"] / 1000),
                         "%.2f / %.2f" % (t["dp_wall"] / 1000, t["dp_cpu"] / 1000),
                         "%.2f / %.2f" % (t["encode_wall"] / 1000, t["encode_cpu"] / 1000)])
        L += table(["pair", "segments", "matched", "differ", "block cover / eq", "DP avg states",
                    "block / local wall s", "align wall / CPU s", "DP wall / CPU s", "encode wall / CPU s"], rows)
        L.append("")

    if fails:
        L += ["**FAILURES:**", ""] + ["- %s / %s: %s" % (r["pair"], r["config"], r["err"]) for r in fails] + [""]
    md = "\n".join(L) + "\n"
    L_cmd = "Command: `%s`\n" % " ".join(shlex.quote(x) for x in sys.argv)
    md = md.replace("\n\n", "\n\n" + L_cmd + "\n", 1)
    open(os.path.join(a.out, "matrix-%s.md" % stamp), "w").write(md)
    print(md)

    with open(os.path.join(a.out, "matrix-%s.csv" % stamp), "w", newline="") as f:
        w = csv.writer(f)
        h = ["pair", "category", "config", "ok", "old", "new", "bsdiff43", "size", "ratio_b43", "diff_wall_s", "diff_cpu_s",
             "diff_rss", "patch_wall_s", "diff_mode",
             "control_raw", "control_stored", "control_codec", "nonzero_raw", "nonzero_stored", "nonzero_codec",
             "map_raw", "map_stored", "extra_raw", "extra_stored", "extra_codec", "header",
             "segments", "matched_fraction", "differing_fraction", "block_covered", "block_match", "dp_avg_states",
             "block_wall_ms", "local_wall_ms", "align_wall_ms", "align_cpu_ms", "dp_wall_ms", "dp_cpu_ms",
             "encode_wall_ms", "encode_cpu_ms", "dp_cost", "flags", "err"]
        w.writerow(h)
        for p in pairs:
            for c in wanted:
                r = results.get((p, c))
                if not r:
                    continue
                s = r.get("stats") or {}
                sec = s.get("sections", {})
                al, b, dp, t = s.get("alignment", {}), s.get("block", {}), s.get("dp", {}), s.get("timings_ms", {})
                g = lambda d, k: (d or {}).get(k)
                w.writerow([p, p.split("-")[0], c, int(bool(r.get("ok"))), oldsize[p], newsize[p], b43(p), r.get("size"),
                            ratio(p, c), r.get("diff_wall"), r.get("diff_cpu"), r.get("diff_rss"), r.get("patch_wall"),
                            sec.get("diff_mode"),
                            g(sec.get("control"), "raw"), g(sec.get("control"), "compressed"), g(sec.get("control"), "codec"),
                            g(sec.get("nonzero"), "raw"), g(sec.get("nonzero"), "compressed"), g(sec.get("nonzero"), "codec"),
                            g(sec.get("map"), "raw"), g(sec.get("map"), "compressed"),
                            g(sec.get("extra"), "raw"), g(sec.get("extra"), "compressed"), g(sec.get("extra"), "codec"),
                            sec.get("header"), al.get("segments"), al.get("matched_fraction"),
                            al.get("differing_fraction_of_matched"), b.get("covered_fraction"), b.get("match_fraction"),
                            dp.get("avg_states"), t.get("block_wall"), t.get("local_wall"), t.get("align_wall"),
                            t.get("align_cpu"), t.get("dp_wall"), t.get("dp_cpu"), t.get("encode_wall"), t.get("encode_cpu"),
                            dp.get("cost"), " ".join(a.flags_of.get(c, [])), r.get("err")])
    if fails:
        print("FAIL: %d failure(s)" % len(fails), file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    main()
