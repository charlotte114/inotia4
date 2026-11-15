# -*- coding: utf-8 -*-
"""
把改好的 game.dat.jpg 塞回 APK，然后 zipalign + apksigner 重签。

用法:
    python repack_apk.py
"""
import os
import shutil
import subprocess
import sys
import zipfile

ROOT = r"D:\Desktop\inotia4"
SRC_APK = os.path.join(ROOT, "艾诺迪亚4_1.2.9_cat.apk")
NEW_APK = os.path.join(ROOT, "艾诺迪亚4_1.2.9_cat_final.apk")
PATCH_FILE = os.path.join(ROOT, "_apk", "assets", "common", "game_res", "game.dat.jpg")
TARGET = "assets/common/game_res/game.dat.jpg"
STRIP_INTERNET = False     # 实测：去掉网络权限会让游戏启动卡死，保持原样

# 删除 FirebaseInitProvider：它会在 Application.onCreate 之前初始化 Firebase，
# 去连 172.217.x.x(Google) 卡 SYN 重传 ~20 秒，这是启动慢的真正原因
MANIFEST_PATCH = None      # 实测无效：GMS 库自身仍会连 Google

# 把 libgame.so 里的服务器域名改成不存在的 TLD：
#   inotia4.com2us.net 现在解析到 34.64.102.23 但服务器已下线，connect() 会卡在 SYN 重传 ~20 秒
#   改成 .nex 后 DNS 立即 NXDOMAIN，启动从 21.5s 降到 1s 量级
SO_TARGET = "lib/armeabi-v7a/libgame.so"
SO_FILE = os.path.join(ROOT, "_apk", "lib", "armeabi-v7a", "libgame.ui.so")
PATCH_SO = True            # 删掉 UIPlay 左下竖排中间的「收费商店」按钮

# classes.dex：把 NetworkTimeChecker 的超时从 5000ms 改成 1000ms 且不重试
#   （原逻辑 5s+10s+15s 三次连接超时 = 那 20 秒）
# 额外替换的 assets（改过的图集等）
ASSET_REPLACE = {
    "assets/common/game_res/UI_TITLEMAIN_01.png.jpg":
        os.path.join(ROOT, "_apk", "assets", "common", "game_res", "UI_TITLEMAIN_01.png.jpg"),
    "assets/common/game_res/memorytext_zhhans.dat.jpg":
        os.path.join(ROOT, "_apk", "assets", "common", "game_res", "memorytext_zhhans.dat.jpg"),
}

DEX_TARGET = "classes.dex"
DEX_FILE = os.path.join(ROOT, "_apk", "classes_final_patched.dex")
PATCH_DEX = True          # qpyou.cn -> qpyou.zz：DNS 立即 NXDOMAIN，消除 DeviceIdentity 的 20s 读阻塞

BT = r"D:\Android\SDK\build-tools\35.0.0"
ZIPALIGN = os.path.join(BT, "zipalign.exe")
APKSIGNER = os.path.join(BT, "apksigner.bat")

# apksigner.bat 需要 java，系统 PATH 里只有 java 的 shim，这里显式指向 Android Studio 自带的 JBR
JAVA_HOME = r"D:\Android\Android Studio\jbr"
KEYTOOL = os.path.join(JAVA_HOME, "bin", "keytool.exe")

ENV = dict(os.environ)
ENV["JAVA_HOME"] = JAVA_HOME
ENV["PATH"] = os.path.join(JAVA_HOME, "bin") + os.pathsep + ENV.get("PATH", "")
KEYSTORE = os.path.join(ROOT, "mod.keystore")
KS_PASS = "inotia123"
KS_ALIAS = "inotia"


def step(msg):
    print("\n" + "=" * 60)
    print(msg)
    print("=" * 60)


def rebuild_zip():
    """逐条复制原 APK 条目，只替换 TARGET（+ 可选改 manifest），保持原压缩方式"""
    new_data = open(PATCH_FILE, "rb").read()
    print(f"补丁文件: {len(new_data)} 字节")

    zin = zipfile.ZipFile(SRC_APK, "r")

    # 准备 manifest 补丁
    manifest_patch = None
    if MANIFEST_PATCH and os.path.exists(MANIFEST_PATCH):
        manifest_patch = open(MANIFEST_PATCH, "rb").read()
        print(f"使用删掉 FirebaseInitProvider 的 manifest: {len(manifest_patch)} 字节")
    elif STRIP_INTERNET:
        import patch_manifest as PM
        raw = zin.read("AndroidManifest.xml")
        patched, ok = PM.patch(raw)
        if ok:
            manifest_patch = patched
        else:
            print("警告: manifest 里没找到 INTERNET 权限，跳过")

    tmp = NEW_APK + ".raw"
    zout = zipfile.ZipFile(tmp, "w", allowZip64=True)

    n_rep = 0
    so_done = False
    dex_done = False
    for info in zin.infolist():
        if info.filename == TARGET:
            zi = zipfile.ZipInfo(info.filename, date_time=info.date_time)
            zi.compress_type = zipfile.ZIP_STORED      # 必须是 STORED，游戏按偏移直读
            zi.external_attr = info.external_attr
            zout.writestr(zi, new_data)
            n_rep += 1
        elif info.filename == DEX_TARGET and PATCH_DEX and os.path.exists(DEX_FILE):
            zi = zipfile.ZipInfo(info.filename, date_time=info.date_time)
            zi.compress_type = info.compress_type
            zi.external_attr = info.external_attr
            zout.writestr(zi, open(DEX_FILE, "rb").read())
            n_rep += 1
            dex_done = True
        elif info.filename == SO_TARGET and PATCH_SO and os.path.exists(SO_FILE):
            zi = zipfile.ZipInfo(info.filename, date_time=info.date_time)
            zi.compress_type = info.compress_type
            zi.external_attr = info.external_attr
            zout.writestr(zi, open(SO_FILE, "rb").read())
            n_rep += 1
            so_done = True
        elif info.filename in ASSET_REPLACE and os.path.exists(ASSET_REPLACE[info.filename]):
            zi = zipfile.ZipInfo(info.filename, date_time=info.date_time)
            zi.compress_type = info.compress_type
            zi.external_attr = info.external_attr
            zout.writestr(zi, open(ASSET_REPLACE[info.filename], "rb").read())
            n_rep += 1
        elif info.filename == "AndroidManifest.xml" and manifest_patch is not None:
            zi = zipfile.ZipInfo(info.filename, date_time=info.date_time)
            zi.compress_type = info.compress_type
            zi.external_attr = info.external_attr
            zout.writestr(zi, manifest_patch)
            n_rep += 1
        else:
            zi = zipfile.ZipInfo(info.filename, date_time=info.date_time)
            zi.compress_type = info.compress_type
            zi.external_attr = info.external_attr
            zout.writestr(zi, zin.read(info.filename))

    zout.close()
    zin.close()
    print(f"替换 {n_rep} 处（dex={dex_done}, so={so_done}），写出中间包 {os.path.basename(tmp)}")
    return tmp


def do_align(tmp):
    out = NEW_APK
    if os.path.exists(out):
        os.remove(out)
    r = subprocess.run([ZIPALIGN, "-p", "-f", "4", tmp, out],
                       capture_output=True, text=True, env=ENV)
    print("zipalign rc =", r.returncode)
    if r.stdout.strip():
        print(r.stdout.strip()[:500])
    if r.stderr.strip():
        print(r.stderr.strip()[:500])
    os.remove(tmp)
    return r.returncode == 0


def do_sign(apk):
    if not os.path.exists(KEYSTORE):
        print("生成签名证书...")
        r = subprocess.run([
            KEYTOOL, "-genkeypair", "-keystore", KEYSTORE,
            "-alias", KS_ALIAS, "-keyalg", "RSA", "-keysize", "2048",
            "-validity", "10000", "-storepass", KS_PASS, "-keypass", KS_PASS,
            "-dname", "CN=InotiaMod, O=Mod, C=CN",
        ], capture_output=True, text=True, env=ENV)
        print("keytool rc =", r.returncode)
        if r.returncode != 0:
            print(r.stdout[-800:], r.stderr[-800:])
    else:
        print("复用已有证书:", KEYSTORE)

    r = subprocess.run([
        APKSIGNER, "sign",
        "--ks", KEYSTORE, "--ks-pass", "pass:" + KS_PASS,
        "--ks-key-alias", KS_ALIAS, "--key-pass", "pass:" + KS_PASS,
        "--v1-signing-enabled", "true", "--v2-signing-enabled", "true",
        "--min-sdk-version", "15",
        apk,
    ], capture_output=True, text=True, env=ENV)
    print("apksigner rc =", r.returncode)
    if r.stdout.strip():
        print(r.stdout.strip()[:600])
    if r.stderr.strip():
        print(r.stderr.strip()[:600])
    return r.returncode == 0


def verify(apk):
    r = subprocess.run([APKSIGNER, "verify", "--verbose", "--print-certs", apk],
                       capture_output=True, text=True, env=ENV)
    print("verify rc =", r.returncode)
    print(r.stdout.strip()[:900])
    if r.stderr.strip():
        print(r.stderr.strip()[:400])
    return r.returncode == 0


def main():
    for p in (SRC_APK, PATCH_FILE):
        if not os.path.exists(p):
            sys.exit("缺少文件: " + p)

    step("1/4 重建 ZIP")
    tmp = rebuild_zip()

    step("2/4 zipalign")
    if not do_align(tmp):
        sys.exit("zipalign 失败")

    step("3/4 签名")
    if not do_sign(NEW_APK):
        sys.exit("签名失败")

    step("4/4 校验")
    verify(NEW_APK)

    # 确认包内数据确实替换了
    z = zipfile.ZipFile(NEW_APK)
    i = z.getinfo(TARGET)
    print(f"\n包内 {TARGET}: {i.file_size} 字节, "
          f"{'STORED' if i.compress_type == 0 else 'DEFLATED'}")
    print(f"输出: {NEW_APK}  ({os.path.getsize(NEW_APK)} 字节)")


if __name__ == "__main__":
    main()
