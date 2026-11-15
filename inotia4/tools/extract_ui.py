# -*- coding: utf-8 -*-
"""
静态解析 UIPlay_Create，提取每个控件的 SetRect/SetID 调用。

做法：线性扫描 + 简易寄存器符号执行。
  - 屏宽/屏高用符号表示（('W',0) / ('H',0)），其它算不出的记 None
  - 遇到 ControlObject_SetRect(0x71ee8) 记一次调用，参数取自 r1..r3 和 [sp]/[sp+4]
  - 遇到 ControlObject_SetID(0x72002) 记一次，之后把 ID 回填给最近一次 SetRect
输出 JSON 供可视化编辑器使用。
"""
import json
import struct
import sys

from elftools.elf.elffile import ELFFile
import capstone

PATH = r"D:\Desktop\inotia4\_apk\lib\armeabi-v7a\libgame.orig.so"
START, END = (int(sys.argv[1], 0), int(sys.argv[2], 0)) if len(sys.argv) > 2 else (0x8978C, 0x89CE4)

FUNC_W = 0x6D1B4        # GetScreenWidth  -> r0
FUNC_H = 0x6D1DC        # GetScreenHeight -> r0
F_SETRECT = 0x71EE8
F_SETID = 0x72002
F_SETTEXT = 0x7201A
F_SETIMG = 0x71F5E
F_CREATE = 0x72274
F_SETSOMETHING = 0x71E56
F_SETHIDE = 0x71DE8
F_REGISTER = 0x7546C

REGS = ["r0", "r1", "r2", "r3", "r4", "r5", "r6", "r7", "r8", "sb", "sl", "fp", "ip"]
UNKNOWN = object()


def load():
    f = open(PATH, "rb")
    e = ELFFile(f)
    t = e.get_section_by_name(".text")
    base, data = t["sh_addr"], t.data()
    secs = []
    for s in e.iter_sections():
        if s["sh_type"] == "SHT_NOBITS" or not s["sh_addr"]:
            continue
        secs.append((s["sh_addr"], s["sh_addr"] + s["sh_size"], s["sh_offset"]))
    f.close()
    return base, data, secs


BASE, TEXT, SECS = load()


def read_mem(vaddr, n):
    for a, b, o in SECS:
        if a <= vaddr and vaddr + n <= b:
            return TEXT[o - BASE + (vaddr - a):][:n] if a == BASE else open(PATH, "rb").read()[o + (vaddr - a):o + (vaddr - a) + n]
    return None


RAW = open(PATH, "rb").read()


def mem(vaddr, n):
    for a, b, o in SECS:
        if a <= vaddr and vaddr + n <= b:
            return RAW[o + (vaddr - a):o + (vaddr - a) + n]
    return None


def u32(vaddr):
    b = mem(vaddr, 4)
    return struct.unpack("<I", b)[0] if b else None


def fmt(v):
    """把符号值转成可读字符串"""
    if v is UNKNOWN or v is None:
        return None
    if isinstance(v, int):
        return str(v)
    if isinstance(v, tuple):
        tag, k = v
        if tag == "W":
            return "W" if k == 0 else "W%+d" % k
        if tag == "H":
            return "H" if k == 0 else "H%+d" % k
        if tag == "addr":
            return "@%#x" % k
    return None


def num(v, W, H):
    """已知 W/H 时把符号值求值"""
    if isinstance(v, int):
        return v
    if isinstance(v, tuple):
        tag, k = v
        if tag == "W":
            return W + k
        if tag == "H":
            return H + k
    return None


def main():
    md = capstone.Cs(capstone.CS_ARCH_ARM, capstone.CS_MODE_THUMB)
    md.detail = True
    code = TEXT[START - BASE: END - BASE]
    insns = list(md.disasm(code, START))

    reg = {r: UNKNOWN for r in REGS}
    stack = {}          # sp 偏移 -> 值
    ctrls = {}          # ctrl 句柄 -> 属性
    seq = 0
    rects = []
    last_rect = None

    def rn(op):
        return md.reg_name(op.reg)

    def val(op):
        if op.type == capstone.CS_OP_IMM:
            return op.imm
        if op.type == capstone.CS_OP_REG:
            return reg.get(rn(op), UNKNOWN)
        if op.type == capstone.CS_OP_MEM:
            m = op.mem
            if m.base == capstone.ARM_REG_SP:
                return stack.get(m.disp, UNKNOWN)
            return UNKNOWN
        return UNKNOWN

    for ins in insns:
        mn, ops = ins.mnemonic, ins.operands
        if mn in ("push", "push.w"):
            continue
        if mn in ("pop", "pop.w", "pop.w"):
            continue
        if mn in ("ldr", "ldr.w") and len(ops) == 2:
            d, s = ops[0], ops[1]
            if d.type != capstone.CS_OP_REG:
                continue
            v = UNKNOWN
            if s.type == capstone.CS_OP_MEM and md.reg_name(s.mem.base) == "pc":
                # PC 相对常数池
                pc = (ins.address + 4) & ~3 if mn == "ldr" else (ins.address + 4)
                lit = u32(pc + s.mem.disp)
                v = ("lit", lit) if lit is not None else UNKNOWN
            elif s.type == capstone.CS_OP_MEM and md.reg_name(s.mem.base) == "sp":
                v = stack.get(s.mem.disp, UNKNOWN)
            reg[rn(d)] = v
            continue
        if mn in ("mov", "mov.w", "movs") and len(ops) == 2:
            d, s = ops
            if d.type == capstone.CS_OP_REG:
                reg[rn(d)] = val(s) if s.type != capstone.CS_OP_REG else reg.get(rn(s), UNKNOWN)
            continue
        if mn in ("add", "add.w", "adds") and len(ops) >= 3:
            d, a, b = ops[0], ops[1], ops[2]
            if d.type == capstone.CS_OP_REG:
                va, vb = val(a), val(b)
                if isinstance(va, int) and isinstance(vb, int):
                    reg[rn(d)] = va + vb
                else:
                    reg[rn(d)] = UNKNOWN
            continue
        if mn in ("sub", "sub.w", "subs") and len(ops) >= 3:
            d, a, b = ops[0], ops[1], ops[2]
            if d.type == capstone.CS_OP_REG:
                va, vb = val(a), val(b)
                if isinstance(va, int) and isinstance(vb, int):
                    reg[rn(d)] = va - vb
                elif isinstance(va, tuple) and isinstance(vb, int):
                    reg[rn(d)] = (va[0], va[1] - vb)
                else:
                    reg[rn(d)] = UNKNOWN
            continue
        if mn in ("str", "str.w") and len(ops) == 2:
            s, d = ops
            if d.type == capstone.CS_OP_MEM and md.reg_name(d.mem.base) == "sp":
                stack[d.mem.disp] = val(s)
            continue
        if mn in ("stm", "stm.w") and len(ops) == 2:
            base_op, reglist = ops
            if base_op.type == capstone.CS_OP_REG and rn(base_op) == "sp":
                disp = 0
                for rn in [r for r in REGS]:
                    if ins.reg_name in ():
                        pass
                # 简化：按寄存器列表顺序写入 sp+0, +4, ...
                names = [r for r in ["r0", "r1", "r2", "r3", "r4", "r5", "r6", "r7",
                                     "r8", "sb", "sl", "fp", "ip", "lr"]]
                written = [r.strip() for r in ins.op_str.split("{")[1].split("}")[0].split(",")]
                for i, rn in enumerate(written):
                    stack[i * 4] = reg.get(rn, UNKNOWN)
            continue
        if mn in ("bl", "bl.w", "blx") and ops:
            tgt = ops[0].imm if ops[0].type == capstone.CS_OP_IMM else None
            if tgt == FUNC_W:
                reg["r0"] = ("W", 0)
            elif tgt == FUNC_H:
                reg["r0"] = ("H", 0)
            elif tgt == F_SETRECT:
                seq += 1
                r = {
                    "seq": seq,
                    "addr": ins.address,
                    "ctrl": fmt(reg.get("r1")),
                    "x": fmt(reg.get("r2")),
                    "y": fmt(reg.get("r3")),
                    "w": fmt(stack.get(0)),
                    "h": fmt(stack.get(4)),
                    "id": None,
                    "text": None,
                    "img": None,
                }
                rects.append(r)
                last_rect = r
            elif tgt == F_SETID:
                if last_rect and last_rect["id"] is None:
                    last_rect["id"] = reg.get("r1") if isinstance(reg.get("r1"), int) else None
                    last_rect["id_addr"] = ins.address
            elif tgt == F_SETIMG:
                if last_rect and last_rect["img"] is None:
                    last_rect["img"] = "yes"
            elif tgt == F_CREATE:
                seq += 1
                h = "ctrl%d" % seq
                ctrls[h] = {"params": [fmt(reg.get(x)) for x in ("r1", "r2", "r3")],
                            "sp0": fmt(stack.get(0))}
                reg["r0"] = h
            else:
                reg["r0"] = UNKNOWN
            continue
        # 其它指令：目的寄存器标未知
        if ops and ops[0].type == capstone.CS_OP_REG and mn not in ("cmp", "tst", "cbz", "cbnz"):
            reg[rn(ops[0])] = UNKNOWN

    print("SetRect 调用 %d 次" % len(rects))
    for r in rects:
        print("seq%-3d @%#08x id=%-6s ctrl=%-8s x=%-8s y=%-8s w=%-6s h=%-6s"
              % (r["seq"], r["addr"], r["id"], r["ctrl"], r["x"], r["y"], r["w"], r["h"]))

    out = sys.argv[3] if len(sys.argv) > 3 else r"D:\Desktop\inotia4\data\rects.json"
    json.dump(rects, open(out, "w", encoding="utf-8"), indent=1, ensure_ascii=False)
    print("\n写出", out)


if __name__ == "__main__":
    main()
