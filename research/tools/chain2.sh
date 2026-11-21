#!/bin/sh
# Second batch.  One driver at a time; every driver is capped at 3 concurrent jobs with hard
# per-job time budgets.  Starts only when the primary chain is no longer running.
cd "$(dirname "$0")/.."
until ! pgrep -f "tools/chain_main.sh" > /dev/null; do sleep 5; done
python3 tools/q2_codecs.py -j 3 --fast > work/q2_part3.log 2>&1
python3 tools/run_csalign.py --missing -j 2 local > work/csalign2.log 2>&1
python3 tools/run_thesis.py dp -j 3 > work/thesis_dp.log 2>&1
python3 tools/run_ctrl.py > work/ctrl.log 2>&1
python3 tools/run_cm.py full_va --align dp -j 3 --timeout 900 > work/cm_full_va_dp.log 2>&1
python3 tools/run_cm.py abl_nolook --align greedy --small -j 3 --timeout 300 > work/cm_abl_nolook.log 2>&1
python3 tools/run_cm.py abl_noind --align greedy --small -j 3 --timeout 300 > work/cm_abl_noind.log 2>&1
python3 tools/run_cm.py t18_va --small -j 3 --timeout 300 > work/cm_t18_va.log 2>&1
python3 tools/run_cm.py lite_va --small -j 3 --timeout 300 > work/cm_lite_va.log 2>&1
C=../bench/corpus; p=upgrade-lazygit-0.43.1-0.44.0-linux-amd64
python3 tools/gobases.py $p go > /dev/null; python3 tools/gobases.py $p sec > /dev/null
for mode in go sec; do
  bin/cm $C/$p/old $C/$p/new work/$p/segs.bin -x 0 -a 383 -v work/$p/old.$mode.vamap work/$p/new.$mode.vamap -c work/$p/cost_rb_$mode.f32 > work/$p/cm_rb_$mode.json 2> work/$p/cm_rb_$mode.err &
done
wait
python3 tools/q4_sections.py > work/q4_sections.log 2>&1
python3 tools/run_thesis.py dpb -j 3 > work/thesis_dpb.log 2>&1
echo chain2 done
