#!/usr/bin/env python3
"""Light container parsing (headers only; nothing is executed): file-offset <-> virtual-address
regions and the section list of an ELF64 / Mach-O 64 / PE32+ file.

As a module: info(path) -> dict(fmt, arch, base, regions=[(off_lo, off_hi, va_lo, name)],
                                sections=[(name, off, size, va)])
As a script: vamap.py <file> [out.vamap]
  The .vamap text file is what cm/q4 read:  first line "base <imagebase>", then one region per
  line "off_lo off_hi va_lo".  Unknown formats give a single identity region (base 0).
"""
import struct, sys

EM = {62: "x86_64", 183: "arm64", 3: "x86", 40: "arm"}


def elf(b):
    if b[4] != 2 or b[5] != 1:
        return None
    (e_type, e_machine, _, e_entry, e_phoff, e_shoff, _, _, e_phentsize, e_phnum, e_shentsize, e_shnum,
     e_shstrndx) = struct.unpack_from("<HHIQQQIHHHHHH", b, 16)
    regions, sections = [], []
    for i in range(e_phnum):
        p_type, p_flags, p_offset, p_vaddr, p_paddr, p_filesz, p_memsz, p_align = struct.unpack_from(
            "<IIQQQQQQ", b, e_phoff + i * e_phentsize)
        if p_type == 1 and p_filesz:
            regions.append((p_offset, p_offset + p_filesz, p_vaddr, "LOAD%d" % i))
    sh = []
    for i in range(e_shnum):
        sh.append(struct.unpack_from("<IIQQQQIIQQ", b, e_shoff + i * e_shentsize))
    if sh and e_shstrndx < len(sh):
        so = sh[e_shstrndx][4]
        for s in sh:
            nm = b[so + s[0]:b.index(b"\0", so + s[0])].decode("latin1")
            if s[1] != 8 and s[1] != 0 and s[5]:     # not NOBITS / NULL
                sections.append((nm, s[4], s[5], s[3]))
    base = min((va - off for off, _, va, _ in regions), default=0)
    return dict(fmt="ELF" + (" rel" if e_type == 1 else " exec" if e_type == 2 else " dyn"), arch=EM.get(e_machine, str(e_machine)),
                base=base if e_type != 1 else 0, regions=regions if e_type != 1 else [], sections=sections)


def macho(b):
    magic, cputype, _, filetype, ncmds, sizeofcmds, flags, _ = struct.unpack_from("<IiiIIIII", b, 0)
    arch = {0x01000007: "x86_64", 0x0100000C: "arm64"}.get(cputype & 0xFFFFFFFF, hex(cputype))
    off = 32
    regions, sections, base = [], [], None
    for _ in range(ncmds):
        cmd, cmdsize = struct.unpack_from("<II", b, off)
        if cmd == 0x19:
            segname = b[off + 8:off + 24].rstrip(b"\0").decode()
            vmaddr, vmsize, fileoff, filesize, _, _, nsects, _ = struct.unpack_from("<QQQQiiII", b, off + 24)
            if filesize:
                regions.append((fileoff, fileoff + filesize, vmaddr, segname))
            if segname == "__TEXT":
                base = vmaddr
            for k in range(nsects):
                so = off + 72 + 80 * k
                sectname = b[so:so + 16].rstrip(b"\0").decode()
                sseg = b[so + 16:so + 32].rstrip(b"\0").decode()
                addr, size, soff = struct.unpack_from("<QQI", b, so + 32)
                stype = struct.unpack_from("<I", b, so + 64)[0] & 0xFF
                if stype not in (1, 12, 18) and size:      # not zerofill
                    sections.append((sseg + "," + sectname, soff, size, addr))
            if nsects == 0 and filesize:
                sections.append((segname, fileoff, filesize, vmaddr))
        off += cmdsize
    if filetype == 1:          # MH_OBJECT: relocatable, addresses are not final
        regions, base = [], 0
    return dict(fmt="Mach-O" + (" obj" if filetype == 1 else ""), arch=arch, base=base or 0, regions=regions, sections=sections)


def pe(b):
    pe_off = struct.unpack_from("<I", b, 0x3C)[0]
    if b[pe_off:pe_off + 4] != b"PE\0\0":
        return None
    machine, nsec, _, _, _, optsize, _ = struct.unpack_from("<HHIIIHH", b, pe_off + 4)
    opt = pe_off + 24
    magic = struct.unpack_from("<H", b, opt)[0]
    if magic != 0x20B:
        return None
    imagebase = struct.unpack_from("<Q", b, opt + 24)[0]
    hdrsize = struct.unpack_from("<I", b, opt + 60)[0]
    regions = [(0, hdrsize, imagebase, "headers")]
    sections = []
    st = opt + optsize
    for i in range(nsec):
        name = b[st + 40 * i:st + 40 * i + 8].rstrip(b"\0").decode("latin1")
        vsize, va, rawsize, rawptr = struct.unpack_from("<IIII", b, st + 40 * i + 8)
        if rawsize:
            regions.append((rawptr, rawptr + rawsize, imagebase + va, name))
            sections.append((name, rawptr, rawsize, imagebase + va))
    return dict(fmt="PE32+", arch={0x8664: "x86_64", 0xAA64: "arm64"}.get(machine, hex(machine)), base=imagebase,
                regions=regions, sections=sections)


def info(path):
    b = open(path, "rb").read()
    r = None
    try:
        if b[:4] == b"\x7fELF":
            r = elf(b)
        elif b[:4] == b"\xcf\xfa\xed\xfe":
            r = macho(b)
        elif b[:2] == b"MZ":
            r = pe(b)
    except (struct.error, ValueError, IndexError):
        r = None
    if r is None:
        fmt = "ar" if b[:8] == b"!<arch>\n" else "unknown"
        r = dict(fmt=fmt, arch="?", base=0, regions=[], sections=[])
    r["size"] = len(b)
    if not r["regions"]:
        r["regions"] = [(0, len(b), 0, "identity")]
        r["identity"] = True
    return r


def write_vamap(inf, out, identity=False):
    with open(out, "w") as f:
        if identity:
            f.write("base 0\n0 %d 0\n" % inf["size"])
        else:
            f.write("base %d\n" % inf["base"])
            for lo, hi, va, _ in inf["regions"]:
                f.write("%d %d %d\n" % (lo, hi, va))


if __name__ == "__main__":
    inf = info(sys.argv[1])
    print(inf["fmt"], inf["arch"], "base=%#x" % inf["base"])
    for lo, hi, va, nm in inf["regions"]:
        print("  region %-12s off %#10x..%#10x va %#x (va-off %#x)" % (nm, lo, hi, va, va - lo))
    for nm, off, size, va in inf["sections"]:
        print("  section %-28s off %#10x size %#9x va %#x" % (nm, off, size, va))
    if len(sys.argv) > 2:
        write_vamap(inf, sys.argv[2])
