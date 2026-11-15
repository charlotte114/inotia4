# -*- coding: utf-8 -*-
"""字符串 xref：找出 .rodata 里某个字符串被哪些函数引用。

libgame.so 是 Thumb PIC 代码，取字符串地址的固定套路是两条指令：
    ldr rN, [pc, #imm]     ; 从字面量池取出「相对偏移」
    add rN, pc             ; 再加当前 PC（= 本条指令地址 + 4）得到绝对地址
所以只认这种成对出现，才不会被 GOT 索引（ldr rX,[pc,#..] 后接 ldr rY,[rZ,rX]）误判。

用法:
    python tools/re_xref.py "appinfo.dat" "m%d.dat" "save%d.dat"
"""
import bisect
import sys

import capstone
from capstone import arm as ARM
from elftools.elf.elffile import ELFFile

SO = r"D:\Desktop\inotia4\_apk\lib\armeabi-v7a\libgame.orig.so"


class X:
    def __init__(self, path=SO):
        self.raw = open(path, "rb").read()
        e = ELFFile(open(path, "rb"))
        text = e.get_section_by_name(".text")
        ro = e.get_section_by_name(".rodata")
        self.base = text["sh_addr"]
        self.text = text.data()
        self.ro_lo = ro["sh_addr"]
        self.ro_hi = ro["sh_addr"] + ro["sh_size"]
        self.syms = []
        for s in e.get_section_by_name(".dynsym").iter_symbols():
            if s["st_shndx"] == "SHN_UNDEF" or not s.name:
                continue
            self.syms.append((s["st_value"] & ~1, s["st_size"], s["st_info"]["type"], s.name))
        self.syms.sort()
        self.addrs = [x[0] for x in self.syms]
        self._index()

    def resolve(self, a):
        i = bisect.bisect_right(self.addrs, a) - 1
        if i < 0:
            return None
        v, z, t, n = self.syms[i]
        if z and a >= v + z:
            return None
        return n

    def _index(self):
        """扫一遍 .text，建立 {字符串地址: [(指令地址, 所在函数)]}

        注意 ldr 和 add 之间常夹着 mov 之类，所以不能要求相邻，
        改为「在后续 5 条指令内找 add rN, pc」。
        """
        md = capstone.Cs(capstone.CS_ARCH_ARM, capstone.CS_MODE_THUMB)
        md.detail = True
        md.skipdata = True
        ins_list = list(md.disasm(self.text, self.base))
        self.refs = {}
        for i, ins in enumerate(ins_list):
            if ins.mnemonic != "ldr" or len(ins.operands) != 2:
                continue
            mem = ins.operands[1]
            if mem.type != capstone.CS_OP_MEM or mem.mem.base != ARM.ARM_REG_PC:
                continue
            reg = ins.op_str.split(",")[0].strip()
            pool = ((ins.address + 4) & ~3) + mem.mem.disp
            off = pool - self.base
            if not (0 <= off <= len(self.text) - 4):
                continue
            lit = int.from_bytes(self.text[off:off + 4], "little")
            for nxt in ins_list[i + 1:i + 6]:
                if nxt.mnemonic.startswith("add") and nxt.op_str.startswith(reg + ",") \
                        and nxt.op_str.split(",")[1].strip() == "pc":
                    tgt = (nxt.address + 4 + lit) & 0xFFFFFFFF
                    self.refs.setdefault(tgt, []).append((ins.address, self.resolve(ins.address)))
                    break
                if nxt.mnemonic.startswith(("ldr", "ldrb", "str", "b", "bl")) and reg in nxt.op_str:
                    break

    def find(self, s):
        b = s.encode()
        out = []
        i = self.raw.find(b)
        while i >= 0:
            if self.ro_lo <= i < self.ro_hi:
                out.append(i)
            i = self.raw.find(b, i + 1)
        return out


if __name__ == "__main__":
    x = X()
    for s in sys.argv[1:]:
        print("=== %r" % s)
        for a in x.find(s):
            hits = x.refs.get(a)
            if not hits:
                print("   str@%#08x  无引用（可能是拼接串或被索引表引用）" % a)
                continue
            for ins, fn in hits:
                print("   str@%#08x  ins=%#08x  %s" % (a, ins, fn))
