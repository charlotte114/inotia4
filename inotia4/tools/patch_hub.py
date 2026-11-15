# -*- coding: utf-8 -*-
"""
主菜单右上角 HIVE 按钮的点击处理（图标已在图集里擦掉）一起禁掉。

调用链：
    0x0D75AC  bl  C2S_HubMain
    0x0D75B4  b.w GotoHubMain(0x68DD4)  →  b.w C2S_HubMain
    0x68DD4   GotoHubMain
把 C2S_HubMain (0x6BD58) 开头改成 movs r0,#0 / bx lr，两个入口都变成空操作。
GotoShowMoreGames(0x68DD0 → 0x6B7D4) 不动，底部「更多游戏」按钮保留。
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

    addr = 0x6BD58
    o = rd.off(addr)
    old = bytes(raw[o:o + 4])
    assert old == b"\x10\xb5\x8e\xb0", "期望 push {r4,lr}; sub sp,#0x38，实际 %s" % old.hex()
    raw[o:o + 4] = b"\x00\x20\x70\x47"      # movs r0,#0 ; bx lr
    print("C2S_HubMain 置空  off %#07x  %s -> %s" % (o, old.hex(), bytes(raw[o:o + 4]).hex()))

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
    print("--- 验证 %#x ---" % addr)
    for i in md.disasm(chunk, addr):
        print("%08x  %-10s %s %s" % (i.address, chunk[i.address - addr:i.address - addr + i.size].hex(),
                                     i.mnemonic, i.op_str))


if __name__ == "__main__":
    main()
