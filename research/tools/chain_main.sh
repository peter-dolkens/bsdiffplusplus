#!/bin/sh
# Primary runs, strictly one driver at a time, each driver capped at 3 concurrent jobs and a hard
# per-pair time budget.  Priority order: conditional coder with address hypotheses (whole stream),
# then the two ablations that decompose its gain.
cd "$(dirname "$0")/.."
python3 tools/run_cm.py full_va -j 3 --timeout 900 > work/cm_full_va.log 2>&1
python3 tools/run_cm.py diff    -j 3 --timeout 600 > work/cm_diff.log 2>&1
python3 tools/run_cm.py diff_id -j 3 --timeout 600 > work/cm_diff_id.log 2>&1
echo chain_main done
