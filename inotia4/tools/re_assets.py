# -*- coding: utf-8 -*-
"""把 APK 里 assets/common/game_res 的每个文件解一遍，输出用途清单。

输出 survey_game_res.txt：
  文件名 | 压缩方式 | 解压后大小 | 容器形态 | count | 记录长度 | 说明
"""
import os
import struct
import sys
import zipfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import inotia_dat as D

APK = r"D:\Desktop\inotia4\艾诺迪亚4_1.2.9_cat_final.apk"
OUT = r"D:\Desktop\inotia4\data\survey_game_res.txt"


def kind_of(name, payload):
    if name.startswith("m") and name[1:].split(".")[0].isdigit():
        return ("地图裸数据", 0, 0, "")
    if D.is_string_table(payload):
        n = struct.unpack_from("<I", payload, 0)[0]
        return ("字符串表", n, 0, "")
    r = D.parse_fixed(payload)
    if r:
        return ("定长记录表", r[0], r[2], "hdr=%d" % r[1])
    return ("未识别", 0, 0, "len=%d" % len(payload))


def main():
    z = zipfile.ZipFile(APK)
    rows = []
    for info in z.infolist():
        p = info.filename
        if not p.startswith("assets/common/game_res/"):
            continue
        base = os.path.basename(p)
        blob = z.read(p)
        comp = "LZMA" if blob[:1] == b"\x01" else "原始"
        try:
            payload = D.unpack(blob)
        except Exception as e:
            rows.append((base, comp, -1, "解压失败", 0, 0, str(e)))
            continue
        k, cnt, rs, extra = kind_of(base, payload)
        rows.append((base, comp, len(payload), k, cnt, rs, extra))
    rows.sort()
    with open(OUT, "w", encoding="utf-8") as f:
        f.write("%-42s %-5s %10s %-14s %7s %5s %s\n"
                % ("file", "comp", "raw", "kind", "count", "rec", "note"))
        for r in rows:
            f.write("%-42s %-5s %10d %-14s %7d %5d %s\n" % r)
    print("->", OUT, len(rows))

    # game.dat 100 张子表
    payload = D.unpack(z.read("assets/common/game_res/game.dat.jpg"))
    subs = D.split_container(payload)
    lines = []
    for i, blob in enumerate(subs):
        c = struct.unpack_from("<I", blob, 0)[0]
        s = struct.unpack_from("<H", blob, 4)[0]
        name = D.SUB_TABLES[i] if i < len(D.SUB_TABLES) else "?"
        lines.append("%3d %-30s %6d x %2dB = %8d  %s"
                     % (i, name, c, s, c * s, "OK" if len(blob) == 6 + c * s else "size mismatch %d" % len(blob)))
    with open(r"D:\Desktop\inotia4\data\survey_game_dat.txt", "w", encoding="utf-8") as f:
        f.write("\n".join(lines))
    print("\n".join(lines[:10]))
    print("...", len(subs), "sub tables")


if __name__ == "__main__":
    main()
