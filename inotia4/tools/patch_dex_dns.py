"""
把 classes.dex 里 qpyou.cn 这个域名族改成不存在的 TLD（qpyou.zz）。

原因：com.com2us.module.util.DeviceIdentity 的 TARGET_SERVER 是
      http://activeuser.qpyou.cn/gateway.php
      （activeuser.qpyou.cn -> 43.145.18.151，服务器只接 TCP 不回包）
      它只调了 setConnectTimeout，没调 setReadTimeout，所以 TCP 握手成功后
      一直读不到响应，卡到内核超时 —— 这就是启动那 20 秒。
      改成 .zz 后 DNS 立刻 NXDOMAIN，抛异常走原有 catch 分支，启动变快。

只换域名末两字节 cn -> zz，长度不变，dex 结构完全不动，只重算 checksum 与 signature。
"""
import hashlib
import os
import struct
import sys
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "_apk", "classes.dex")
DST = os.path.join(ROOT, "_apk", "classes_patched.dex")

SRC_PAT = b"qpyou.cn"
DST_PAT = b"qpyou.zz"      # 等长替换：.cn -> .zz


def fix_dex_header(d):
    """重算 dex 的 SHA1 signature 和 adler32 checksum"""
    d = bytearray(d)
    sig = hashlib.sha1(d[32:]).digest()
    d[12:32] = sig
    ck = zlib.adler32(bytes(d[12:])) & 0xFFFFFFFF
    struct.pack_into("<I", d, 8, ck)
    return bytes(d)


def verify(d):
    ok = True
    fs = struct.unpack_from("<I", d, 32)[0]
    if fs != len(d):
        print(f"  [X] header file_size={fs} 实际={len(d)}")
        ok = False
    if struct.unpack_from("<I", d, 8)[0] != (zlib.adler32(d[12:]) & 0xFFFFFFFF):
        print("  [X] checksum 不自洽")
        ok = False
    if hashlib.sha1(d[32:]).digest() != d[12:32]:
        print("  [X] signature 不自洽")
        ok = False
    return ok


def main():
    d = open(SRC, "rb").read()
    print(f"原始 classes.dex: {len(d)} 字节")
    n = d.count(SRC_PAT)
    print(f"找到 {SRC_PAT.decode()} 出现 {n} 次")
    if n == 0:
        sys.exit("没找到目标字符串，中止")

    out = d.replace(SRC_PAT, DST_PAT)
    assert len(out) == len(d), "长度变了，危险"

    # 只应改动那 2n 个字节
    diff = sum(1 for a, b in zip(d, out) if a != b)
    print(f"改动字节数: {diff}（应为 {2 * n}）")

    out = fix_dex_header(out)
    print("已重算 checksum + signature")

    print("校验:")
    ok = verify(out)
    print("  [OK] 全部自洽" if ok else "  [X] 有问题")
    if not ok:
        sys.exit(1)

    open(DST, "wb").write(out)
    print(f"已写出 {DST}  ({len(out)} 字节)")

    # 抽样确认替换结果
    p = out.find(b"http://activeuser")
    print("抽样:", out[p:p + 44].decode("ascii", "replace"))


if __name__ == "__main__":
    main()
