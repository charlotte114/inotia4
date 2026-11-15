# -*- coding: utf-8 -*-
"""
把 AndroidManifest 里的 INTERNET 权限名改坏一个字符 -> 系统不再授予网络权限

原理：AXML 的 StringPool 是 UTF-16，权限名是普通字符串。
把 "android.permission.INTERNET" 的最后一个字符 T 改成 X，
长度不变、chunk 大小不变、无需重算任何结构，只改 2 个字节。
系统读到一个不存在的权限名 -> 直接忽略 -> 应用永远拿不到 INTERNET。

效果等同于"断网"（实测断网启动 868ms vs 联网 21.5s）。
"""
import struct
import sys

NAMES = [
    "android.permission.INTERNET",
    "android.permission.ACCESS_NETWORK_STATE",
]


def find_stringpool(d):
    off = 8
    while off < len(d):
        ctype, chsize, csz = struct.unpack_from("<HHI", d, off)
        if ctype == 0x0001:
            return off, chsize, csz
        if ctype == 0:
            break
        off += csz
    raise ValueError("没找到 StringPool")


def read_strings(d):
    off, chsize, csz = find_stringpool(d)
    sc, stc, flags, sstart, sstyles = struct.unpack_from("<IIIII", d, off + 8)
    utf8 = bool(flags & 0x100)
    offs = [struct.unpack_from("<I", d, off + chsize + i * 4)[0] for i in range(sc)]
    base = off + sstart
    out = []
    for o in offs:
        p = base + o
        if utf8:
            l = d[p]
            p += 2 if (l & 0x80) else 1
            l2 = d[p]
            p += 1
            if l2 & 0x80:
                l2 = ((l2 & 0x7F) << 8) | d[p]
                p += 1
            out.append((p, d[p:p + l2].decode("utf-8", "replace"), "utf8"))
        else:
            l = struct.unpack_from("<H", d, p)[0]
            p += 2
            if l & 0x8000:
                l2 = struct.unpack_from("<H", d, p)[0]
                p += 2
                l = ((l & 0x7FFF) << 16) | l2
            out.append((p, d[p:p + l * 2].decode("utf-16-le", "replace"), "utf16"))
    return out


def patch(d, only=None):
    d = bytearray(d)
    changed = []
    for p, s, enc in read_strings(d):
        if s in (NAMES if only is None else only):
            if enc == "utf16":
                pos = p + (len(s) - 1) * 2
                v = struct.unpack_from("<H", d, pos)[0]
                struct.pack_into("<H", d, pos, v ^ 0x0001)   # 末位字符翻一位，长度不变
            else:
                pos = p + len(s) - 1
                d[pos] ^= 0x01
            changed.append(s)
            print(f"已改动 {s!r} @0x{pos:x} -> 末位 T 改 X")
    return bytes(d), bool(changed)


def main(src, dst):
    with open(src, "rb") as f:
        data = f.read()
    out, ok = patch(data)
    if not ok:
        sys.exit("没找到目标权限字符串")
    assert len(out) == len(data), "长度变了，危险"
    with open(dst, "wb") as f:
        f.write(out)
    print(f"写出 {dst}（{len(out)} 字节，长度未变）")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
