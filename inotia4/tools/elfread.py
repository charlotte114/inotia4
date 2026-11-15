# -*- coding: utf-8 -*-
"""从 ELF 虚拟地址读数据（跨 section）"""
import struct

from elftools.elf.elffile import ELFFile


class Reader:
    def __init__(self, path):
        self.raw = open(path, "rb").read()
        f = open(path, "rb")
        e = ELFFile(f)
        self.secs = []
        for s in e.iter_sections():
            if s["sh_type"] == "SHT_NOBITS" or not s["sh_addr"]:
                continue
            self.secs.append((s["sh_addr"], s["sh_addr"] + s["sh_size"], s["sh_offset"]))
        f.close()

    def off(self, vaddr):
        for a, b, o in self.secs:
            if a <= vaddr < b:
                # 段可能重叠（.text 在 LOAD 段内），优先取包含的那个
                pass
        # 简单：找第一个 vm 区间包含的
        for a, b, o in self.secs:
            if a <= vaddr < b:
                return o + (vaddr - a)
        raise ValueError("%#x 未映射" % vaddr)

    def u32(self, vaddr):
        o = self.off(vaddr)
        return struct.unpack("<I", self.raw[o:o + 4])[0]

    def u16(self, vaddr):
        o = self.off(vaddr)
        return struct.unpack("<H", self.raw[o:o + 2])[0]

    def bytes(self, vaddr, n):
        o = self.off(vaddr)
        return self.raw[o:o + n]


if __name__ == "__main__":
    import sys
    r = Reader(sys.argv[1])
    addr = int(sys.argv[2], 16)
    n = int(sys.argv[3]) if len(sys.argv) > 3 else 8
    for i in range(n):
        a = addr + i * 4
        print("%#010x  %#010x  %d" % (a, r.u32(a), r.u32(a)))
