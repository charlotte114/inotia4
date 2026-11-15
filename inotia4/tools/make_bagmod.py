# -*- coding: utf-8 -*-
"""
给「第一个主线任务」的奖励里追加一个 背包（大）

【重要】奖励表里的物品字段不是直接写物品 ID，而是写了 `物品ID - 30`
    表内 0   -> 物品 30  金币
    表内 15  -> 物品 45  卓越灵药
    表内 4   -> 物品 34  背包（大）
  依据：奖励表里出现过的 94 个不同值，全部 +30 后 100% 落在 ITEMDATABASE 的 ID 集合内，
  而不做偏移只有 74/94 命中。DEALINFOBASE 的 type0 p1 同样适用（值域 1..27 -> 物品 31..57）。

做法（零副作用）:
  1. 把该任务原有的奖励记录整段复制到 QUESTREWARDBASE 末尾
  2. 再追加一条 背包（大）× 1，难度掩码 0x3F（六档全发）
  3. 只改这个任务的 奖励起始 / 奖励条数，指向新的末段
  这样其它任务引用的旧区间完全没动，不会有任何偏移连锁。
"""
import sys
import struct
import shutil
import os

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import inotia_dat as T

OFFSET = 30                # 表内值 + 30 = 真实物品 ID
ITEM_BAG_BIG = 34          # 真实物品 ID：memorytext[34] = 背包（大）
MASK_ALL = 0x3F            # 六档难度全发
QUEST_INDEX = 2            # QUESTINFOBASE[2] = 刺杀格里普顿伯爵（游戏里第一个主线）

SRC = "assets/common/game_res/game.dat.jpg"
BAK = "assets/common/game_res/game.dat.orig.jpg"


def real_item(v):
    return v + OFFSET


def main():
    payload, comp = T.load_pair(SRC)

    qs, _, _ = T.sub_table(payload, "QUESTINFOBASE")
    rw, _, rc = T.sub_table(payload, "QUESTREWARDBASE")

    start = struct.unpack_from("<H", qs[QUEST_INDEX], 0x1A)[0]
    count = qs[QUEST_INDEX][0x1C]
    print(f"任务[{QUEST_INDEX}] 原奖励区间: [{start}, {start + count}) 共 {count} 条")

    # 1) 复制原奖励到末尾
    new_start = len(rw)
    rw.extend(rw[start:start + count])

    # 2) 追加背包（大）—— 表内写 34-30=4
    rw.append(struct.pack("<HHB", ITEM_BAG_BIG - OFFSET, 1, MASK_ALL))

    # 3) 让任务指向新段
    q = bytearray(qs[QUEST_INDEX])
    struct.pack_into("<H", q, 0x1A, new_start)
    q[0x1C] = count + 1
    qs[QUEST_INDEX] = bytes(q)

    payload = T.set_sub_table(payload, "QUESTINFOBASE", qs)
    payload = T.set_sub_table(payload, "QUESTREWARDBASE", rw)

    if not os.path.exists(BAK):
        shutil.copy2(SRC, BAK)
        print(f"已备份原表 -> {BAK}")

    T.save(SRC, payload, comp)
    print(f"已写回 {SRC}（载荷 {len(payload)} 字节，奖励表 {len(rw)} 条）")

    # ---------------- 校验 ----------------
    pl2, _ = T.load_pair(SRC)
    txt, _ = T.load_pair("assets/common/game_res/memorytext_zhhans.dat.jpg")
    names = T.strings(txt)
    items, _, _ = T.sub_table(pl2, "ITEMDATABASE")
    ids = {struct.unpack_from("<H", x, 0)[0] for x in items}

    q2, _, _ = T.sub_table(pl2, "QUESTINFOBASE")
    r2, _, rc2 = T.sub_table(pl2, "QUESTREWARDBASE")
    print(f"\n校验: 子表 {len(T.split_container(pl2))} 张, 奖励表 {rc2} 条")

    s2 = struct.unpack_from("<H", q2[QUEST_INDEX], 0x1A)[0]
    n2 = q2[QUEST_INDEX][0x1C]
    title = names[struct.unpack_from("<H", q2[QUEST_INDEX], 2)[0]]
    print(f"任务[{QUEST_INDEX}] {title!r}")
    for k in range(s2, s2 + n2):
        raw = struct.unpack_from("<H", r2[k], 0)[0]
        qt = struct.unpack_from("<H", r2[k], 2)[0]
        rid = real_item(raw)
        flag = "OK" if rid in ids else "!! 不在物品表"
        print(f"   [{k}] 表内{raw:<5} +30={rid:<5} {names[rid] if rid < len(names) else '?'}"
              f" ×{qt}  掩码=0x{r2[k][4]:02x}  {flag}")


if __name__ == "__main__":
    main()
