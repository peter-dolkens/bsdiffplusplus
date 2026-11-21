#!/bin/bash
# Reproduces bench/corpus from scratch. Idempotent: skips pairs that already exist.
# Safety: downloads are untrusted data. Nothing downloaded is ever executed; downloaded C
# sources are only compiled directly with `cc` (no configure/make). Downloads live in bench/downloads.
set -u
BENCH="$(cd "$(dirname "$0")" && pwd)"
DL="$BENCH/downloads"
CORPUS="$BENCH/corpus"
WORK="$DL/work"
mkdir -p "$DL" "$CORPUS" "$WORK"
export ZERO_AR_DATE=1
FAILED=()
GH=https://github.com

# fetch BASEURL ASSET -> sets FETCHED to the local path (cached per base url)
fetch() {
  local d="$DL/$(echo "$1" | sed 's|https://||; s|[^A-Za-z0-9._-]|_|g')"; mkdir -p "$d"
  FETCHED="$d/$2"; [ -s "$FETCHED" ] && return 0
  echo "  download $1/$2" >&2
  curl -sSL --fail --retry 3 -o "$FETCHED.part" "$1/$2" && mv "$FETCHED.part" "$FETCHED" || { rm -f "$FETCHED.part"; return 1; }
}
unpack() { # archive destdir (fresh, empty)
  mkdir -p "$2"
  case "$1" in
    *.zip) unzip -q -o "$1" -d "$2" ;;
    *.tar.gz|*.tgz) tar -xzf "$1" -C "$2" ;;
    *) return 1 ;;
  esac
}
extract_bin() { # archive member-name outfile
  local d; d="$(mktemp -d "$WORK/x.XXXXXX")"
  unpack "$1" "$d" || { rm -rf "${d:?}"; return 1; }
  local m; m="$(find "$d" -type f -name "$2" | head -1)"
  if [ -n "$m" ]; then cp "$m" "$3"; rm -rf "${d:?}"; return 0; fi
  rm -rf "${d:?}"; return 1
}
done_pair() { [ -s "$CORPUS/$1/old" ] && [ -s "$CORPUS/$1/new" ]; }
fail() { echo "FAILED: $1" >&2; FAILED+=("$1"); rm -rf "${CORPUS:?}/${1:?}"; }

# upgrade_pair NAME OLD_BASEURL OLD_ASSET OLD_MEMBER NEW_BASEURL NEW_ASSET NEW_MEMBER
# MEMBER "-" means the asset itself is the raw executable.
upgrade_pair() {
  local n="$1"; done_pair "$n" && { echo "skip $n"; return; }
  echo "pair $n"; mkdir -p "$CORPUS/$n"
  local side base asset member
  for side in old new; do
    if [ $side = old ]; then base="$2"; asset="$3"; member="$4"; else base="$5"; asset="$6"; member="$7"; fi
    fetch "$base" "$asset" || { fail "$n"; return; }
    if [ "$member" = "-" ]; then cp "$FETCHED" "$CORPUS/$n/$side"
    else extract_bin "$FETCHED" "$member" "$CORPUS/$n/$side" || { fail "$n"; return; }; fi
  done
}
rel() { echo "$GH/$1/releases/download/$2"; }

# ---------------- upgrade ----------------
upgrade_pair upgrade-jq-1.7-1.7.1-linux-arm64 \
  "$(rel jqlang/jq jq-1.7)" jq-linux-arm64 - "$(rel jqlang/jq jq-1.7.1)" jq-linux-arm64 -
upgrade_pair upgrade-jq-1.7-1.7.1-windows-amd64 \
  "$(rel jqlang/jq jq-1.7)" jq-windows-amd64.exe - "$(rel jqlang/jq jq-1.7.1)" jq-windows-amd64.exe -
upgrade_pair upgrade-jq-1.7.1-1.8.0-macos-arm64 \
  "$(rel jqlang/jq jq-1.7.1)" jq-macos-arm64 - "$(rel jqlang/jq jq-1.8.0)" jq-macos-arm64 -
upgrade_pair upgrade-ripgrep-14.0.3-14.1.0-linux-amd64 \
  "$(rel BurntSushi/ripgrep 14.0.3)" ripgrep-14.0.3-x86_64-unknown-linux-musl.tar.gz rg \
  "$(rel BurntSushi/ripgrep 14.1.0)" ripgrep-14.1.0-x86_64-unknown-linux-musl.tar.gz rg
upgrade_pair upgrade-ripgrep-13.0.0-14.1.0-windows-amd64 \
  "$(rel BurntSushi/ripgrep 13.0.0)" ripgrep-13.0.0-x86_64-pc-windows-msvc.zip rg.exe \
  "$(rel BurntSushi/ripgrep 14.1.0)" ripgrep-14.1.0-x86_64-pc-windows-msvc.zip rg.exe
upgrade_pair upgrade-fd-10.2.0-10.3.0-linux-arm64 \
  "$(rel sharkdp/fd v10.2.0)" fd-v10.2.0-aarch64-unknown-linux-gnu.tar.gz fd \
  "$(rel sharkdp/fd v10.3.0)" fd-v10.3.0-aarch64-unknown-linux-gnu.tar.gz fd
upgrade_pair upgrade-bat-0.25.0-0.26.0-macos-arm64 \
  "$(rel sharkdp/bat v0.25.0)" bat-v0.25.0-aarch64-apple-darwin.tar.gz bat \
  "$(rel sharkdp/bat v0.26.0)" bat-v0.26.0-aarch64-apple-darwin.tar.gz bat
upgrade_pair upgrade-lazygit-0.43.1-0.44.0-linux-amd64 \
  "$(rel jesseduffield/lazygit v0.43.1)" lazygit_0.43.1_Linux_x86_64.tar.gz lazygit \
  "$(rel jesseduffield/lazygit v0.44.0)" lazygit_0.44.0_Linux_x86_64.tar.gz lazygit
upgrade_pair upgrade-lazygit-0.40.2-0.44.0-macos-arm64 \
  "$(rel jesseduffield/lazygit v0.40.2)" lazygit_0.40.2_Darwin_arm64.tar.gz lazygit \
  "$(rel jesseduffield/lazygit v0.44.0)" lazygit_0.44.0_Darwin_arm64.tar.gz lazygit
upgrade_pair upgrade-fzf-0.56.0-0.56.1-linux-arm64 \
  "$(rel junegunn/fzf v0.56.0)" fzf-0.56.0-linux_arm64.tar.gz fzf \
  "$(rel junegunn/fzf v0.56.1)" fzf-0.56.1-linux_arm64.tar.gz fzf

# ---------------- patch: compile small C projects ----------------
# srcdir NAME URL-BASE ARCHIVE -> extracts into $WORK/src-NAME (flattened, cached)
srcdir() {
  local name="$1" s="$WORK/src-$1"
  [ -e "$s/.done" ] && return 0
  fetch "$2" "$3" || return 1
  rm -rf "${s:?}"; unpack "$FETCHED" "$s" || return 1
  # archives extract into one top-level dir (zips: flat); flatten it
  local top; top="$(find "$s" -mindepth 1 -maxdepth 1 -type d | head -1)"
  if [ -n "$top" ] && [ "$(find "$s" -mindepth 1 -maxdepth 1 | wc -l)" -eq 1 ]; then
    mv "$top"/* "$s"/ && rmdir "$top"
  fi
  touch "$s/.done"
}
# cc_build OUT ARCH SRCROOT FLAGS -- files...   (paths normalised so old/new builds differ only by source)
cc_build() {
  local out="$1" arch="$2" root="$3" flags="$4"; shift 5
  local f files=(); for f in "$@"; do files+=("$root/$f"); done
  cc -O2 -arch "$arch" -w -ffile-prefix-map="$root"=. -I"$root" $flags -o "$out" "${files[@]}"
}
ar_build() {
  local out="$1" arch="$2" root="$3" flags="$4"; shift 5
  local od; od="$(mktemp -d "$WORK/o.XXXXXX")"; local f objs=()
  for f in "$@"; do
    local o="$od/$(basename "${f%.c}").o"
    cc -O2 -arch "$arch" -w -ffile-prefix-map="$root"=. -I"$root" $flags -c "$root/$f" -o "$o" || { rm -rf "${od:?}"; return 1; }
    objs+=("$o")
  done
  rm -f "$out"; ar -crs "$out" "${objs[@]}" 2>/dev/null; local rc=$?; rm -rf "${od:?}"; return $rc
}

ZLIB_LIB="adler32.c compress.c crc32.c deflate.c gzclose.c gzlib.c gzread.c gzwrite.c infback.c inffast.c inflate.c inftrees.c trees.c uncompr.c zutil.c"
BZ_LIB="blocksort.c bzlib.c compress.c crctable.c decompress.c huffman.c randtable.c"
SQL_FLAGS="-DSQLITE_THREADSAFE=0 -DSQLITE_OMIT_LOAD_EXTENSION"
LUA_FLAGS="-DLUA_USE_POSIX -DLUA_USE_DLOPEN"
lua_lib() { (cd "$1/src" && ls *.c | grep -v -e '^lua\.c$' -e '^luac\.c$' | sed 's|^|src/|'); }

# patch_pair NAME ARCH OLD_SRCNAME NEW_SRCNAME BUILDFUNC  (BUILDFUNC out arch srcroot)
patch_pair() {
  local n="$1" arch="$2" oldsrc="$3" newsrc="$4" fn="$5"; done_pair "$n" && { echo "skip $n"; return; }
  echo "pair $n"; mkdir -p "$CORPUS/$n"
  "$fn" "$CORPUS/$n/old" "$arch" "$WORK/src-$oldsrc" && "$fn" "$CORPUS/$n/new" "$arch" "$WORK/src-$newsrc" || fail "$n"
}
b_minigzip() { cc_build "$1" "$2" "$3" "-DHAVE_UNISTD_H -DHAVE_STDARG_H -Dfdopen=fdopen -D_LARGEFILE64_SOURCE=1" -- $ZLIB_LIB test/minigzip.c; }
b_libz()     { ar_build "$1" "$2" "$3" "-DHAVE_UNISTD_H -DHAVE_STDARG_H -Dfdopen=fdopen" -- $ZLIB_LIB; }
b_lua()      { cc_build "$1" "$2" "$3" "$LUA_FLAGS" -- $(lua_lib "$3") src/lua.c; }
b_liblua()   { ar_build "$1" "$2" "$3" "$LUA_FLAGS" -- $(lua_lib "$3"); }
b_sqlite()   { cc_build "$1" "$2" "$3" "$SQL_FLAGS" -- sqlite3.c shell.c; }
b_bzip2()    { cc_build "$1" "$2" "$3" "-D_FILE_OFFSET_BITS=64" -- $BZ_LIB bzip2.c; }

Z=https://zlib.net/fossils; L=https://www.lua.org/ftp; Q=https://www.sqlite.org/2024; B=https://sourceware.org/pub/bzip2
srcdir zlib-1.3   $Z zlib-1.3.tar.gz   || echo "zlib 1.3 download failed" >&2
srcdir zlib-1.3.1 $Z zlib-1.3.1.tar.gz || echo "zlib 1.3.1 download failed" >&2
srcdir lua-5.4.6  $L lua-5.4.6.tar.gz  || echo "lua 5.4.6 download failed" >&2
srcdir lua-5.4.7  $L lua-5.4.7.tar.gz  || echo "lua 5.4.7 download failed" >&2
srcdir sqlite-3.45.1 $Q sqlite-amalgamation-3450100.zip || echo "sqlite 3.45.1 download failed" >&2
srcdir sqlite-3.45.2 $Q sqlite-amalgamation-3450200.zip || echo "sqlite 3.45.2 download failed" >&2
srcdir bzip2-1.0.6 $B bzip2-1.0.6.tar.gz || echo "bzip2 1.0.6 download failed" >&2
srcdir bzip2-1.0.8 $B bzip2-1.0.8.tar.gz || echo "bzip2 1.0.8 download failed" >&2

patch_pair patch-zlib-minigzip-1.3-1.3.1-macos-arm64    arm64  zlib-1.3 zlib-1.3.1 b_minigzip
patch_pair patch-zlib-minigzip-1.3-1.3.1-macos-x86_64   x86_64 zlib-1.3 zlib-1.3.1 b_minigzip
patch_pair patch-lua-5.4.6-5.4.7-macos-arm64            arm64  lua-5.4.6 lua-5.4.7 b_lua
patch_pair patch-lua-5.4.6-5.4.7-macos-x86_64           x86_64 lua-5.4.6 lua-5.4.7 b_lua
patch_pair patch-sqlite-shell-3.45.1-3.45.2-macos-arm64 arm64  sqlite-3.45.1 sqlite-3.45.2 b_sqlite
patch_pair patch-bzip2-1.0.6-1.0.8-macos-x86_64         x86_64 bzip2-1.0.6 bzip2-1.0.8 b_bzip2
patch_pair patch-libz.a-1.3-1.3.1-macos-x86_64          x86_64 zlib-1.3 zlib-1.3.1 b_libz
patch_pair patch-liblua.a-5.4.6-5.4.7-macos-arm64       arm64  lua-5.4.6 lua-5.4.7 b_liblua

# ---------------- other: non-executable data ----------------
n=other-lua-src-tar-5.4.6-5.4.7
if ! done_pair $n; then
  echo "pair $n"; mkdir -p "$CORPUS/$n"
  for v in 5.4.6 5.4.7; do
    s=old; [ $v = 5.4.7 ] && s=new
    # uncompressed tar of the source tree (sorted names, no dotfiles)
    mkdir -p "$WORK/tarroot-$v" && rm -rf "${WORK:?}/tarroot-$v/lua-$v" && cp -R "$WORK/src-lua-$v" "$WORK/tarroot-$v/lua-$v" && rm -f "$WORK/tarroot-$v/lua-$v/.done"
    (cd "$WORK/tarroot-$v" && find "lua-$v" -type f | LC_ALL=C sort | tar -cf "$CORPUS/$n/$s" -T -) || { fail $n; break; }
  done
fi
n=other-sqlite3.c-3.45.1-3.45.2
if ! done_pair $n; then
  echo "pair $n"; mkdir -p "$CORPUS/$n"
  cp "$WORK/src-sqlite-3.45.1/sqlite3.c" "$CORPUS/$n/old" && cp "$WORK/src-sqlite-3.45.2/sqlite3.c" "$CORPUS/$n/new" || fail $n
fi

# ---------------- verification ----------------
echo; echo "== verification =="
for d in "$CORPUS"/*/; do
  n="$(basename "$d")"
  ok=1
  { [ -s "$d/old" ] && [ -s "$d/new" ]; } || ok=0
  [ $ok = 1 ] && cmp -s "$d/old" "$d/new" && ok=0
  printf '%-56s %s  %s\n' "$n" "$([ $ok = 1 ] && echo OK || echo BAD)" "$(file -b "$d/new" 2>/dev/null | cut -c1-70)"
  [ $ok = 1 ] || FAILED+=("verify:$n")
done
[ ${#FAILED[@]} -eq 0 ] || { echo "PROBLEMS: ${FAILED[*]}" >&2; exit 1; }
