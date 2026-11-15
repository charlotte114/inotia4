# -*- coding: utf-8 -*-
"""统计 game_res 里 png.jpg 的真实尺寸（PNG IHDR）和 m*.dat 地图头部。"""
import os
import re
import struct
import zipfile

APK = r"D:\Desktop\inotia4\艾诺迪亚4_1.2.9_cat_final.apk"
OUT = r"D:\Desktop\inotia4\data\survey_images.txt"


def png_size(b):
    if b[:8] != b"\x89PNG\r\n\x1a\n":
        return None
    w, h = struct.unpack_from(">II", b, 16)
    bitdepth, ctype = b[24], b[25]
    return w, h, bitdepth, ctype


def main():
    z = zipfile.ZipFile(APK)
    rows = []
    maps = []
    for i in z.infolist():
        p = i.filename
        if not p.startswith("assets/common/game_res/"):
            continue
        base = os.path.basename(p)
        b = z.read(p)
        if base.endswith(".png.jpg"):
            s = png_size(b)
            rows.append((base, s, i.file_size))
        elif re.fullmatch(r"m\d+\.dat\.jpg", base):
            maps.append((int(base[1:].split(".")[0]), len(b)))
    rows.sort()
    with open(OUT, "w", encoding="utf-8") as f:
        f.write("%-42s %10s %6s %4s %3s %10s\n" % ("file", "w", "h", "bit", "ct", "zipB"))
        for name, s, zs in rows:
            if s:
                f.write("%-42s %10d %6d %4d %3d %10d\n" % (name, s[0], s[1], s[2], s[3], zs))
            else:
                f.write("%-42s %10s\n" % (name, "非PNG"))
    print("图集", len(rows), "地图", len(maps))
    tot = sum(s[0] * s[1] * 4 for _, s, _ in rows if s)
    print("图集像素总量约 %.1f Mpx (RGBA 约 %.1f MB 显存)" % (tot / 1e6, tot / 1e6))
    big = sorted([r for r in rows if r[1]], key=lambda r: -r[1][0] * r[1][1])[:12]
    for n, s, zs in big:
        print("  %-40s %5dx%-5d" % (n, s[0], s[1]))


if __name__ == "__main__":
    main()
