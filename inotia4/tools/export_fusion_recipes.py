"""把 game.dat 里的融合器配方表导出成 public/content/fusion.json。

原版数据结构（`inotia_dat.py` 已能按名取表）：

- `RECIPEBASE`   12 字节/条：配方名文本、产物物品、材料起始下标、材料种类数、
                1 字节恒为 1、配方编号、1 字节恒为 1、菜单掩码
- `MIXTUREBASE`   3 字节/条：物品编号、数量

材料表是「平铺」的：每条配方只记起始下标和种类数，下一条的起始下标就是上一条的
终点。用相邻两条的差值交叉验证过 69 条，全部吻合。

菜单归属（掩码值）由配方本身的内容判定，不依赖 UI 侧的 tab 顺序：
3 = 混沌合成（混沌 / 深渊混沌两条），4 = 药水合成，8 = 宝石强化，
17 = 宝石孔生成，32 = 传说装备。

产物为「装备」（ITEMDATABASE 64，一条没有具体属性的占位条目）的配方，表示
不是产出一件新物品，而是改造玩家选中的那件装备（混沌改数值、宝石孔加孔）。
"""
import collections
import json
import os
import struct
import sys

TOOLS = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, TOOLS)
import inotia_dat as T

ROOT = os.path.dirname(os.path.dirname(TOOLS))
TEXTS_PATH = os.path.join(ROOT, "inotia4", "data", "memorytext_zhhans.txt")
GAME_DAT = os.path.join(ROOT, "inotia4", "_apk", "assets", "common", "game_res", "game.dat.jpg")
OUTPUT = os.path.join(ROOT, "public", "content", "fusion.json")
ITEMS_JSON = os.path.join(ROOT, "public", "content", "items.json")

MENU_BY_MASK = {
    3: "chaos",
    4: "potion",
    8: "gem",
    17: "socket",
    32: "legendary",
}

MENUS = [
    {"id": "potion", "name": "药水合成"},
    {"id": "gem", "name": "宝石强化"},
    {"id": "socket", "name": "宝石孔生成"},
    {"id": "chaos", "name": "混沌合成"},
    {"id": "legendary", "name": "传说装备"},
]

# 产物是这条占位物品时，表示改造玩家选中的装备。
TARGET_EQUIPMENT = 64


def load_texts():
    texts = {}
    with open(TEXTS_PATH, encoding="utf-8") as handle:
        for line in handle:
            if "\t" in line:
                key, value = line.rstrip("\n").split("\t", 1)
                if key.isdigit():
                    texts[int(key)] = value
    return texts


def main():
    texts = load_texts()
    payload = T.load(GAME_DAT)
    items, _, _ = T.sub_table(payload, "ITEMDATABASE")
    recipes, _, _ = T.sub_table(payload, "RECIPEBASE")
    mixtures, _, _ = T.sub_table(payload, "MIXTUREBASE")

    def item_name(index):
        return texts[struct.unpack_from("<H", items[index], 0)[0]]

    with open(ITEMS_JSON, encoding="utf-8") as handle:
        content = json.load(handle)["items"]

    by_name = collections.defaultdict(list)
    for slug, entry in content.items():
        by_name[entry["name"]].append(slug)

    def slug_of(index):
        name = item_name(index)
        hits = by_name.get(name, [])
        if len(hits) != 1:
            raise SystemExit(f"物品「{name}」在 items.json 里对应 {len(hits)} 条，无法唯一映射")
        return hits[0]

    rows = []
    for index, record in enumerate(recipes):
        name_text, product, start = struct.unpack_from("<3H", record, 0)
        count = record[6]
        recipe_id = struct.unpack_from("<H", record, 8)[0]
        mask = record[11]

        menu = MENU_BY_MASK.get(mask)
        if menu is None:
            raise SystemExit(f"配方 {index} 的菜单掩码 {mask} 没有映射")

        materials = []
        for step in range(count):
            entry = mixtures[start + step]
            materials.append({
                "item": slug_of(struct.unpack_from("<H", entry, 0)[0]),
                "quantity": entry[2],
            })

        rows.append({
            "id": f"recipe-{recipe_id}",
            "recipeId": recipe_id,
            "menu": menu,
            "name": texts[name_text],
            "target": "equipment" if product == TARGET_EQUIPMENT else None,
            "product": None if product == TARGET_EQUIPMENT else slug_of(product),
            "materials": materials,
            "start": start,
            "count": count,
        })

    # 材料表是平铺的，69 条配方必须正好铺满 MIXTUREBASE，不重不漏。
    spans = sorted((row["start"], row["count"], row["recipeId"]) for row in rows)
    cursor = 0
    for start, count, recipe_id in spans:
        if start != cursor:
            raise SystemExit(f"配方 {recipe_id} 的材料区间从 {start} 开始，但上一条结束在 {cursor}")
        cursor += count
    if cursor != len(mixtures):
        raise SystemExit(f"配方铺到 {cursor}，材料表却有 {len(mixtures)} 条")

    for row in rows:
        row.pop("start")
        row.pop("count")

    document = {
        "version": 1,
        "source": "game.dat RECIPEBASE + MIXTUREBASE（原始物品编号按名称映射到 items.json）",
        "menus": MENUS,
        "recipes": rows,
    }

    with open(OUTPUT, "w", encoding="utf-8", newline="\n") as handle:
        json.dump(document, handle, ensure_ascii=False, indent=2)
        handle.write("\n")

    counts = collections.Counter(row["menu"] for row in rows)
    print(f"写出 {OUTPUT}：{len(rows)} 条配方")
    for menu in MENUS:
        print(f"  {menu['name']:6} {counts[menu['id']]:2} 条")


if __name__ == "__main__":
    main()
