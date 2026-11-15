# -*- coding: utf-8 -*-
"""在 libgame.so 里按关键字搜字符串，带上下文，用于定位反调试/防护/路径等。"""
import re
import sys

SO = r"D:\Desktop\inotia4\_apk\lib\armeabi-v7a\libgame.orig.so"


def main():
    data = open(SO, "rb").read()
    pats = sys.argv[1:] or ["ptrace", "frida", "xposed", "magisk", "hack", "root"]
    for p in pats:
        print("=== %s" % p)
        seen = set()
        for m in re.finditer(p.encode(), data, re.I):
            s = data[max(0, m.start() - 50): m.start() + 70]
            txt = "".join(chr(c) if 32 <= c < 127 else "." for c in s)
            if txt in seen:
                continue
            seen.add(txt)
            print("  %#08x %s" % (m.start(), txt))


if __name__ == "__main__":
    main()
