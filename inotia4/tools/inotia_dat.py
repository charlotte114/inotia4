# -*- coding: utf-8 -*-
"""
艾诺迪亚4 (Inotia 4) game_res 数据表拆包/回包工具

格式（逆向自 lib/armeabi-v7a/libgame.so）:
    LZMA_GetDecodeSize  @0x0779a0  -> u32 @ src+6  = 解压后大小
    LZMA_DecodeEx       @0x0779ae  -> 数据从 src+14 开始
    LzmaDecodeProperties@0x091d8c  -> 5 字节 LZMA 属性 @ src+1
    调用方传入 src = 文件指针 + 1，所以真实偏移全部 +1。

文件布局:
    [0]      u8   0x01  版本/压缩标志
    [1]      u8   0x00
    [2..6]   5B   LZMA 属性 (实测 5d 00 00 00 01 = lc3 lp0 pb2, dict 16MB)
    [7..10]  u32  解压后字节数
    [11..14] u32  保留 (0)
    [15..]        LZMA 裸流

解压后的载荷有三种形态:
    A) 定长记录表   [u32 count][表头...][count * 定长记录]
    B) 字符串池     [u32 count][count * 3字节偏移][NUL 分隔的 UTF-8 字符串]
    C) 裸数据       (m*.dat 地图，无表头)
"""
import lzma
import os
import struct
import sys

PROPS = b"\x5d\x00\x00\x00\x01"   # lc=3 lp=0 pb=2, dict=16MB
LC, LP, PB = 3, 0, 2
DICT = 1 << 24
SRC_OFF = 15                      # LZMA 裸流起点


def _filters(dict_size=DICT):
    return [{"id": lzma.FILTER_LZMA1, "lc": LC, "lp": LP, "pb": PB,
             "dict_size": dict_size}]


def unpack(blob):
    """整个文件的 bytes -> 解压后的载荷

    blob[0] != 0x01 表示这张表没压缩（如 i_portrait / i_title / i_worldmap
    这几个内嵌贴图的索引），原样返回。
    """
    if blob[0] != 1:
        return blob
    props = blob[2:7]
    prop = props[0]
    dict_size = int.from_bytes(props[1:5], "little")
    lc, lp, pb = prop % 9, (prop // 9) % 5, prop // 45
    dec = lzma.LZMADecompressor(
        format=lzma.FORMAT_RAW,
        filters=[{"id": lzma.FILTER_LZMA1, "lc": lc, "lp": lp, "pb": pb,
                  "dict_size": max(dict_size, 4096)}],
    )
    return dec.decompress(blob[SRC_OFF:])


def pack(payload, compressed=True):
    """解压后的载荷 -> 完整文件 bytes

    compressed=False 用于那几个本来就未压缩的索引表，原样输出。
    """
    if not compressed:
        return payload
    comp = lzma.LZMACompressor(format=lzma.FORMAT_RAW, filters=_filters())
    stream = comp.compress(payload) + comp.flush()
    head = bytes([1, 0]) + PROPS + struct.pack("<I", len(payload)) + b"\x00" * 4
    return head + stream


def load(path):
    with open(path, "rb") as f:
        return unpack(f.read())


def load_pair(path):
    """-> (载荷, 是否 LZMA 压缩)"""
    with open(path, "rb") as f:
        blob = f.read()
    return unpack(blob), blob[:1] == b"\x01"


def save(path, payload, compressed=True):
    with open(path, "wb") as f:
        f.write(pack(payload, compressed))


# ---------------------------------------------------------------- 定长表

def parse_fixed(payload):
    """返回 (count, header_offset, record_size)。找不到合适分帧则返回 None。"""
    if len(payload) < 8:
        return None
    count = struct.unpack_from("<I", payload, 0)[0]
    if count < 2 or count > 500000:
        return None
    total = len(payload)
    # 表头长度不固定（含列描述），用整除性加合理性打分挑最优
    best = None
    for hdr in range(4, min(200, total // count + 1)):
        body = total - hdr
        if body <= 0 or body % count:
            continue
        size = body // count
        if not 2 <= size <= 255:
            continue
        score = 0
        if size % 2 == 0:
            score += 2          # 记录长度极少是奇数
        if hdr % 4 == 0:
            score += 2
        if hdr <= 24:
            score += 1
        if best is None or score > best[0]:
            best = (score, count, hdr, size)
    return None if best is None else best[1:]


def records(payload):
    info = parse_fixed(payload)
    if not info:
        return None
    count, hdr, size = info
    return [payload[hdr + i * size: hdr + (i + 1) * size] for i in range(count)]


# ---------------------------------------------------------------- 字符串表

def is_string_table(payload):
    """u32 count + count*3 字节偏移，且偏移单调递增指向池起点"""
    if len(payload) < 16:
        return False
    count = struct.unpack_from("<I", payload, 0)[0]
    if not 2 <= count <= 500000:
        return False
    pool = 4 + count * 3
    if pool >= len(payload):
        return False
    first = int.from_bytes(payload[4:7], "little")
    return abs(first - pool) <= 4


def strings(payload):
    """-> list[str]，下标即记录 id"""
    count = struct.unpack_from("<I", payload, 0)[0]
    offs = [int.from_bytes(payload[4 + i * 3: 7 + i * 3], "little")
            for i in range(count)]
    out = []
    for o in offs:
        e = payload.find(b"\x00", o)
        if e < 0:
            e = len(payload)
        out.append(payload[o:e].decode("utf-8", "replace"))
    return out


def rebuild_strings(texts):
    """list[str] -> 载荷（重新计算偏移与池）"""
    count = len(texts)
    pool = 4 + count * 3
    blob = b"\x00".join(t.encode("utf-8") for t in texts) + b"\x00"
    offs, cur = [], pool
    for t in texts:
        offs.append(cur)
        cur += len(t.encode("utf-8")) + 1
    head = struct.pack("<I", count) + b"".join(o.to_bytes(3, "little") for o in offs)
    return head + blob


# ---------------------------------------------------------------- game.dat 容器

# game.dat 内 100 张子表的顺序（按 .so 的 .dynsym 索引序反推，
# 已用 MAPINFOBASE=416 对上 m0..m415、CHARCLASSBASE=6 对上 6 职业、ITEMDATABASE 最大表 三点交叉验证）
SUB_TABLES = [
    "CHARCLASSBASE", "STATUSINFOBASE", "STATUSASSIGNBASE", "STATUSDICEBASE",
    "ATTRINITBASE", "PORTRAITBASE", "PORTRAITCLASSBASE", "CHARACTERSTATEBASE",
    "CHARACTERSTATECHANGEBASE", "CONDITIONBASE", "EXPRESSBASE", "CONSTBASE",
    "STATUSBASE", "ITEMDATABASE", "ITEMCLASSBASE", "ITEMMIXLINKBASE",
    "MANAGEMBASE", "ITEMRARITYGRADEBASE", "ITEMGRADEBASE", "ITEMENCHANTBASE",
    "ITEMSTATICBASE", "ITEMRECOVERBASE", "ITEMBUFFBASE", "ITEMOPTINFOBASE",
    "ITEMSTATICOPTBASE", "DEALINFOBASE", "RECIPEBASE", "MIXTUREBASE",
    "ACTDATABASE", "ACTUNITBASE", "ACTTRANSMITBASE", "BUFFDATABASE",
    "BUFFUNITBASE", "ACTTRANSMIT_ADDBASE", "SKILLDESCBASE", "SKILLTRAINPOINTBASE",
    "SKILLTRAINBASE", "CHARACTERCOSTUMEGROUPBASE", "CHARACTERCOSTUMEBASE",
    "CHARACTERCOSTUMEPALETTEBASE", "IMAGEFILEBASE", "ANIMATIONAREABASE",
    "ANIMATIONTYPEBASE", "ANIMATIONDEFINEBASE", "MONSTERCOSTUMEBASE",
    "MONSKILLTYPEBASE", "MONSKILLBASE", "MONDATABASE", "MONAIINFOBASE",
    "QUESTDROPBASE", "NPCFUNCBASE", "NPCINFOBASE", "NPCFUNCLINKBASE",
    "NPCDESCBASE", "NPCABILITYBASE", "NPCCOSTUMEBASE", "MAPFEATUREINFOBASE",
    "MAPCOLORBASE", "MAPINFOBASE", "EVTINFOBASE", "EVTCONDBASE",
    "EVTCMDBASE", "EFFECTINFOBASE", "CHOICEBASE", "QUESTGROUPBASE",
    "QUESTINFOBASE", "QUESTLINKBASE", "QUESTCOMPLETEBASE", "QUESTOBJECTCHANGEBASE",
    "QUESTPREPAREBASE", "QUESTREWARDBASE", "QUESTGENBASE", "SYMBOLBASE",
    "TEXTDATABASE", "TIPBASE", "MONSTERDROPBASE", "OPENITEMBOXBASE",
    "DROPINFOBASE", "DROPDETAILINFOBASE", "DROPEVTBASE", "SOUNDINFOBASE",
    "HELPTEXTBASE", "MAXLEVELBASE", "INSTALLBASE", "CASHITEMGROUPBASE",
    "CASHITEMBASE", "ITEMPACKBASE", "PORTALINFOBASE", "ITEMDESCBASE",
    "COLORRATEBASE", "COLORRATEDATABASE", "MERCENARYINFOBASE", "MERCENARYSKILLBASE",
    "MERCENARYGROUPSKILLBASE", "CHEATCHARBASE", "CHEATCHARITEMBASE",
    "SOUNDEFFECTBASE", "SOUNDBGMBASE", "CHARGEDITEMBASE", "CHARGEDITEMPRODUCTBASE",
]


def split_container(payload):
    """game.dat 载荷 -> [子表 bytes, ...]

    布局: [u32 count][count × 3字节累计结束偏移][子表数据...]
    数据起点 = 5 + count*3
    """
    count = struct.unpack_from("<I", payload, 0)[0]
    ends = [int.from_bytes(payload[5 + i * 3: 8 + i * 3], "little")
            for i in range(count)]
    base = 5 + count * 3
    bounds = [base] + [base + e for e in ends]
    return [payload[bounds[i]:bounds[i + 1]] for i in range(count)]


def rebuild_container(subs):
    """[子表 bytes, ...] -> game.dat 载荷

    注意偏移表从第 5 字节开始（u32 count 之后还有 1 个填充字节），
    所以数据起点 = 5 + count*3。
    """
    count = len(subs)
    out, cur = [], 0
    for s in subs:
        cur += len(s)
        out.append(cur.to_bytes(3, "little"))
    # 末尾有 1 字节 0 填充，原文件即如此，保留
    return (struct.pack("<I", count) + b"\x00"
            + b"".join(out) + b"".join(subs) + b"\x00")


def sub_table(payload, name):
    """按表名取子表 -> (records, record_size, count)"""
    idx = SUB_TABLES.index(name)
    blob = split_container(payload)[idx]
    count = struct.unpack_from("<I", blob, 0)[0]
    size = struct.unpack_from("<H", blob, 4)[0]
    return [blob[6 + i * size: 6 + (i + 1) * size] for i in range(count)], size, count


def set_sub_table(payload, name, records):
    """按表名整体替换子表记录，返回新的 game.dat 载荷"""
    subs = split_container(payload)
    idx = SUB_TABLES.index(name)
    size = len(records[0])
    subs[idx] = struct.pack("<I", len(records)) + struct.pack("<H", size) + b"".join(records)
    return rebuild_container(subs)


# ---------------------------------------------------------------- CLI

def cmd_info(paths):
    for p in paths:
        name = os.path.basename(p)
        try:
            payload = load(p)
        except Exception as e:
            print(f"{name:<28} 解压失败 {type(e).__name__}")
            continue
        kind = "字符串表" if is_string_table(payload) else ""
        if not kind:
            r = parse_fixed(payload)
            kind = f"定长表 count={r[0]} hdr={r[1]} size={r[2]}" if r else "裸数据"
        print(f"{name:<28} 解压 {len(payload):>8} B   {kind}")


def cmd_text(path, out_path, limit=0):
    texts = strings(load(path))
    if limit:
        texts = texts[:limit]
    with open(out_path, "w", encoding="utf-8") as f:
        for i, t in enumerate(texts):
            f.write(f"{i}\t{t}\n")
    print(f"导出 {len(texts)} 条 -> {out_path}")


def cmd_roundtrip(path):
    payload, compressed = load_pair(path)
    again = unpack(pack(payload, compressed))
    same = again == payload
    print(f"{os.path.basename(path)}: {len(payload)} B  "
          f"{'LZMA' if compressed else '原始'}  "
          f"回包后 {'完全一致 OK' if same else '不一致 失败'}")
    return same


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        print("用法: python inotia_dat.py info <*.dat.jpg ...>")
        print("      python inotia_dat.py text <表> <输出.txt> [条数]")
        print("      python inotia_dat.py roundtrip <表>")
        return 1
    cmd, rest = argv[1], argv[2:]
    if cmd == "info":
        cmd_info(rest)
    elif cmd == "text":
        cmd_text(rest[0], rest[1], int(rest[2]) if len(rest) > 2 else 0)
    elif cmd == "roundtrip":
        return 0 if all(cmd_roundtrip(p) for p in rest) else 1
    else:
        print("未知命令:", cmd)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
