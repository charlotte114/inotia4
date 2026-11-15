# -*- coding: utf-8 -*-
"""
主菜单底部那行「©2012-2019 Com2uS Corp. V. 1.2.9」整行不显示。

DrawVerNumber_Title (0x0D83B8)：从图集 0x4F 里取图（r0=0 取第 0x11 张=版权文字底图，
否则取 r0+7=数字字模），画到 (r1, r2)。只有 DrawTitle_Title 里两处调用它：
    0x0D879A  (画 ©2012 Com2uS Corp. V.)
    0x0D87C8  (画 -2019 等数字)
把函数开头改成 bx lr，两处调用都变成空操作，整行消失，图集不用动。
"""
import shutil
import sys

sys.path.insert(0, r"D:\Desktop\inotia4\tools")
from elfread import Reader

BASE = r"D:\Desktop\inotia4\_apk\lib\armeabi-v7a"
SRC = BASE + r"\libgame.ui0.so"
DST = BASE + r"\libgame.ui.so"


def main():
    shutil.copy(SRC, DST)
    rd = Reader(DST)
    raw = bytearray(open(DST, "rb").read())

    addr = 0xD83B8
    o = rd.off(addr)
    old = bytes(raw[o:o + 4])
    assert old == b"\xf0\xb5\x0d\x46", "期望 push {r4,r5,r6,r7,lr}; mov r5,r1，实际 %s" % old.hex()
    raw[o:o + 4] = b"\x70\x47\x00\xbf"          # bx lr ; nop
    print("DrawVerNumber_Title 置空  off %#07x  %s -> %s" % (o, old.hex(), bytes(raw[o:o + 4]).hex()))

    open(DST, "wb").write(raw)

    import capstone
    from elftools.elf.elffile import ELFFile
    f = open(DST, "rb")
    e = ELFFile(f)
    t = e.get_section_by_name(".text")
    base, data = t["sh_addr"], t.data()
    f.close()
    md = capstone.Cs(capstone.CS_ARCH_ARM, capstone.CS_MODE_THUMB)
    md.detail = True
    chunk = data[addr - base:addr - base + 8]
    print("\n--- 验证 %#x ---" % addr)
    for i in md.disasm(chunk, addr):
        print("%08x  %-10s %s %s" % (i.address, chunk[i.address - addr:i.address - addr + i.size].hex(),
                                     i.mnemonic, i.op_str))


if __name__ == "__main__":
    main()
