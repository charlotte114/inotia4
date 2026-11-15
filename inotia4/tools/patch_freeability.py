# -*- coding: utf-8 -*-
"""
「初始化能力点」免费（原本 20000 铜币 = 2 金币）。

函数 0x0D1DA0（能力点重置按钮回调）：
    bl   INVEN_GetMoney        ; 取金币
    movw r1, #0x4E20           ; 20000
    cmp  r0, r1
    blt  0xD1DC8               ; 不够 -> 弹「金币不足」
    movw r0, #0x4E20
    bl   INVEN_MinusMoney      ; 扣 20000
    bl   0xD2470               ; -> bl CHAR_InitializeStatus 真正重置

补丁：两处立即数改成 0 —— 判定恒成立、扣款为 0。
   0xD1DA6  movw r1, #0x4E20 -> mov.w r1, #0   (44f62061 -> 4ff00001)
   0xD1DAE  movw r0, #0x4E20 -> mov.w r0, #0   (44f62060 -> 4ff00000)
"""
import os
import sys

from elftools.elf.elffile import ELFFile

BASE = r"D:\Desktop\inotia4\_apk\lib\armeabi-v7a"
SRC = os.path.join(BASE, "libgame.ui.so")

EDITS = [
    # 能力点重置（0xD1DA0）
    (0xD1DA6, bytes.fromhex("44f62061"), bytes.fromhex("4ff00001"), "能力点 判定价格 movw r1,#0x4E20 -> mov.w r1,#0"),
    (0xD1DAE, bytes.fromhex("44f62060"), bytes.fromhex("4ff00000"), "能力点 扣款金额 movw r0,#0x4E20 -> mov.w r0,#0"),
    # 技能点重置（0x8E9AC，UISkill_ButtonSkillPointResetExe），结构与上面完全一致
    (0x8E9B2, bytes.fromhex("44f62061"), bytes.fromhex("4ff00001"), "技能点 判定价格 movw r1,#0x4E20 -> mov.w r1,#0"),
    (0x8E9BA, bytes.fromhex("44f62060"), bytes.fromhex("4ff00000"), "技能点 扣款金额 movw r0,#0x4E20 -> mov.w r0,#0"),
]


def vaddr_to_off(path, vaddr):
    with open(path, "rb") as f:
        e = ELFFile(f)
        for s in e.iter_sections():
            if s["sh_type"] == "SHT_NOBITS":
                continue
            a, size = s["sh_addr"], s["sh_size"]
            if a and a <= vaddr < a + size:
                return s["sh_offset"] + (vaddr - a)
    raise RuntimeError("vaddr %#x not mapped" % vaddr)


def main():
    raw = bytearray(open(SRC, "rb").read())
    changed = 0
    for addr, old, new, tag in EDITS:
        off = vaddr_to_off(SRC, addr)
        cur = bytes(raw[off:off + 4])
        if cur == new:
            print("0x%X 已是补丁状态，跳过" % addr)
            continue
        if cur != old:
            raise RuntimeError("0x%X 字节不符：%s（期望 %s）" % (addr, cur.hex(), old.hex()))
        raw[off:off + 4] = new
        changed += 1
        print("0x%X  %s -> %s   %s" % (addr, old.hex(), new.hex(), tag))
    if changed:
        open(SRC, "wb").write(raw)

    import capstone
    md = capstone.Cs(capstone.CS_ARCH_ARM, capstone.CS_MODE_THUMB)
    md.detail = True
    print("--- 改后 0xD1DA0 起 ---")
    code = bytes(raw[vaddr_to_off(SRC, 0xD1DA0):vaddr_to_off(SRC, 0xD1DA0) + 24])
    for ins in md.disasm(code, 0xD1DA0):
        print("%08x  %-9s %s %s" % (ins.address, code[ins.address - 0xD1DA0:ins.address - 0xD1DA0 + ins.size].hex(),
                                    ins.mnemonic, ins.op_str))


if __name__ == "__main__":
    main()
