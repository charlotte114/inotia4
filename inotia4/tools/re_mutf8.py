# -*- coding: utf-8 -*-
"""按 MUTF-8 规范解码 dex 字符串：长度前缀是 UTF-16 码元数，不是字节数。

单字节 0x01-0x7f -> 1 码元；0xc0 0x80 -> U+0000；2/3 字节序列各 1 码元。
"""
import struct
import sys


def read_uleb(d, p):
    n = 0
    sh = 0
    while True:
        b = d[p]
        p += 1
        n |= (b & 0x7f) << sh
        sh += 7
        if not b & 0x80:
            return n, p


def read_mutf8(d, p, units):
    out = []
    got = 0
    while got < units:
        b = d[p]
        if b < 0x80:
            out.append(b)
            p += 1
            got += 1
        elif (b & 0xe0) == 0xc0:
            out.append(((b & 0x1f) << 6) | (d[p + 1] & 0x3f))
            p += 2
            got += 1
        elif (b & 0xf0) == 0xe0:
            out.append(((b & 0x0f) << 12) | ((d[p + 1] & 0x3f) << 6) | (d[p + 2] & 0x3f))
            p += 3
            got += 1
        else:
            p += 1
            got += 1
    return "".join(chr(c) for c in out), p


def strings(path):
    d = open(path, "rb").read()
    str_size, str_off = struct.unpack_from("<2I", d, 0x38)
    out = []
    for i in range(str_size):
        off = struct.unpack_from("<I", d, str_off + 4 * i)[0]
        n, p = read_uleb(d, off)
        s, _ = read_mutf8(d, p, n)
        out.append(s)
    return out


if __name__ == "__main__":
    ss = strings(sys.argv[1])
    with open(sys.argv[2], "w", encoding="utf-8") as f:
        for i, s in enumerate(ss):
            f.write("[%d] %s\n" % (i, s))
    print("->", sys.argv[2], len(ss))
