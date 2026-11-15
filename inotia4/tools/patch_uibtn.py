# -*- coding: utf-8 -*-
"""
删除 UIPlay 左下竖排按钮中间那个（控件 ID=2，y=232）—— 收费商店按钮。

UIPlay_Create 里三块几乎相同的代码：
    块1 0x897fa-0x89832 之前  y=0x9b(155)  SetID(1)  保存
    块2 0x897fa-0x89832       y=0xe8(232)  SetID(2)  收费商店   <-- 删掉
    块3 0x89834-...           y=0x135(309) SetID(3)  游戏菜单

做法：在 0x897fa 处放一条无条件短跳 b 0x89834，整块跳过，控件根本不会被创建。
"""
import shutil
import struct

from elftools.elf.elffile import ELFFile

BASE = r"D:\Desktop\inotia4\_apk\lib\armeabi-v7a"
SRC = BASE + r"\libgame.orig.so"
DST = BASE + r"\libgame.ui0.so"

BLOCK_START = 0x897FA      # 第二块起点
BLOCK_END = 0x89834        # 第三块起点


def thumb_b16(pc, target):
    """16 位无条件跳转 b（±2KB）"""
    off = target - (pc + 4)
    assert off % 2 == 0
    imm = off // 2
    assert -2048 <= off <= 2046, off
    return struct.pack("<H", 0xE000 | (imm & 0x7FF))


def vaddr_to_off(path, vaddr):
    f = open(path, "rb")
    e = ELFFile(f)
    for s in e.iter_sections():
        a, sz = s["sh_addr"], s["sh_size"]
        if s["sh_type"] != "SHT_NOBITS" and a and a <= vaddr < a + sz:
            f.close()
            return s["sh_offset"] + (vaddr - a)
    f.close()
    raise ValueError("addr not mapped")


def main():
    shutil.copy(SRC, DST)
    raw = bytearray(open(DST, "rb").read())

    # ① 跳过第二块（收费商店按钮），控件不再创建
    off = vaddr_to_off(DST, BLOCK_START)
    code = thumb_b16(BLOCK_START, BLOCK_END)
    old = bytes(raw[off:off + 4])
    raw[off:off + 2] = code
    raw[off + 2:off + 4] = b"\x00\xbf"      # nop 填充，保持 4 字节对齐
    print("① 删除收费商店按钮")
    print("   文件偏移 %#x  原 %s  新 %s" % (off, old.hex(), bytes(raw[off:off + 4]).hex()))

    # ② 保存按钮 y: 0x9b(155) -> 0xe8(232)，即原收费商店按钮的位置
    off2 = vaddr_to_off(DST, 0x897E2)
    old2 = bytes(raw[off2:off2 + 2])
    assert old2 == b"\x9b\x23", old2.hex()
    raw[off2:off2 + 2] = b"\xe8\x23"
    print("② 保存按钮下移到原商城位置")
    print("   文件偏移 %#x  原 %s  新 %s" % (off2, old2.hex(), bytes(raw[off2:off2 + 2]).hex()))

    open(DST, "wb").write(raw)

    # 反汇编验证（直接读新文件）
    import capstone
    f = open(DST, "rb")
    e = ELFFile(f)
    t = e.get_section_by_name(".text")
    base = t["sh_addr"]
    data = t.data()
    f.close()
    md = capstone.Cs(capstone.CS_ARCH_ARM, capstone.CS_MODE_THUMB)
    md.detail = True
    start = 0x897F2
    chunk = data[start - base: start - base + 40]
    print("\n--- 验证 0x897f2 起 ---")
    for i in md.disasm(chunk, start):
        b = chunk[i.address - start:i.address - start + i.size]
        print("%08x  %-10s %-8s %s" % (i.address, b.hex(), i.mnemonic, i.op_str))


if __name__ == "__main__":
    main()
