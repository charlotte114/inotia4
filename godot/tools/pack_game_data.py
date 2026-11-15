"""把 public/ 下的剧情内容与素材打成一个内容包，供导出时随 APK 分发。

为什么需要这一步：运行时是「进这张图才加载这张图要的素材」，所以 11,756 张 PNG
走 Godot 的导入管线会把编辑器和首包都拖垮（见 AGENTS.md 第 4 节）。这里改成
打一个普通的 zip，导出时作为一个非资源文件原样进包，首次启动解压到应用私有目录。

用法：
    python godot/tools/pack_game_data.py

产物：
    godot/game_data.zip   （已被 .gitignore 忽略，不入库）

条目路径保持 content/… 与 assets/…，与仓库 public/ 下的布局一致，
运行时解压出来的目录结构就和开发时读到的一样。
"""

from __future__ import annotations

import os
import sys
import time
import zipfile

# 已压缩过的格式不再压一遍：PNG/MP3 用存储，纯文本类用 deflate。
STORED_SUFFIXES = {".png", ".jpg", ".jpeg", ".webp", ".mp3", ".ogg", ".wav"}

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
PUBLIC = os.path.join(REPO_ROOT, "public")
OUTPUT = os.path.join(REPO_ROOT, "godot", "game_data.zip")
SOURCE_DIRS = ("content", "assets")

# Godot 的 Android 构建模板。安装模板时生成，重装会覆盖，所以每次打包都检查一遍。
ANDROID_GRADLE = os.path.join(REPO_ROOT, "godot", "android", "build", "build.gradle")


def ensure_android_template() -> None:
    """让 APK 别把内容包再压一遍。

    Android 上被压缩过的 asset 不能随机定位，而运行时是用 ZipReader 读这个包的
    中央目录和各条目，压过就打不开。这行配置只能写在构建模板里。
    """
    if not os.path.isfile(ANDROID_GRADLE):
        print("提示：还没安装 Android 构建模板，导出前请在 Godot 的「项目」菜单里装一次。")
        return

    with open(ANDROID_GRADLE, encoding="utf-8") as handle:
        text = handle.read()

    if 'noCompress "zip"' in text:
        print("构建模板：已配置 zip 不压缩")
        return

    lines = text.splitlines()
    for index, line in enumerate(lines):
        if line.strip().startswith("aaptOptions"):
            indent = line[: len(line) - len(line.lstrip())] + "    "
            lines.insert(index + 1, indent + 'noCompress "zip"')
            break
    else:
        print("警告：构建模板里找不到 aaptOptions，请手工加上 noCompress \"zip\"")
        return

    with open(ANDROID_GRADLE, "w", encoding="utf-8", newline="\n") as handle:
        handle.write("\n".join(lines) + "\n")

    print("构建模板：已补上 noCompress \"zip\"")



def collect() -> list[tuple[str, str]]:
    """返回 (磁盘绝对路径, 包内相对路径) 的列表。"""
    entries: list[tuple[str, str]] = []
    for name in SOURCE_DIRS:
        base = os.path.join(PUBLIC, name)
        if not os.path.isdir(base):
            sys.exit(f"找不到目录：{base}")
        for current, _dirs, files in os.walk(base):
            for file_name in files:
                absolute = os.path.join(current, file_name)
                relative = os.path.relpath(absolute, PUBLIC).replace(os.sep, "/")
                entries.append((absolute, relative))
    entries.sort(key=lambda item: item[1])
    return entries


def main() -> None:
    ensure_android_template()

    entries = collect()
    if not entries:
        sys.exit("没有任何文件可打包")

    print(f"源目录：{PUBLIC}")
    print(f"待打包：{len(entries)} 个文件")

    started = time.time()
    stored = 0
    compressed = 0

    with zipfile.ZipFile(OUTPUT, "w", allowZip64=True) as archive:
        for index, (absolute, relative) in enumerate(entries, start=1):
            suffix = os.path.splitext(absolute)[1].lower()
            if suffix in STORED_SUFFIXES:
                archive.write(absolute, relative, zipfile.ZIP_STORED)
                stored += 1
            else:
                archive.write(absolute, relative, zipfile.ZIP_DEFLATED, compresslevel=6)
                compressed += 1
            if index % 2000 == 0:
                print(f"  {index}/{len(entries)}")

    size = os.path.getsize(OUTPUT)
    elapsed = time.time() - started
    print()
    print(f"产物：{OUTPUT}")
    print(f"大小：{size / 1024 / 1024:.1f} MB")
    print(f"条目：{len(entries)}（存储 {stored}、压缩 {compressed}）")
    print(f"耗时：{elapsed:.1f} 秒")


if __name__ == "__main__":
    main()
