# -*- coding: utf-8 -*-
"""按 MUTF-8 正确解码 dex 字符串（\\xc0\\x80 代表 U+0000），导出指定索引。"""
import struct
import sys

path = sys.argv[1]
out = sys.argv[2]
d = open(path, "rb").read()
str_size, str_off = struct.unpack_from("<2I", d, 0x38)


def mutf8(b):
    return b.replace(b"\xc0\x80", b"\x00").decode("utf-8", "replace")


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
    s = mutf8(d[p:p + n])
    if len(s) > 200:
        lines.append("===== [%d] len=%d chars" % (i, len(s)))
        lines.append(s)
        lines.append("")
open(out, "w", encoding="utf-8").write("\n".join(lines))
print("->", out, len(lines))
