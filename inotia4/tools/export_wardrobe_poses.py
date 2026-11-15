# -*- coding: utf-8 -*-
"""从 i_player 精灵包还原 wardrobe.json 的 pose 条目（人物姿态表）。

用法：
    py inotia4/tools/export_wardrobe_poses.py --check
        对着 public/content/wardrobe.json 已有的 220 条逐字段回归，打印匹配情况。
    py inotia4/tools/export_wardrobe_poses.py --fill 4076-4084
        把缺失的 pose 补进 wardrobe.json（默认只报告，加 --write 才落盘）。

布局（依据 inotia4/docs/COMBO_FX_REVERSE.md 第十一、十二节，本轮实测校正）：

  动画段（段号 = pose 号）
      [0]       u8  槽数 n
      [1..]     n × 8 字节槽
  槽 = u8 flags / u8 100 / u16 帧号 / i16 x / i16 y
      帧号同时是「帧记录」所在段的段号（不是全局表，就在同一个 i_player 包里）。

  帧记录段
      [0]       u8  层数 m
      [1..]     m × 11 字节层
  层 = @0 u8 flags / @1 u8 angle÷10 / @2 u8 恒 10 / @3 u8 alpha×100
       @4 u8 piece / @5 u8 部件类型 / @6..7 i16 x / @8..9 i16 y / @10 u8 恒 0

  JSON 的 x/y = 层的 x/y + 槽的 x/y；槽 flags 的 0x40 表示整帧镜像，
  此时层的 x 与 angle 都要取反（实测 4064/4065 一类条目）。

  翻转位的实测口径（220 条回归逼出来的，与直觉不同，别改回去）：
      scaleX = (层 flags ^ 槽 flags) & 0x40      —— 镜像槽要翻转整帧的左右
      scaleY =  层 flags & 0x80                   —— 槽的 0x80 不参与，
                                                    4043/4049 一类的 JSON 只有层位生效

  piece ≥ 100 的层是「施法道具」：part 固定记作 cast-prop-1（只出现 2 次，
  是 4086 那组施法动作里的武器幻影）。

  帧数 = 槽数 + 5：最后 5 帧重复末槽（收招保持），ticks 即帧数；
  ticks-0 的条目没有这段尾巴，帧数等于槽数（4152 那组是 +4，属既有数据的特例，
  回归时按各条目自己的 ticks 算尾巴，不再假定固定 5 帧）。

朝向：每 3 个连续段一组，按「west(+east 镜像) / north / south」循环
（段号 % 3：0 = west，1 = north，2 = south），与 wardrobe.json 现有的
4050–4096 全部吻合。
"""
import argparse
import json
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import re_pack

RES = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                   "_apk", "assets", "common", "game_res")
WARDROBE = os.path.join(os.path.dirname(os.path.dirname(os.path.dirname(
    os.path.abspath(__file__)))), "public", "content", "wardrobe.json")

# 部件类型 id → wardrobe.json 的 part 名（用 220 条现有条目反推，1:1 无歧义）
PARTS = {
    0: "body", 1: "coat-front", 2: "head-front", 3: "cloak", 4: "main-blade",
    5: "shield", 6: "bow", 7: "staff", 8: "off-blade", 9: "two-handed",
    10: "crossbow", 11: "coat-back", 12: "head-back", 13: "main-dagger",
    14: "off-dagger", 15: "orb",
}

# 朝向由段号推：每 3 段一组
FACING_BY_MOD = {0: "west", 1: "north", 2: "south"}

FRAME_RATE = 16.666666666666668
TAIL_FRAMES = 5


class Pack:
    def __init__(self):
        re_pack.RES = RES
        _, _, _, _, _, segs = re_pack.segments("i_player")
        self.segs = segs

    def slots(self, pose):
        data = self.segs[pose][2]
        if data is None:
            raise ValueError("pose %d 的段解不开" % pose)
        n = data[0]
        if len(data) != 1 + n * 8:
            raise ValueError("pose %d 槽宽对不上：%d != 1 + %d*8" % (pose, len(data), n))
        out = []
        for i in range(n):
            s = data[1 + i * 8:1 + (i + 1) * 8]
            out.append({
                "flags": s[0],
                "frame": struct.unpack_from("<H", s, 2)[0],
                "x": struct.unpack_from("<h", s, 4)[0],
                "y": struct.unpack_from("<h", s, 6)[0],
            })
        return out

    def layers(self, frame):
        data = self.segs[frame][2]
        if data is None:
            raise ValueError("帧记录 %d 的段解不开" % frame)
        n = data[0]
        if len(data) != 1 + n * 11:
            raise ValueError("帧 %d 层宽对不上：%d != 1 + %d*11" % (frame, len(data), n))
        return [data[1 + i * 11:1 + (i + 1) * 11] for i in range(n)]

    def decode(self, pose, tail=TAIL_FRAMES):
        frames = []
        for slot in self.slots(pose):
            mirrored = bool(slot["flags"] & 0x40)
            parts = []
            for L in self.layers(slot["frame"]):
                # alpha 为 0 的层是占位（4043 第 3 帧那种「空帧」），JSON 里不记
                if L[3] == 0:
                    continue
                x = struct.unpack_from("<h", bytes(L), 6)[0]
                angle = L[1] * 10
                if mirrored:
                    x, angle = -x, -angle
                prop = L[4] >= 100
                parts.append({
                    "part": "cast-prop-1" if prop else PARTS[L[5]],
                    "piece": 0 if prop else L[4],
                    "x": x + slot["x"],
                    "y": struct.unpack_from("<h", bytes(L), 8)[0] + slot["y"],
                    "scaleX": -1 if (L[0] ^ slot["flags"]) & 0x40 else 1,
                    "scaleY": -1 if L[0] & 0x80 else 1,
                    "angle": angle,
                    "alpha": round(L[3] / 100.0, 2),
                })
            frames.append(parts)
        # 收招保持：末尾复制 5 帧末槽
        frames.extend([frames[-1]] * tail)
        return frames


def entry(frames, mirror_x):
    return {"mirrorX": mirror_x, "frameRate": FRAME_RATE, "frames": frames}


def build(pack, pose, ticks):
    facing = FACING_BY_MOD[pose % 3]
    tail = TAIL_FRAMES if ticks else 0
    frames = pack.decode(pose, tail)
    out = {}
    if facing == "west":
        out["pose-%d-west-ticks-%d" % (pose, ticks)] = entry(frames, False)
        out["pose-%d-east-ticks-%d" % (pose, ticks)] = entry(frames, True)
    else:
        out["pose-%d-%s-ticks-%d" % (pose, facing, ticks)] = entry(frames, False)
    return out


def read_wardrobe():
    with open(WARDROBE, encoding="utf-8") as f:
        return json.load(f)


def cmd_check(pack):
    wardrobe = read_wardrobe()
    poses = wardrobe["poses"]
    ok = bad = skipped = 0
    problems = []
    for key, want in poses.items():
        pose = int(key.split("-")[1])
        ticks = int(key.rsplit("-", 1)[1])
        try:
            tail = max(0, ticks - len(pack.slots(pose)))
            got = pack.decode(pose, tail)
        except ValueError as error:
            skipped += 1
            problems.append((key, "解不开", str(error)))
            continue
        if len(got) != len(want["frames"]):
            bad += 1
            problems.append((key, "帧数", len(got), len(want["frames"])))
            continue
        mismatch = None
        for i, (gf, wf) in enumerate(zip(got, want["frames"])):
            if len(gf) != len(wf):
                mismatch = ("第 %d 帧部件数" % i, len(gf), len(wf))
                break
            for g, w in zip(gf, wf):
                diff = [f for f in ("part", "piece", "x", "y", "scaleX", "scaleY", "angle", "alpha")
                        if g[f] != w[f]]
                if diff:
                    mismatch = ("第 %d 帧 %s" % (i, diff[0]), g[diff[0]], w[diff[0]])
                    break
            if mismatch:
                break
        if mismatch:
            bad += 1
            problems.append((key,) + mismatch)
        else:
            ok += 1
    print("回归：%d 条全对，%d 条有差异，%d 条解不开（共 %d 条）"
          % (ok, bad, skipped, len(poses)))
    for p in problems[:15]:
        print("   ", p)
    return bad == 0 and skipped == 0


def cmd_fill(pack, poses, write):
    wardrobe = read_wardrobe()
    existing = wardrobe["poses"]
    added = {}
    for pose in poses:
        ticks = len(pack.slots(pose)) + TAIL_FRAMES
        for key, value in build(pack, pose, ticks).items():
            if key in existing:
                print("已存在，跳过", key)
                continue
            added[key] = value
    for key, value in added.items():
        print("%-30s %d 帧" % (key, len(value["frames"])))
    print("共 %d 条" % len(added))
    if not (write and added):
        return True
    existing.update(added)
    # 保持原文件的紧凑格式与末尾换行
    text = json.dumps(wardrobe, separators=(",", ":"), ensure_ascii=False) + "\n"
    with open(WARDROBE, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    print("已写入", WARDROBE)
    return True


def parse_range(text):
    out = []
    for chunk in text.split(","):
        chunk = chunk.strip()
        if "-" in chunk:
            a, b = chunk.split("-")
            out.extend(range(int(a), int(b) + 1))
        elif chunk:
            out.append(int(chunk))
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true", help="对现有条目做回归")
    ap.add_argument("--fill", help="要补的段号，如 4076-4084")
    ap.add_argument("--write", action="store_true", help="真的写进 wardrobe.json")
    args = ap.parse_args()
    if not args.check and not args.fill:
        ap.error("要么 --check，要么 --fill <段号>")
    pack = Pack()
    ok = True
    if args.check:
        ok = cmd_check(pack) and ok
    if args.fill:
        ok = cmd_fill(pack, parse_range(args.fill), args.write) and ok
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
