# -*- coding: utf-8 -*-
"""
去掉「注册/登录 Com2uS Hive」弹窗，并模拟用户按下「否」。

背景：
  0x0D81B0  bl  isAuthUser
  0x0D81B6  cbz r0, 0xD81BE     ; 未登录 -> 弹窗
  0x0D81B8  bl  0xD810C         ; 已登录 -> 云端流程
  0x0D81BE  ... 0xD81E8 bl SetMessageBox(msg, 是, 否)
  0x0D81EC  movs r0,#1 ; 返回

  「否」的回调 MainMenu_HubLoginCancel(0xD821A) 做两件事：
      DialogNotify(1, 0)   -> bl 0x7AEF0
      返回
  只把 cbz 改成 return（第一版补丁）会让调用方一直等结果，画面卡在主菜单。

补丁：把 0xD81B6 起 10 字节改成
      movs r0, #1
      movs r1, #0
      bl   0x7AEF0        ; 等价于「否」——把结果 (1,0) 投递给弹窗队列
      b    0xD81EC        ; 直接返回 1
"""
import os
import sys

from elftools.elf.elffile import ELFFile
import keystone

BASE = r"D:\Desktop\inotia4\_apk\lib\armeabi-v7a"
SRC = os.path.join(BASE, "libgame.ui.so")

ADDR = 0xD81B6
OLD = bytes.fromhex("10b1" "fff7a8ff" "16e0d2f7")  # cbz r0,#0xd81be ; bl 0xd810c ; b 0xd81ec ; bl 0xab084
NEW = None                                  # 由 keystone 生成

CODE = """
movs r0, #1
movs r1, #0
bl   #0x7AEF0
b    #0xD81EC
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
    code, _ = ks.asm(CODE, ADDR)
    new = bytes(code)
    assert len(new) == 10, len(new)

    off = vaddr_to_off(SRC, ADDR)
    raw = bytearray(open(SRC, "rb").read())
    cur = bytes(raw[off:off + len(new)])
    print("目标处原字节:", cur.hex())
    print("将要写入:  ", new.hex())
    if not (cur == OLD or cur == new):
        raise RuntimeError("0x%X 处既不是原指令也不是补丁后指令，拒绝修改" % ADDR)
    raw[off:off + len(new)] = new
    open(SRC, "wb").write(raw)
    print("已写入 0x%X（跳过 Hive 登录弹窗并模拟按「否」）" % ADDR)

    # 反汇编自检（直接读改后的字节）
    import capstone
    md = capstone.Cs(capstone.CS_ARCH_ARM, capstone.CS_MODE_THUMB)
    md.detail = True
    print("--- 改后反汇编 ---")
    for ins in md.disasm(new, ADDR):
        print("%08x  %-10s %s %s" % (ins.address, new[ins.address - ADDR:ins.address - ADDR + ins.size].hex(),
                                     ins.mnemonic, ins.op_str))


if __name__ == "__main__":
    main()
