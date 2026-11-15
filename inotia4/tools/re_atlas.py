# -*- coding: utf-8 -*-
"""探测 game_res 里 i_*.dat（图集/精灵索引表）的记录布局。

思路：载荷 = [头 H 字节][N × recsize]，用 (len - H) % N == 0 穷举 H，
再按"第一条记录里的字段应落在合理范围"打分，输出候选。
"""
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import inotia_dat as D

RES = r"D:\Desktop\inotia4\_apk\assets\common\game_res"


def probe(name):
    p = os.path.join(RES, name + ".dat.jpg")
    blob = open(p, "rb").read()
    payload = D.unpack(blob)
    n = struct.unpack_from("<I", payload, 0)[0]
    total = len(payload)
    cands = []
    for h in range(4, 64):
        body = total - h
        if body <= 0 or body % n:
            continue
        rs = body // n
        if not 2 <= rs <= 512:
            continue
        # 打分：记录首字段应是递增 id 或小整数
        first = struct.unpack_from("<H", payload, h)[0]
        second = struct.unpack_from("<H", payload, h + 2)[0] if rs >= 4 else 0
        score = 0
        if first < 2000:
            score += 2
        if h % 4 == 0:
            score += 1
        if second and second < 4096:
            score += 1
        cands.append((score, h, rs, n, first, second))
    cands.sort(reverse=True)
    print("%-16s payload=%7d count=%5d  候选(hdr,recsize): %s"
          % (name, total, n, [(c[1], c[2]) for c in cands[:4]]))
    if cands:
        s, h, rs, cnt, f1, f2 = cands[0]
        print("   首选 hdr=%d rec=%dB 首记录: %s"
              % (h, rs, payload[h:h + min(rs, 32)].hex(" ")))
        for i in range(3):
            o = h + i * rs
            print("   [%d] %s" % (i, payload[o:o + min(rs, 32)].hex(" ")))
    return payload, n


if __name__ == "__main__":
    for nm in ["i_npc", "i_item", "i_player", "i_monster", "i_monster_boss", "i_tile",
               "i_portrait", "i_title", "i_worldmap", "i_skillicon", "i_effect",
               "i_effect2", "i_efx", "i_imgfont", "i_imoticon", "i_map_image",
               "i_mapfeature", "i_sign", "i_ui_gamemenu"]:
        probe(nm)
