# -*- coding: utf-8 -*-
"""在 i_*.dat 里自动定位偏移表：尝试 (起始, 字节宽度, 标志位) 组合，要求单调且末值≈文件尾。"""
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import inotia_dat as D

RES = r"D:\Desktop\inotia4\_apk\assets\common\game_res"


def probe(name):
    payload = D.unpack(open(os.path.join(RES, name + ".dat.jpg"), "rb").read())
    n = struct.unpack_from("<I", payload, 0)[0]
    total = len(payload)
    best = []
    for w in (3, 4):
        for start in range(4, 64):
            need = start + w * (n + 1)
            if need >= total:
                continue
            vals = [int.from_bytes(payload[start + w * i: start + w * (i + 1)], "little")
                    for i in range(n + 1)]
            for mask in (0, 0x800000, 0x80000000):
                v = [x & ~mask if mask else x for x in vals]
                if any(v[i] > v[i + 1] for i in range(n)):
                    continue
                end = v[-1]
                if end == 0:
                    continue
                slack = total - end
                if 0 <= slack <= 4 * w * n and end > start:
                    best.append((abs(slack), w, start, hex(mask), end, slack))
    best.sort()
    print("%-16s payload=%7d count=%5d" % (name, total, n))
    for b in best[:4]:
        print("      w=%d start=%2d mask=%s 末偏移=%d 剩余=%d" % (b[1], b[2], b[3], b[4], b[5]))
    if not best:
        print("      （无匹配）")


if __name__ == "__main__":
    for nm in sys.argv[1:] or ["i_portrait", "i_tile", "i_player", "i_monster"]:
        probe(nm)
