#!/usr/bin/env python3
"""Build REPORT.md from report_src/main.tmpl by substituting {{KEY}} with the tables in
results/tables.json (run tools/tables.py first) and {{include:file}} with report_src/<file>."""
import json, os, re, sys
R = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
t = json.load(open(os.path.join(R, "results", "tables.json")))
src = open(os.path.join(R, "report_src", "main.tmpl")).read()
src = re.sub(r"\{\{include:([^}]+)\}\}", lambda m: open(os.path.join(R, "report_src", m.group(1))).read(), src)
missing = []
def sub(m):
    k = m.group(1)
    if k in t:
        return t[k]
    missing.append(k)
    return "*(not measured: the run for this table did not complete)*"
out = re.sub(r"\{\{([A-Za-z0-9_]+)\}\}", sub, src)
open(os.path.join(R, "REPORT.md"), "w").write(out)
print("REPORT.md written, %d bytes; missing tables: %s" % (len(out), missing or "none"))
