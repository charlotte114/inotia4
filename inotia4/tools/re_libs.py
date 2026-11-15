# -*- coding: utf-8 -*-
"""统计 lib 目录下三个 .so 的概况：ELF 头、依赖、符号、字符串特征。"""
import os
from elftools.elf.elffile import ELFFile

LIB = os.path.join(r"D:\Desktop\inotia4\_apk", "lib", "armeabi-v7a")


def info(name):
    p = os.path.join(LIB, name)
    e = ELFFile(open(p, "rb"))
    needed = [t.needed for t in e.get_section_by_name(".dynamic").iter_tags()
              if t.entry.d_tag == "DT_NEEDED"]
    syms = list(e.get_section_by_name(".dynsym").iter_symbols())
    funcs = [s.name for s in syms if s["st_info"]["type"] == "STT_FUNC" and s["st_shndx"] != "SHN_UNDEF"]
    com = e.get_section_by_name(".comment")
    print("%-16s size=%8d  needed=%s" % (name, os.path.getsize(p), needed))
    print("   dynsym=%d  导出函数=%d  例: %s" % (len(syms), len(funcs), ", ".join(funcs[:8])))
    if com:
        print("   编译器:", com.data().replace(b"\x00", b" ").decode("ascii", "replace")[:120])


for n in ["libgame.so", "libfmodex.so", "libfmodevent.so"]:
    info(n)
