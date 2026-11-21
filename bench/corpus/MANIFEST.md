# Corpus manifest

Reproduced by `bench/fetch_corpus.sh` (downloads cached in `bench/downloads/`, never executed).
Each pair is `<pair>/old` and `<pair>/new`. Sizes in bytes (old -> new).

## upgrade: real release executables (extracted from official GitHub release assets)

GitHub release download URL pattern: `https://github.com/<repo>/releases/download/<tag>/<asset>`.

| pair | what | format / arch | lang | source (old -> new) | size old -> new |
|---|---|---|---|---|---|
| upgrade-jq-1.7-1.7.1-linux-arm64 | jq, patch release | ELF aarch64, static | C | jqlang/jq `jq-1.7` -> `jq-1.7.1`, asset `jq-linux-arm64` (raw) | 1,721,904 -> 1,709,616 |
| upgrade-jq-1.7-1.7.1-windows-amd64 | jq, patch release | PE32+ x64 (.exe) | C | jqlang/jq `jq-1.7` -> `jq-1.7.1`, asset `jq-windows-amd64.exe` (raw) | 999,936 -> 985,088 |
| upgrade-jq-1.7.1-1.8.0-macos-arm64 | jq, minor bump | Mach-O arm64 | C | jqlang/jq `jq-1.7.1` -> `jq-1.8.0`, asset `jq-macos-arm64` (raw) | 807,984 -> 841,376 |
| upgrade-fzf-0.56.0-0.56.1-linux-arm64 | fzf, patch release | ELF aarch64, static | Go | junegunn/fzf `v0.56.0` -> `v0.56.1`, `fzf-<v>-linux_arm64.tar.gz` (member `fzf`) | 3,735,552 -> 3,735,552 |
| upgrade-fd-10.2.0-10.3.0-linux-arm64 | fd, minor release | ELF aarch64 PIE, dynamic (gnu) | Rust | sharkdp/fd `v10.2.0` -> `v10.3.0`, `fd-<v>-aarch64-unknown-linux-gnu.tar.gz` (member `fd`) | 3,386,704 -> 3,597,840 |
| upgrade-bat-0.25.0-0.26.0-macos-arm64 | bat, minor release | Mach-O arm64 | Rust | sharkdp/bat `v0.25.0` -> `v0.26.0`, `bat-<v>-aarch64-apple-darwin.tar.gz` (member `bat`) | 5,278,536 -> 5,842,592 |
| upgrade-ripgrep-14.0.3-14.1.0-linux-amd64 | ripgrep, minor release | ELF x86-64 static-PIE (musl) | Rust | BurntSushi/ripgrep `14.0.3` -> `14.1.0`, `ripgrep-<v>-x86_64-unknown-linux-musl.tar.gz` (member `rg`) | 6,040,792 -> 6,590,000 |
| upgrade-ripgrep-13.0.0-14.1.0-windows-amd64 | ripgrep, bigger gap (13 -> 14.1) | PE32+ x64 (.exe, msvc) | Rust | BurntSushi/ripgrep `13.0.0` -> `14.1.0`, `ripgrep-<v>-x86_64-pc-windows-msvc.zip` (member `rg.exe`) | 4,644,864 -> 5,332,480 |
| upgrade-lazygit-0.43.1-0.44.0-linux-amd64 | lazygit, minor release | ELF x86-64, static | Go | jesseduffield/lazygit `v0.43.1` -> `v0.44.0`, `lazygit_<v>_Linux_x86_64.tar.gz` (member `lazygit`) | 18,772,120 -> 18,854,040 |
| upgrade-lazygit-0.40.2-0.44.0-macos-arm64 | lazygit, bigger gap (0.40.2 -> 0.44.0) | Mach-O arm64 | Go | jesseduffield/lazygit `v0.40.2` -> `v0.44.0`, `lazygit_<v>_Darwin_arm64.tar.gz` (member `lazygit`) | 17,819,330 -> 18,794,242 |

Size classes: under 2 MB: 3 (jq x3); over 10 MB: 2 (lazygit x2). Gap pairs: ripgrep 13->14.1, lazygit 0.40.2->0.44.0, jq 1.7.1->1.8.0.
No Linux x86-64 pair is "adjacent patch" for C; the C coverage is jq. The macOS x86-64 architecture is covered by the patch category.

## patch: security-patch-like small source changes (built locally, `cc -O2`, macOS)

Compiled directly with `cc -O2 -w -ffile-prefix-map=<srcroot>=.` (no configure/make), identical flags for old and new, `ZERO_AR_DATE=1` for archives.
Source tarballs are the official ones (zlib.net/fossils, lua.org/ftp, sqlite.org/2024, sourceware.org/pub/bzip2).

| pair | what | format / arch | source (old -> new) | size old -> new |
|---|---|---|---|---|
| patch-zlib-minigzip-1.3-1.3.1-macos-arm64 | `test/minigzip.c` + all of zlib, linked in | Mach-O arm64 | zlib 1.3 -> 1.3.1 | 108,288 -> 108,112 |
| patch-zlib-minigzip-1.3-1.3.1-macos-x86_64 | same | Mach-O x86_64 | zlib 1.3 -> 1.3.1 | 103,104 -> 102,928 |
| patch-lua-5.4.6-5.4.7-macos-arm64 | `lua` interpreter (no readline, `LUA_USE_POSIX LUA_USE_DLOPEN`) | Mach-O arm64 | Lua 5.4.6 -> 5.4.7 | 298,224 -> 298,128 |
| patch-lua-5.4.6-5.4.7-macos-x86_64 | same | Mach-O x86_64 | Lua 5.4.6 -> 5.4.7 | 266,792 -> 266,696 |
| patch-sqlite-shell-3.45.1-3.45.2-macos-arm64 | sqlite3 shell (`shell.c`+`sqlite3.c`, `THREADSAFE=0 OMIT_LOAD_EXTENSION`) | Mach-O arm64 | SQLite amalgamation 3450100 -> 3450200 | 1,330,832 -> 1,330,960 |
| patch-bzip2-1.0.6-1.0.8-macos-x86_64 | `bzip2` executable | Mach-O x86_64 | bzip2 1.0.6 -> 1.0.8 | 113,072 -> 113,072 (same size, different content) |
| patch-libz.a-1.3-1.3.1-macos-x86_64 | static library libz (ar archive) | ar archive of x86_64 objects | zlib 1.3 -> 1.3.1 | 116,368 -> 116,536 |
| patch-liblua.a-5.4.6-5.4.7-macos-arm64 | static library liblua (ar archive) | ar archive of arm64 objects | Lua 5.4.6 -> 5.4.7 | 399,960 -> 400,216 |

## other: non-executable data

| pair | what | format | source | size old -> new |
|---|---|---|---|---|
| other-lua-src-tar-5.4.6-5.4.7 | uncompressed tar of the whole Lua source tree (sorted file order) | POSIX tar | lua.org/ftp lua-5.4.6.tar.gz -> lua-5.4.7.tar.gz | 1,524,224 -> 1,537,536 |
| other-sqlite3.c-3.45.1-3.45.2 | SQLite amalgamation `sqlite3.c` (single 9 MB C source file) | ASCII C text | sqlite.org/2024 amalgamation 3450100 -> 3450200 | 9,018,109 -> 9,022,640 |

## Notes
- Pair name prefix before the first `-` is the category used for per-category averages.
- Substitutions vs. the original plan: none of the downloads failed. jq 1.7 -> 1.7.1 uses the linux-arm64 asset (the linux-amd64 binary is over 2 MB and a third pair under 2 MB was needed).
