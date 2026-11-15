# -*- coding: utf-8 -*-
"""
擦掉 UI_TITLEMAIN_01.png.jpg 里那行「©2012 Com2uS Corp. V.」+ 数字字模。
范围 x 2..536, y 995..1018（实测非透明包围盒），背景本来就是全透明，
所以把这块 alpha 清零即可，不影响其它元素。
"""
import shutil
import sys

sys.path.insert(0, r"D:\Desktop\inotia4\tools")
import pngtool

SRC = r"D:\Desktop\inotia4\_apk\assets\common\game_res\UI_TITLEMAIN_01.png.jpg"
BAK = SRC + ".orig"

# 要清掉的两块：(x0, y0, x1, y1, 说明)
# 注意：右上角按钮实测 bbox x 625..695 / y 2..68，左侧背景魔法阵右边沿 x=601，
# 中间 602..624 是透明间隔。所以擦除范围取 612..712，两边都留安全边，绝不能碰到 601。
REGIONS = [
    (0, 996, 545, 1022, "©2012 Com2uS Corp. V. + 数字字模"),
    (612, 0, 712, 76, "右上角 HIVE 按钮"),
    (700, 500, 820, 572, "左上角喇叭图标"),
]


def main():
    import os.path
    if not os.path.exists(BAK):
        shutil.copy(SRC, BAK)
        print("备份 ->", BAK)

    w, h, nch, px = pngtool.read_png(BAK)
    buf = bytearray(px)
    cleared = 0
    for X0, Y0, X1, Y1, _tag in REGIONS:
        for y in range(Y0, min(Y1 + 1, h)):
            for x in range(X0, min(X1 + 1, w)):
                i = (y * w + x) * nch
                if buf[i + 3]:
                    buf[i] = buf[i + 1] = buf[i + 2] = buf[i + 3] = 0
                    cleared += 1
    pngtool.write_png(SRC, w, h, nch, bytes(buf))
    print("清掉 %d 个像素，写出 %s" % (cleared, SRC))

    # 自检：擦除区之外的像素必须与原图完全一致（防止再次擦坏背景美术）
    changed = 0
    for y in range(h):
        for x in range(w):
            i = (y * w + x) * nch
            if buf[i:i + nch] == px[i:i + nch]:
                continue
            inside = any(X0 <= x <= X1 and Y0 <= y <= Y1 for X0, Y0, X1, Y1, _t in REGIONS)
            if not inside:
                changed += 1
    print("自检：擦除区之外被误改的像素 = %d" % changed)
    assert changed == 0, "擦除范围越界，已中止"

    # 复查
    w2, h2, n2, px2 = pngtool.read_png(SRC)
    for X0, Y0, X1, Y1, tag in REGIONS:
        left = 0
        for y in range(Y0, Y1 + 1):
            for x in range(X0, X1 + 1):
                if px2[(y * w2 + x) * n2 + 3]:
                    left += 1
        print("复查 %-32s 剩余非透明像素: %d" % (tag, left))


if __name__ == "__main__":
    main()
