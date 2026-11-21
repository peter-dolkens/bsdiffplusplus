"""Shared helpers for the research scripts (stdlib only)."""
import math, os

R = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CORPUS = os.path.normpath(os.path.join(R, "..", "bench", "corpus"))
WORK = os.path.join(R, "work")
RESULTS = os.path.join(R, "results")


def pairs():
    return sorted(x for x in os.listdir(CORPUS) if os.path.isdir(os.path.join(CORPUS, x)))


def cat(pair):
    return pair.split("-")[0]


def paths(pair):
    return os.path.join(CORPUS, pair, "old"), os.path.join(CORPUS, pair, "new")


def newsize(pair):
    return os.path.getsize(paths(pair)[1])


def wavg(items):
    """items: [(size, newsize)] -> sqrt(new)-weighted mean of size/new (same as bench/bench.py)."""
    items = [(s, n) for s, n in items if s is not None]
    if not items:
        return None
    w = sum(math.sqrt(n) for _, n in items)
    return sum(math.sqrt(n) * (s / n) for s, n in items) / w


def summary(per_pair, cats=("all", "upgrade", "patch", "other")):
    """per_pair: {pair: size} -> {cat: weighted avg of size/new}."""
    out = {}
    for c in cats:
        out[c] = wavg([(s, newsize(p)) for p, s in per_pair.items() if c == "all" or cat(p) == c])
    return out


def short(pair):
    """Short display name."""
    s = pair
    for a, b in (("upgrade-", "u:"), ("patch-", "p:"), ("other-", "o:"), ("-macos-", "-mac-"),
                 ("-windows-", "-win-"), ("-linux-", "-lin-"), ("x86_64", "x64"), ("amd64", "x64")):
        s = s.replace(a, b)
    return s


def md_table(header, rows, align=None):
    out = ["| " + " | ".join(header) + " |"]
    out.append("|" + "|".join((" ---: " if (align and align[i] == "r") else " --- ") for i in range(len(header))) + "|")
    for r in rows:
        out.append("| " + " | ".join(str(x) for x in r) + " |")
    return "\n".join(out)
