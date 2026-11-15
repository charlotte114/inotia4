# libgame.so 逆向分析

对象：`lib/armeabi-v7a/libgame.so`（1,725,020 B，32 位 ARM Thumb-2）
基线副本：`_apk/lib/armeabi-v7a/libgame.orig.so`（MD5 `831b9d36fbf4bcbc927e6bd86d8c8e2a`）
成品副本：`_apk/lib/armeabi-v7a/libgame.ui.so`（MD5 `e8ce46a91632efc108bd84332b879f48`，与成品包内一致）

---

## 1. ELF 结构

```
ELF32 / EM_ARM(40) / ET_DYN / EABI v5 / Thumb-2 / PIE
interp      /system/bin/linker
链接器      GNU gold 1.11
编译器      GCC 4.8 / 4.6 20120106 / 4.4.3 混合（多年代代码累积）
DT_NEEDED   libfmodex.so libfmodevent.so libGLESv1_CM.so libdl.so liblog.so libstdc++.so libm.so libc.so
DT_RELCOUNT 3795（前 3795 个重定位可跳过）
```

| 段 | vaddr | 大小 | 说明 |
|---|---|---:|---|
| `.dynsym` | 0x148 | 0x208C0 | 8332 项 |
| `.dynstr` | 0x20A08 | 0x2D40A | 185,354 B 符号名 |
| `.hash` | 0x4DE14 | 0x1027C | SysV hash |
| `.rel.dyn` | 0x5E090 | 0x78D8 | 3867 条 |
| `.rel.plt` | 0x65968 | 0x820 | 260 条 |
| `.plt` | 0x66188 | 0xC44 | 外部调用跳板（**ARM 态，必须 `blx` 进**） |
| `.text` | 0x66DD0 | 0xF5A34 | 1,006,132 B 代码 |
| `.ARM.extab` | 0x15C804 | 0x79D8 | 异常表 |
| `.ARM.exidx` | 0x1641DC | 0xC000 | 展开索引 |
| `.rodata` | 0x1701E0 | 0x2BC64 | 179,300 B 常量/字符串 |
| `.data.rel.ro.local` | 0x19DC68 | 0x1D4 | |
| `.data.rel.ro` | 0x19DE68 | 0x1CF0 | |
| `.got` | 0x19FC70 | 0x2390 | |
| `.data` | 0x1A2000 | 0x3978 | 图集名表等 |
| `.bss` | 0x1A5978 | 0x43342E | 4.2 MB 全局状态（全部玩法数据） |

> **关键：符号未 strip。** 8332 个动态符号里 6376 个 `STT_FUNC`（共 779,858 B，占 .text 77.5%）、1685 个 `STT_OBJECT`、268 个未定义导入。函数名全部可读，逆向成本极低。
> Thumb 函数的 `st_value` **带 bit0**（如 `C2S_HubMain = 0x6BD59`），处理时要 `& ~1`。

---

## 2. 导入函数画像（268 个未定义符号）

| 类别 | 代表 | 说明 |
|---|---|---|
| 图形 | `glClear` `glTexImage2D` `glDrawArrays` `glDrawElements` `glOrthox` `glFrustumx` `glCompressedTexImage2D` `glCopyTexSubImage2D` … 共 66 个 | **OpenGL ES 1.x 固定管线**（`libGLESv1_CM.so`），含 `glColor4x`/`glOrthox` 等定点接口 |
| 网络 | `socket` `connect` `select` `send` `recvfrom` `getaddrinfo` `gethostbyname` `inet_addr` `inet_ntoa` | **native 自己建 TCP 连接**，不经 Java |
| 文件 | `open` `read` `write` `lseek` `stat` `statfs` `mmap` `munmap` `opendir` `readdir` `realpath` `access` `chmod` `unlink` `rename` `mkdir` | 直接走 POSIX，配合 `MMappedFile` 读 assets |
| 线程/同步 | `pthread_create` `pthread_join` `pthread_mutex_*` `pthread_cond_*` `pthread_key_create` `pthread_setspecific` | |
| 时间 | `clock_gettime` `gettimeofday` `time` `localtime` `mktime` `nanosleep` `usleep` `clock` | |
| 音频 | `FMOD_EventSystem_*`（17 个）`FMOD_Event_*` `FMOD_EventGroup_*` `FMOD_EventParameter_*` | 来自 `libfmodevent.so` |
| 日志 | `__android_log_print` `__android_log_write` | |
| 其它 | `dlopen` `dlsym` `dladdr` `popen` `system` `kill` `getpid` `gettid` `sysconf` `uname` `setlocale` | `system()`/`popen()` 存在但**无调用点**（用 `re_xref` 全表扫过） |

**没有任何反调试导入**：无 `ptrace`，无 `frida`/`xposed`/`magisk` 字符串，无 `__system_property_get("ro.debuggable")` 检查。

---

## 3. 引擎与模块划分（按符号前缀，360 组）

```
COM2US GEM 引擎（GEM_LIB_*，源码路径见 §12）
├── 平台抽象层 CS_*（333）      fs / net / grp / uic / knl / hlp / mda / im / util
├── 图形 SGL_*（89）           Texture / Device / Font / PixelFormat
│   ├── GRP_*（79）            像素级 blit：Normal/Filter/Gray/Gamma/Sepia/Palette 565
│   ├── GRPX_*（54）           高层绘制：LoadImage/DrawPart/DrawPartFlip/WithMask/Flush
│   ├── CSOGL_*（56）          GL ES 后端实现
│   ├── CSFB_*（55）           软件帧缓冲后端实现（与 CSOGL 同接口，可切换）
│   └── SPR_*（19）            精灵：Draw/Rotate/FlipRotate/Scale/Pattern/Palette
├── 数据 EXCELDATA_*（19）     定长表加载、LZMA 解包、MemoryText
├── 资源 RES_* / FILEPACK_* / MMappedFile / ResourceManager / textureStore
├── UI 框架 UI_*（69）+ Control*（Object/Button/Scroll/Item/Skill）+ TouchHandle（42）+ InputLayer/InputQueue
├── 场景 Scene_*（189，54 个 POPUP_SC_* × 7 个回调）+ POPUPSTATE_*（12）+ STATE_*（34）
├── 任务管理 TM_*（7）         协作式任务链表，每帧 TM_Run 轮转
└── 第三方：libpng 1.2.24、zlib inflate、LZMA SDK、cJSON、nlohmann-HerculesJson、Base64、MD5、crc32/adler32
```

### 玩法系统（业务层）

| 前缀 | 数量 | 职责 |
|---|---:|---|
| `CHAR_*` | 354 | 角色：属性/状态/装备/技能/动作/AI/绘制/寻路/伤害计算 |
| `ITEMSYSTEM_*` | 75 | 物品：生成/词条/强化/镶嵌/宝石/鉴定/宝箱 |
| `SAVE_*` | 68 | 存档：分块读写 + 加密 + 云同步合并（`_v119` 新旧两套） |
| `MAP_*` | 55 | 地图：加载/图层/寻路/遮挡/震动/聚焦 |
| `UIMix_*` | 52 | 合成界面 |
| `MEM_*` | 51 | 内存池 + 带游标的顺序读（`MEM_ReadUint8_S` 等） |
| `SNASYS_*` | 46 | 精灵包（sprite/frame/animation/palette/costume/mani） |
| `CHARSYSTEM_*` | 45 | 角色池管理 + 掉落判定 |
| `UIEquip/UIInApp/UISkill_*` | 各 44 | 装备/内购/技能界面 |
| `UTIL_*` | 43 | 位操作/网络序/数组去重/网络检测 |
| `PARTY_*` | 36 | 队伍（3 人） |
| `MAPITEMSYSTEM/SOUNDSYSTEM` | 各 36 | 地面物品 / 声音 |
| `NET_*`（35） | 35 | 缓冲区序列化：`NET_WriteInt8/16/32` `NET_ReadString` … |
| `QUESTSYSTEM_*` | 35 | 任务 |
| `UIMercenary/UIDesc` | 各 35 | 佣兵/物品描述 |
| `UIStore_*` | 34 | 商店 |
| `EVTSYSTEM_*` | 33 | 剧情事件机 |
| `APPINFO/INVEN` | 各 33 | 设置 / 背包 |
| `C2S_*` | 31 | Com2uS Hub 客户端协议（登录/存档云同步/内购） |
| `MONSTERAI_*` | 22 | 14 种怪物 AI 脚本（`MONSTERAI_00`…`MONSTERAI_14`） |
| `MAPSYSTEM/MERCENARYSYSTEM/DEALSYSTEM` | 21/17/17 | 地图系统/佣兵/商店货架 |
| `GAMELOADER_*` | 14 | 分阶段加载（每阶段画进度条） |
| `NetworkStore_*` | 26 | 联网商店（已废弃） |

---

## 4. 启动与主循环

### 4.1 JNI 入口

| 符号 | 地址 | 作用 |
|---|---|---|
| `JNI_OnLoad` | 0x127A6C | 缓存 `JavaVM`/`JNIEnv`/类引用，注册 native 方法 |
| `nativePreInit` | 0x1294E8 | 读 `jGetRenderer`/`jGetOriginalWidth/Height`，`jSetOriginalSize` |
| `nativeInit` | 0x12A398 | `CS_knlGetVersionString` → `CS_getBuildVersion` → **`jProtectionInitialize`** → 4 次虚调用（模块注册）→ `TM_Initialize` |
| `nativeRender` | 0x128EAC | **每帧入口**，14 字节：`push {r4,lr}; mov r0,r2; bl TM_Run(0x139BB4); bl ThreadProc(0x139EDC); pop {r4,pc}` |
| `nativeEvent` | 0x1293AC | 触摸/按键事件入队（`handleCletEvent`） |
| `nativePause/Resume/Done/ReDraw` | 0x1294C8/0x129424/0x12A008/0x128E9C | 生命周期 |
| `nativeHerculesInit` | 0x67CAC | 20 字节，只返回 |
| `jExitPopup` `jGotoURL` `jGotoMoreGame` `jGotoPLUS` `jAlertPopup` `jOpenActivity` `jCloseActivity` `jShowLoadDialog` `jDismissLoadDialog` `jRequestNotice` `jGetStringFromXml(Ex)` `jGetSerialNo` `jGetCC_MD5` … | 0x67280–0x67D4C | 37 个 Java 回调桥（`j*` 前缀） |
| `Java_com_com2us_module_inapp_InApp_resultPostInApp` | 0xE94AC | **4292 B，全 so 第 10 大函数**：内购回调，解析 Google Play 回执 |
| `Java_com_com2us_peppermint_HubBridge_*` | 0xE76DC/0xE7714 | Hive 桥 |
| `Java_com_com2us_module_activeuser_ActiveUser_jni*` | 0xE7744–0xE8056 | ActiveUser（DID 上报、公告、条款） |
| `Java_com_com2us_module_mercury_Mercury_native*` | 0xEA6A0–0xEA708 | Mercury 推广 |
| `Java_com_com2us_wrapper_WrapperHttp_nativeConnectCB` | 0x128D88 | Java 侧 HTTP 回调 |
| `Java_com_com2us_wrapper_WrapperProtection_native*` | 0x12D1C8/0x12D4F8 | 反内存修改模块 |

### 4.2 帧循环（`MainProcess` 0x92650）

```
CS_knlCurrentTime() → 存全局 tick
UI_PopupProcess()
STATE_NextStartProcess()          ← 状态机推进（含 STATE_Set 切换）
if (g_bPopupMsgOn) UIPopupMsg_Process()
if (POPUPSTATE_Exist()) POPUPSTATE_Process()      ← 弹窗栈优先
else if (g_pfnExtraProcess) g_pfnExtraProcess()
NOTIFIER_Process(2)
SOUNDSYSTEM_Process()
CS_knlSetTimer(handle, max(now - frameStart, 10))   ← 最短 10ms 间隔
```
即：**弹窗栈 > 额外处理 > 通知器 > 声音**，最后用 `max(dt,10ms)` 限速。

### 4.3 顶层状态（`STATE_*` / `GAMESTATE_*`）

```
STATE_EnterInit   → STATE_ProcessInit    (0xD724C) 初始化/解包
STATE_EnterLogo   → STATE_ProcessLogo    (0xD73B0) logo
STATE_EnterTitle  → STATE_ProcessTitle   (0xD884C) 标题（点开始）
STATE_EnterMainMenu → STATE_ProcessMainMenu (0xD80B8) 存档列表
STATE_EnterGame   → STATE_ProcessGame    (0xD6FA4) 正式游戏
STATE_EnterGrade                      ← 评级/分级提示
GAMESTATE_*：EnterPlay/ProcessPlay/PressKeyPlay/DrawPlay（0xD71364–0xD71BB0）
              EnterEvent/ProcessEvent/DrawEvent（剧情）
              EnterMapChange/ProcessMapChange/DrawMapChange（过图）
```

`GAMELOADER_ProcessLoad`(0xAAE34) 是加载阶段机：每完成一步就 `GAMELOADER_FlushProgress` 画一次进度，按 `MEMORYTEXT` 取当前步骤文案。加载顺序（由调用序列还原）：

```
EXCELDATA_Create → MEMORYTEXT_Create_E
SNASYS_CreateGroup ×4 → SGRP_Initialize ×4 → IMGFONT_Create
OBJVIEWPOOL_Create → CHARSYSTEM_Create → MAPSYSTEM_Create → ACTSYSTEM_Create
ITEMSYSTEM_Create → BUFFSYSTEM_Create → HATESYSTEM_Create → PATHPOOL_Create
CHARSTATEPOOL_Create → EFFECTSYSTEM_Create → MONSYSTEM_Create → GOBJSYSTEM_Create
ASSYSTEM_Create → EVTSYSTEM_Create → QUESTSYSTEM_Create → NPCSYSTEM_Create
INSTANTMSGSYSTEM_Create → TEXTINPUTSYSTEM_Create → POS_Initialize
GENSYSTEM_Create → CHARLOCSYSTEM_Create → MAPTRASHSYSTEM_Create
NAMEHIDESYSTEM_Create → DROPSYSTEM_Create …
```

`GAME_Initialize`(0xAA6CC) 则是「新档/读档后」把上面这些系统逐个 `Initialize`，最后 `MAP_Load` + `PLAYER_SetMainPlayer` + 送新手物品。

---

## 5. 数据加载链

### 5.1 资源读取（不经 Java）

```
RES_LoadToHeap(name, &size)   @0x784B6
  ├ CS_knlGetResourceID(name, &size)  @0x138ECC → ResourceManager::Add(name, &size) @0x143628
  │   失败则 ResourceManager::Remove(name) @0x1423F0
  ├ CS_knlCalloc(size)
  └ CS_knlGetResource(id, buf, size)  @0x138DD8
       └ ResourceManager::FindInformation(id) @0x1422B8
          └ MMappedFile::Open(path, off, len, 1, 3) @0x141588
             └ MMappedFile::GetPointer() @0x140C08

RES_LoadToPool(name, &ptr, &size)  @0x784FC   = LoadToHeap + memcpy 到自己的堆
RES_MoveToFileEx(...)              @0x785F0   = 解 LZMA 后写回文件（热更新用）
```

`ResourceManager` 按文件名建索引，`MMappedFile` 直接把 APK 内 STORED 的 `assets` 映射进内存——这就是所有 `assets` 条目必须不压缩的原因。

### 5.2 表加载

```
EXCELDATA_LoadGameData @0xA98D8   "game.dat"
  → FILE_IsExist / EXCELDATA_LoadFullFile / LoadFullResource
  → MEM_ReadUint16_S(表数) / MEM_ReadUint24_S(偏移)
  → EXCELDATA_Create @0x6FEE8（4208 B，全 so 第 9 大函数）
     按 .dynsym 顺序把每张表指针塞进 XXX_pData，count/size 塞进 XXX_nRecordCount/nRecordSize

EXCELDATA_LoadMemoryText @0xA9750  → MEMORYTEXT_Create / Create_E
EXCELDATA_LoadFileText   @0xA9856  → FILETEXT_Load @0xA9D08   "filetext.dat"
FILEPACK_Load            @0xA99C0  "i_pack.dat"（增量资源包索引）
```

### 5.3 地图

```
MAP_Load(mapID, flag) @0xB4F38
  CS_knlSprintk("m%d.dat", id) → RES_LoadToPool
  首字节 == 1 → LZMA_Decode @0x77A1A
  MEM_ReadUint8_S ×3 → MAP_LoadBase(x,y,w,h) @0xB3AB0
     每格 2 字节：tile = ((b0 & 0x0F) << 8) | b1   （12 位，0..4095）
  MAP_LoadLayer(x,y,buf) @0xB4D58
     u16 layerType
     4 × u16 → MAPFEATURESYSTEM_AddLayer @0xB56CC
     u8 blockCount，每 block：u8（高 4 位 = 0..3 层）、u16 条数
       每条 4B：dx, dy, packed, tileLo
         packed bit0-2 = tile 高位；bit3 = 0x800 标记；bit4-7 = 类型 0..5
         类型 0/1/2/3 → MAP_AddLayerTile / AddShadowTile1 / AddShadowTile2 / AddTopTile
         类型 4      → MAP_AddNPCItemLocation（NPC/物件）
         类型 5      → MAPTRASHSYSTEM_Find + CHARSYSTEM_Produce（生成物/怪物点）
```

实测：m0 = 25×30=750 格 + 7 block（1/39/267/0/20/4/14 条），尾部剩 25 B（事件 id 表，未完全解析）。
`i_tile` 的 1983 个精灵段覆盖 tile id 0..1880。

### 5.4 精灵包

```
SNASYS_LoadPack(packID, subID) @0xC680C
  FILEPACK_Seek(0) → FILE_ReadUint32 = count
  FILEPACK_Seek(14 + 3*subID) → FILE_ReadUint24 ×2
     start = 值 & 0x7FFFFF,  end = 值 & 0x7FFFFF
     bit23(0x800000) = 该段 LZMA
  FILEPACK_Seek(start) → FILE_Read(len) → 可选 LZMA_Decode
  → SNASYS_LoadDataFromMemory @0xC677C
     段内又是「u32 count + 14B 头 + (count+1)×3B 偏移」的二级索引
```

**实测修正（比上面的原始反汇编结论更细）**：外层偏移表并不总是 `18 + 3*subID`，也不是所有包都 delta=0。
逐包验证（`tools/re_pack.py`，14,935 段全部解压成功）得到：

| 项 | 实际 |
|---|---|
| 表起点 | 多数包 18；`i_portrait` 是 **16**（前面还有 60 B 头） |
| delta | 多数包 0；`i_item`/`i_npc`/`i_tile`/`i_effect`/`i_efx`/`i_imoticon`/`i_portrait` 是 **1** |
| 段数 | 一般等于 `u32@0`；`i_portrait` 的 `u32@0=3328` 是**精灵总数**，表里只有 **14** 项（13 chunk + 哨兵） |

即真实段区间是 `[(off[i] & 0x7FFFFF) + delta, (off[i+1] & 0x7FFFFF) + delta)`，
delta=1 与 LZMA 容器「调用方传文件指针+1」是同一个 off-by-one 家族。
判定方法：掩码 + delta 后必须单调不减且末值恰好等于文件长度。

段内还有一层 LZMA 容器，13 字节头 + 裸流：

```
[0..4]   5B  props（5d 00 00 00 01 = lc3 lp0 pb2 dict16MB）
[5..8]   u32 解压后长度
[9..12]  u32 保留
[13..]   裸流
```
`i_portrait` 的段多一个前导 `0x00`，流从 +14 开始——这是最初「i_portrait 解不开」的真正原因，
不是索引表的问题。`tools/re_pack.py` 的 `unpack_segment()` 两种头都试。

19 个包合计 **14,935 段，全部解压成功，其中 6,886 段二次压缩，解压总量 6,199,134 B**。
段内二级索引以 `i_npc` 段 0 为例：外层 706 B → 解压 1887 B，二级头 `81 21 00 37 00 0F 00 31 00 1D …`。

六类对象由 `SNASYS_ReadObjectCountAll`(0xC6720) 读出：从偏移 4 起连读 7 个 u16 到 0x1C，
即 sprite/frame/animation/palette/costume/mani 各自的 count。

---

## 6. 存档系统

### 6.1 路径

```
SAVE_GetSaveFileName(slot, out) @0xBE768
  CS_knlSprintk(out, "save%d.dat", slot)
  HubSave_GetKey()      @0x69700
  HubSave_GetFolderName @0x6972C
     = 16 个字节的 MD5(包名) 拼成 32 位十六进制字符串（CC_MD5 @0x13785C）
  HubSave_MakeFullName  @0x697C4
     "/sdcard/Android/data/%s/files/%s"
```

所以存档实际落在 `/sdcard/Android/data/com.com2us.inotia4.cat/files/d332b56cf8a2b9c51bbe7f431ce6602f/save0.dat`
（`d332b56cf8a2b9c51bbe7f431ce6602f` = `md5("com.com2us.inotia4.cat")`，32 个十六进制字符）。
`HubSave_MakeFolder`(0x697A0) 用 `CS_fsIsExist`/`CS_fsMkDir` 建目录，`HubSave_MoveFile`(0x697F8) 做原子替换（先写新文件、`SAVE_SaveDataAsNameAndKey`、再 `CS_fsRemove` 旧文件）。

### 6.2 分块

`SAVE_Save/SAVE_Load` 把存档拆成固定块，每块一对函数：

| 块 | Save | Load |
|---|---|---|
| 头/版本 | `SAVE_SaveInformation` 0xBE960 | `SAVE_LoadInformation` 0xBE9D4 |
| Player | `SAVE_SavePlayer` 0xBF954 | `SAVE_LoadPlayer` 0xBEB10 |
| Character | `SAVE_SaveCharacter` 0xBEC20 | `SAVE_LoadCharacter` 0xBEE98 |
| Inventory | `SAVE_SaveInventory` 0xBF09C | `SAVE_LoadInventory` 0xBF11C |
| Item | `SAVE_SaveItem` 0xBEBA8 | `SAVE_LoadItem` 0xBEDC4 |
| Quest | `SAVE_SaveQuest` 0xBF1BC | `SAVE_LoadQuest` 0xBF274 |
| Event | `SAVE_SaveEvent` 0xBF30C | `SAVE_LoadEvent` 0xBF3F0 |
| ETC | `SAVE_SaveETC` 0xBF544 | `SAVE_LoadETC` 0xBF5C0 |
| 佣兵槽 | `SAVE_SaveMercenarySlot` 0xBF83C | `SAVE_LoadMercenarySlot` 0xBEA48 |

另有 `SAVE_Merge/UnMerge`（云存档合并）、`SAVE_ChangeKey`（换密钥）、`_v119` 后缀的兼容旧版函数。

### 6.3 加密

```
ENCRYPT_Process2(buf, len, mode, key) @0x755F0
  mode == 0（加密）：
      seed = MATH_GetRandom(0, 0xFF)     @0x77BA0
      buf[len]   = seed
      buf[len+1] = ?
      逐字节： buf[i] ^= key[i % keylen] + ENCRYPT_GetKey(i, acc)
  mode != 0（解密）：读回 seed 反向还原
ENCRYPT_GetKey @0x755E4
```
`SAVE_SaveDataAsNameAndKey` / `SAVE_LoadDataAsNameAndKey` 是带名字+密钥的变体，供云同步用。
`jProtectionInitialize` 走的是另一套：`loadProtectionFile` → `CS_fsOpen/Seek/Read` → `CS_knlGetCom2usUDID` → `CS_knlDecryptData`。

---

## 7. UI 框架

### 7.1 控件对象

`ControlObject_*`（101 个）是基类，字段偏移由 `Set/Get` 的存取指令直接读出：

| 偏移 | 字段 | 证据 |
|---|---|---|
| `+0x00` | ID | `ControlObject_SetID`(0x72002) `str r4,[r5]` |
| `+0x04` | Type | `ControlObject_SetType`(0x71DD4) `str r1,[r0,#4]` |
| `+0x08` | Active | `ControlObject_SetActive`(0x71DE8) 写 `'1'`(0x31)/`'0'`(0x30) 到 `+8` |
| `+0x0C` | Show | `ControlObject_SetShow`(0x71E08) 写 `'1'`/`'0'` 到 `+0xC` |
| `+0x10` | Rect.x | `ControlObject_SetRect`(0x71EE8) `str r6,[r5,#0x10]` |
| `+0x14` | Rect.y | 同上 `str r7,[r5,#0x14]` |
| `+0x18` | Rect.w | 同上 `str r3,[r5,#0x18]`（来自 `[sp,#0x28]`） |
| `+0x1C` | Rect.h | 同上 `str r3,[r5,#0x1C]`（来自 `[sp,#0x2C]`） |
| `+0x20` | FocusType | `ControlObject_SetFocusType`(0x7201A) `ldr/str [r0,#0x20]` |
| `+0x28` | UserType | 0x71DDA `ldr r0,[r0,#0x28]` / 0x71DDE `str r1,[r0,#0x28]` |
| `+0x2C` | 子节点链表头 | 0x71E24 `ldr r0,[r0,#0x2C]` |

> 注意 Show/Active 存的是**字符** `'1'`/`'0'`（0x31/0x30），不是 0/1。这是这套 UI 框架的历史包袱，读的时候要按字节比较。

子类：`ControlButton`(19)、`ControlScroll`(26)、`ControlItem`(11)、`ControlSkill`(12)。

### 7.2 控件树与绘制

```
UI_CreateGroupBaseControl(layer, x, y, w, h) @0x7ADB4   ← 所有界面容器的起点
ControlObject_AddControlObject(parent, child) @0x72274
ControlButton_Create(...) @0x7891C
ControlObject_SetFocusType @0x7201A
ControlObject_SetControlEventCallType @0x71E56
```
绘制统一走 `GRPX_Start`(0x68EDC) … `GRPX_End`(0x68EEC) 包夹，中间 `GRPX_DrawPart*` 画图集切片。
`UI_SetPopupProcessInfo`(0x7AEF0) 是弹窗结果投递入口（`(id, result)`），`UI_PopupProcess`(0x7AE7C) 是弹窗处理器。

### 7.2b 分辨率与缩放（改 UI 坐标必看）

这套引擎把**基准分辨率固定为 960×640**（`0x3C0`×`0x280`），由 `resolutioninit`(0x6C31C) 在启动时算好：

```
resolutioninit @0x6C31C
  s14 = (float)devW ;  s15 = (float)devH
  if (devW / devH > 1.5)          ← 比 3:2 更宽（现代手机都是）
        scale = 640.0f / devH     ← 按高度贴
  else  scale = 960.0f / devW     ← 按宽度贴
  resW = (int)(devW * scale)
  resH = (int)(devH * scale)

GetResolutionWidth()  @0x6C378   return g_resW            ; 全局 +8
GetResolutionHeight() @0x6C384   return g_resH            ; 全局 +0xC
CalcResolutionWidth() @0x6C390   (g_resW - 0x3C0 + sign) >> 1   ← 960 画布外的左偏移
CalcResolutionHeight()@0x6C3A8   (g_resH - 0x280 + sign) >> 1
GetScalevalue()       @0x6C3C0   (float)g_resH / 640.0f   ← 全局缩放因子（除数就是 0x6C3DC 的 640.0f）
GetScalePlusW/H()     @0x6C3E4 / 0x6C428   带 0x258/0x25B 修正的缩放版
```

所以 `UIPlay_Create` 里所有 `movs r3,#0x9b` 之类的坐标都是**960×640 基准下的值**，运行时再乘 `GetScalevalue()` 换算到实际屏幕。

**但要分两派**（实测各函数调用表）：

| 用法 | 代表函数 | 说明 |
|---|---|---|
| 调 `GetScalevalue` / `GetScalePlusW/H` | `STATE_DrawMainMenu`(0xD7940) | 绘制时自己乘缩放，坐标写死 960×640 也正确 |
| 只调 `SGL_GetScreenWidth/Height` | `UIPlay_Create`(0x8978C)、`Scene_Init_POPUP_SC_SAVESLOT`(0xD473C) | 用**实际屏幕像素**做基准（如 `x = 屏宽 - 93`），坐标是物理像素 |
| 调 `CalcResolutionWidth` | `STATE_EnterMainMenu`(0xD75B8) | 主菜单按「960 画布外的左偏移」居中 |

改坐标前先确认目标函数属于哪一派，否则会差一个 1.6875 倍。
（实测魅族 20：逻辑坐标 2400×1080，比例 2.222 > 1.5 → 按高度贴，scale = 640/1080，resW = 1422，resH = 640。）

### 7.3 场景（弹窗）注册表

54 个 `POPUP_SC_*`，每个 7 个回调（`Scene_Init/Process/Draw/Event/Event_KeyPress/Resume/Terminate`）：

```
POPUP_SC_CHARACTER_INFO  POPUP_SC_CHOICE       POPUP_SC_DAILY_REWARD
POPUP_SC_EQUIP           POPUP_SC_INAPP_ARMOR  POPUP_SC_INAPP_GEMSHOP
POPUP_SC_INAPP_GOODS     POPUP_SC_INAPP_HOT    POPUP_SC_INAPP_PACKAGE
POPUP_SC_INAPP_WEAPON    POPUP_SC_INPUT_ITEMCOUNT
POPUP_SC_MERCENARY_MANAGER  POPUP_SC_MIX       POPUP_SC_NPC
POPUP_SC_NPC_QUEST       POPUP_SC_NPC_REST     POPUP_SC_NPC_REVIVE
POPUP_SC_OPTION_MMENU    POPUP_SC_QUESTMENU    POPUP_SC_SAVESLOT
POPUP_SC_SELECT_CHARACTER POPUP_SC_SHORTCUT_MENU  POPUP_SC_SKILL
POPUP_SC_STORE           POPUP_SC_SYSTEMMENU   POPUP_SC_WIPEOUT
POPUP_SC_WORLDMAP
```
栈由 `POPUPSTATE_Push/Pop/Process/Event`（0xBC68C 起，每项 32 字节：`{sceneID, flag, Init, Process, Draw, Event, Terminate, Resume}`）管理。

### 7.4 图集目录表

`.data:0x1A102C` 起 **98 项 × 64 字节**的定长结构，前 16 字节是文件名：

```
i  文件名                i  文件名
0  portrait_001.png      49 UI_BUT_JP_004.png
1  portrait_002.png      ...
…                        79 UI_TITLEMAIN_01.png
13 portrait_014.png      80 UI_TITLEMAIN_02.png
14 ui_002.png            81 UI_TITLEMAIN_BUT_ENG_001.png
15 UI_003.png            …（7 种语言各 1）
…                        88 UI_TITLESLOT_01.png
22 UI_015.png            89 UI_TITLE_SETTINGS_01.png
23 item_001.png          90 UI_013.png
…                        91 UI_cashitem88icon.png
31 item_009.png          92 UI_010.png
32 UI_iconskill75_001    93 UI_011.png
…                        94 UI_012.png
35 UI_iconskill75_004    95 ETC_ban_001.png
36 UI_BUT_ENG_001.png    96 UI_BUT_RightCancel.png
…                        97 UI_BUT_Probability.png
```

`IMGSYS_UnitLoad(i)`(0x69A40) = `GRPX_LoadImage(&g_imgTable[i])`，`IMGSYS_GetLoc(i)`(0x69C68) 用 `cmp r0,#0x61` 的 switch 分发坐标。
`Default.png` / `GRB.png` / `Googleplay.png` / `ETC_ban_002(_K).png` 不在表里，由代码按名直取。

完整列表：`data/imgtable.txt`（`tools/re_imgtable.py` 生成）。

---

## 8. 网络

### 8.1 服务器

| 用途 | 地址 | 位置 |
|---|---|---|
| 游戏 TCP 服务器 | `inotia4.com2us.net` | `.rodata:0x174891`，由 `Network_Connect`(0xDB0CC) 的 `CS_netConnect` 使用 |
| Hub / 计费 / 推送 | `https://api-gsp.qpyou.cn/` | `.rodata:0x1706C2`（Java 侧另有 50 处 `*.qpyou.cn`） |
| 条款 | `http://terms.withhive.com/terms/policy/view/M31` | `.rodata:0x1725C3` |
| 公告图 | `https://image-glb.qpyou.cn/markup/img/2015/tf/inotia4_1.html` | `.rodata:0x1725F3` |
| 更多游戏 | `http://m.com2us.com/r?c=2760` | `.rodata:0x17265F` |

### 8.2 连接流程

```
Network_Connect(ip) @0xDB0CC
  CS_knlGetSystemProperty("MODEL",    buf, 0x32)   ← 设备信息上报准备
  CS_knlGetSystemProperty("SYSTEMVERSION", buf, 0x2A)
  CS_knlSprintk(buf, "UDID")                        ← 拼设备唯一号
  Network_SetState(1)
  OpenActivity()                                    ← 唤起 Activity
  CS_netConnect(0x139B10)                           ← 实际 connect()
  CS_knlCurrentTime() → 记 startTime
  CS_knlSetTimer(100ms 轮询)                        ← 每 100ms 检查一次
Network_ConnectCB @0xDBBFC
  if (state != 1) → CS_knlUnsetTimer; return
  if (now - startTime < 45000) → 再 CS_knlSetTimer(100ms)，继续等
  else → CS_knlUnsetTimer，放弃
NW_Connect / NW_SOCKET_CONNECT_CB / NW_SOCKET_READ_HEADER_CB / NW_SOCKET_READ_DATA_CB
  ← 应用层协议：读头 → 读体 → NW_C2SReceive
NET_WriteInt8/16/32, NET_ReadString …       ← 报文序列化
C2S_*（31 个）                               ← Hub 客户端协议：guest/create、guest/auth、
                                               savedata/upload、savedata/download、
                                               savedata/delete、device/registered、
                                               migration/hubv1、social/send_app_message …
```
`CS_netConnect` 是**同步阻塞**调用（内部走 `connect()`），应用层的 45 秒窗口只能在它返回后生效；
所以服务器下线时，真正的耗时由内核 SYN 重传决定（实测 20 秒），45 秒只是「返回后还不通就彻底放弃」的上限。


### 8.3 网络状态检测

`UTIL_CheckAirplane`(0xCCC68)、`UTIL_CheckRoamig`(0xCCCC0)、`UTIL_CheckNetworkDisable`(0xCCCBc)、
`UTIL_CheckDSLock`(0xCCCAC)、`CS_netGetActiveNetwork`/`CS_netGetActiveWiFi` 决定是否发请求。

---

## 9. 反修改（Protection）模块

`WrapperProtection` 是 Com2uS 自带的内存修改检测，全在 0x12BB60–0x12D550：

| 函数 | 地址 | 作用 |
|---|---|---|
| `jProtectionInitialize` | 0x12D4D0 | 在 `nativeInit` 里调用：`getAppData` → `finishDirectly` → `jGetMemoryHackCheckType` |
| `getAppData` | 0x12D3E8 | `loadProtectionFile` → `makeHackAppList` → `getCurrentTime` → `jGetDataFromWeb` |
| `loadProtectionFile` | 0x12D1E8 | `CS_fsOpen/Seek/Read` 读文件 → `CS_knlGetCom2usUDID` → `CS_knlDecryptData` |
| `makeHackAppList` | 0x12C768 | 分配内存 → `decryptString` 解出**作弊 App 包名黑名单** |
| `decryptString` | 0x12BC00 | 逐字节 `idiv` 取模的字符串解密 |
| `saveProtectionFile` | 0x12C900 | 写回检测结果 |
| `finishDirectly` | 0x12BCD0 | 命中黑名单时 `exit`/`kill` 进程 |
| `getDataFromWebDirectly` | 0x12CCB0 | 从服务器拉最新黑名单 |
| `jGetMemoryHackCheckType` | 0x67D4C | 由 Java 传入的检测等级 |

**`getDataFromWebDirectly` 在成品包里被整段 NOP 掉了**（0x12CCCA–0x12CCDD，20 字节），
这是重打包作者 Noname 干的，不是我们改的；对应的笔记 `lib/armeabi-v7a/关于so的改动.txt` 就是他漏删的。
NOP 之后该函数在 0x12CCDE 直接 `b 0x12CDFA` 跳过整段 JNI 调用，等于不联网取黑名单。

---

## 10. 音频

```
LoadFMOD(path) @0x68590
  FMOD_EventSystem_Create / Init / Load("Inotia_03.fev")
  FMOD_EventSystem_SetMediaPath("/sdcard/Android/data/%s/files/SND/")
FMODsndPlay / FMODsndBGMPlay / FmodSoundID / FmodSoundIDStop / FmodSoundIDPause
FMODsetParameter / FMODgetParameter / FMODgetVolume / FMODupdate / IsFmodPlaying
```
事件名形如 `inotia_03/BGM/...`、`inotia_03/SE/...`，与 `SOUNDINFOBASE`(99 条)、`SOUNDEFFECTBASE`(170)、`SOUNDBGMBASE`(14) 三张表对应。
另有 `SOUNDSYSTEM_*`（36 个）是游戏层封装：`SetBGM`/`PlayEx`/`StopAll`/`GetVolumeBGM`…

---

## 11. 关键函数索引

| 功能 | 符号 | 地址 | 大小 |
|---|---|---|---:|
| 世界地图生成 | `WORLDMAPBUILDER_Maker` | 0xCD1ED | **18,352** |
| 精灵绘制（含旋转/翻转） | `SPR_DrawEx` | 0xC47F9 | 6,676 |
| zlib 解压 | `inflate` | 0x12E231 | 5,036 |
| 事件系统主循环 | `EVTSYSTEM_Process` | 0xA82F1 | 4,788 |
| 精灵绘制（翻转旋转） | `SPR_DrawFlipRotate` | 0xC30B1 | 4,676 |
| 联网商店数据 | `NetworkStore_InitializeMenuData` | 0xDC34D | 4,480 |
| 内购回调 | `InApp_resultPostInApp` | 0xE94AC | 4,292 |
| 表加载核心 | `EXCELDATA_Create` | 0x6FEE8 | 4,208 |
| 精灵绘制（旋转） | `SPR_DrawRotate` | 0xC2059 | 4,184 |
| 角色绘制 | `CHAR_Draw` | 0x9BBC1 | 2,016 |
| 掉落判定 | `CHARSYSTEM_DropItem` | 0xA43C0 | — |
| 队伍掉率加成 | `PARTY_GetItemChance` | 0xBB6F0 | — |
| 背包金钱 | `INVEN_GetMoney` / `SetMoney` / `AddMoney` / `MinusMoney` | 0xACAF8 / 0xACB14 / 0xACB3C / 0xACC64 | |
| 属性重置 | `CHAR_InitializeStatus` | 0x9C5A8 | |
| 主菜单 HUD 创建 | `UIPlay_Create` | 0x8978C | 1,436 |
| 主菜单绘制 | `STATE_DrawMainMenu` | 0xD7940 | 1,912 |
| 存档文件名 | `SAVE_GetSaveFileName` | 0xBE768 | 80 |
| 存档加密 | `ENCRYPT_Process2` | 0x755F0 | 260 |
| 消息弹窗（按文本表 id） | `UIPopupMsg_CreateOKFromTextData` | 0x8BD40 | |
| 消息弹窗（按字符串） | `UIPopupMsg_CreateFromTextData` | 0x8BCE8 | |
| 弹窗结果投递 | `UI_SetPopupProcessInfo` | 0x7AEF0 | |
| Hive 登录状态 | `isAuthUser` | 0x6B6FC | |
| Hive 主入口 | `C2S_HubMain` | 0x6BD58 | 120 |
| 更多游戏 | `GotoHubMain` | 0x68DD4 | 4 |

---

## 12. 泄露的原始源码路径（8 条）

`.rodata` 里保留了编译机的绝对路径，可直接还原工程结构：

```
/Users/sanghyun/Desktop/com2us/task/Inotia/Android/AndroidStudioPorting/
  Inotia4_Google_Global/src/main/jni/gamesrc/
    GameCode/w_HubSave.c
    Hub/Hub2Extend.c
    Source/GEM_LIB_InputQueue.c
    Source/GEM_LIB_Logger.c
    Source/GEM_LIB_Stack.c
    Source/Ino3_UI.c
    Source/s_character.c
    Source/s_save.c
```

从命名可以确认：**引擎叫 GEM（`GEM_LIB_*`），是从 Inotia 3 移植来的（`Ino3_UI.c`）**，用 Android Studio 移植工程（`AndroidStudioPorting`）重编到 Android。

---

## 13. 本包对 so 的 10 处改动（37 字节）

基线 `libgame.orig.so` → 成品 `libgame.ui.so`。补丁链：
`patch_uibtn.py`(orig→ui0) → `patch_hub.py`(ui0→ui) → `patch_hublogin.py`(ui→ui) → `patch_freeability.py`(ui→ui) → `patch_msg_reset.py`(ui→ui)

| # | 地址 | 函数 | 原字节 | 新字节 | 反汇编 | 语义 |
|---|---|---|---|---|---|---|
| 1 | 0x6BD58–0x6BD5B | `C2S_HubMain` | `10 b5 8e b0` | `00 20 70 47` | `push {r4,lr}; sub sp,#0x38` → `movs r0,#0; bx lr` | HIVE 主入口置空。调用方两处：`ExeHubButton`(0xD75AC) 直接 `bl`，`MainMenu_HubLoginOK`(0xD75B4) → `GotoHubMain`(0x68DD4) → `b.w`。**底部「更多游戏」是另一条链**：`0xD7568 bl GotoShowMoreGames`(0x68DD0) → `b.w onHubMoreGames`(0x6B7D4) → `CS_hubShowDialog` 类调用，未被本次补丁触及 |
| 2 | 0x897E2 | `UIPlay_Create`+0x56 | `9b` | `e8` | `movs r3,#0x9b` → `#0xe8` | 保存按钮 y 155→232（原收费商店位置） |
| 3 | 0x897FA–0x897FD | `UIPlay_Create`+0x6E | `df f8 f4 34` | `1b e0 00 bf` | `ldr.w r3,[pc,#0x4f4]` → `b 0x89834; nop` | 整块跳过收费商店按钮创建 |
| 4 | 0x8E9B2–0x8E9B5 | `UISkill_ButtonSkillPointResetExe`+0x6 | `44 f6 20 61` | `4f f0 00 01` | `movw r1,#0x4E20` → `mov.w r1,#0` | 技能点重置判定价 20000→0 |
| 5 | 0x8E9BA–0x8E9BD | 同上+0xE | `44 f6 20 60` | `4f f0 00 00` | `movw r0,#0x4E20` → `mov.w r0,#0` | 技能点重置扣款 20000→0 |
| 6 | 0x8E9C6 | 同上+0x1A | `20` | `8d` | `movs r0,#0x20` → `#0x8D` | 成功提示改用消息 0x8D（技能点已返还） |
| 7 | 0xD1DA6–0xD1DA9 | `CharacterInfo_ButtonStatResetExe`+0x6 | `44 f6 20 61` | `4f f0 00 01` | 同上 | 能力点重置判定价→0 |
| 8 | 0xD1DAE–0xD1DB1 | 同上+0xE | `44 f6 20 60` | `4f f0 00 00` | 同上 | 能力点重置扣款→0 |
| 9 | 0xD1DBA | 同上+0x1A | `20` | `8c` | `movs r0,#0x20` → `#0x8C` | 成功提示改用消息 0x8C |
| 10 | 0xD81B6–0xD81BF | 匿名（0xD81A8 内） | `10 b1 ff f7 a8 ff 16 e0 d2 f7` | `01 20 00 21 a2 f7 99 fe 15 e0` | `cbz r0,0xD81BE; bl 0xD810C; b 0xD81EC` → `movs r0,#1; movs r1,#0; bl 0x7AEF0; b 0xD81EC` | 跳过 Hive 注册/登录弹窗，直接投递「否」的结果 |

### 补丁 10 的原理（容易踩坑）

原逻辑：
```
0xD81A8  push {r0-r4,lr}
0xD81B0  bl  isAuthUser(0x6B6FC)
0xD81B4  mov r4,r0
0xD81B6  cbz r0, 0xD81BE     ; 未登录 → 弹窗
0xD81B8  bl  0xD810C         ; 已登录 → 云端流程（见下）
0xD81BC  b   0xD81EC
0xD81BE  bl  GetConfigLanguage(0xAB084)
         ... bl GetCharStringFromPlist(0x68E48) 取文案
0xD81E8  bl  SetMessageBox(0x8BE08)   ; 弹「注册/登录 Com2uS Hive」
0xD81EC  movs r0,#1; add sp,#0x10; pop {r4,pc}
```

已登录分支 `0xD810C` 做的是**宝石弹窗检查**：
```
0xD811E  bl  C2S_HubIsRequiredGemPopup(0x6AF10)
0xD8124  cbz r0, 0xD817A        ; 不需要 → 直接 UI_SetPopupProcessInfo(1,2)
0xD812A  bl  C2S_HubSetbNeedGemPopup(0x6AF20)   ; 清标记
0xD813E  bl  GetConfigLanguage / GetCharStringFromPlist 取文案
0xD8174  bl  UIPopupMsg_CreateOK(0x8BDD4)
```

弹窗的「否」回调 `MainMenu_HubLoginCancel`(0xD821A) = `movs r0,#1; movs r1,#0; bl UI_SetPopupProcessInfo; pop; b STATE_HideModuleMainMenu`。

**第一版只把 `cbz` 改成 `b 0xD81EC` 会卡死**：调用方在等弹窗结果，不收结果就不往下走。
定稿必须显式投递 `UI_SetPopupProcessInfo(1, 0)`，等价于「每次自动按否」。

### 消息 id → 文本映射（TEXTDATABASE，game.dat 第 73 张表，190 条 × 2B）

| 消息 id | memorytext 索引 | 原文 |
|---:|---:|---|
| `0x0A` | 35326 | 您的金币不足。 |
| `0x20` | 35348 | 购买成功。 |
| `0x8C` | 35456 | 能力点已被初始化。→ 现「能力点已返还」 |
| `0x8D` | 35457 | 技能点已被初始化。→ 现「技能点已返还」 |
| `0x8E` | 35458 | 增加了佣兵槽。 |

---

## 14. 已确认的数值/结构常量

| 常量 | 位置 | 含义 |
|---|---|---|
| `movw #0x4E20` | 0x8E9B2/0x8E9BA/0xD1DA6/0xD1DAE/0x843A2/0x843AA | 20000 铜币 = 2 金币（重置/佣兵格价格） |
| `movw r1,#0xFA` | 0xA4598 | 掉落率除数 250（rate/2500 × 队伍加成） |
| `movw r4,#0xAFC7` | 0xDBC12 | 连接超时 45000 ms |
| `cmp r2,#0xA` | 0x926BE | 帧最短间隔 10 ms |
| `mov.w fp,#0x50` | 0x8992E | 技能按钮边长 80 |
| `mov.w r8,#0x4B` | 0x897AC | 左侧竖排按钮（保存/收费商店/菜单）边长 75，`SetRect` 的 w=h=r8 |
| `mov.w sl,#0x4E` | 0x897B0 | 左侧竖排按钮用的图集分组号 78 |
| `sub.w fp,r7,#0x5D` | 0x897BE | `fp = 屏宽 - 93`，右侧竖排按钮的 x |
| `movw r3,#0x135` | 0x89856 | 游戏菜单按钮 y = 309 |
| 0x17B7F8 / 0x17B810 | .rodata | 6 个技能按钮距右/距底 `[462,362,262,249,181,87]` / `[89,89,89,179,242,245]` |
| `cmp r0,#0x61` | 0x69C7A | 图集索引上限 97（表长 98） |
| `movs r0,#0x4F` / `#0x50` | 0xD7948/0xD7958 | 主菜单背景用图集 79(UI_TITLEMAIN_01)/80(UI_TITLEMAIN_02) |

---

## 15. 分析工具（本轮新增，位于 `tools/`）

| 脚本 | 用途 |
|---|---|
| `re_calls.py` | 全 .text 线性扫描 `bl/b.w`，建调用图；`modules` / `callers` / `callees` / `edges` 四种视图 |
| `re_xref.py` | 字符串 xref：只认 `ldr rN,[pc,#imm]` + 后续 5 条内 `add rN,pc` 的 PIC 套路，避免把 GOT 索引误判。**注意 `ldr` 与 `add` 之间常夹 `mov`/`movs`，所以按「窗口」匹配而不是「相邻」** |
| `re_modules.py` | 按 `XXX_` 前缀导出 360 个模块的函数清单 → `data/sym_by_module.txt` |
| `re_imgtable.py` | 解析 `.data:0x1A102C` 的 98 项图集目录，并与 APK 内 `*.png.jpg` 做双向覆盖校验 → `data/imgtable.txt` |
| `re_pack.py` | 解析 `i_*.dat` 精灵包（外层 3B 偏移 + 段内二级索引），统计 count/压缩段/解压量 |
| `re_map.py` | 解析 `m*.dat` 地图（地形层 + 图层 block + 4B 记录），验证尾部剩余字节 |
| `re_assets.py` | 批量解 549 个 `game_res` 文件，输出形态/条数/记录长度 → `data/survey_game_res.txt` |
| `re_images.py` | 读 PNG IHDR，导出 103 张图集的真实尺寸 → `data/survey_images.txt` |
| `re_libs.py` | 三个 so 的 ELF 概况（依赖/符号数/编译器） |
| `re_strings.py` | 按关键字搜 so 内字符串并打印上下文 |
| `re_mutf8.py` | 按 MUTF-8 规范正确解码 dex 字符串（长度前缀是 UTF-16 码元数，不是字节数） |
| `re_dexstrings.py` / `re_dexreadme.py` | 导出 dex 字符串池 / 作者留言 |
| `re_pack_probe.py` | 穷举 `(起始, 宽度, 标志位)` 组合来定位未知偏移表 |
| `re_atlas.py` | 早期探针（已被 `re_pack.py` 取代，保留作对照） |

依赖：`capstone 5.0.9`、`pyelftools 0.33`、`keystone-engine 0.9.2`。
> capstone 5 的坑：`ARM_OP_MEM` / `ARM_REG_PC` 已移到 `capstone.arm` 子模块，顶层 `capstone` 里没有。

---

## 16. 未解出的部分（明确记录，避免重复劳动）

| 项 | 现状 |
|---|---|
| `m*.dat` 尾部剩余字节 | 每个地图末尾 2–49 B 未解析，疑为「事件 id 列表」（`MAP_AddEventID` 0xB5xxx） |
| `i_*.dat` 的头部字段 | 各包取值不同（`i_npc`=`a6 00 02 00 97 00 4b 00 …`、`i_player`=`86 0b 10 00 dc 03 f0 00 …`），疑为 7 个 u16 的「各类对象总数」，与 `SNASYS_ReadObjectCountAll` 读 7 个 u16 吻合，但字段顺序未逐一验证 |
| `m*.dat` 的 `layerType` / 4 个 `u16` | `layerType` 取值 0/59/68/91/99 等，与 `MAPFEATURESYSTEM_AddLayer` 的语义未完全对上 |
| `Protection` 黑名单内容 | `makeHackAppList` 里 `decryptString` 的密钥未还原，黑名单是加密常量 |
| `eventdata` 的 3 种命令语义 | 只知 `EVTCMDBASE` 78 条全是 1/2/3，各命令参数含义未解 |
| `i_portrait` 的段内结构 | 外层 13 个 chunk 已能全部解压（合计 223,677 B），但段内精灵记录（`81 xx 00 yy 00 …`）的字段布局与其它 `i_*` 不同，未逐字段解析 |

### 本轮已解决、可删除的旧疑问

| 旧结论 | 修正 |
|---|---|
| `i_portrait` 索引表读不出来 | **已解**：表起点 16、delta=1、14 项（13 chunk + 哨兵），`u32@0=3328` 是精灵总数而非段数；段头多一个 0x00 所以流从 +14 起 |
| 精灵包统一「18 + 3*subID、delta=0」 | **修正**：表起点与 delta 逐包不同，见 §5.4 |
| `C2S_HubMain` 同时管「更多游戏」 | **修正**：「更多游戏」走 `GotoShowMoreGames`(0x68DD0) → `onHubMoreGames`(0x6B7D4)，与 `C2S_HubMain` 无关 |
| `Network_Connect` 读 `PHONENUMBER`/`AIRPLANE_MODE` | **修正**：读的是 `MODEL` 与 `SYSTEMVERSION`，再拼 `UDID`；`AIRPLANE_MODE` 由 `UTIL_CheckAirplane`(0xCCC68) 单独读 |
| `ControlObject` 的 `+8`=UserType、`+0xC`=Show、`+0x10`=Active | **修正**：`+8`=Active、`+0xC`=Show、`+0x10..0x1C`=Rect(x,y,w,h)、`+0x20`=FocusType、`+0x28`=UserType、`+0x2C`=子节点链表头 |
| UI 坐标是「物理像素」 | **修正**：分两派——`STATE_DrawMainMenu` 乘 `GetScalevalue()`（960×640 基准），`UIPlay_Create` 直接用 `SGL_GetScreenWidth/Height`（物理像素），见 §7.2b |

---

## 17. 融合器（道具合成）

村里「融合器」NPC（`NPCFUNCBASE` 功能 35「道具合成」）打开一套五类合成的界面，
不是单一功能。以下结构已用「材料区间必须铺满材料表」自证（见 §17.4）。

### 17.1 五类合成

界面菜单项是 `memorytext_zhhans` 里连续的 5 条（35290–35294）：

| 文本 id | 名称 | 说明 |
|---|---|---|
| 35290 | 药水合成 | 药水逐级合成 |
| 35291 | 宝石强化 | 3 颗同级宝石 → 1 颗高一级 |
| 35292 | 宝石孔生成 | 给装备加一个宝石孔 |
| 35293 | 混沌合成 | 重掷装备数值（混沌 / 深渊混沌两档） |
| 35294 | 传说装备 | 用材料直接做出指定传说装备 |

代码侧对应 `UIMix_*`（52 个符号）：`UIMix_ButtonMenuListExe` 就是这 5 项的菜单
分派，分别调用 `UIMix_SetType(0..4)`；`UIMix_StartMix` 再按类型走不同分支。

### 17.2 `RECIPEBASE`（12 字节/条，69 条）

| 偏移 | 类型 | 含义 |
|---|---|---|
| +0 | u16 | 配方名（文本 id） |
| +2 | u16 | 产物物品（`ITEMDATABASE` 下标）；**64 是占位条目「装备」**，表示改造玩家选中的那件装备，而不是产出新物品 |
| +4 | u16 | 材料在 `MIXTUREBASE` 里的起始下标 |
| +6 | u8 | 材料种类数（1–4） |
| +7 | u8 | 恒为 1，含义未定 |
| +8 | u16 | 配方编号 176–244（任务「把配方放进融合器」按它指认） |
| +10 | u8 | 恒为 1，含义未定 |
| +11 | u8 | 属性位掩码（`MIXSYSTEM_GetRecipeCount`/`IsNeedEuip` 按**位**读它）：bit0＝这条配方要选一件装备当目标（`IsNeedEuip` 就是 `掩码 & 1`，混沌、深渊混沌、宝石孔三条置位）；bit1＝混沌合成页、bit2＝药水合成页、bit3＝宝石强化页、bit4＝宝石孔生成页、bit5＝传说装备页 |

### 17.3 `MIXTUREBASE`（3 字节/条，189 条）

| 偏移 | 类型 | 含义 |
|---|---|---|
| +0 | u16 | 物品（`ITEMDATABASE` 下标） |
| +2 | u8 | 数量 |

平铺存放：配方只记起点和种类数，下一条的起点就是上一条的终点。混沌与深渊混沌
两条排在表尾（181–188），其余按药水 → 宝石 → 石孔 → 传说装备的顺序从 0 排起。

### 17.4 自证：69 条配方正好铺满 189 条材料

按起始下标排序后逐条累加，终点必须等于 189，中间不许有缝也不许重叠。
`inotia4/tools/export_fusion_recipes.py` 每次导出都会跑这个检查——任何一条
配方的字段读错，累加就会错位并直接报错。69 条全部通过，说明 +4/+6 的含义正确。

配套校验：混沌（`recipe-176`）的四样材料导出后与 `public/content/crafting.json`
里早已存在的 `chaos` 完全一致，属独立来源互证。

### 17.5 配方内容

| 类别 | 条数 | 例子 |
|---|---:|---|
| 混沌合成 | 2 | 混沌＝魔法树枝×2＋魔力结晶×2＋秘银×6＋混沌之鳞×1；深渊混沌＝卓越之花×1＋脉动结晶×3＋秘银×8＋混沌之鳞×1 |
| 药水合成 | 10 | 恢复药水（小）＝生命之叶×3；恢复药水（中）＝恢复药水（小）×2＋生命之叶×2；再生药水系＝对应恢复药水×1＋根源之果×(2/4/6/8) |
| 宝石强化 | 4 | 中级宝石＝低级宝石×3，高级＝中级×3，顶级＝高级×3，混沌＝顶级×3 |
| 宝石孔生成 | 1 | 锋利脚爪×8＋低级宝石×1 |
| 传说装备 | 52 | 安格巴德的剑＝秘银×2＋皮革×7＋低级宝石×1；飞龙骑士的翅膀＝秘银×4＋染血的毛×25＋顶级宝石×2 |

完整 69 条见 `public/content/fusion.json`（由 `inotia4/tools/export_fusion_recipes.py`
从 `game.dat` 生成，原始物品编号按名称映射回 `items.json` 的 id，映射前已验证
材料与产物在 `items.json` 里都能唯一命中）。

### 17.6 规则文案（原版提示，约束实现的判定）

| 文本 id | 文案 | 落在规则里 |
|---|---|---|
| 35416 | 只有装备类道具才可以进行合成 | 目标必须是装备 |
| 35417 | 不含宝石孔的道具才可以进行合成 | **有宝石孔的装备不能混沌合成** |
| 35418 | 武器、防具类道具才可以进行合成 | 排除首饰等其它装备位 |
| 35419 | 深渊混沌道具无法进行混沌合成 | 深渊混沌是终态，不能再进混沌 |
| 35421 | 已经达到合成最大值，无法再次进行合成 | 数值上限即「合成最大值」 |
| 35299 | 没有合成配方可以使用。请完成融合器发布的任务，获得合成配方 | 配方需解锁 |
| 35333 / 35513 | [%s] 的配方已登录到融合器中 / 必须先登录配方 | 传说装备需要先登录配方才出现在列表 |
| 35411 | 请设定合成次数 | 支持一次连续合成多次 |
| 35451 | 是否使用宝石进行合成？（%d GEM） | 宝石合成可能消耗的是「宝石」这一资源计数 |

### 17.7 配方登录（已解出）

「传说装备要先登录配方才出现在列表」的整条链路：

```
CHAR_UseItemEx（使用物品）
  └─ 物品记录 +7 字节 bit1 置位 → CHAR_ProcessRecipe(item)
       └─ ITEMSYSTEM_DecideRecipeID(item)
            在 ITEMMIXLINKBASE（52 行 × 4B：u16 物品, u16 配方行号）里找出
            该物品的所有行，**随机挑一行**（rand(0, 命中数-1)），返回第 2 字段
            （RECIPEBASE 行号，0–68）
       └─ MIXSYSTEM_AddRecipeBook(行号)：配方簿位图对应位置 1，已置位就无事发生
       └─ 弹「[%s] 的配方已登录到融合器中」（文本 35333），返回 1 → 物品被消耗
```

要点：

- `ITEMMIXLINKBASE` 的 52 行正好覆盖 RECIPEBASE 的 17–68 行（52 条传说装备配方），
  按 12 种「合成套装」道具分组：D/C/B/A/S 级武器 5/5/5/5/7 条，防具 4/4/4/4/3/3/3 条。
  也就是说**用一个套装道具随机学会这套里的一条**，同套的其余配方还得再开。
- 原版**不去重**：`DecideRecipeID` 只按物品编号匹配，已学会的配方也会被再次随机命中
  （那次使用就浪费了）。
- 配方簿只记传说装备这 52 条：`MIXSYSTEM_GetRecipeBookSize` 取「传说页配方数」
  算字节大小（(52+7)>>3 = 7 字节），`SAVE_SaveETC` / `SAVE_LoadETC` 负责随存档读写；
  `MIXSYSTEM_Initialize` 把它清零。其余 17 条（混沌、药水、宝石、宝石孔）不进簿、
  一开始就可用。
- 12 种套装道具来自任务奖励（内容里见 `campaign.json`），不在商店也不在掉落表。

### 17.8 §17 未确定的部分

| 项 | 现状 |
|---|---|
| 菜单 tab 的排列顺序 | **已解一半**（见 §17.9）：行数、行序、行高公式、行底图集都定下来了——建行循环正好建 5 行，顺序即文本池 35290–35294。仍未解的是 `UIMix_SetType(0..4)` 的实参到 +11 掩码 bit 位之间那一步映射 |
| 合成金币费用 | `RECIPEBASE` / `MIXTUREBASE` / `ITEMMIXLINKBASE` 里都没有费用字段，`MIXSYSTEM_GetCost` 的算法还没反出来。**项目已决定：合成一律免费**，`crafting.json` 的 `costPerLevel` 归 0，界面不画金币行 |
| 35417「不含宝石孔的道具才可以进行合成」的归属 | 文案与混沌那几条（35416／35418／35419）连在一起，但反汇编里没找到引用它的代码：合成页的拒绝走的是小整数索引（0x5e／0x60／0xa），到文本 id 的映射表没定位。**项目已决定：不做这条限制**，任何武器／防具（含已开孔的）都能混沌合成 |
| `+7` / `+10` 两个恒为 1 的字节 | 恒为 1，可能是「是否默认可用」或「每批产出数量」，未验证 |

### 17.9 菜单列表的版面（2026-11-13 补）

「左栏那列竖排菜单到底几行、什么顺序、拿什么当底」这三件事在 §17.8 里一直挂着。
重新读 `UIMix_CreateMainControl`(0x856A0) 与 `UIMix_ButtonMenuListDraw`(0x84FDC) 后定下来：

| 结论 | 依据 |
|---|---|
| 菜单**正好 5 行** | 建行的循环体是 `cmp r6,#0x14` 配 `adds r6,#4`，20/4 = 5 次；对应文本池 35290–35294 五条 |
| 行序即文本序：药水合成 → 宝石强化 → 宝石孔生成 → **混沌合成** → 传说装备 | 同上；混沌合成是**一条**菜单，混沌／深渊混沌两档在这一页里选（`RECIPEBASE` +11 的 bit1 一共也只有这两条） |
| 滚动组原点 (10,55)、尺寸 442×343 | `UI_CreateGroupBaseControl(parent,10,0x37,0x1ba,0x157)` |
| 行高 = 字高 + 41 | `movs r0,#1; bl GRPX_GetFontHeight` → `add.w r7,r0,#0x29`，之后每行按 r7 累加 |
| 行底是**图集 19（`UI_007.png`）里的深色胶囊**，不是通用按钮皮 | `IMGSYS_GetGroup(0x13)` 取图集、`IMGSYS_GetLoc(0x13,3)` 取第 3 号块，两处 `GRPX_DrawPart` 铺整行 |
| 文字色 `#E2CB9E`，居中 | `movs r0,#0xe2 / r1,#0xcb / r2,#0x9e` → `GRPX_SetTextColor`(0x69576)；宽度用 `GRPX_GetStringWidth` 求中点后 `GRPX_DrawStringWithFont` |
| 选中/普通行另叠一层半透明色 | `ldrb r3,[sb,#0x44]; cmp r3,#1` 分两路：state==1 → `(255,130,59)` alpha 30；否则 `(89,85,79)` alpha 20，都走 `GRPX_FillRectRGBAlpha` |

**仍解不出的两处**（实现时按实测/间接约束取值，别当成原版事实）：

1. **块矩形**。`IMGSYS_GetLoc` 的 `TBH` 跳转表没能自洽解开：表在 0x69C82、
   `case 0` 的偏移 0xC9 指到 0x69D46（正是第一个 case 函数体，那里是
   `ldr r0,[pc,#…]; movs r3,#0x18; add r0,pc; b`，即「基址 + 24×序号」），
   但 `case 19` 的偏移只有 0x93，落到表内部，与「函数体按 case 顺序排」矛盾。
   所以胶囊按**实测包围盒**取：可见部分 (87,192)-(373,262)，裁剪时连四周透明留白
   一起取 (84,186,295,82)——留白是必须的，只裁可见包围盒会让相邻两行糊成一摞。
2. **字高**。`GRPX_GetFontHeight` 读的是运行时才填的数组（指针在 BSS，静态是 0），
   字模高度取不到。于是行高只能间接夹：按 20 号字算是 61，按「343 的组正好装下 5 行」
   算是 68.6。

### 17.11 原版控件树（2026-11-13 解出）

`UIMix_CreateMainControl`(0x856A0) 里每个控件都是显式传 rect 建出来的，逐个读下来是这样
（坐标是**该控件父节点内**的坐标，`SetRect(r0=临时结构, r1=控件, r2=x, r3=y, [sp]=w, [sp+4]=h)`）：

| 控件 | 位置与尺寸 | 反汇编 |
|---|---|---|
| 左面板组 | 462×423，在主控件里水平居中 | `0x856DE` 起算 x，`0x856F6` 建 |
| 菜单列表组 | (10, 55) 442×343 | `0x85712` |
| 菜单行 ×5 | (0, i×H) 442×H，H = `GRPX_GetFontHeight(1)+0x29` | `0x85718`–`0x85754`，`cmp r6,#0x14` |
| 关闭钮 | (381, −24) 62×62（骑在面板右上角外面） | `0x857A4`–`0x857B4` |
| 加工区左组 | (102, 62) 295×58 | `0x85846` |
| ├ 小按钮 ×2 | (0,0) 与 (244,0)，各 51×58 | `0x85878`、`0x858B4` |
| 合成目标格 | (213, 62) | `0x85832` `ControlItem_Create` |
| 材料组 | (60, 239) 380×74 | `0x858D8` |
| └ 材料格 ×4 | 组内循环建 4 个 | `0x858E2`–`0x85902` |
| 右面板组（背包） | (484, 0) 395×431 | `0x85916` |
| ├ 背包网格组 | (34, 27) 323×323 | `0x85928` |
| ├ 背包格 4×4 | 步长 83（74 格 + 9 缝） | `0x85940`、`0x8595C` |
| ├ 分类列组 | (389, 12) 57×396 | `0x85976` |
| └ 分类按钮 ×6 | 高 69／70，竖排 | `0x85998`–`0x859DA` |

**关键结论：原版没有「顶部按种类分页」这一层。** 左面板里同时放着菜单列表（442×343 那五行）
和加工区，两套控件叠在同一块区域，靠 `UIMix_SetOnRecipeListControl`／`SetOffRecipeListControl`
切换显隐——也就是「要么看见五行菜单，要么看见这一类的加工区」，和本项目当前行为一致。
`UIMix_SetType(0..4)` 换的是加工区里那套子件（材料、目标格、组合钮）。

**三副面孔**：这三套控件叠在同一块区域，靠显隐切换，所以从左到右是三步：
五类菜单 →（选一类）这一类自己的控件 →（选一条配方）加工区。
`UIMix_CreateRecipeGroupControl`(0x85A30) 建的是**单独一屏**的配方列表：滚动区
`SetRect(scroll, 39, 90, 321, 条数×56)`——10 条药水就是 560 高，会占满整个左栏，
所以它和材料组（239）、组合钮（360）不可能同屏。实现里因此拆成 Menu／Recipes／Detail 三个视图。

**还读不出的三处**：加工区左组那两枚 51×58 小按钮画的是什么（大概是配方翻页或数量加减）、
材料格在 380 宽里怎么分布（循环里没显式传 x 偏移，实现按 3×102+74=380 铺）、
组合钮的 rect（推算 (139,360) 205×62：205×62 的命中区配 229×82 的图、绘制时左上角让 12/9，
且 360+62=422 正好停在 423 高组的底边）。

### 17.12 图集块矩形表（顺带解开）

`IMGSYS_GetLoc(i, k)` 的 `TBH` 跳转表仍没自洽解开（case 偏移与函数体顺序矛盾），
但**表本身是 24 字节一条的 (x, y, w, h)**，而且可以按内容反查：

- 图集 79（`UI_TITLEMAIN_01`）的基址是 `0x179F58`（§19 已用），实测前 5 条与文档完全一致；
- 图集 19（`UI_007.png`）的基址是 **`0x178B30`**——块 3 = (86,188,288,82)，正是融合器菜单行
  那枚深色胶囊（裁剪时按这条取，别再靠实测包围盒猜）。

反查办法：在 `.rodata` 里按 24 字节步长扫四元组，用「已知某个块长什么样」去命中，
命中后按 24 字节回推基址即可；图集尺寸决定每条 (x+w, y+h) 的上界。

### 17.14 目标格两侧的切换三角（2026-11-14 接上）

`UIMix_ButtonUpExe`（0x84F9C）与 `UIMix_ButtonDownExe`（0x84FBC）各 32 字节，除返回 1 之外
只做一件事：

```
+1 :  index = index + 1;  if (index > count) index = 1;
−1 :  index = index − 1;  if (index <= 0)    index = count;
```

也就是「在可当目标的道具之间上下切换」——不用回背包点。这两个按钮就是目标格两侧那两枚。

- **位置**：加工区左组 (102,62) 内的 x=0 与 x=244，各 51×58（`UIMix_CreateMainControl`
  的 0x85878 / 0x858B4）。
- **图标**：`IMGSYS_GetGroup(0xF)` + `IMGSYS_GetLoc(0xF,36)`。图集 15 = `UI_003.png`，
  块 36 = (306,312,149,75)——这一块里是**金色 chevron** 与**橙色发光 chevron** 两份
  （后一份就是按下态，`ButtonUpDraw` 在 state==1 时再叠块 37，混合模式 2）。
  实际裁的是金色那枚 (316,309,45,47) → `assets/ui/crafting/arrow-prev.png`，右按钮水平镜像。
- **图集 15 的块表基址 = 0x1791D8**（24 字节/条；30–39 是 3×3 的 149×75 格）。
  这是继图集 19（0x178B30）之后第二张按「内容反查」定出来的表，方法同 §17.12。
- **实现上的取舍**：候选 = `TargetProblemOf == null` 的背包条目（保持背包顺序）；
  没选过时「＋」从第一件起、「−」从最后一件起；两端回绕；只有 1 个候选时压暗，
  点了也不会有变化。
- **没做**：原版面板右上角还有一枚 62×62 的按钮（0x857A4：`(381,−24,62,62)`）。
  它相对面板顶边往上 24 像素，而我们这套版面的面板在 y=0，照搬会落到屏幕外；
  何况右上角已经有世界场景共用的菜单叉，再加一枚就是两个 X。**暂不加**，
  真要做得先决定撤哪一个。

### 17.13 批量合成（2026-11-14，本项目新增，**不是原版行为**）

原版 `UIMix_StartMix` 一次只提交一次：报价、复核、扣料、给产物各走一遍。用户要求药水与
宝石能连着做多份，于是加了批量，规则如下：

- **只有固定产出类能批量**（配方带 `product`：药水 10 条、宝石 4 条、传说装备 52 条）；
  改造类（宝石孔）每批只能改同一件装备一次，恒为 1。
- 材料按次数累计校验（3 片叶子一瓶、做 5 瓶就要 15 片），**差一份就整批拒绝**，
  不做「能做几份做几份」——那会让玩家点一次拿到意料之外的份数。
- 扣料与产出在**同一个事务**里重复 N 次，中途失败整体回滚（`FusionBench.Commit`）。
- 报价里带 `Batch`，确认框第二行写「连续合成 N 次」，原版那句「是否以当前的配方来合成？」
  一个字不改。

**版面落点**：材料组到 313、组合钮从 360 起，中间这条 47 像素空带在原版控件树里是空的
（§17.11），批量行就卡在这里，左右与组合钮对齐（205 宽）。两端用菜单行那枚胶囊的小号版
（`Patch` 的 cap 收到 10）——通用按钮皮的 cap 是 80，铺到 34 宽会压成两个看不清的小圆点。
这一行属于「用户要的功能」，改版面时不必去找原版依据。

**顺带修的关闭钮**：`CloseOpenMenuPage`（右上角那个叉的落点）原来只关背包／技能／状态／
任务／队伍，漏了融合器与商人页，所以开着融合器点叉没反应。现在两者都走各自的 `Escape`——
提示或确认框开着时先收那一层，与按 Esc 同一次序。`--closecheck` 与 `--craftcheck` 都覆盖了。

### 17.10 界面结构改动（2026-11-13 晚，用户指定；**已被 §17.11 取代**）

> 这一节记的是「用户当时要的两级结构」：顶部三个种类页签 + 左栏具体合成 + 固定成品/材料双框。
> 2026-11-14 用户看到原版控件树后改口「完全照原版重建」，实现已按 §17.11 重做，
> 顶部页签与双框都不在代码里了。保留本节只为说明「为什么中间会有一版那样」。

用户看了 §17.9 那一版后，把结构改成了两级：

```
顶部：合成种类（药水 / 宝石 / 装备）   ← 常驻
左栏：该种类下的具体合成（1–2 项）
左栏下半：上面成品框、下面材料框       ← 常驻，合成到哪一步都在原位
```

对应的归属：药水 → 药水合成；宝石 → 宝石强化、宝石孔生成；装备 → 混沌合成、传说装备。

**这不是逆向结论**，是用户要的界面结构（原版只有 §17.9 那五个菜单项，没有上一层分类），
所以下面这些也不再照原版：

| 项 | 原版 | 现在 |
|---|---|---|
| 菜单列表位置与尺寸 | 442×343 的组在 (10,55)、行宽 442、行高 字高+41 | 340×40 的行、行间 6，落在两块固定框上方（用户：按钮太大） |
| 行底 | 图集 19 的胶囊整块铺满 | 同一枚胶囊，但按 340×40 缩放（九宫格两端各留 34 像素不拉伸） |
| 成品框 | 只有改造类才画目标格 | 常驻：固定产出类亮产物，改造类亮选中的装备，没选就摆空格 |
| 材料框 | 四个 74 格排在 y=187 | 常驻：四个 64 格排在 y=264 |

改这几处只需动 `godot/domain/Ui/CraftingLayout.cs` 与 `CraftingPanel.Draw*`；
`domain/` 里的合成规则（材料、报价、提交、混沌重掷）一行没动，`--craftcheck` 与 343 项域自测照旧。

---

## 18. 技能书（use kind 51）

原版物品用途编号 51（`ITEMSYSTEM_IsSkillBook` 就是 `x == 0x33`）。

### 18.1 书与技能的绑定

- **书一造出来就带技能**：`ITEMSYSTEM_CreateItem` 对技能书一律走
  `ITEMSYSTEM_MakeSkillBook`——遍历 `SKILLTRAINBASE`（93 行：u8 职业、u16 技能 id、
  u8 旗标、u8 最高等级、u16 数值列），取数值列非零的行、**等概率随机挑一行**，
  把行号写进物品实例的附加数据（实例 +0x18，标记 u16@0 = 36、技能行号 u16@2）。
  **不分职业**：开出来的书可能是别的职业的，用不了只能卖。
- `SKILLTRAINBASE` 的 93 行 = 六职业 15 条 × 6 = 90，外加 3 条职业号 255 的特殊技能
  （本库未收录）。可训练技能的最高等级在 +6（普通 4 级或 3 级）。
- **被封印的技能书**（文本 964–969，六职业各一）解封后按该职业的技能池随机出书
  （`ITEMSYSTEM_ReleaseSealed → ReleaseSealedSkillBook`）。

### 18.2 使用

`CHAR_UseItemEx` → `CHAR_ProcessSkillBook(char, item)`：

1. 实例附加数据里取出技能行号；
2. 行的职业必须等于角色的职业（`char+0xd`），否则失败；
3. 该技能当前等级必须低于行里的最高等级，否则失败；
4. 通过则等级 +1，消耗技能书。

配套文案：35441「是否学习该技能？」（使用前确认）、35442「学习条件不足。」
（失败）、35688（说明：只能对已达到普通上限的技能使用，技能书可升到 8 级）。

### 18.3 本库的对应实现

- 进背包掷技能：`BagRules.RollLearnSkill`，池为内容 `classSkills` 六职业并集（90 条），
  次序随 `items.json` 键序；真实游戏路径在 `BattleCharacter.CharacterBagRules`。
- 使用：`Bag.Use` 校验职业 → `SkillUseHandler` → `SkillProgression.Book`
  （普通上限之上才能用，升到满级为止）——与原版口径一致。
- 界面：背包「使用」技能书先弹「是否学习该技能？」确认框，再结算。

---

## 19. 标题页动态背景（STATE_ProcessTitle）

标题页不是一张静态图，而是**两层图集 + 四组各自振荡的烟雾层**叠出来的。
绘制入口 `STATE_ProcessTitle`(0xD884C) → `0xD8800`（每帧调用）→ 三个子绘制
`0xD8390`（底层）、`0xD83F4`（振荡层参数表）、`0xD86C0`（固定元素）。

### 19.1 图集与图块矩形

两张图集来自 `game_res/UI_TITLEMAIN_01.png.jpg`（图集 79，标题主层）与
`UI_TITLEMAIN_02.png.jpg`（图集 80，烟雾层），都是 1024x1024 的原始 PNG。

**注意**：仓库里的 `UI_TITLEMAIN_01.png.jpg` 是**改包版**——版权行、右上 HIVE 按钮、
喇叭图标三块已被 `patch_title_img.py` 擦除。未改的原版在同目录的 `.orig` 里。
本库采用的是改包版（用户确认）：`(0,996)-(545,1022)`、`(612,0)-(712,76)`、
`(700,500)-(820,572)` 三块是透明的。

图块矩形不在 `i_title.dat`，而在 `.rodata` 的硬编码表里：
`IMGSYS_GetLoc(i)`(0x69C68) 是一个 98 项的 `TBH` 跳转表（`cmp r0,#0x61`），
每个 case 算出「表基址」，末尾 `mla r0, r3, r1, r0`（r3=0x18=24 字节/条）返回
第 r1 条的地址。每条形如 `(x, y, w, h)` 四个 u32。

图集 79（基址 `0x179F58`）：

| idx | x | y | w | h | 内容 |
|---:|---:|---:|---:|---:|---|
| 0 | 2 | 2 | 600 | 640 | 背景魔法阵（左半） |
| 1 | 2 | 645 | 617 | 353 | INOTIA 龙 logo |
| 2 | 622 | 645 | 295 | 122 | 副标题左 |
| 3 | 920 | 645 | 72 | 71 | 喇叭图标 |
| 4 | 923 | 719 | 92 | 86 | Assassin 徽标 |
| 5 | 622 | 770 | 298 | 122 | 副标题右 |
| 6–11 | 66/228/235/248/261/274 | 1001 | 159/4/10/10/10/10 | 18 | 版权行字块 |

图集 80（基址 `0x17A168`）：

| idx | x | y | w | h | 内容 |
|---:|---:|---:|---:|---:|---|
| 0 | 2 | 2 | 600 | 640 | 魔法阵（右半） |
| 1 | 2 | 645 | 248 | 234 | 烟雾团 A |
| 2 | 253 | 645 | 304 | 235 | 烟雾团 B |

### 19.2 六组振荡参数（`0xD7A28` 起初始化）

**是六组，不是四组**——六个更新块（A–F）各配一个方向字节 `+0x50..+0x55`。
结构形状相同：`int 透明度`(0..100)、`int 计步器`、`int 上限`、`bool 方向`、`int 阈值`。
初值写在 `0xD7A28`–`0xD7A60`，由「`[+0x20]` 为零才初始化」的闸门守住，只跑一次：

| 组 | 更新块 | 透明度字段 | 计步字段 | 初始透明度 | 初始计步 | 方向0增量 | 方向1增量 | 阈值 | 方向字节 |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|
| A | `0xD7A80` | `+0x38` | `+0x24` | 60 | 0 | +1 | +2 | 59 (0x3B) | `+0x55` |
| B | `0xD7AE6` | `+0x3C` | `+0x2C` | 50 | 90 | −1 | +1 | 49 (0x31) | `+0x54` |
| C | `0xD7B4E` | `+0x40` | `+0x28` | 60 | 90 | +1 | +2 | 59 (0x3B) | `+0x53` |
| D | `0xD7BB2` | `+0x44` | `+0x34` | 30 | 180 | −1 | +1 | 29 (0x1D) | `+0x52` |
| E | `0xD7C32` | `+0x48` | `+0x20` | 50 | 0 | −1 | +1 | 49 (0x31) | `+0x51` |
| F | `0xD7CB8` | `+0x4C` | `+0x30` | 40 | 180 | −1 | +1 | 39 (0x27) | `+0x50` |

置数段逐条（`0xD7A28`–`0xD7A60`）：`r1=0x3C` 写 `[+0x38]`、`[+0x40]`（透明度 60）；
`r2=0x5A` 写 `[+0x28]`、`[+0x2C]`（计步 90）；`r2=0xB4` 写 `[+0x30]`、`[+0x34]`（计步 180）；
`r1=0x1E` 写 `[+0x44]`（透明度 30）；`r2=0x32` 写 `[+0x3C]`、`[+0x48]`（透明度 50）；
`r2=0x28` 写 `[+0x4C]`（透明度 40）；`r2=0`（闸门值）写 `[+0x20]`、`[+0x24]`（计步 0）；
最后六个 `strb` 把 `[+0x50]`–`[+0x55]` 全置 1。

**错开相位的是「初始透明度」，不是计步器**：上面这组数（60/50/60/30/50/40）六层各不相同，
这就是六层不会同步明灭的原因。2026-11-12 重核之前的版本把这一列读成了计步初值、
把透明度全写成 0，结果六层几乎同步（`计步器`只写不读，错不开任何东西）。

**两个容易解错的地方**：

1. **只有 A、C 是「只涨不落」**。这两块的 `ite eq / mvneq / movne` 立即数是
   `#1` 与 `#2`——**两个都是正的**，所以它们在两个方向上都只增不减，
   是锯齿（涨满后清零重爬）。B/D/E/F 的立即数是 `#-1` 与 `#1`，才是来回摆。
   把 A/C 当成「涨 2 落 1」会把锯齿做成往返，画面观感差别很大。
2. **「方向」不是升降开关**，它只是「这一步用哪个增量」的选择子：
   `ldrb 方向; cmp 方向,#0; ite eq; mvneq 增量0; movne 增量1; add 透明度, 增量`。

计步器的上限 `0x168`（360）写在各块 `str r1,[r4,#计步器]` 处，每帧 +1 取模；
它只用于错开各层节奏，**不参与透明度判定**。

### 19.3 每帧更新规则（以组 B 为例，`0xD7AE6` 起）

```c
计步器 = (计步器 + 1) % 360;               // 0x168，到点归零

增量 = (方向 == 0) ? 增量0 : 增量1;        // A/C 两个增量都为正，B/D/E/F 是 -1/+1
透明度 += 增量;

if (透明度 > 100) { 透明度 = 0; 方向 = 0; }
else if (透明度 <= 阈值) { 方向 = 1; }
```

撞到 100 的处置是**直接清零**（不是反向走回）。绘制时取补数当 alpha：
`rsb r3, r3, #0x64`（`100 - 透明度`），塞进 `GRPX_DrawPart` 的第 6 个参数
（`sp+8`）——所以透明度**越大画面越暗**，这个名字在原版里是反着用的。

透明度是 `0..100` 的整数刻度，**不是浮点**。

### 19.4 布局算法（`0xD7940` 起）

```c
scale = GetScalevalue();                     // 0x6C3C0
scre[W] = (int)(scale * 某常量);              // 存进 [global]
x = (GetResolutionWidth()  / 2) - 300 - GetScalePlusW();
y = (GetResolutionHeight() / 2) -  60 - GetScalePlusH();
```

魔法阵按 600×640 居中铺底。**六层烟雾的落点基准不是它，而是 960×640 的设计画布**：

```c
r6 = (GetResolutionWidth()  - 960) / 2;      // 0xD7A00–0xD7A0C：先减 0x3C0 再右移 1
r7 = (GetResolutionHeight() - 640) / 2;      // 0xD7A10–0xD7A22：先减 0x280 再右移 1
```

六个绘制块的 `(x, y)` 都是 `r6/r7` 加常数，`GRPX_DrawPart` 的锚点参数给 2（居中），
所以下表是**烟团中心**在 960×640 画布上的位置（2026-11-12 逐条跟出来的）：

| 层 | 振荡器 | 贴图 | x | y | 位置 |
|---|---|---:|---:|---:|---|
| A `0xD7AE2` | `[+0x38]` | 图集 80 idx 1 | 237 | 133 | 左上 |
| B `0xD7B4A` | `[+0x3C]` | idx 1 | 730 | 97 | 右上 |
| C `0xD7BAE` | `[+0x40]` | idx 1 | 197 | 470 | 左下 |
| D `0xD7C20` | `[+0x44]` | idx 1 | 750 | 473 | 右下 |
| E `0xD7CB4` | `[+0x48]` | idx 2 | 162 | 285 | 左中 |
| F `0xD7D16` | `[+0x4C]` | idx 2 | 785 | 285 | 右中 |

即**左簇三团（162/197/237 三个横位，纵向 133/285/470）、右簇三团（730/750/785，纵向 97/285/473）**
——这就是「左右两团烟」的来历：每侧三团叠在三个高度上，靠透明度错相轮流亮，
看上去就是那一侧的烟在慢慢翻。**六层的位置一律不动，贴图也不旋转**（§19.8）。

贴图那个 `idx` 是从 `IMGSYS_GetLoc(0x50, 1)` 与 `IMGSYS_GetLoc(0x50, 2)` 两次取值、
以及各块末尾 `mov r3, r8` / `mov r3, sb` 的寄存器追踪读出来的：A–D 用 idx 1，
E、F 用 idx 2。

### 19.5 绘制顺序

1. `0xD8390`：画图集 79 的 idx 0（背景魔法阵），整屏铺底
2. `0xD86C0`：画固定元素（logo idx 1、副标题 idx 2/5、喇叭 idx 3 等）
3. 六组振荡层各画一次图集 80 的 idx 1/2（两团烟雾），alpha 取 `100 - 透明度`
4. 最后画一层整体暗化/亮化（`0xD7D1A` 起读一个全局 byte，非零时画）

### 19.6 本库的对应实现

- 素材：`public/assets/ui/title-menu/anim/atlas-01.png` 与 `atlas-02.png`
  （从改包版 `game_res` 原样解出，1024x1024）。
- 规则：`godot/domain/Ui/TitleAnimation.cs`（纯 C#，无 Godot 类型），
  含六组振荡器的逐帧更新与 alpha 换算，配回归用例。
- 表现：`godot/scripts/Ui/TitleBackdrop.cs` 按 §19.4 的六个落点画六层烟雾，
  默认走一条慢波；`--plumeexact` 换回这六组振荡器逐格推进。详见 §19.9。
- **已知差异**：原版 alpha 走的是 `GRPX_DrawPart` 的整型 alpha 参数，
  Godot 侧用 `Modulate.A` 浮点，最终像素合成方式不同；抖动细节可能对不上，
  但锯齿周期、各组阈值与相位关系是按数值复刻的。

### 19.7 §19 未确定的部分

| 项 | 现状 |
|---|---|
| `0xD7D1A` 那层全局明暗的触发条件 | 读 `[r3]` 的 byte 非零才画，该字节的写入方没定位 |
| **第七组振荡器（`+0x58`/`+0x5C`）画的是什么** | 它用 <code>mvneq #4 / movne #5</code>（比其他六组大一个数量级），判定也特殊——不是 `<c>&lt;= 阈值</c>` 而是 `<c>&lt; 0</c>`。跟着它的 `DrawPart` 走，取的是图集 79 的 idx 8，而那张表 idx 6–15 全是 y=1001 的 10×18 / 159×18 碎片（版权行那排小字）。改包版把版权行擦成 0% 不透明，所以这块现在不可见。原版这里应该有一行以 −4/+5 幅度闪烁的文字，**未复刻** |

2026-11-12 补掉的两行：六层各画哪一块（§19.4 的表）、六层精确落点（同一张表）。

### 19.8 旋转是额外加的，不是原版行为

**结论先行：反汇编里标题页没有任何旋转。**

证据：

- 标题页整条绘制路径（`0xD7940` 的布局块、`0xD86C0` 的固定元素、`0xD7EEE` 之后的按钮与
  版权行段）最终都调 `GRPX_DrawPart`(0x68F30) → `SGL_DrawTexturePartEx`(0x6D250)。
  后者 1140 字节，整段只做「矩形裁剪 + 像素搬运」，**没有任何角度或变换参数**。
- 全库只有两处 `SGL_Context_GLfixed::Rotate`(0x13B550) 调用点——`0x6C760` 与 `0x6D13A`，
  两处的角度实参都是硬编码 `mov.w r1, #0x5A0000`（90°），属 `SGL_SetContextScale` 一类的
  **屏幕转屏**逻辑，与标题页无关。
- 六组振荡器的更新块只有透明度、计步器、方向三个字段，没有角度。
- 两团烟雾（图集 80 的 idx 1/2）是单帧静态 PNG，没有多帧序列。

**那为什么曾经加过旋转**：用户观察到「云会转」，要求在复刻基础上实现转动的观感。
`TitleBackdrop` 一度给六层烟雾加了原地自转（±6~11°/秒、方向交替、速度略有差别）。
**2026-11-12 起自转已撤掉**：贴图自转会把云的纹理拧起来，观感是「几团灰抹布在转」，
不是烟。现在改成六层各走一条慢圆轨道（见 §19.9），位置连续变化、贴图不转。

**这条要单列，因为它不是逆向结论**：后来者若拿「原版会转」当成事实去改别处，就错了。

#### 魔法阵为什么不转（走过的弯路，记下来免得再试一遍）

第一版让魔法阵整体绕中心转（3°/秒），结果是「转久了露出灰色阴影」。
排查过程与本结论：

1. **不是缩放的锅**。理论上按角度实时算 `s = max(vw/bw, vh/bh)` 就能盖满
   （`bw = s(|cos|W + |sin|H)` 等），实测 90° 时确实自动升到 1.50、包围盒 960×1800，
   公式是对的。
2. **是长宽比决定的**。魔法阵画布是 1200×640 的**长条**，绕中心旋转时外接盒
   虽然够大，但**四个角本来就没有像素**——斜角处必然露出底下的颜色。
   这是几何死结：45° 要 1.77 倍才能盖满，但那时外围整圈符文会被裁掉；
   90° 要 1.5 倍，长边竖起来又会顶出上下。**没有任何缩放能既盖满又不破坏构图。**
3. 试过「静止铺底 + 旋转层叠加」，结果是两层接缝在斜角处叠出一道暗线，更难看。

**结论：魔法阵不转。** 画面「活起来」交给六层软边的烟雾——它们是边缘柔化的云团，
平移或换相位都不会产生接缝，四角也永远不会露出底色。
（原先为旋转加的 `RotationCoverFactor`、`CircleDegPerSecond`、`--titleangle` 调试参数
都已删除，不要复活。）

### 19.9 当前实现：六个固定落点 + 自转／漂移／呼吸（哪些是逆向、哪些是表现层）

2026-11-12 把 §19.4 那六个落点、贴图分配、初值全部对出来之后，画面构成已经确定：
**魔法阵静态不动，六团烟固定在左右两侧各三个高度。**

**照逆向做的**：

- 六个落点与贴图（§19.4 的表）——原版逐条跟出来的常量，不是估的。
- 六组振荡器的结构、阈值、增量、初值（§19.2／§19.3）在
  `godot/domain/Ui/TitleAnimation.cs` 里，配 6 条回归用例。

**表现层自己拿主意的（`godot/scripts/Ui/TitleBackdrop.cs`）**：

1. **每层原地自转**（8～13 度/秒，左簇正转、右簇反转）。这是从「只做明暗」退回
   来的原因：背景图 `background.png` 本身已经烘着左右两团静态烟，只叠一层半透明
   明灭，实测两帧全屏平均差只有 0.2/255，画面上等于静止。贴图四边 alpha 实测
   最大 6/255（等于全透明），所以旋转不会露出矩形硬边。
2. **整簇慢漂移**（半径 42 设计像素、24 秒一圈，同簇相位错开 1.1 弧度，左右反相）。
   自转动得最厉害的是外圈，而外圈正好是 alpha 渐隐到 0 的地方；加位移才让轮廓
   移动，肉眼才读得出「在转」。半径 42 相对烟团 250×235 只有六分之一，仍在原版
   落点附近。
3. **缩放呼吸**（±5%、17 秒，相位按层错开）：团本身起伏，翻涌感比单纯平移明显。
4. **默认走一条慢波**（9 秒一轮），同一侧的上／中／下三档错开三分之一周期，
   右侧再反相半个周期。为什么不用反汇编那六组直接画：A/C 两块只有 **21 格**一轮
   （两个增量都是正的，涨满就清零重爬），按 60fps 一格一帧推就是每 0.35 秒闪一次，
   观感是闪不是转。想看原样：`--plumeexact`（折算成每秒 8 格）。
5. **提亮 1.55 倍、最大不透明度 0.95**：烟的贴图偏灰（A 块平均 alpha 只有 26/255），
   直接叠到这张偏亮的背景图上几乎看不出来。提亮后才像原版画面里的白烟。

所以：位置、贴图、明灭的数值依据都是逆向来的；**自转、漂移、呼吸、提亮、慢波**
是照着用户对原版的描述补的观感，**不要当成逆向结论**。想更贴反汇编就开
`--plumeexact`；想调快慢改 `WaveSeconds`／`DriftSeconds`，想调幅度改
`DriftRadius`／`BreatheAmplitude`。
