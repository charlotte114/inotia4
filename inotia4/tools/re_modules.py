# -*- coding: utf-8 -*-
"""按模块前缀导出 libgame.so 的函数清单 -> sym_by_module.txt"""
import collections
import os
import sys

from elftools.elf.elffile import ELFFile

SO = r"D:\Desktop\inotia4\_apk\lib\armeabi-v7a\libgame.orig.so"
OUT = r"D:\Desktop\inotia4\data\sym_by_module.txt"


def main():
    e = ELFFile(open(SO, "rb"))
    mods = collections.defaultdict(list)
    for s in e.get_section_by_name(".dynsym").iter_symbols():
        if s["st_shndx"] == "SHN_UNDEF" or not s.name:
            continue
        if s["st_info"]["type"] != "STT_FUNC":
            continue
        n = s.name
        key = n.split("_")[0] if "_" in n else "(global)"
        mods[key].append((s["st_value"] & ~1, s["st_size"], n))
    with open(OUT, "w", encoding="utf-8") as f:
        for k in sorted(mods, key=lambda x: (-len(mods[x]), x)):
            f.write("### %s (%d)\n" % (k, len(mods[k])))
            for a, z, n in sorted(mods[k]):
                f.write("  %08x %6d %s\n" % (a, z, n))
    print("->", OUT, "模块数", len(mods))


if __name__ == "__main__":
    main()
