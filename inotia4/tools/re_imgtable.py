# -*- coding: utf-8 -*-
"""导出 libgame.so 内嵌的 UI 图集目录（.data 段 0x1A102C 起，每项 64 字节，共 98 项）。

编号 i 就是 IMGSYS_UnitLoad(i) / IMGSYS_GetLoc(i) 的参数，也是 GRPX_LoadImage 的表索引。
IMGSYS_GetLoc 的 switch 上限是 cmp r0,#0x61（97），所以表长确定为 98。

用法: python tools/re_imgtable.py [输出文件]
"""
import os
import sys
import zipfile

SO = r"D:\Desktop\inotia4\_apk\lib\armeabi-v7a\libgame.orig.so"
TABLE = 0x1A102C
STRIDE = 64
COUNT = 98
APK = r"D:\Desktop\inotia4\艾诺迪亚4_1.2.9_cat_final.apk"


def main():
    d = open(SO, "rb").read()
    names = []
    for i in range(COUNT):
        raw = d[TABLE + STRIDE * i: TABLE + STRIDE * (i + 1)]
        names.append(raw.split(b"\x00")[0].decode("ascii", "replace"))

    z = zipfile.ZipFile(APK)
    present = {os.path.basename(i.filename).replace(".png.jpg", ".png")
               for i in z.infolist()
               if i.filename.startswith("assets/common/game_res/") and i.filename.endswith(".png.jpg")}
    used = set(names)
    lines = ["%3d  %s" % (i, n) for i, n in enumerate(names)]
    lines.append("")
    lines.append("图集表 %d 项；APK 内 png.jpg %d 个" % (COUNT, len(present)))
    lines.append("表里有、包里没有（运行时/下载/广告图）: %s" % sorted(used - present))
    lines.append("包里有、表里没有（未被引用）: %s" % sorted(present - used))
    text = "\n".join(lines)
    if len(sys.argv) > 1:
        open(sys.argv[1], "w", encoding="utf-8").write(text)
    print("OK")


if __name__ == "__main__":
    main()
