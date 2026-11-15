# -*- coding: utf-8 -*-
"""导出 dex 字符串池（避免控制台 GBK 编码问题，直接写文件）。"""
import struct
import sys

path = sys.argv[1]
out = sys.argv[2]
d = open(path, "rb").read()
str_size, str_off = struct.unpack_from("<2I", d, 0x38)
lines = []
for i in range(str_size):
    off = struct.unpack_from("<I", d, str_off + 4 * i)[0]
    p = off
    n = 0
    sh = 0
    while True:
        b = d[p]
        p += 1
        n |= (b & 0x7f) << sh
        sh += 7
        if not b & 0x80:
            break
    lines.append("%5d %r" % (i, d[p:p + n].decode("utf-8", "replace")))
open(out, "w", encoding="utf-8").write("\n".join(lines))
print("->", out, len(lines))
