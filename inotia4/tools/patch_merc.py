# -*- coding: utf-8 -*-
"""
佣兵界面 3 个锁住的格子：解锁价格 20000 -> 0（免费开）。

UIMercenary_ButtonListExe (0x8433C)：
    0x8439E  bl   INVEN_GetMoney
    0x843A2  movw r1, #0x4E20     ; 判定用价格 20000
    0x843A6  cmp  r0, r1
    0x843A8  blt  0x843C4         ; 不足 -> 弹「您的金币不足」(对话框 0xA)
    0x843AA  movw r0, #0x4E20     ; 扣款金额 20000
    0x843AE  bl   INVEN_MinusMoney
    0x843B2  bl   UIMercenary_OpenSlotUIInAppProcess
两处 movw 都改成 mov.w #0，比较恒成立、扣 0 金币。
"""
import shutil
import sys

sys.path.insert(0, r"D:\Desktop\inotia4\tools")
from elfread import Reader

import keystone

BASE = r"D:\Desktop\inotia4\_apk\lib\armeabi-v7a"
SRC = BASE + r"\libgame.ui0.so"
DST = BASE + r"\libgame.ui.so"

ks = keystone.Ks(keystone.KS_ARCH_ARM, keystone.KS_MODE_THUMB)


def main():
    shutil.copy(SRC, DST)
    rd = Reader(DST)
    raw = bytearray(open(DST, "rb").read())

    patches = [
        (0x843A2, bytes.fromhex("44f62061"), bytes(ks.asm("mov.w r1, #0")[0]),
         "判定价格 20000 -> 0"),
        (0x843AA, bytes.fromhex("44f62060"), bytes(ks.asm("mov.w r0, #0")[0]),
         "扣款金额 20000 -> 0"),
    ]
    for addr, old, new, tag in patches:
        o = rd.off(addr)
        cur = bytes(raw[o:o + 4])
        assert cur == old, "%s 期望 %s 实际 %s" % (tag, old.hex(), cur.hex())
        assert len(new) == 4, (tag, len(new))
        raw[o:o + 4] = new
        print("%-22s off %#07x  %s -> %s" % (tag, o, cur.hex(), new.hex()))

    open(DST, "wb").write(raw)

    # 自检
    import capstone
    from elftools.elf.elffile import ELFFile
    f = open(DST, "rb")
    e = ELFFile(f)
    t = e.get_section_by_name(".text")
    base, data = t["sh_addr"], t.data()
    f.close()
    md = capstone.Cs(capstone.CS_ARCH_ARM, capstone.CS_MODE_THUMB)
    md.detail = True
    start = 0x8439E
    chunk = data[start - base:start - base + 0x18]
    print("\n--- 验证 %#x ---" % start)
    for i in md.disasm(chunk, start):
        print("%08x  %-10s %-8s %s" % (i.address, chunk[i.address - start:i.address - start + i.size].hex(),
                                       i.mnemonic, i.op_str))


if __name__ == "__main__":
    main()
