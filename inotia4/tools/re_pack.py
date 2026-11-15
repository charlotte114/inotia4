# -*- coding: utf-8 -*-
"""i_*.dat 精灵包（SNASYS pack）完整解析。

布局（由 SNASYS_LoadDataFromMemory @0xc677c 反推，并经 14935/14935 段全部解压验证）：
    [0..3]   u32 count（注意：i_portrait 的这个值 3328 是「精灵总数」，不是段数）
    [4..]    头部若干字节（各包不同，i_npc 14B、i_portrait 15B）
    然后     (n+1) × 3B 偏移；bit23(0x800000) = 该段是 LZMA 压缩
    段 i 的区间 = [(off[i] & 0x7fffff) + delta, (off[i+1] & 0x7fffff) + delta)

段内还有一个 LZMA 容器（13 字节头 + 裸流）：
    [0..4]  5B props（5d 00 00 00 01 = lc3 lp0 pb2 dict16MB）
    [5..8]  u32 解压后长度
    [9..12] u32 保留
    [13..]  裸流
  i_portrait 的段多一个前导 0x00，流从 +14 开始（脚本两种都试）。

实测有三点偏差，脚本自动探测：
  1) 表起点不固定：多数包是 18，i_portrait 是 16；
  2) delta 不固定：多数包 0，i_item/i_npc/i_tile/i_effect/i_efx/i_imoticon/i_portrait 是 1；
  3) 段数不一定等于 u32@0（i_portrait）。
判定标准：掩码后加 delta 必须单调不减，且末值恰好等于文件长度。
"""
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import inotia_dat as D

RES = r"D:\Desktop\inotia4\_apk\assets\common\game_res"
FLAG = 0x800000
MASK = 0x7FFFFF


def find_table(payload):
    """返回 (表起点, delta, 偏移列表)；找不到返回 (None, None, None)

    偏移表项数不一定等于 u32@0 的 count：i_portrait 的 u32@0=3328 是「精灵总数」，
    而表里只有 14 项（13 个 chunk + 1 个末尾哨兵）。所以项数也一起探测：
    对每个 (start, delta) 顺序读 3B 项，一旦某前缀恰好「单调不减且末值 == 文件长度」
    就认为找到了表。
    """
    total = len(payload)
    for delta in (0, 1):
        for start in range(16, 24):
            if start + 3 * 2 >= total:
                continue
            pos = []
            ok = True
            i = 0
            while start + 3 * (i + 1) <= total and i < 8192:
                o = int.from_bytes(payload[start + 3 * i: start + 3 * (i + 1)], "little")
                v = (o & MASK) + delta
                if pos and v < pos[-1]:
                    ok = False
                    break
                pos.append(v)
                if v == total and i >= 1:
                    offs = [int.from_bytes(payload[start + 3 * k: start + 3 * (k + 1)], "little")
                            for k in range(i + 1)]
                    return start, delta, offs
                i += 1
            if not ok:
                continue
    return None, None, None


def unpack_segment(seg):
    """段内 LZMA 容器：13 字节头（props 5B + 解压长度 4B + 保留 4B）+ 裸流。

    i_portrait 的段多一个前导 0x00，流从 +14 开始；两种都试。
    """
    import lzma
    for head, props_at in ((13, 0), (14, 1)):
        props = seg[props_at:props_at + 5]
        if len(props) < 5 or props[0] != 0x5D:
            continue
        prop = props[0]
        ds = int.from_bytes(props[1:5], "little")
        lc, lp, pb = prop % 9, (prop // 9) % 5, prop // 45
        try:
            dec = lzma.LZMADecompressor(
                format=lzma.FORMAT_RAW,
                filters=[{"id": lzma.FILTER_LZMA1, "lc": lc, "lp": lp, "pb": pb,
                          "dict_size": max(ds, 4096)}])
            return dec.decompress(seg[head:])
        except Exception:
            continue
    return None


def segments(name):
    payload = D.unpack(open(os.path.join(RES, name + ".dat.jpg"), "rb").read())
    count = struct.unpack_from("<I", payload, 0)[0]
    start, delta, offs = find_table(payload)
    if offs is None:
        return payload, count, None, None, None, None
    base = start + 3 * (count + 1)
    # 真实段数 = 偏移表项数 - 1（可能小于 u32@0 的 count，见 find_table 注释）
    nseg = len(offs) - 1
    out = []
    for i in range(nseg):
        a = (offs[i] & MASK) + delta
        b = (offs[i + 1] & MASK) + delta
        comp = bool(offs[i] & FLAG)
        seg = payload[a:b]
        if comp:
            seg = unpack_segment(seg)
            if seg is None:
                out.append((i, comp, None, 0))
                continue
        out.append((i, comp, seg, len(seg)))
    return payload, count, start, delta, base, out


if __name__ == "__main__":
    names = ["i_imgfont", "i_item", "i_npc", "i_sign", "i_imoticon", "i_skillicon",
             "i_title", "i_worldmap", "i_portrait", "i_player", "i_monster",
             "i_monster_boss", "i_tile", "i_effect", "i_effect2", "i_efx",
             "i_map_image", "i_mapfeature", "i_ui_gamemenu"]
    tot_seg = tot_comp = tot_raw = 0
    for n in names:
        payload, count, start, delta, base, segs = segments(n)
        if segs is None:
            print("%-15s count=%5d  偏移表定位失败" % (n, count))
            continue
        ok = [s for s in segs if s[2] is not None]
        comp = sum(1 for s in ok if s[1])
        raw = sum(s[3] for s in ok)
        tot_seg += len(segs)
        tot_comp += comp
        tot_raw += raw
        print("%-15s count=%5d  表@%2d delta=%d 数据@%6d  段数=%5d 解出=%5d 压缩=%5d 解压量=%8d"
              % (n, count, start, delta, base, len(segs), len(ok), comp, raw))
    print("\n合计：%d 个段，其中 LZMA 压缩 %d，解压后 %d 字节"
          % (tot_seg, tot_comp, tot_raw))
