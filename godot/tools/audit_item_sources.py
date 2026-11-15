"""审计：任务道具能不能拿到。

对每个 category=quest 的物品，收集内容里所有「产出点」与「消耗点」。
产出点为空且被消耗 = 剧情走不下去（软锁）。
"""
import collections
import json
import os

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
CONTENT = os.path.join(ROOT, "public", "content")


def load(name):
    with open(os.path.join(CONTENT, name), encoding="utf-8") as f:
        return json.load(f)


items = load("items.json")["items"]
drops = load("drops.json")
shops = load("shops.json")
campaign = load("campaign.json")
crafting = load("crafting.json")

granted = collections.defaultdict(list)
taken = collections.defaultdict(list)

# ── 掉落与固定产出 ───────────────────────────────────────────────────────
for enemy, rows in drops.get("questItems", {}).items():
    for row in rows:
        granted[row["item"]].append(f"掉落:{enemy}")

for table_name in ("chests", "breakables"):
    for table_key, table in drops.get(table_name, {}).items():
        for field in ("fixed", "unique"):
            for entry in table.get(field, []) or []:
                if isinstance(entry, dict) and entry.get("item"):
                    granted[entry["item"]].append(f"{table_name}.{field}:{table_key}")

# ── 地图上的任务道具刷新点 ───────────────────────────────────────────────
import glob

map_dir = os.path.join(CONTENT, "maps")
spawn_count = 0
for path in glob.glob(os.path.join(map_dir, "*.json")):
    with open(path, encoding="utf-8") as f:
        map_data = json.load(f)
    for spawn in map_data.get("questItemSpawns", []) or []:
        if spawn.get("item"):
            granted[spawn["item"]].append(f"地图刷点:{map_data.get('id')}")
            spawn_count += 1

# ── 商店固定货 ───────────────────────────────────────────────────────────
for shop_id, shop in shops.get("shops", {}).items():
    for entry in shop.get("fixedStock", []) or []:
        if isinstance(entry, dict) and entry.get("item"):
            granted[entry["item"]].append(f"商店:{shop_id}")

# ── 合成：材料是消耗，产物靠 rule 决定，只记材料 ─────────────────────────
for op_name, op in crafting.get("operations", {}).items():
    for mat in op.get("materials", []) or []:
        taken[mat["item"]].append(f"合成材料:{op_name}")

# ── 剧本命令（item-give / item-take 是平铺字段）───────────────────────────
def walk(node, path):
    if isinstance(node, dict):
        kind = node.get("type")
        if kind == "item-give" and node.get("item"):
            granted[node["item"]].append(f"剧本:{path}")
        elif kind == "item-take" and node.get("item"):
            taken[node["item"]].append(f"剧本:{path}")
        elif kind == "quest":
            inner = node.get("command") or {}
            if inner.get("item"):
                taken[inner["item"]].append(f"任务命令:{path}")
        for value in node.values():
            walk(value, path)
    elif isinstance(node, list):
        for value in node:
            walk(value, path)


for script_id, script in campaign.get("scripts", {}).items():
    walk(script, script_id)

# ── 任务：目标、交付、接取补给、奖励 ─────────────────────────────────────
for quest in campaign.get("quests", []):
    qid = quest.get("id", "?")
    for goal in quest.get("goals", []) or []:
        if goal.get("kind") == "item" and goal.get("item"):
            taken[goal["item"]].append(f"任务目标:{qid}")
    for entry in quest.get("handoverItems", []) or []:
        if isinstance(entry, dict) and entry.get("item"):
            taken[entry["item"]].append(f"任务交付:{qid}")
    for entry in quest.get("onAcceptItems", []) or []:
        if isinstance(entry, dict) and entry.get("item"):
            granted[entry["item"]].append(f"接任务给予:{qid}")
    for entry in (quest.get("rewards") or {}).get("items", []) or []:
        if isinstance(entry, dict) and entry.get("item"):
            granted[entry["item"]].append(f"任务奖励:{qid}")

quest_items = {iid: meta for iid, meta in items.items() if meta.get("category") == "quest"}

dead = []
orphan = []
for iid, meta in sorted(quest_items.items()):
    if iid in granted:
        continue
    if iid in taken:
        dead.append((iid, meta.get("name", "?"), taken[iid]))
    else:
        orphan.append((iid, meta.get("name", "?")))

print(f"任务道具 {len(quest_items)} 件")
print(f"  有产出点的   {sum(1 for i in quest_items if i in granted)}")
print(f"  无产出但被要   {len(dead)}")
print(f"  无产出也无人要 {len(orphan)}")
print()
if dead:
    print("=== 无产出但被要求（剧情会卡住）===")
    for iid, name, where in dead:
        print(f"  {iid:28} {name:12} {where[:3]}")
print()
if orphan:
    print("=== 无产出也无人要求（孤立条目）===")
    for iid, name in orphan:
        print(f"  {iid:28} {name}")
