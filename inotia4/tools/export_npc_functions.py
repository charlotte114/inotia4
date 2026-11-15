"""把 game.dat 里的 NPC 功能表导出成内容文件 public/content/npc-functions.json。

原版「走近 NPC 能做什么」不是写在剧情数据里的，而是三张表串起来的：

    NPCINFOBASE[i][9]            —— 第 i 号 NPC 的功能槽（指向 NPCFUNCLINKBASE 的行号）
      → NPCFUNCLINKBASE[槽] = (npc 索引, 功能索引)
        → NPCFUNCBASE[功能]  = (名称文本 id, 参数 1, 参数 2, 参数 3)

地图实体上的 characterId（`npc-39`）就是 NPCINFOBASE 的下标，所以这三张表足以
还原「这把剑是干什么的」。名称文本取自 memorytext_zhhans.txt 的文本池。

用法：python inotia4/tools/export_npc_functions.py
"""
import json
import os
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
sys.path.insert(0, HERE)
import inotia_dat as T  # noqa: E402

GAME_DAT = os.path.join(ROOT, "inotia4", "_apk", "assets", "common", "game_res", "game.dat.jpg")
TEXT = os.path.join(ROOT, "inotia4", "data", "memorytext_zhhans.txt")
OUTPUT = os.path.join(ROOT, "public", "content", "npc-functions.json")

# 已经接上行为的几个功能。其余功能留在表里（名称照原样），由客户端按「没有实现」处理。
#
# pull-sword 的奖励按职业给：原版物品表里有 5 件 3 级的「黑色」套件
# （黑色法杖 / 黑色弓 / 黑刃短剑 / 黑刃之剑 / 黑刃双手剑），售价 0、不上架，
# 职业覆盖正好是 6 个职业全覆盖，和「地上拔出一把剑，按你练的职业给你一把」
# 吻合。这是从物品表反推的，不是从 so 里读出来的常量——真机上如果对不上，
# 改这里的映射即可，逻辑不用动。
BEHAVIORS = {
    "0": {"kind": "rest"},
    "39": {"kind": "revive", "percent": 100},
    "44": {
        "kind": "pull-sword",
        "rewardByClass": {
            "dark-knight": "black-sword",
            "assassin": "black-dagger",
            "warlock": "black-staff",
            "priest": "black-staff",
            "ranger": "black-bow",
            "warrior": "heavy-black-sword",
        },
    },
}


def load_texts(path):
    texts = {}
    with open(path, encoding="utf-8") as handle:
        for line in handle:
            if "\t" not in line:
                continue
            key, value = line.rstrip("\n").split("\t", 1)
            if key.isdigit():
                texts[int(key)] = value
    return texts


def main():
    texts = load_texts(TEXT)
    payload = T.load(GAME_DAT)
    funcs, _, func_count = T.sub_table(payload, "NPCFUNCBASE")
    info, info_size, info_count = T.sub_table(payload, "NPCINFOBASE")
    links, link_size, link_count = T.sub_table(payload, "NPCFUNCLINKBASE")

    functions = []
    for index, record in enumerate(funcs):
        text_id = struct.unpack_from("<H", record, 0)[0]
        name = texts.get(text_id, "")
        if not name:
            # 36/37 在文本池里是空行，对应地图上的宝箱（普通／优质），客户端走开箱那条路。
            name = "开箱" if index in (36, 37) else ""
        functions.append({
            "index": index,
            "textId": text_id,
            "name": name,
            "params": [record[2], record[3], record[4]],
        })

    # 一个 NPC 可以挂多个功能：NPCINFOBASE 的末字节是它的**第一条**连接记录，
    # 后面同 npc 索引的记录顺延（npc-17 = 读取邮件 + 编写邮件，npc-8 = 正式/练习游戏）。
    npcs = {}
    mismatched = []
    for npc_index, record in enumerate(info):
        first = record[info_size - 1]
        if first >= link_count:
            continue
        funcs_of_npc = []
        slot = first
        while slot < link_count and struct.unpack_from("<H", links[slot], 0)[0] == npc_index:
            func_index = links[slot][2]
            if func_index >= func_count:
                mismatched.append((slot, npc_index))
                break
            funcs_of_npc.append(func_index)
            slot += 1
        if not funcs_of_npc:
            continue
        npcs[f"npc-{npc_index}"] = funcs_of_npc

    # 反查一遍：连接表里出现的 npc 索引都应该能从 NPCINFOBASE 走回来，否则说明表结构理解错了。
    for slot in range(link_count):
        npc_index = struct.unpack_from("<H", links[slot], 0)[0]
        if npc_index >= info_count or f"npc-{npc_index}" not in npcs:
            mismatched.append((slot, npc_index))

    document = {
        "version": 1,
        "note": "由 inotia4/tools/export_npc_functions.py 从 game.dat 导出："
                "NPCFUNCBASE（功能名与参数）/ NPCINFOBASE（NPC→功能槽）/ NPCFUNCLINKBASE（槽→功能）。",
        "functions": functions,
        "npcs": dict(sorted(npcs.items(), key=lambda pair: int(pair[0][4:]))),
        "behaviors": BEHAVIORS,
    }

    with open(OUTPUT, "w", encoding="utf-8") as handle:
        json.dump(document, handle, ensure_ascii=False, indent=1)
        handle.write("\n")

    print(f"写出 {OUTPUT}")
    print(f"  功能 {len(functions)} 条，有功能的 NPC {len(npcs)} 个")
    print(f"  对不上的槽位 {len(mismatched)} 个：{mismatched[:8]}")
    reachable = sorted({value for values in npcs.values() for value in values})
    print(f"  实际绑定的功能索引：{reachable}")


if __name__ == "__main__":
    main()
