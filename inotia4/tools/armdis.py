import sys
from elftools.elf.elffile import ELFFile
import capstone

PATH = r"D:\Desktop\inotia4\_apk\lib\armeabi-v7a\libgame.orig.so"

f = open(PATH, "rb")
elf = ELFFile(f)
text = elf.get_section_by_name(".text")
base = text["sh_addr"]
data = text.data()
f.close()


def disasm(vaddr, count=80, show_bytes=True):
    off = vaddr - base
    md = capstone.Cs(capstone.CS_ARCH_ARM, capstone.CS_MODE_THUMB)
    md.detail = True
    chunk = data[off:off + count * 4]
    out = []
    for i in md.disasm(chunk, vaddr):
        b = chunk[i.address - vaddr:i.address - vaddr + i.size]
        out.append("%08x  %-10s %-8s %s" % (i.address, b.hex(), i.mnemonic, i.op_str))
    return out


if __name__ == "__main__":
    a = int(sys.argv[1], 16)
    n = int(sys.argv[2]) if len(sys.argv) > 2 else 80
    print("text vaddr=%08x size=%d" % (base, len(data)))
    for line in disasm(a, n):
        print(line)
