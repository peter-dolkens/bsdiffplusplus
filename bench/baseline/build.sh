#!/bin/sh
# Builds the baseline bsdiff 4.3 / bspatch 4.3 (patched copies in this dir).
set -e
D="$(cd "$(dirname "$0")" && pwd)"
cc -O2 -o "$D/bsdiff43" "$D/bsdiff.c" -lbz2
cc -O2 -o "$D/bspatch43" "$D/bspatch.c" -lbz2
