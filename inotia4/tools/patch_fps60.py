# -*- coding: utf-8 -*-
"""
把帧率锁到 60（每帧约 16.67ms），不依赖屏幕刷新率。

原理：
  Java 的 GLSurfaceView 每帧回调 `nativeRender`(0x128EAC)：
      push {r4,lr}
      mov  r0, r2
      bl   0x139BB4        ; 渲染
      bl   0x139EDC        ; 提交/交换缓冲
      pop  {r4,pc}
  在这两条调用之后加一段「查时钟、算本帧耗时、补睡差额」的代码即可把帧间隔
  拉到 16.67ms；因为后续 vsync 会被错过，实际帧率就锁在 60。

放代码的地方：
  FPS 统计模块（FPS_getTime/FPS_init/FPS_calc）是死代码——全 so 没有任何地方
  调用它们（只互相调用），所以 0xA9D20..0xA9DB0 共 144 字节可以直接占用。

用到的时间函数（.plt 入口，按 .rel.plt 顺序算出：0x66188+20+i*12）：
  clock_gettime = 0x6661C   usleep = 0x666E8

补丁内容：
  1) 0xA9D20 起写入新例程（渲染 → 提交 → 补睡到 16.67ms → 返回）
  2) 0x128EAC 原 12 字节改成 `b.w 0xA9D20` + NOP 填充
"""
import os
import sys

from elftools.elf.elffile import ELFFile
import capstone
import keystone

BASE = r"D:\Desktop\inotia4\_apk\lib\armeabi-v7a"
SRC = os.path.join(BASE, "libgame.ui.so")

CAVE = 0xA9D20
CAVE_LEN = 0xA9DB0 - 0xA9D20          # 144 字节
NATIVE_RENDER = 0x128EAC
NR_ORIG = bytes.fromhex("10b5101c10f080fe11f012f810bd")

STUB_CLOCK_GETTIME = 0x6661C
STUB_USLEEP = 0x666E8

FRAME_NS = 16666666                    # 16.666666 ms

CODE = """
        push {r4, r5, r6, lr}
        sub  sp, #16
        mov  r6, r2
        movs r0, #1
        mov  r1, sp
        blx  #0x6661C
        mov  r0, r6
        bl   #0x139BB4
        bl   #0x139EDC
        movs r0, #1
        add  r1, sp, #8
        blx  #0x6661C
        ldr  r2, [sp, #8]
        ldr  r3, [sp]
        subs r2, r2, r3
        ldr  r4, [sp, #12]
        ldr  r5, [sp, #4]
        subs r4, r4, r5
        bpl  L1
        movw r5, #0xCA00
        movt r5, #0x3B9A
        add  r4, r4, r5
        subs r2, r2, #1
L1:
        cmp  r2, #0
        bne  Ldone
        movw r5, #0x502A
        movt r5, #0x00FE
        cmp  r4, r5
        bge  Ldone
        subs r0, r5, r4
        lsrs r0, r0, #10
        cbz  r0, Ldone
        blx  #0x666E8
Ldone:
        add  sp, #16
        pop  {r4, r5, r6, pc}
"""


def vaddr_to_off(path, vaddr):
    with open(path, "rb") as f:
        e = ELFFile(f)
        for s in e.iter_sections():
            if s["sh_type"] == "SHT_NOBITS":
                continue
            a, size = s["sh_addr"], s["sh_size"]
            if a and a <= vaddr < a + size:
                return s["sh_offset"] + (vaddr - a)
    raise RuntimeError("vaddr %#x not mapped" % vaddr)


def main():
    ks = keystone.Ks(keystone.KS_ARCH_ARM, keystone.KS_MODE_THUMB)
    code, _ = ks.asm(CODE, CAVE)
    code = bytes(code)
    print("例程长度 %d 字节（可用 %d）" % (len(code), CAVE_LEN))
    assert len(code) <= CAVE_LEN, "代码放不下"

    # nativeRender 入口改成跳转
    call_site = b""
    ks2 = keystone.Ks(keystone.KS_ARCH_ARM, keystone.KS_MODE_THUMB)
    call_site = bytes(ks2.asm("b.w #0x%X" % CAVE, NATIVE_RENDER)[0])
    tramp = call_site + b"\x00\xbf" * ((len(NR_ORIG) - len(call_site)) // 2)
    print("跳转桩 %d 字节: %s" % (len(tramp), tramp.hex()))
    assert len(tramp) == len(NR_ORIG), (len(tramp), len(NR_ORIG))

    raw = bytearray(open(SRC, "rb").read())

    off_cave = vaddr_to_off(SRC, CAVE)
    off_nr = vaddr_to_off(SRC, NATIVE_RENDER)

    cur_nr = bytes(raw[off_nr:off_nr + len(NR_ORIG)])
    print("nativeRender 当前字节:", cur_nr.hex())
    if cur_nr == tramp:
        print("已经是补丁后状态，跳过")
        return
    if cur_nr != NR_ORIG:
        raise RuntimeError("nativeRender 字节不符，拒绝修改")

    raw[off_cave:off_cave + CAVE_LEN] = code + b"\x00" * (CAVE_LEN - len(code))
    raw[off_nr:off_nr + len(NR_ORIG)] = tramp
    open(SRC, "wb").write(raw)
    print("已写入：code cave @%#x，跳转桩 @%#x" % (CAVE, NATIVE_RENDER))

    md = capstone.Cs(capstone.CS_ARCH_ARM, capstone.CS_MODE_THUMB)
    md.detail = True
    print("--- 改后例程反汇编 ---")
    for ins in md.disasm(code, CAVE):
        print("%08x  %-8s %s %s" % (ins.address, code[ins.address - CAVE:ins.address - CAVE + ins.size].hex(),
                                    ins.mnemonic, ins.op_str))
    print("--- 改后 nativeRender ---")
    for ins in md.disasm(tramp, NATIVE_RENDER):
        print("%08x  %-8s %s %s" % (ins.address, tramp[ins.address - NATIVE_RENDER:ins.address - NATIVE_RENDER + ins.size].hex(),
                                    ins.mnemonic, ins.op_str))


if __name__ == "__main__":
    main()
