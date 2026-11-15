# -*- coding: utf-8 -*-
"""libgame.so 调用图分析：把 bl/b.w 目标解析到最近的导出符号，统计模块间调用。

用法:
  python tools/re_calls.py edges <前缀A> <前缀B>    列出 A->B 的调用边
  python tools/re_calls.py callers <符号名>         谁调用这个函数
  python tools/re_calls.py callees <符号名>         这个函数调用了谁
  python tools/re_calls.py modules                  模块被调次数排行
"""
import sys
import bisect
import collections

from elftools.elf.elffile import ELFFile
import capstone

SO = r"D:\Desktop\inotia4\_apk\lib\armeabi-v7a\libgame.orig.so"


class So:
    def __init__(self, path=SO):
        self.raw = open(path, "rb").read()
        elf = ELFFile(open(path, "rb"))
        text = elf.get_section_by_name(".text")
        self.base = text["sh_addr"]
        self.text = text.data()
        self.syms = []          # (addr, size, type, name) 按地址排序
        self.by_name = {}
        for s in elf.get_section_by_name(".dynsym").iter_symbols():
            if s["st_shndx"] == "SHN_UNDEF" or not s.name:
                continue
            # Thumb 函数符号的 st_value 带 bit0（如 C2S_HubMain = 0x6bd59），统一抹掉
            v = s["st_value"] & ~1
            self.syms.append((v, s["st_size"], s["st_info"]["type"], s.name))
            self.by_name.setdefault(s.name, v)
        self.syms.sort()
        self.addrs = [x[0] for x in self.syms]
        # 所有函数入口（用于 bl 目标精确匹配）
        self.func_starts = sorted(a for a, z, t, n in self.syms if t == "STT_FUNC")

    def resolve(self, addr):
        """返回 (符号名, 偏移)；addr 落在某函数内则给出 +偏移"""
        i = bisect.bisect_right(self.addrs, addr) - 1
        if i < 0:
            return None, 0
        a, z, t, n = self.syms[i]
        if z and addr >= a + z:
            return None, 0
        return n, addr - a

    def disasm(self, vaddr, count=60):
        md = capstone.Cs(capstone.CS_ARCH_ARM, capstone.CS_MODE_THUMB)
        md.detail = True
        off = vaddr - self.base
        chunk = self.text[off:off + count * 4]
        out = []
        for ins in md.disasm(chunk, vaddr):
            out.append((ins.address, ins.size, ins.mnemonic, ins.op_str, ins))
        return out

    def calls(self):
        """线性扫描 .text，收集 (调用者, 被调目标)。skipdata 保证不中断。"""
        md = capstone.Cs(capstone.CS_ARCH_ARM, capstone.CS_MODE_THUMB)
        md.detail = True
        md.skipdata = True
        res = []
        for ins in md.disasm(self.text, self.base):
            if ins.mnemonic in ("bl", "blx", "b", "b.w") and ins.op_str.startswith("#"):
                try:
                    tgt = int(ins.op_str[1:], 16)
                except ValueError:
                    continue
                res.append((ins.address, tgt, ins.mnemonic))
        return res


def prefix(name):
    if not name:
        return "?"
    if "_" in name:
        return name.split("_")[0]
    return name


def main():
    so = So()
    if len(sys.argv) < 2:
        print(__doc__)
        return
    cmd = sys.argv[1]
    raw = so.calls()
    # 把每条调用归到其所在函数
    edges = collections.Counter()
    callers_of = collections.defaultdict(collections.Counter)
    callees_of = collections.defaultdict(collections.Counter)
    for addr, tgt, mn in raw:
        src, _ = so.resolve(addr)
        dst, off = so.resolve(tgt)
        if not src:
            continue
        dstn = dst or ("sub_%x" % tgt)
        edges[(prefix(src), prefix(dstn))] += 1
        callers_of[dstn][src] += 1
        callees_of[src][dstn] += 1

    if cmd == "edges":
        a, b = sys.argv[2], sys.argv[3]
        for (x, y), n in sorted(edges.items(), key=lambda kv: -kv[1]):
            if x == a and y == b:
                print("%-24s -> %-24s %d" % (x, y, n))
    elif cmd == "callers":
        for n, c in callers_of[sys.argv[2]].most_common():
            print("%6d  %s" % (c, n))
    elif cmd == "callees":
        for n, c in callees_of[sys.argv[2]].most_common():
            print("%6d  %s" % (c, n))
    elif cmd == "modules":
        tot = collections.Counter()
        for (x, y), n in edges.items():
            tot[y] += n
        for k, v in tot.most_common(60):
            print("%-24s %5d" % (k, v))
        print("\n-- 调用边数 %d, 函数 %d" % (len(edges), len(callees_of)))


if __name__ == "__main__":
    main()
