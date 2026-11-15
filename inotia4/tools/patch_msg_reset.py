# -*- coding: utf-8 -*-
"""
把「初始化能力点」成功后的提示从「购买成功。」改成「能力点已被初始化。」。

机制：
  消息以 id 走 ShowMessage(0x8BD40)，id 经 TEXTDATABASE（game.dat 第 73 张表，
  每条 2 字节 = memorytext 索引）映射到文本。
    0x20(32)  -> 35348 「购买成功。」
    0x8C(140) -> 35456 「能力点已被初始化。」
  重置能力点的回调 0xD1DA0 在成功后发的是 0x20：
      0xD1DBA  movs r0, #0x20
      0xD1DC2  bl   0x8BD40
  改成 0x8C 即可。

  同类：技能点重置在 0x8E9C6 也是 #0x20，对应文本 0x8D(141)「技能点已被初始化。」
        （本脚本默认只改能力点，可用参数 --skill 一起改）
"""
import os
import sys

from elftools.elf.elffile import ELFFile

BASE = r"D:\Desktop\inotia4\_apk\lib\armeabi-v7a"
SRC = os.path.join(BASE, "libgame.ui.so")

EDITS = [
    (0xD1DBA, bytes.fromhex("2020"), bytes.fromhex("8c20"), "能力点重置成功提示 0x20 -> 0x8C"),
]
SKILL = [
    (0x8E9C6, bytes.fromhex("2020"), bytes.fromhex("8d20"), "技能点重置成功提示 0x20 -> 0x8D"),
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
    edits = EDITS + (SKILL if "--skill" in sys.argv else [])
    raw = bytearray(open(SRC, "rb").read())
    changed = 0
    for addr, old, new, tag in edits:
        off = vaddr_to_off(SRC, addr)
        cur = bytes(raw[off:off + 2])
        if cur == new:
            print("0x%X 已是补丁状态，跳过" % addr)
            continue
        if cur != old:
            raise RuntimeError("0x%X 字节不符：%s（期望 %s）" % (addr, cur.hex(), old.hex()))
        raw[off:off + 2] = new
        changed += 1
        print("0x%X  %s -> %s   %s" % (addr, old.hex(), new.hex(), tag))
    if changed:
        open(SRC, "wb").write(raw)
    print("完成，共改 %d 处" % changed)


if __name__ == "__main__":
    main()
