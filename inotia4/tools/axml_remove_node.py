# -*- coding: utf-8 -*-
"""
从 AndroidManifest.xml(AXML) 里删除指定 provider 节点

原理：AXML 的每个 chunk 自带 size，START_TAG 的 chunk size 只包含自身（16 + 属性数*20），
不包含子节点。所以删掉一段完整子树后，只要改文件头 offset=4 的总 size 即可，
StringPool / ResourceMap 全都不用动（多余的字符串没人引用不报错）。

FirebaseInitProvider 会在 Application.onCreate 之前由系统自动初始化，
拉起 Firebase 去连 Google 服务器；国内 SYN 不可达会卡约 20 秒。
"""
import struct
import sys

RES_XML_TYPE = 0x0003
START_TAG = 0x0102
END_TAG = 0x0103


def chunks(d):
    """返回 [(offset, type, header_size, size, name_index)]，只解析顶层 chunk"""
    off = 8
    out = []
    while off + 8 <= len(d):
        ctype, chsize, csz = struct.unpack_from("<HHI", d, off)
        name_idx = None
        if ctype in (START_TAG, END_TAG):
            # ResXMLTree_node: header(16) 后是 ResXMLTree_attrExt/endExt，name 在 header 之后
            name_idx = struct.unpack_from("<I", d, off + 20)[0]
        out.append((off, ctype, chsize, csz, name_idx))
        if csz == 0:
            break
        off += csz
    return out


def string_at(d, pool_off, pool_hdr, idx):
    sc, stc, flags, sstart, sstyles = struct.unpack_from("<IIIII", d, pool_off + 8)
    off_table = pool_off + pool_hdr
    so = struct.unpack_from("<I", d, off_table + idx * 4)[0]
    base = pool_off + sstart
    p = base + so
    if flags & 0x100:                       # UTF-8
        l = d[p]; p += 2 if (l & 0x80) else 1
        l2 = d[p]; p += 1
        if l2 & 0x80:
            l2 = ((l2 & 0x7F) << 8) | d[p]; p += 1
        return d[p:p + l2].decode("utf-8", "replace")
    l = struct.unpack_from("<H", d, p)[0]; p += 2
    if l & 0x8000:
        l2 = struct.unpack_from("<H", d, p)[0]; p += 2
        l = ((l & 0x7FFF) << 16) | l2
    return d[p:p + l * 2].decode("utf-16-le", "replace")


def tag_name(d, pool, off):
    name_idx = struct.unpack_from("<I", d, off + 20)[0]
    return string_at(d, pool[0], pool[1], name_idx)


def remove_node(d, target):
    """删掉 <target ...> ... </target> 整段，返回新 bytes"""
    d = bytearray(d)
    cs = chunks(d)
    pool = next(((o, h, s) for o, t, h, s, _ in cs if t == 0x0001), None)
    if pool is None:
        raise ValueError("没找到 StringPool")

    start = end = None
    base_depth = None
    depth = 0
    for o, t, h, s, _ in cs:
        if t == START_TAG:
            if start is None and tag_name(d, pool, o) == target:
                start = o
                base_depth = depth          # 该节点所处的父层深度
            depth += 1
        elif t == END_TAG:
            depth -= 1
            if start is not None and depth == base_depth:
                end = o + s
                break
    if start is None or end is None:
        return bytes(d), False

    out = d[:start] + d[end:]
    struct.pack_into("<I", out, 4, len(out))   # 更新文件总 size
    print(f"删除节点 <{target}>：原 {len(d)} 字节 -> {len(out)} 字节（去掉 {end - start}）")
    return bytes(out), True


def main(src, dst, target="provider"):
    with open(src, "rb") as f:
        data = f.read()
    out, ok = remove_node(data, target)
    if not ok:
        sys.exit(f"没找到 <{target}> 节点")
    with open(dst, "wb") as f:
        f.write(out)
    print("写出", dst)


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2], sys.argv[3] if len(sys.argv) > 3 else "provider")
