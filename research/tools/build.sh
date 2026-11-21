#!/bin/sh
# Builds the research C tools into research/bin.
set -e
R="$(cd "$(dirname "$0")/.." && pwd)"
mkdir -p "$R/bin"
W="-O2 -Wall -Wno-unused-function"
clang $W -o "$R/bin/thesis" "$R/tools/thesis.c" -lz -lbz2 -lm
clang $W -o "$R/bin/cm" "$R/tools/cm.c" -lm
clang $W -o "$R/bin/q4" "$R/tools/q4.c" -lm
clang $W -o "$R/bin/bcj" "$R/tools/bcj.c" -lm
clang $W -I/opt/homebrew/include -L/opt/homebrew/lib -o "$R/bin/lzdict" "$R/tools/lzdict.c" -llzma
