# -*- coding: utf-8 -*-
"""m*.dat 地图格式完整解析（由 MAP_Load / MAP_LoadBase / MAP_LoadLayer 反推）。

    [0] u8  b0（未用/保留）
    [1] u8  b1（未用/保留）
    [2] u8  w  宽（格）
    [3] u8  h  高（格）
    [4 ..]  w*h × 2B  地形层：tile = ((b0 & 0x0F) << 8) | b1   （12 位 tile id）
    之后是 MAP_LoadLayer：
        u16  layerType
        4 × u16  layer 参数（交给 MAPFEATURESYSTEM_AddLayer）
        u8   blockCount
        每个 block：
            u8   高 4 位 = 目标层(0..3 → AddLayerTile/Shadow1/Shadow2/TopTile)，低 4 位 = ?
            u16   recordCount
            每条 record 4B：
                u8 dx, u8 dy, u8 packed, u8 tileLo
                packed: bit0-2 = tile 高位, bit3 = 0x800 标记, bit4-7 = 类型(0..5)
                tile = ((packed & 7) << 8) | tileLo
"""
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import inotia_dat as D

RES = r"D:\Desktop\inotia4\_apk\assets\common\game_res"
KIND = ["AddLayerTile", "AddShadowTile1", "AddShadowTile2", "AddTopTile"]
RTYPE = ["普通", "普通", "普通", "普通", "NPC/物件", "生成物"]


def parse(name):
    pl = D.unpack(open(os.path.join(RES, name + ".dat.jpg"), "rb").read())
    b0, b1, w, h = pl[0], pl[1], pl[2], pl[3]
    body = pl[4:]
    tiles = [((body[2 * i] & 0x0F) << 8) | body[2 * i + 1] for i in range(w * h)]
    p = 2 * w * h
    layer_type = struct.unpack_from("<H", body, p)[0]
    p += 2
    layers = struct.unpack_from("<4H", body, p)
    p += 8
    blocks = body[p]
    p += 1
    stats = []
    for _ in range(blocks):
        head = body[p]
        p += 1
        cnt = struct.unpack_from("<H", body, p)[0]
        p += 2
        kind = (head >> 4) & 0xF
        p += 4 * cnt
        stats.append((kind, cnt))
    return dict(name=name, w=w, h=h, tiles=w * h, layer_type=layer_type,
                layers=layers, blocks=blocks, stats=stats,
                consumed=p, total=len(body), b0=b0, b1=b1,
                tile_min=min(tiles), tile_max=max(tiles))


if __name__ == "__main__":
    names = sys.argv[1:] or ["m0", "m1", "m100", "m200", "m415"]
    for n in names:
        r = parse(n)
        ok = "OK" if r["consumed"] == r["total"] else "剩余 %d B" % (r["total"] - r["consumed"])
        print("%-5s %2dx%-2d=%-5d格  layerType=%-3d layers=%s  blocks=%d %s  tile %d..%d  %s"
              % (r["name"], r["w"], r["h"], r["tiles"], r["layer_type"], r["layers"],
                 r["blocks"], r["stats"], r["tile_min"], r["tile_max"], ok))
