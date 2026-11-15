# -*- coding: utf-8 -*-
"""
把「重置能力点 / 重置技能点」成功提示的文本改成「重置成功」。

文本表：assets/common/game_res/memorytext_zhhans.dat.jpg
  35456  「能力点已被初始化。」 -> 重置能力点成功提示（消息 id 0x8C）
  35457  「技能点已被初始化。」 -> 重置技能点成功提示（消息 id 0x8D）

只改这两条，其它文本一律不动；原文件备份为 .orig。
"""
import os
import shutil
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import inotia_dat

PATH = r"D:\Desktop\inotia4\_apk\assets\common\game_res\memorytext_zhhans.dat.jpg"
BAK = PATH + ".orig"
NEW_ABILITY = "能力点已返还"
NEW_SKILL = "技能点已返还"

TARGETS = [
    (35456, "能力点已被初始化。", NEW_ABILITY),
    (35457, "技能点已被初始化。", NEW_SKILL),
]


def main():
    if not os.path.exists(BAK):
        shutil.copy(PATH, BAK)
        print("备份 ->", BAK)

    payload = inotia_dat.load(BAK)
    texts = inotia_dat.strings(payload)
    print("文本条数:", len(texts))

    changed = 0
    for idx, old, new in TARGETS:
        cur = texts[idx]
        if cur == new:
            print("%d 已是「%s」，跳过" % (idx, new))
            continue
        if cur != old:
            raise RuntimeError("%d 处文本不符：%r（期望 %r）" % (idx, cur, old))
        texts[idx] = new
        changed += 1
        print("%d  %r -> %r" % (idx, cur, new))

    if not changed:
        print("无需改动")
        return

    out = inotia_dat.rebuild_strings(texts)
    inotia_dat.save(PATH, out)

    # 复查
    check = inotia_dat.strings(inotia_dat.load(PATH))
    for idx, _old, _new in TARGETS:
        print("复查 %d -> %r" % (idx, check[idx]))
    print("写出", PATH, os.path.getsize(PATH), "字节")


if __name__ == "__main__":
    main()
