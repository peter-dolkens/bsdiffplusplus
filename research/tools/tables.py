#!/usr/bin/env python3
"""Regenerate every table of REPORT.md from results/*.json.

Writes results/tables.json ({key: markdown}) and results/tables.md.  Tolerates missing result
files: the corresponding keys are simply absent and build_report.py prints "not measured".
"""
import json, math, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from common import *

SECT = {}
CUR = [None]


def sect(key):
    CUR[0] = key
    SECT[key] = []


def emit(s=""):
    SECT[CUR[0]].append(s)


def load(name):
    p = os.path.join(RESULTS, name)
    try:
        return json.load(open(p)) if os.path.exists(p) else None
    except ValueError:
        return None


def n(x):
    return "-" if x is None else f"{int(round(x)):,}"


def pct(a, b):
    return "-" if a is None or not b else f"{100.0 * (a / b - 1):+.1f}%"


CATS = ("all", "upgrade", "patch", "other")


def catrow(label, per_pair, base=None):
    """row of sqrt-weighted size/new by category, optionally with change vs a base dict (same pairs)"""
    s = summary(per_pair)
    row = [label]
    for c in CATS:
        v = s[c]
        cell = "-" if v is None else f"{v:.4f}"
        if base is not None and v is not None:
            b = summary({p: base[p] for p in per_pair})[c]
            cell += f" ({100 * (v / b - 1):+.1f}%)"
        row.append(cell)
    return row


CATHDR = ["", "all", "upgrade", "patch", "other"]
q1 = load("q1.json")
PAIRS = pairs()
b43 = {p: q1[p]["patch"] for p in PAIRS}


def okcm(d, p):
    return d is not None and p in d and "bytes" in d[p]


# ------------------------------------------------------------------------------------------ Q1
sect("T1")
hdr = ["pair", "new", "patch", "patch/new", "ctrl", "diff", "extra", "ctrl %", "diff %", "extra %", "matched % of new", "non-zero % of diff", "records"]
rows = []
for p in PAIRS:
    r = q1[p]
    rows.append([short(p), n(r["new"]), n(r["patch"]), f"{r['patch']/r['new']:.4f}", n(r["ctrl_c"]), n(r["diff_c"]), n(r["extra_c"]),
                 f"{100*r['ctrl_c']/r['patch']:.0f}", f"{100*r['diff_c']/r['patch']:.0f}", f"{100*r['extra_c']/r['patch']:.0f}",
                 f"{100*r['diff_raw']/r['new']:.1f}", f"{100*r['nz']/max(1,r['diff_raw']):.2f}", n(r["triples"])])
emit(md_table(hdr, rows, "lrrrrrrrrrrrr"))
sect("T1s")
rows = []
for c in CATS:
    sel = [p for p in PAIRS if c == "all" or cat(p) == c]
    w = {k: wavg([(q1[p][k], q1[p]["new"]) for p in sel]) for k in ("patch", "ctrl_c", "diff_c", "extra_c")}
    rows.append([c, f"{w['patch']:.4f}", f"{w['ctrl_c']:.4f}", f"{w['diff_c']:.4f}", f"{w['extra_c']:.4f}",
                 f"{100*w['ctrl_c']/w['patch']:.1f}%", f"{100*w['diff_c']/w['patch']:.1f}%", f"{100*w['extra_c']/w['patch']:.1f}%"])
emit(md_table(["category (sqrt-weighted)", "patch/new", "ctrl/new", "diff/new", "extra/new", "ctrl share", "diff share", "extra share"], rows, "lrrrrrrr"))

# ------------------------------------------------------------------------------------------ Q2
q2 = load("q2.json")
CODECS = [("bzip2", "bzip2 -9"), ("zlib", "zlib -9"), ("lzma", "LZMA2 -9e"), ("lzma_pb0", "LZMA2 -9e pb=0"), ("lzma_lp2", "LZMA2 -9e lp=2"),
          ("brotli", "Brotli q11, 16 MiB window"), ("zstd", "zstd --ultra -22 --long"), ("lzma_x86", "x86 BCJ + LZMA2"), ("lzma_arm64", "ARM64 BCJ + LZMA2"),
          ("zstd_pf", "zstd -22 --patch-from old"), ("brotli_dict", "Brotli q11 -D old"), ("lzma_dict", "LZMA2 -9e, preset dict = old")]


def codec_summary(kind, base):
    rows = []
    for key, label in CODECS:
        have = [p for p in PAIRS if p in q2 and kind in q2[p] and (key in q2[p][kind] or q2[p][kind]["raw"] == 0) and (base in q2[p][kind] or q2[p][kind]["raw"] == 0)]
        if len(have) < 10:
            continue
        tot = sum(q2[p][kind].get(key, 0) for p in have)
        tb = sum(q2[p][kind].get(base, 0) for p in have)
        row = [label, str(len(have)), n(tot), pct(tot, tb)]
        for c in CATS:
            sel = [p for p in have if (c == "all" or cat(p) == c) and q2[p][kind]["raw"]]
            w = sum(math.sqrt(q2[p][kind]["raw"]) for p in sel)
            if not w:
                row.append("-"); continue
            v = sum(math.sqrt(q2[p][kind]["raw"]) * q2[p][kind][key] / q2[p][kind]["raw"] for p in sel) / w
            vb = sum(math.sqrt(q2[p][kind]["raw"]) * q2[p][kind][base] / q2[p][kind]["raw"] for p in sel) / w
            row.append(f"{v:.3f} ({100*(v/vb-1):+.1f}%)")
        rows.append(row)
    return md_table(["codec", "pairs", "total bytes", "vs " + base, "all", "upgrade", "patch", "other"], rows, "lrrrrrrr")


if q2:
    for key, kind, base in (("T2x", "extra", "bzip2"), ("T2d", "diff", "bzip2"), ("T2c", "ctrl", "bzip2"), ("T2c6", "ctrl6", "zlib"), ("T2nz", "nz2", "zlib")):
        sect(key)
        emit(codec_summary(kind, base))
    sect("T2xp")
    keys = ["raw", "bzip2", "zlib", "lzma", "brotli", "zstd", "lzma_x86", "lzma_arm64", "zstd_pf", "brotli_dict", "lzma_dict"]
    rows = [[short(p)] + [n(q2[p]["extra"].get(k)) for k in keys] for p in PAIRS if p in q2 and "extra" in q2[p]]
    emit(md_table(["pair"] + keys, rows, "l" + "r" * len(keys)))
    sect("T2ref")
    keys = ["bzip2", "lzma", "lzma_x86", "lzma_arm64", "zstd_pf", "brotli_dict", "lzma_dict"]
    rows = [[short(p), n(b43[p])] + [n(q2[p]["new"].get(k)) for k in keys] for p in PAIRS if p in q2 and "new" in q2[p]]
    emit(md_table(["pair", "bsdiff 4.3"] + keys, rows, "l" + "r" * (len(keys) + 1)))
    sect("T2refs")
    rows = []
    for k, label in (("zstd_pf", "zstd --patch-from (level per section 2.4)"), ("brotli_dict", "brotli -q 11 --large_window=30 -D old"), ("lzma_dict", "LZMA2 -9e, preset dict = old")):
        have = [p for p in PAIRS if p in q2 and "new" in q2[p] and k in q2[p]["new"]]
        if have:
            rows.append(catrow(f"{label} ({len(have)} pairs)", {p: q2[p]["new"][k] for p in have}, b43))
            rows.append(catrow(f"  bsdiff 4.3 on the same {len(have)} pairs", {p: b43[p] for p in have}))
    emit(md_table(CATHDR, rows, "lrrrr"))


# ------------------------------------------------------------------------------------------ Q3 / Q4
def cm_load(cfg, align, small=False):
    return load("cm_%s_%s%s.json" % (cfg, align, "_small" if small else ""))


def thesis_diff(x):
    m = x["modes"][x["best_mode"]]
    return m["map"] + min(m["nz_z"], m["nz_bz"], m["K"])


def diff_tables(align, key):
    th = load("thesis_%s.json" % align)
    if not th:
        return
    cd, ci, cv, cf = cm_load("diff", align), cm_load("diff_id", align), cm_load("diff_va", align), cm_load("full_va", align)
    if align == "greedy":
        cd, cv = cm_load("diff", align, True), cm_load("diff_va", align, True)
    g = lambda d, p: d[p]["flag_bytes"] + d[p]["val_bytes"] if okcm(d, p) else None
    cols = {k: {} for k in ("b43", "th", "cm", "id", "va")}
    rows = []
    for p in PAIRS:
        if p not in th:
            continue
        x = th[p]
        a, b = g(cd, p), g(ci, p)
        c = g(cv, p) if g(cv, p) is not None else g(cf, p)
        if a is None and b is None and c is None:
            continue
        cols["th"][p] = thesis_diff(x)
        if align == "b43":
            cols["b43"][p] = q1[p]["diff_c"]
        for k, v in (("cm", a), ("id", b), ("va", c)):
            if v is not None:
                cols[k][p] = v
        best = c if c is not None else (b if b is not None else a)
        rows.append([short(p), n(x["D"]), n(x["modes"][0]["K"]), n(cols["b43"].get(p)), n(cols["th"][p]), n(a), n(b), n(c), pct(best, cols["th"][p]), pct(c, a) if a and c else "-"])
    if not rows:
        return
    sect(key)
    emit(md_table(["pair", "matched bytes", "of which differ", "bsdiff 4.3 (bzip2 diff)", "thesis (map + non-zero)", "CM", "CM + addr, identity map", "CM + addr, container map",
                   "best CM vs thesis", "addr vs no addr"], rows, "lrrrrrrrrr"))
    sect(key + "s")
    rows = []
    for k, label in (("b43", "bsdiff 4.3 diff section"), ("th", "thesis: BWT map + non-zero string"), ("cm", "CM, no address hypotheses"),
                     ("id", "CM + address hypotheses, identity map (format-agnostic)"), ("va", "CM + address hypotheses, container VA map")):
        if cols[k]:
            common = [p for p in cols[k] if p in cols["th"]]
            rows.append(catrow(f"{label} ({len(common)} pairs)", {p: cols[k][p] for p in common}, {p: cols["th"][p] for p in common}))
    emit(md_table(CATHDR, rows, "lrrrr"))
    return cols


diff_tables("b43", "T3")
diff_tables("dp", "T3dp")

orc = load("q4_oracle_b43.json")
if orc:
    sect("T4o")
    rows = []
    for p in PAIRS:
        if p not in orc:
            continue
        r = orc[p]
        a, b = r["identity"], r["vamap"]
        top = sorted(((v, k) for k, v in b["first"].items() if v), reverse=True)[:4]
        lt = sum(b.get("learned_bytes", {}).values())
        rows.append([short(p), r["fmt"], r["arch"], n(b["diff"]), f"{100*a['explained']/max(1,a['diff']):.1f}%", f"{100*b['explained']/max(1,b['diff']):.1f}%",
                     f"+{100*lt/max(1,b['diff']):.1f}%", ", ".join(f"{k} {100*v/max(1,b['diff']):.0f}%" for v, k in top)])
    emit(md_table(["pair", "format", "arch", "differing matched bytes", "explained, identity map", "explained, container map", "added by learned targets", "main classes (container map)"], rows, "lllrrrrl"))
    sect("T4t")
    agg = {}
    for p in orc:
        for k, v in orc[p]["vamap"]["trig"].items():
            a = agg.setdefault(k, [0] * 5)
            for i in range(5):
                a[i] += v[i]
    rows = [[k, n(v[0]), n(v[1]), f"{100*v[2]/max(1,v[1]):.1f}%", n(v[3]), f"{100*v[4]/max(1,v[3]):.1f}%"] for k, v in agg.items() if v[0]]
    emit(md_table(["trigger (visible in the old file)", "fields", "predicts a change", "exactly right", "predicts no change", "right"], rows, "lrrrrr"))

# per-section table for the Go pair, if present
secs = load("q4_sections_b43.json")
if secs:
    for p, key in (("upgrade-lazygit-0.43.1-0.44.0-linux-amd64", "T4go"), ("upgrade-lazygit-0.40.2-0.44.0-macos-arm64", "T4gomac"),
                   ("upgrade-ripgrep-14.0.3-14.1.0-linux-amd64", "T4rg"), ("upgrade-bat-0.25.0-0.26.0-macos-arm64", "T4bat"),
                   ("upgrade-ripgrep-13.0.0-14.1.0-windows-amd64", "T4rgw")):
        if p not in secs:
            continue
        sect(key)
        rows = []
        for r in secs[p]:
            if r["size"] < 2048 and r["extra"] + r["differ"] < 256:
                continue
            mt = r["size"] - r["extra"]
            rows.append([r["name"], n(r["size"]), n(r["extra"]), f"{100*r['extra']/r['size']:.1f}%", n(r["differ"]), f"{100*r['differ']/max(1,mt):.1f}%",
                         f"{100*r['explained']/max(1,r['differ']):.0f}%", n(r["cm_matched"]), n(r["cm_extra"])])
        emit(md_table(["section (new file)", "size", "unmatched (extra)", "extra %", "matched but differing", "% of matched", "oracle-explained", "CM bytes, matched", "CM bytes, extra"], rows, "lrrrrrrrr"))

gr = load("go_rbase_sections.json")
if gr:
    sect("Tgo")
    rows = [[r[0], n(r[1]), n(r[2]), n(r[3]), pct(r[3], r[2]) if r[2] else "-", n(r[4]), pct(r[4], r[2]) if r[2] else "-"] for r in gr]
    rows.append(["**total**", ""] + [f"**{n(sum(r[k] for r in gr))}**" if k in (2, 3, 4) else pct(sum(r[k - 1] for r in gr), sum(r[2] for r in gr)) for k in (2, 3, 4, 4, 5)][:0] +
                [f"**{n(sum(r[2] for r in gr))}**", f"**{n(sum(r[3] for r in gr))}**", pct(sum(r[3] for r in gr), sum(r[2] for r in gr)),
                 f"**{n(sum(r[4] for r in gr))}**", pct(sum(r[4] for r in gr), sum(r[2] for r in gr))])
    emit(md_table(["section", "size", "P1+P2", "+ bases = section starts", "", "+ Go-aware bases", ""], rows, "lrrrrrr"))

# ------------------------------------------------------------------------------------------ Q5
thg = load("thesis_greedy.json")
cg = cm_load("diff", "greedy", True)
if thg and cg:
    cfgs = [("diff", "CM flags"), ("abl_nolook", "CM flags, no old look-ahead"), ("abl_noind", "CM flags, no old-window memory"), ("diff_va", "CM flags + addr")]
    data = {c: cm_load(c, "greedy", True) for c, _ in cfgs}
    use = [(c, l) for c, l in cfgs if data[c] and sum(1 for p in cg if okcm(data[c], p)) == sum(1 for p in cg if okcm(cg, p))]
    hdr = ["pair", "D", "K", "BWT + midpoint", "non-zero string (zlib/bzip2)"] + [l for c, l in use] + ["CM values", "CM + addr values"]
    rows, tot = [], None
    for p in PAIRS:
        if not okcm(cg, p):
            continue
        x = thg[p]
        m = x["modes"][2]
        nz = min(m["nz_z"], m["nz_bz"], m["K"])
        vals = [m["map"], nz] + [data[c][p]["flag_bytes"] for c, _ in use] + [cg[p]["val_bytes"]]
        vals.append(data["diff_va"][p]["val_bytes"] if okcm(data["diff_va"], p) else None)
        rows.append([short(p), n(x["D"]), n(m["K"])] + [n(v) for v in vals])
        tot = [a + (b or 0) for a, b in zip(tot, vals)] if tot else [v or 0 for v in vals]
    rows.append(["**total**", "", ""] + [f"**{n(v)}**" for v in tot])
    sect("T5")
    emit(md_table(hdr, rows, "l" + "r" * (len(hdr) - 1)))

# ------------------------------------------------------------------------------------------ totals
HDR = 71


def vm_bytes(p):
    f = os.path.join(WORK, p, "old.vamap")
    k = sum(1 for l in open(f) if l[0].isdigit()) if os.path.exists(f) else 1
    return 0 if k <= 1 else 2 * (2 + 12 * k)


def totals(align, key):
    th = load("thesis_%s.json" % align)
    cf = cm_load("full_va", align)
    ctl = (load("ctrl.json") or {}).get(align, {})
    if not th:
        return
    have = [p for p in PAIRS if p in th]
    cols = {"bsdiff 4.3": {p: b43[p] for p in have}}
    cols["thesis format (DESIGN 7)"] = {p: th[p]["total"] for p in have}
    if q2 and align == "b43":
        def tot(p, keys):
            x = th[p]
            m = x["modes"][2]
            def f(kind, raw):
                if not raw:
                    return 0
                c = [q2[p][kind][k] for k in keys if k in q2[p][kind]]
                return min(min(c), raw) if c else None
            parts = [f("ctrl6", x["ctrl_raw"]), f("nz2", m["K"]), f("extra", x["E"])]
            return None if any(v is None for v in parts) else HDR + m["map"] + sum(parts)
        for label, keys in (("thesis format, LZMA2 back end", ("lzma", "lzma_pb0")), ("thesis format, BCL Brotli back end", ("brotli",)), ("thesis format, zstd -22 back end", ("zstd",))):
            ok = [p for p in have if p in q2 and all(k in q2[p] for k in ("extra", "ctrl6", "nz2"))]
            d = {p: tot(p, keys) for p in ok}
            cols[label] = {p: v for p, v in d.items() if v is not None}
    if cf:
        ok = [p for p in have if okcm(cf, p)]
        ctrl = lambda p: min(th[p]["ctrl_z"], th[p]["ctrl_bz"], th[p]["ctrl_raw"])
        cols["CM stream + zlib/bzip2 control"] = {p: 60 + ctrl(p) + cf[p]["bytes"] + vm_bytes(p) for p in ok}
        if ctl:
            ok2 = [p for p in ok if p in ctl]
            cols["CM stream incl. control records"] = {p: 60 + ctl[p]["ctrl_bytes"] + cf[p]["bytes"] + vm_bytes(p) for p in ok2}
    if cf and q2 and align == "b43":
        def combo(p, ekeys, ckeys):
            x = th[p]
            if p not in q2 or "extra" not in q2[p] or "ctrl6" not in q2[p]:
                return None
            e = [q2[p]["extra"][k] for k in ekeys if k in q2[p]["extra"]] if x["E"] else [0]
            c = [q2[p]["ctrl6"][k] for k in ckeys if k in q2[p]["ctrl6"]] if x["ctrl_raw"] else [0]
            if not e or not c:
                return None
            return 60 + min(min(c), x["ctrl_raw"]) + cf[p]["flag_bytes"] + cf[p]["val_bytes"] + min(min(e), x["E"]) + vm_bytes(p)
        for label, ek, ck in (("P1+P2+P3: CM for matched bytes, Brotli for extra and control", ("brotli",), ("brotli",)),
                              ("P1+P2+P3 with LZMA2", ("lzma", "lzma_pb0"), ("lzma", "lzma_pb0", "brotli")),
                              ("P1+P2+P3 with LZMA2, old file as dictionary for extra", ("lzma", "lzma_pb0", "lzma_dict"), ("lzma", "lzma_pb0", "brotli"))):
            d = {p: combo(p, ek, ck) for p in have if okcm(cf, p)}
            cols[label] = {p: v for p, v in d.items() if v is not None}
    if q2:
        for k, label in (("zstd_pf", "outside: zstd --patch-from"), ("lzma_dict", "outside: LZMA2, old as preset dictionary")):
            cols[label] = {p: q2[p]["new"][k] for p in have if p in q2 and "new" in q2[p] and k in q2[p]["new"]}
    # hybrid: per pair, the smaller of the best delta column and the whole-file LZMA2-with-dictionary stream
    lz = cols.get("outside: LZMA2, old as preset dictionary", {})
    bestkey = "CM stream incl. control records" if cols.get("CM stream incl. control records") else "CM stream + zlib/bzip2 control"
    if lz and cols.get(bestkey):
        ps = [p for p in cols[bestkey] if p in lz]
        cols["per pair: smaller of CM stream and whole-file LZMA2-with-dictionary"] = {p: min(cols[bestkey][p], lz[p] + 20) for p in ps}
    names = [k for k in cols if cols[k]]
    sect(key)
    rows = [[short(p), n(newsize(p))] + [n(cols[k].get(p)) for k in names] for p in have]
    emit(md_table(["pair", "new"] + names, rows, "l" + "r" * (len(names) + 1)))
    sect(key + "s")
    rows = []
    for k in names:
        ps = list(cols[k])
        rows.append(catrow(f"{k} ({len(ps)} pairs)", cols[k], {p: b43[p] for p in ps}))
    emit(md_table(CATHDR, rows, "lrrrr"))
    return cols


T6 = totals("b43", "T6")
T6dp = totals("dp", "T6dp")
T6dpb = totals("dpb", "T6dpb")

# ------------------------------------------------------------------------------------------ alignments compared (thesis coding)
al = {a: load("thesis_%s.json" % a) for a in ("greedy", "b43", "dp", "dpb", "dpo")}
if al["dp"] and al["dpb"]:
    common = [p for p in PAIRS if p in al["dp"] and p in al["dpb"]]
    if common:
        sect("T7b")
        emit(md_table(CATHDR, [catrow(f"project DP, local matches only ({len(common)} pairs)", {p: al["dp"][p]["total"] for p in common}),
                               catrow("project DP, local matches + FFT block alignment", {p: al["dpb"][p]["total"] for p in common}, {p: al["dp"][p]["total"] for p in common})], "lrrrr"))
if al["b43"]:
    names = [(a, l) for a, l in (("greedy", "8-byte hash, one candidate (format harness)"), ("b43", "bsdiff 4.3 (SA of old, greedy extension)"),
                                 ("dp", "project DP, local matches")) if al[a]]
    common = [p for p in PAIRS if all(p in al[a] for a, _ in names)]
    sect("T7")
    rows = [[short(p)] + [n(al[a][p]["total"]) for a, _ in names] + [n(al[a][p]["nseg"]) for a, _ in names if a in ("b43", "dp")] for p in common]
    emit(md_table(["pair"] + [l for _, l in names] + ["segments: bsdiff 4.3", "segments: DP"][:sum(1 for a, _ in names if a in ("b43", "dp"))], rows, "l" + "r" * (len(names) + 2)))
    sect("T7s")
    rows = [catrow(f"{l} ({len(common)} pairs)", {p: al[a][p]["total"] for p in common}, {p: al["b43"][p]["total"] for p in common}) for a, l in names]
    emit(md_table(CATHDR, rows, "lrrrr"))

# ------------------------------------------------------------------------------------------ control coding
ctl = load("ctrl.json")
if ctl:
    sect("T8")
    rows = []
    for align in ("b43", "dp"):
        th = load("thesis_%s.json" % align)
        if not th or align not in ctl:
            continue
        ps = [p for p in PAIRS if p in th and p in ctl[align]]
        a = sum(min(th[p]["ctrl_z"], th[p]["ctrl_bz"], th[p]["ctrl_raw"]) for p in ps)
        b = sum(ctl[align][p]["ctrl_bytes"] for p in ps)
        rows.append([align, str(len(ps)), n(sum(ctl[align][p]["segments"] for p in ps)), n(a), n(b), pct(b, a)])
    emit(md_table(["alignment", "pairs", "segments", "varints + best of zlib/bzip2", "diagonal list + adaptive gamma", "change"], rows, "lrrrrr"))

# ------------------------------------------------------------------------------------------ BCJ then bsdiff
bcj = load("q7_bcj.json")
if bcj:
    sect("T9")
    ps = [p for p in PAIRS if bcj.get(p)]
    rows = [[short(p), n(b43[p]), n(bcj[p]), pct(bcj[p], b43[p])] for p in ps]
    emit(md_table(["pair", "bsdiff 4.3", "branch-convert both files, then bsdiff 4.3", "change"], rows, "lrrr"))
    sect("T9s")
    emit(md_table(CATHDR, [catrow(f"bsdiff 4.3 ({len(ps)} pairs)", {p: b43[p] for p in ps}), catrow("branch-convert, then bsdiff 4.3", {p: bcj[p] for p in ps}, b43)], "lrrrr"))

# ------------------------------------------------------------------------------------------ extra string: LZ codecs vs the CM extra model
cfb = cm_load("full_va", "b43")
if cfb and q2:
    have = [p for p in PAIRS if okcm(cfb, p) and p in q2 and "extra" in q2[p] and all(k in q2[p]["extra"] for k in ("bzip2", "lzma", "brotli", "lzma_dict"))]
    if have:
        sect("Tx")
        cols = [("bzip2", "bzip2 -9"), ("brotli", "Brotli q11"), ("lzma", "LZMA2 -9e"), ("lzma_dict", "LZMA2, dict = old")]
        rows = [[short(p), n(q2[p]["extra"]["raw"])] + [n(q2[p]["extra"][k]) for k, _ in cols] + [n(cfb[p]["extra_bytes"])] for p in have if q2[p]["extra"]["raw"] > 50000]
        tot = [sum(q2[p]["extra"][k] for p in have) for k, _ in cols] + [sum(cfb[p]["extra_bytes"] for p in have)]
        rows.append([f"**total, {len(have)} pairs**", n(sum(q2[p]["extra"]["raw"] for p in have))] + [f"**{n(v)}**" for v in tot])
        rows.append(["vs bzip2", ""] + [pct(v, tot[0]) for v in tot])
        emit(md_table(["pair (extra > 50 kB shown)", "raw"] + [l for _, l in cols] + ["CM extra model"], rows, "l" + "r" * 6))

# ------------------------------------------------------------------------------------------ ablations, reduced models, base-relative offsets
sect("Tabl")
lines = []
g0 = cm_load("diff", "greedy", True)
rows = []
for cfg, label in (("diff", "all flag contexts"), ("abl_nolook", "without the old bytes after the position (look-ahead)"), ("abl_noind", "without the old-window -> digit memories")):
    d = cm_load(cfg, "greedy", True)
    if d and g0:
        ps = [p for p in g0 if okcm(d, p) and okcm(g0, p)]
        if len(ps) == len([p for p in g0 if okcm(g0, p)]):
            rows.append([label, str(len(ps)), n(sum(d[p]["flag_bytes"] for p in ps)), n(sum(d[p]["val_bytes"] for p in ps)), pct(sum(d[p]["flag_bytes"] + d[p]["val_bytes"] for p in ps), sum(g0[p]["flag_bytes"] + g0[p]["val_bytes"] for p in ps))])
if len(rows) > 1:
    lines.append("Context ablations (greedy alignment, 14 pairs, no address hypotheses; verified streams):\n")
    lines.append(md_table(["flag/value model", "pairs", "flag bytes", "value bytes", "matched bytes vs full model"], rows, "lrrrr"))
fv = cm_load("full_va", "b43")
rows = []
for cfg, label in (("t18_va", "2^18-entry tables (1 MB + 1 MB + 4 x 0.5 MB) instead of 2^22"), ("lite_va", "2^18-entry tables and 8 flag + 7 value contexts instead of 17 + 17")):
    d = cm_load(cfg, "b43", True)
    if d and fv:
        ps = [p for p in d if okcm(d, p) and okcm(fv, p)]
        if ps:
            a = sum(d[p]["flag_bytes"] + d[p]["val_bytes"] for p in ps); b = sum(fv[p]["flag_bytes"] + fv[p]["val_bytes"] for p in ps)
            rows.append([label, str(len(ps)), n(b), n(a), pct(a, b)])
if rows:
    lines.append("\nReduced models (bsdiff 4.3 alignment, the 14 pairs up to 3.8 MB, address hypotheses with container map, bytes for matched data; verified streams; times were not comparable on the shared machine and are not shown):\n")
    lines.append(md_table(["model", "pairs", "full model bytes", "reduced model bytes", "change"], rows, "lrrrr"))
rows = []
pgo = "upgrade-lazygit-0.43.1-0.44.0-linux-amd64"
if fv and okcm(fv, pgo):
    base = fv[pgo]["flag_bytes"] + fv[pgo]["val_bytes"]
    rows.append(["address hypotheses, container map (section 4.3)", n(base), ""])
    for mode, label in (("sec", "+ base-relative offset32, bases = section starts (container headers only)"), ("go", "+ base-relative offset32, bases = .text, rodata and the pclntab sub-tables (Go-aware)")):
        f = os.path.join(WORK, pgo, "cm_rb_%s.json" % mode)
        try:
            r = json.load(open(f))
            v = r["flag_bytes"] + r["val_bytes"]
            rows.append([label + ("" if r.get("verified", True) else " (not decoded back)"), n(v), pct(v, base)])
        except (OSError, ValueError, KeyError):
            pass
if len(rows) > 1:
    lines.append("\nBase-relative offsets on the Go pair lazygit 0.43.1 -> 0.44.0 (linux x64), bytes for all matched data:\n")
    lines.append(md_table(["configuration", "bytes", "change"], rows, "lrr"))
if lines:
    for l in lines:
        emit(l)
else:
    del SECT["Tabl"]

# ------------------------------------------------------------------------------------------ speed
cf = cm_load("full_va", "b43")
if cf:
    sect("Tspeed")
    rows = []
    for p in PAIRS:
        if okcm(cf, p) and cf[p].get("verified", True):
            mb = newsize(p) / 1e6
            rows.append([short(p), f"{mb:.2f}", f"{cf[p]['enc_s']:.1f}", f"{cf[p]['dec_s']:.1f}", f"{mb/max(0.01,cf[p]['dec_s']):.2f}"])
    emit(md_table(["pair", "new MB", "encode CPU s", "decode CPU s", "decode MB/s"], rows, "lrrrr"))

json.dump({k: "\n".join(v) for k, v in SECT.items()}, open(os.path.join(RESULTS, "tables.json"), "w"), indent=1)
with open(os.path.join(RESULTS, "tables.md"), "w") as f:
    for k, v in SECT.items():
        f.write("## %s\n\n%s\n\n" % (k, "\n".join(v)))
print("tables:", " ".join(SECT))
