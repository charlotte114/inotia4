# -*- coding: utf-8 -*-
"""
艾诺迪亚4 任务奖励 / NPC 商店 编辑器

字段结构（逆向自 libgame.so）:

QUESTREWARDBASE  394 条 × 5 字节（由 QUESTSYSTEM_ApplyReward 0x0bd3a2 处的循环确认）
    +0 u16   物品索引 —— **不是物品 ID，真实物品 ID = 该值 + 30**
             0 -> 金币(30) / 15 -> 卓越灵药(45) / 4 -> 背包·大(34) / 567 -> 贝勒塞骑士团的板甲(597)
             依据：奖励表出现过的 94 个不同值，全部 +30 后 100% 落在 ITEMDATABASE 内；
             不做偏移只有 74/94 命中（多为巧合）。DEALINFOBASE 的 type0 p1 同理。
    +2 u16   数量
    +4 u8    难度掩码，bit(d) 表示第 d 难度才发这条奖励（0x3F = 六档全发）

QUESTINFOBASE    507 条 × 32 字节（同上函数确认）
    +0x0a u16 经验奖励
    +0x0d s8  当前难度位索引（mask = 1 << 该值）
    +0x17 u16 提交物品起始索引
    +0x19 u8  提交物品条数
    +0x1a u16 奖励起始索引
    +0x1c u8  奖励条数
    → 任务 i 的奖励 = QUESTREWARDBASE[起始, 起始+条数)

DEALINFOBASE     78 条 × 8 字节
    +0 u16   NPC id
    +2 u8    货架槽位（0..47，超出会被 AddSaleDirect 直接丢弃）
    +3 u8    交易类型（0..8）
    +4 u16   参数1
    +6 u16   参数2

交易类型（DEALSYSTEM_MakeSale 的 TBH 跳转表，目标 = PC + 半字*2）:
    0 -> 0xa51ea  DEALSYSTEM_AddSaleAsItemID(p1)
                  -> ITEMSYSTEM_CreatePerfectItem(p1) -> AddSale
                  **p1 就是物品模板 ID，这一支就是"直接卖指定物品"**
    1 -> 0xa51de  另一套循环分支
    2,3,4,6,7 -> 0xa55a2  直接返回，无效类型
    5 -> 0xa535c  仅当第二个参数为 4 或 6 时生效（特殊商人）
    8 -> 0xa52a0  用 MATH_GetRandom 两次，按进度区间随机取货（随机商人）
"""
import struct
import inotia_dat as T

QUEST_REWARD_SIZE = 5
DEAL_SIZE = 8


# ---------------------------------------------------------------- 任务奖励

def quest_rewards(payload):
    """-> [(索引, 物品ID, 数量, 难度掩码), ...]"""
    recs, size, _ = T.sub_table(payload, "QUESTREWARDBASE")
    return [(i, struct.unpack_from("<H", r, 0)[0],
             struct.unpack_from("<H", r, 2)[0], r[4]) for i, r in enumerate(recs)]


def set_quest_reward(payload, index, item_id=None, qty=None, mask=None):
    """改单条奖励记录"""
    recs, size, _ = T.sub_table(payload, "QUESTREWARDBASE")
    r = bytearray(recs[index])
    if item_id is not None:
        struct.pack_into("<H", r, 0, item_id)
    if qty is not None:
        struct.pack_into("<H", r, 2, qty)
    if mask is not None:
        r[4] = mask
    recs[index] = bytes(r)
    return T.set_sub_table(payload, "QUESTREWARDBASE", recs)


def set_quest_reward_of(payload, quest_index, item_id=None, qty=None, mask=None):
    """按任务改奖励（改该任务的第一条奖励记录，最常用）"""
    qs, _, _ = T.sub_table(payload, "QUESTINFOBASE")
    start = struct.unpack_from("<H", qs[quest_index], 0x1A)[0]
    return set_quest_reward(payload, start, item_id, qty, mask)


def quest_detail(payload, quest_index):
    """按任务查它的奖励列表 -> (经验, [(物品ID, 数量, 难度掩码), ...])"""
    qs, _, _ = T.sub_table(payload, "QUESTINFOBASE")
    recs, _, _ = T.sub_table(payload, "QUESTREWARDBASE")
    q = qs[quest_index]
    exp = struct.unpack_from("<H", q, 0x0A)[0]
    start = struct.unpack_from("<H", q, 0x1A)[0]
    count = q[0x1C]
    out = []
    for k in range(start, min(start + count, len(recs))):
        out.append((struct.unpack_from("<H", recs[k], 0)[0],
                    struct.unpack_from("<H", recs[k], 2)[0], recs[k][4]))
    return exp, out


# ---------------------------------------------------------------- NPC 商店

def deals(payload):
    """-> [(NPCC id, 槽位, 类型, 参数1, 参数2), ...]"""
    recs, size, _ = T.sub_table(payload, "DEALINFOBASE")
    out = []
    for r in recs:
        out.append((struct.unpack_from("<H", r, 0)[0], r[2], r[3],
                    struct.unpack_from("<H", r, 4)[0],
                    struct.unpack_from("<H", r, 6)[0]))
    return out


def make_deal(npc, slot, item_id, deal_type=0, param2=0):
    """造一条货架记录。deal_type=0 表示直接卖 item_id 这个物品。"""
    return (struct.pack("<H", npc) + bytes([slot & 0xFF, deal_type & 0xFF])
            + struct.pack("<H", item_id) + struct.pack("<H", param2))


def add_deal(payload, npc, slot, item_id, deal_type=0, param2=0):
    recs, size, _ = T.sub_table(payload, "DEALINFOBASE")
    recs.append(make_deal(npc, slot, item_id, deal_type, param2))
    return T.set_sub_table(payload, "DEALINFOBASE", recs)


def set_deal(payload, index, **kw):
    """改第 index 条货架。kw 可含 npc/slot/type/p1/p2"""
    recs, size, _ = T.sub_table(payload, "DEALINFOBASE")
    npc, slot, typ, p1, p2 = deals(payload)[index]
    recs[index] = make_deal(kw.get("npc", npc), kw.get("slot", slot),
                            kw.get("p1", p1), kw.get("type", typ),
                            kw.get("p2", p2))
    return T.set_sub_table(payload, "DEALINFOBASE", recs)


# ---------------------------------------------------------------- 物品价格

ITEM_PRICE_OFF = 12        # ITEMDATABASE 记录内 u16 价格字段偏移

"""
ITEMDATABASE     1018 条 × 23 字节
    +0  u16  物品 ID（30..1047 连续；**物品 ID == memorytext 索引**，可直接查中文名）
    +2  u16  图集/分类索引
    +12 u16  **价格（金币）**  —— 依据：30金币=0 / 31手提包=90 / 32背包小=540 /
             33背包中=3300 / 34背包大=4300 / 35药水小=15 / 45卓越灵药=3600，
             严格随等级递增；全表仅 81 条非 0，其余 937 条（含全部 928 件装备）为 0，
             说明原版设计上商店只卖消耗品与背包，装备不上架。
    +16 u16  卖价相关（多数为 0）
    +18..22  装备属性（攻击/防御等；非装备行为 0xFF 填充）

把价格改成 1 即等同于"商店随便买"：购买时按 +12 扣金币，改小不会破坏结构。
"""


def item_prices(payload):
    """-> {物品ID: 价格}"""
    recs, size, _ = T.sub_table(payload, "ITEMDATABASE")
    return {struct.unpack_from("<H", r, 0)[0]:
            struct.unpack_from("<H", r, ITEM_PRICE_OFF)[0] for r in recs}


def set_item_price(payload, item_id, price):
    """改单个物品的价格"""
    recs, size, _ = T.sub_table(payload, "ITEMDATABASE")
    hit = 0
    for i, r in enumerate(recs):
        if struct.unpack_from("<H", r, 0)[0] == item_id:
            b = bytearray(r)
            struct.pack_into("<H", b, ITEM_PRICE_OFF, price & 0xFFFF)
            recs[i] = bytes(b)
            hit += 1
            break
    if not hit:
        raise KeyError(f"物品 {item_id} 不在 ITEMDATABASE 中")
    return T.set_sub_table(payload, "ITEMDATABASE", recs)


def set_all_prices(payload, price=1, only_nonzero=False):
    """批量改价。only_nonzero=True 时只改原本有价格的那批（消耗品/背包）。

    返回 (新载荷, 改动条数)
    """
    recs, size, _ = T.sub_table(payload, "ITEMDATABASE")
    n = 0
    for i, r in enumerate(recs):
        old = struct.unpack_from("<H", r, ITEM_PRICE_OFF)[0]
        if only_nonzero and old == 0:
            continue
        if old == price:
            continue
        b = bytearray(r)
        struct.pack_into("<H", b, ITEM_PRICE_OFF, price & 0xFFFF)
        recs[i] = bytes(b)
        n += 1
    return T.set_sub_table(payload, "ITEMDATABASE", recs), n


# ---------------------------------------------------------------- 演示

if __name__ == "__main__":
    src = "game.dat.jpg"
    payload, compressed = T.load_pair(src)

    print("=== 有商店的 NPC（类型 0/1/5/8）===")
    for i, d in enumerate(deals(payload)):
        if d[2] in (0, 1, 5, 8):
            print(f"  [{i:>2}] npc={d[0]:<4} 槽={d[1]} 型={d[2]} p1={d[3]:<5} p2={d[4]}")

    print("\n=== 任务奖励前 10 条 ===")
    for i, item, amt in quest_rewards(payload)[:10]:
        print(f"  [{i:>3}] 物品={item:<5} 标记={amt}")

    # 演示改动：给 npc 2 加一个槽位，直接卖物品 500（类型 0 = 按物品ID上架）
    new = add_deal(payload, npc=2, slot=7, item_id=500)
    check = deals(new)[-1]
    print(f"\n追加后最后一条: npc={check[0]} 槽={check[1]} 型={check[2]} p1={check[3]}")

    # 只写副本，不动原文件
    T.save("game.dat.mod.jpg", new, compressed)
    print(f"已写出 game.dat.mod.jpg（{len(new)} 字节载荷）")
