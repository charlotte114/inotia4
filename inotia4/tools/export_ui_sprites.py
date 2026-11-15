# -*- coding: utf-8 -*-
"""从原版图集里裁出界面用的小贴图，写到 public/assets/ui/ 下。

原版界面按钮大多不是独立 PNG，而是烘在大图集里的一块（见 data/imgtable.txt）。
项目里那些 `public/assets/ui/*.png` 就是这样裁出来的；本脚本把「裁哪一块」写成表，
让每个裁出来的图都能追溯回「哪张图集的哪个矩形」，不用靠记忆。

用法：
    python inotia4/tools/export_ui_sprites.py

表里每一项：(图集文件名, x, y, 宽, 高, 输出相对路径, 说明)
坐标是图集内像素、左上角为原点，取自非透明包围盒（alpha > 16 的最小矩形）。
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import pngtool

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
APK = os.path.join(ROOT, "inotia4", "_apk", "assets", "common", "game_res")
OUT = os.path.join(ROOT, "public", "assets", "ui")

# 存档槽界面（UI_TITLESLOT_01）右上角那枚「删除」叉：55×55，实测包围盒 (957,179)-(1011,233)。
# 把它单独裁出来是因为存档行要按行放一枚，而不是整页只放一枚。
#
# 融合器菜单行的整行底：图集 19（UI_007.png）里那枚深色胶囊。可见胶囊的包围盒是
# (87,192)-(373,262)，但**按图块连同四周透明留白一起裁**（84,186,295,82）：原版
# 一行 442×(字高+41)，底图是整块铺满的，留白让相邻两行之间留出缝；
# 只裁可见包围盒会让五行糊成一摞。
# 反汇编 UIMix_ButtonMenuListDraw(0x84FDC) 走的是 IMGSYS_GetLoc(0x13, 3)：图集 0x13=19、
# 第 3 号块；矩形表的 case 分发没解开，所以按该图集的块间距取留白。
# 注意：不要换成 assets/ui/common/button.png——那枚是浅灰半透明的通用按钮皮，
# 原版菜单行用的是这枚不透明的深色底（中心像素 (67,63,57,255)）。
SPRITES = [
    ("UI_TITLESLOT_01.png.jpg", 956, 178, 57, 57,
     "title-menu/cancel.png", "存档槽行的删除叉（原版图集 88 的右上角按钮）"),
    ("UI_007.png.jpg", 84, 186, 295, 82,
     "crafting/menu-row.png", "融合器菜单行的整行底（原版图集 19 的深色胶囊，含留白）"),
    # 目标格两侧的 ▲▼（原版 UIMix_ButtonUpExe/DownExe 只做 index±1 回绕，就是在可合成的
    # 装备之间切换）。绘制走 IMGSYS_GetGroup(15)+GetLoc(15,36)，图集 15 = UI_003.png，
    # 块 36 = (306,312,149,75)；那 149 宽里有金色箭头与橙色发光两份，这里只取金色那枚
    # （软阴影一起裁，阈值以上的包围盒 316,309,45,47）。
    ("UI_003.png.jpg", 316, 309, 45, 47,
     "crafting/arrow-prev.png", "融合器目标格两侧的切换箭头（原版图集 15 块 36 的金色 chevron）"),
]


def main():
    for source, x, y, width, height, target, note in SPRITES:
        src = os.path.join(APK, source)
        if not os.path.exists(src):
            raise SystemExit("找不到图集：%s" % src)
        dst = os.path.join(OUT, target)
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        pngtool.crop_scale(src, dst, x, y, width, height, 1)
        print("  %-28s <- %s (%d,%d,%d,%d)  %s" % (target, source, x, y, width, height, note))


if __name__ == "__main__":
    main()
