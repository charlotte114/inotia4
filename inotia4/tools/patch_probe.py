# -*- coding: utf-8 -*-
"""探测脚本：把候选控件的 x 坐标改成互不相同的值，一次截图即可看清谁是谁。
用完即弃，正式改法走 patch_hud.py。"""
import shutil
import sys

sys.path.insert(0, r"D:\Desktop\inotia4\tools")
from elfread import Reader

BASE = r"D:\Desktop\inotia4\_apk\lib\armeabi-v7a"
SRC = BASE + r"\libgame.ui0.so"
DST = BASE + r"\libgame.ui.so"

# (地址, 原2字节, 新x值, 说明)
PROBES = [
    (0x89BC8, b"\xb1\x22", 220, "ID30 x 177->220"),
    (0x89C00, b"\x5b\x22", 240, "ID31 x  91->240"),
    (0x89C3A, b"\x19\x22", 120, "ID32 x  25->120"),
]


def main():
    shutil.copy(SRC, DST)
    rd = Reader(DST)
    raw = bytearray(open(DST, "rb").read())
    for addr, old, val, tag in PROBES:
        o = rd.off(addr)
        cur = bytes(raw[o:o + 2])
        assert cur == old, "%s 期望 %s 实际 %s" % (tag, old.hex(), cur.hex())
        raw[o:o + 2] = bytes([val, 0x22])
        print("%-16s off %#07x  %s -> %s" % (tag, o, cur.hex(), bytes(raw[o:o + 2]).hex()))
    open(DST, "wb").write(raw)


if __name__ == "__main__":
    main()
