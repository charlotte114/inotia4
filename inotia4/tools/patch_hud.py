# -*- coding: utf-8 -*-
"""
HUD 调整：左下移动轮盘（ID 22 控件）放大 + 右移；右侧 6 个技能按钮放大。

UIPlay_Create 0x89b74 起的 ID=22 控件：
    mov.w r2,#0x17c(380) -> str [sp],[sp#4]  (w,h)
    movs  r2,#0xa(10)                        (x)
    sub.w r3,r0,#0x17c                       (y = 屏高-380)
    改成 420×420，x=60，y=屏高-420

技能按钮：尺寸 fp=0x50(80) -> 0x60(96)
          位置来自 .rodata 0x17b7f8 的 6 个 u32（距右边）与 0x17b810 的 6 个 u32（距底边），
          x = 屏宽 - t1 - 3, y = 屏高 - t2 + 2, 各 +8 保持视觉中心不动。
"""
import shutil
import struct
import sys

sys.path.insert(0, r"D:\Desktop\inotia4\tools")
from elfread import Reader

BASE = r"D:\Desktop\inotia4\_apk\lib\armeabi-v7a"
SRC = BASE + r"\libgame.ui0.so"   # 已删收费商店按钮、保存下移
DST = BASE + r"\libgame.ui.so"

T1_ADDR = 0x17B7F8      # 6 个 u32：距屏幕右边
T2_ADDR = 0x17B810      # 6 个 u32：距屏幕底边


def main():
    shutil.copy(SRC, DST)
    rd = Reader(DST)
    raw = bytearray(open(DST, "rb").read())
    edits = []

    def poke(vaddr, new_bytes, tag):
        o = rd.off(vaddr)
        old = bytes(raw[o:o + len(new_bytes)])
        raw[o:o + len(new_bytes)] = new_bytes
        edits.append((tag, vaddr, o, old, new_bytes))

    # ① 技能按钮 80 -> 96
    poke(0x8992E, bytes.fromhex("4ff0600b"), "技能按钮尺寸 mov.w fp,#0x60")

    # ② 技能按钮坐标 +8（x 左移 8、y 上移 8，配合尺寸 +16 保持中心）
    for i in range(6):
        a = T1_ADDR + i * 4
        v = rd.u32(a)
        poke(a, struct.pack("<I", v + 8), "技能%d 距右 %d->%d" % (i, v, v + 8))
    for i in range(6):
        a = T2_ADDR + i * 4
        v = rd.u32(a)
        poke(a, struct.pack("<I", v + 8), "技能%d 距底 %d->%d" % (i, v, v + 8))

    # ③ 轮盘：ID=21 控件 252 -> 320，x 10 -> 60，y = 屏高-320
    #    0x89b42 起 16 字节整体重排：
    #    mov r1,r7 / movw r2,#320 / str r2,[sp] / str r2,[sp,#4] / subs r3,r0,r2 / movs r2,#60 / mov r0,r4
    if len(sys.argv) <= 2:
        print("轮盘：未指定参数，保持原样")
        open(DST, "wb").write(raw)
        for tag, v, o, old_b, new_b in edits:
            print("%-28s vaddr %#08x  %s -> %s" % (tag, v, old_b.hex(), new_b.hex()))
        return
    import keystone
    ks = keystone.Ks(keystone.KS_ARCH_ARM, keystone.KS_MODE_THUMB)
    SZ, WX = int(sys.argv[1]), int(sys.argv[2])
    A = lambda t: bytes(ks.asm(t)[0])
    block = (A("mov r1, r7") + A("mov.w r2, #%d" % SZ)
             + A("str r2, [sp]") + A("str r2, [sp, #4]")
             + A("subs r3, r0, r2") + A("movs r2, #%d" % WX)
             + A("mov r0, r4"))
    assert len(block) == 16, len(block)
    poke(0x89B42, block, "轮盘 size=%d x=%d y=H-%d" % (SZ, WX, SZ))

    open(DST, "wb").write(raw)

    for tag, v, o, old, new in edits:
        print("%-28s vaddr %#08x  off %#07x  %s -> %s" % (tag, v, o, old.hex(), new.hex()))

    # 反汇编自检
    import capstone
    f = open(DST, "rb")
    from elftools.elf.elffile import ELFFile
    e = ELFFile(f)
    t = e.get_section_by_name(".text")
    base, data = t["sh_addr"], t.data()
    f.close()
    md = capstone.Cs(capstone.CS_ARCH_ARM, capstone.CS_MODE_THUMB)
    md.detail = True
    for start, n in ((0x89B3E, 9), (0x8992C, 3)):
        chunk = data[start - base:start - base + n * 4]
        print("\n--- %#x ---" % start)
        for i in md.disasm(chunk, start):
            print("%08x  %-10s %-8s %s" % (i.address, chunk[i.address - start:i.address - start + i.size].hex(),
                                           i.mnemonic, i.op_str))


if __name__ == "__main__":
    main()
