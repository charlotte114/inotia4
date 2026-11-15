# 艾诺迪亚4 单机改造说明

包名 `com.com2us.inotia4.cat`，版本 1.2.9 (29)，targetSdk 27，仅含 `armeabi-v7a`。
引擎为 Com2us 自研 GEM，界面布局、游戏逻辑全部编译在 `libgame.so` 里，资源与数据在 `assets/common/game_res/`。

---

## 0. 目录结构与文档索引

### 目录

```
inotia4/
├── README.md                      本文件（总入口）
├── 艾诺迪亚4_1.2.9_cat_final.apk  成品，已签名
├── 艾诺迪亚4_1.2.9_cat.apk        原包基线，所有补丁以它为源（勿删）
├── mod.keystore                   签名密钥
├── _apk/                          原包解包目录 + 全部 .orig 原始备份（打包基础）
├── tools/                         数据处理 / 补丁 / 打包 / 逆向脚本
├── docs/                          分析文档
│   ├── APK_FILES.md
│   ├── SO_REVERSE.md
│   └── dex3_notes.txt
└── data/                          数据快照，可由 tools/re_*.py 重新生成
    ├── memorytext_zhhans.txt      文本表全文（35,811 条，方便 grep 文案）
    ├── imgtable.txt               98 项图集目录
    ├── survey_game_dat.txt        100 张子表规模
    ├── survey_game_res.txt        549 个资源文件解包形态
    ├── survey_images.txt          103 张图集真实尺寸
    ├── sym_by_module.txt          6,376 个导出函数按模块分组
    ├── so_sources.txt             so 内残留的构建机源码路径
    ├── dex3_strings_utf8.txt      classes3.dex 字符串
    └── rects.json                 UI 控件坐标（extract_ui.py 导出）
```

### 文档

| 文件 | 内容 |
|---|---|
| 本文件 | 成品信息、改动清单、数据结构速查、工具链、重建步骤 |
| [docs/APK_FILES.md](docs/APK_FILES.md) | **安装包逐项文件清单**：2081 个条目按目录分类，每个文件的用途、格式、体积；manifest 组件表；运行时才生成的文件 |
| [docs/SO_REVERSE.md](docs/SO_REVERSE.md) | **libgame.so 逆向分析**：ELF 结构、8332 个符号的模块划分、启动与主循环、数据加载链、存档/加密、UI 框架、网络、反修改模块、10 处补丁的逐条反汇编 |
| `docs/dex3_notes.txt` | 重打包作者 Noname 留在 `classes3.dex` 里的两段说明原文 |
| `data/sym_by_module.txt` | 6376 个导出函数按 360 个模块前缀分组，含地址与大小 |
| `data/imgtable.txt` | so 内嵌的 98 项 UI 图集目录（编号 ↔ 文件名） |
| `data/survey_game_res.txt` | 549 个 `game_res` 文件的解包形态（容器类型/条数/记录长度） |
| `data/survey_game_dat.txt` | `game.dat` 内 100 张子表的规模 |
| `data/survey_images.txt` | 103 张图集的真实像素尺寸 |

---

## 1. 成品

| 项目 | 值 |
|---|---|
| 文件 | `艾诺迪亚4_1.2.9_cat_final.apk` |
| 大小 | 49,578,397 字节 |
| MD5 | `7769585e8df7a1f9b35b0d1158574e71` |
| 签名 | v1 + v2 + v3，证书 `CN=InotiaMod`（`mod.keystore`） |
| 基线包 | `艾诺迪亚4_1.2.9_cat.apk`（未改动，所有补丁以它为源） |
| 安装 | `adb install -r 艾诺迪亚4_1.2.9_cat_final.apk`，或把 APK 拷到手机直接点安装 |

签名与官方不同，换到别人手机上若报签名冲突，必须先卸载（会清存档）。

---

## 2. 改动清单

### 2.1 `libgame.so`：37 字节，10 处

补丁链基于 `_apk/lib/armeabi-v7a/libgame.orig.so`，产物为 `libgame.ui.so`。

| 地址 | 原始 | 改后 | 作用 |
|---|---|---|---|
| `0x6BD58`–`0x6BD5B` | `C2S_HubMain` 函数体 | `movs r0,#0; bx lr` | 禁用 Hub 跳转，右上角 HIVE 点击无反应 |
| `0x897E2` | `movs r3,#0x9b` (y=155) | `movs r3,#0xe8` (y=232) | 保存按钮下移到原收费商店位置 |
| `0x897FA`–`0x897FD` | `ldr.w r3,[pc,#0x4f4]` | `b 0x89834` + nop | 整块跳过收费商店按钮的创建（控件不存在，无热区） |
| `0x8E9B2`–`0x8E9B5` | `movw r1,#0x4E20` | `mov.w r1,#0` | 技能点重置：判定价 20000 铜币 → 0 |
| `0x8E9BA`–`0x8E9BD` | `movw r0,#0x4E20` | `mov.w r0,#0` | 技能点重置：扣款 → 0 |
| `0x8E9C6` | `movs r0,#0x20` | `movs r0,#0x8D` | 技能点重置成功提示改用消息 0x8D |
| `0xD1DA6`–`0xD1DA9` | `movw r1,#0x4E20` | `mov.w r1,#0` | 能力点重置：判定价 → 0 |
| `0xD1DAE`–`0xD1DB1` | `movw r0,#0x4E20` | `mov.w r0,#0` | 能力点重置：扣款 → 0 |
| `0xD1DBA` | `movs r0,#0x20` | `movs r0,#0x8C` | 能力点重置成功提示改用消息 0x8C |
| `0xD81B6`–`0xD81BF` | `cbz r0,0xD81BE` + `bl SetMessageBox` | `movs r0,#1; movs r1,#0; bl 0x7AEF0; b 0xD81EC` | 跳过 Hive 注册/登录弹窗，等价于每次自动按「否」 |

说明：`0xD81B6` 那处不能简单 `return`。调用方在等弹窗结果，不投递结果会卡死在主菜单，所以必须直接调 `DialogNotify(1,0)` 把「否」的结果送出去。

### 2.2 图集 `UI_TITLEMAIN_01.png.jpg`（1024×1024，APK 内 STORED）

擦除三块（alpha 清零），备份 `UI_TITLEMAIN_01.png.jpg.orig`：

| 区域 (x0,y0,x1,y1) | 内容 |
|---|---|
| `0,996,545,1022` | 主菜单底部版权行 `©2012 Com2uS Corp. V.` + 数字字模 |
| `612,0,712,76` | 右上角 HIVE 按钮（实测按钮本体 625–695 / 2–68） |
| `700,500,820,572` | 左上角喇叭按钮（实测 707–812 / 508–563） |

**注意**：图集顶部 `x 0–601` 是背景魔法阵美术，右边缘在 `x=601`。擦 HIVE 绝不能越过 612，否则屏幕上会缺一块黑。`patch_title_img.py` 带越界自检。

### 2.3 文本表 `memorytext_zhhans.dat.jpg`

| 索引 | 原文 | 现文 |
|---|---|---|
| 35456 | 能力点已被初始化。 | **能力点已返还** |
| 35457 | 技能点已被初始化。 | **技能点已返还** |

只改这两条。商店共用的「购买成功。」（35348）没动。

### 2.4 `classes.dex`

`classes_final_patched.dex`（8,994,896 B，原 8,994,848 B）：给 `DeviceIdentity.getDIDSynchronized` 注入固定 DID，跳过启动时的设备号上行请求。

原逻辑无 readTimeout，服务器下线后每次启动阻塞 21.5 秒。改动后启动约 1 秒。

### 2.5 未改动

`game.dat` 与原版逐字节一致（MD5 `fb3d6f77cae53eba8e6cbcaabdc55c8d`）。
物品价格、任务奖励、商店货架、掉落概率全部是原版数值。

---

## 3. 数据结构速查

### LZMA 容器（所有 assets 下的 `.dat.jpg`）

```
[0] u8 0x01（非 01 表示未压缩）  [1] 0x00
[2..6] 5B LZMA 属性（5d 00 00 00 01 = lc3/lp0/pb2/字典16MB）
[7..10] u32 解压后大小   [11..14] u32 保留   [15..] LZMA 裸流
```

### `game.dat` 容器

```
[u32 count=100][1 字节填充][100 × 3B 累计结束偏移][各子表…][末尾 1 字节填充]
数据起点 = 305
每张子表 = [u32 count][u16 每记录字节数][记录…]
```

子表顺序 = `.dynsym` 中 `XXX_nRecordCount` 的索引序。关键索引：

| 索引 | 表 | 索引 | 表 |
|---|---|---|---|
| 0 | CHARCLASSBASE | 58 | MAPINFOBASE |
| 13 | ITEMDATABASE (23B/条) | 65 | QUESTINFOBASE (32B) |
| 25 | DEALINFOBASE (8B) | 70 | QUESTREWARDBASE (5B) |
| 51 | NPCINFOBASE | 73 | TEXTDATABASE (2B) |
| 75 | MONSTERDROPBASE | 77 | DROPINFOBASE (29B) |
| 78 | DROPDETAILINFOBASE (8B) | 79 | DROPEVTBASE (10B) |

### 关键表字段

- **ITEMDATABASE**：`+0 u16 物品ID`、`+12 u16 价格`（价格单位是铜币）
- **DEALINFOBASE**：`+0 u16 NPC`、`+2 u8 槽位(0–47)`、`+3 u8 类型(0–8)`、`+4 u16 p1`、`+6 u16 p2`；类型 0 = `AddSaleAsItemID(p1)`
- **QUESTREWARDBASE**：`+0 u16 物品ID`（**真实物品 ID = 表内值 + 30**，0 = 金币）、`+2 u16 数量`、`+4 u8 难度掩码`（0x3F = 六档全发）
- **QUESTINFOBASE**：`+0x0a u16 经验`、`+0x17 u16 提交起始`、`+0x1a u16 奖励起始`、`+0x1c u8 奖励数`

### 字符串表 `memorytext_*.dat`

```
[u32 count][count × 3B 绝对偏移][NUL 分隔 UTF-8 池]，池起点 = 4 + count*3
```
`memorytext_zhhans` 共 35,811 条，`&N` 是换行转义。物品名索引 == 物品 ID（≥30）。

### 金三角换算

```
金币 = 金额 / 10000    银币 = (金额 / 100) % 100    铜币 = 金额 % 100
```
所有金额内部统一按铜币存。`movw #0x4E20` = 20000 铜币 = 2 金币。

### 掉率

`DROPDETAILINFOBASE` 的 rate 字段单位为 1/2500，实际概率 = `rate ÷ 2500 × (1 + 队伍加成/1000)`。
队伍加成来自 `PARTY_GetItemChance`（`0xBB6F0`）累加队伍三人 `CHAR_GetAttr(char, 0x5C)`。
判定在 `CHARSYSTEM_DropItem`（`0xA43C0`），`0xA4598` 的 `movw r1,#0xFA`（250）是统一除数，改它等于全局调爆率。

当前原版典型值：主掉落 2400 → 96%，1200 → 48%，600 → 24%，300 → 12%，100 → 4%。

### 地图 `m*.dat`（本轮新解，详见 SO_REVERSE.md §5.3）

```
[0] u8 保留  [1] u8 保留  [2] u8 w  [3] u8 h
[4..]      w*h × 2B 地形层：tile = ((b0 & 0x0F) << 8) | b1   （12 位，0..4095）
之后 MAP_LoadLayer：
  u16 layerType
  4 × u16 → MAPFEATURESYSTEM_AddLayer
  u8 blockCount
  每 block：u8（高 4 位 = 层 0..3）、u16 条数
    每条 4B：dx, dy, packed, tileLo
      packed bit0-2 = tile 高位；bit3 = 0x800 标记；bit4-7 = 类型
      类型 0/1/2/3 → AddLayerTile / AddShadowTile1 / AddShadowTile2 / AddTopTile
      类型 4      → AddNPCItemLocation（NPC/物件）
      类型 5      → MAPTRASHSYSTEM_Find + CHARSYSTEM_Produce（生成物/怪物点）
```
实测 m0 = 25×30=750 格 + 7 block（1/39/267/0/20/4/14 条），尾部剩 25 B（疑为事件 id 表，未解）。
解析工具 `tools/re_map.py`。

### 精灵包 `i_*.dat`（详见 SO_REVERSE.md §5.4）

```
[0..3]  u32 count（i_portrait 的 3328 是精灵总数，不是段数）
[4..17] 头（各包长度不同）
[18..]  (n+1) × 3B 偏移，bit23(0x800000) = 该段 LZMA
段 i = [(off[i] & 0x7FFFFF) + delta, (off[i+1] & 0x7FFFFF) + delta)
```
表起点（多数 18、`i_portrait` 16）与 delta（多数 0、7 个包是 1）逐包不同，脚本会自动探测。
段内还有一层 13 字节头的 LZMA 容器（`i_portrait` 的段多一个前导 0x00）。
19 个包合计 14,935 段，**全部解压成功**，其中 6,886 段二次压缩，解压总量 6,199,134 B。
解析工具 `tools/re_pack.py`。

---

## 4. 工具链

```
tools/
  inotia_dat.py        LZMA 容器 / game.dat 子表 / 字符串表 读写
  inotia_edit.py       商店、任务奖励、物品价格 编辑
  armdis.py            Thumb 反汇编指定地址
  elfread.py           vaddr → 文件偏移读 u32
  extract_ui.py        解析任意 UI_Create 函数，导出控件坐标 JSON
  pngtool.py           纯 Python PNG 读写（环境无 Pillow 时用）

补丁（按顺序执行，后一个吃前一个的输出）：
  patch_uibtn.py       orig → ui0     删收费商店按钮 + 保存按钮下移
  patch_hub.py         ui0  → ui      禁用 Hub 入口
  patch_hublogin.py    ui   → ui      跳过 Hive 登录弹窗
  patch_freeability.py ui   → ui      能力点/技能点重置免费
  patch_msg_reset.py   ui   → ui      两条提示改用正确消息 id（--skill 含技能点）

资源（直接改 _apk 下的文件）：
  patch_title_img.py   擦图集里的版权行 / HIVE 按钮 / 喇叭按钮
  patch_text_reset.py  改 memorytext 35456 / 35457 文案

打包：
  repack_apk.py        逐条复制原包 → 替换 dex / so / assets → zipalign -p 4 → apksigner 签 v1+v2+v3

逆向分析（2026-10-29 新增，详见 SO_REVERSE.md §15）：
  re_calls.py          bl/b.w 调用图：modules / callers / callees / edges
  re_xref.py           字符串 xref（ldr+add pc 套路，窗口匹配）
  re_modules.py        按前缀导出 360 个模块的函数清单
  re_imgtable.py       解析 98 项图集目录并与 APK 内文件双向校验
  re_pack.py           解析 i_*.dat 精灵包两级索引
  re_map.py            解析 m*.dat 地图（地形层 + 图层 block + 4B 记录）
  re_assets.py         批量解 549 个 game_res 文件
  re_images.py         读 PNG IHDR 导出图集真实尺寸
  re_libs.py           三个 so 的 ELF 概况
  re_strings.py        按关键字搜字符串
  re_mutf8.py          正确解码 dex 字符串（长度前缀是 UTF-16 码元数）
  re_pack_probe.py     穷举定位未知偏移表
```

### 完整重建

```bash
cd /d/Desktop/inotia4
python tools/patch_uibtn.py
python tools/patch_hub.py
python tools/patch_hublogin.py
python tools/patch_freeability.py
python tools/patch_msg_reset.py --skill
python tools/patch_title_img.py      # 幂等：从 .orig 读、写回
python tools/patch_text_reset.py     # 幂等
python tools/repack_apk.py
```

已验证：按上述顺序重跑，生成的 `libgame.ui.so` 与成品包内的 so **逐字节一致**（MD5 `e8ce46a91632efc108bd84332b879f48`）。

依赖：`capstone`、`pyelftools`、`keystone-engine`；JDK 用 `/d/Android/Android Studio/jbr`；`zipalign` / `apksigner` 在 `D:\Android\SDK\build-tools`。

---

## 5. 已回滚 / 放弃的改动

| 改动 | 状态 | 原因 |
|---|---|---|
| 80+ 件消耗品改 1 铜币 | 已回滚 | 用户要求恢复 |
| 首个任务奖励追加「背包（大）」 | 已回滚 | 用户要求恢复 |
| 两个商人各上架 4 件顶级装备 | 已回滚 | 用户要求恢复 |
| 佣兵格 20000 铜币 → 0 | 已回滚 | 用户要求恢复 2 金币原价 |
| 锁 60 帧（code cave 插 usleep） | 已回滚 | 用户对比后选择原版 120Hz |
| 技能按钮 80 → 96 | 已回滚 | 用户觉得原尺寸够用 |
| 移动轮盘改位置/大小 | 放弃 | 轮盘 = 装饰圆环(控件21) + 4 个独立箭头(控件30–33)，坐标带缩放换算，改一个另一个不跟 |
| 改 so 里的服务器域名 | 放弃 | 实测无效，超时不在 connect 上，最终改用 dex 注入 DID |
| 删 FirebaseInitProvider / 去网络权限 | 放弃 | 实测无效，且去权限会导致启动卡死 |

---

## 6. 注意事项

1. **存档只在手机本地**。Hive 与云端备份已彻底失效，换机需手动拷
   `Android/data/com.com2us.inotia4.cat/files/d332b56cf8a2b9c51bbe7f431ce6602f/`（该串 = `md5("com.com2us.inotia4.cat")`）下的存档文件。
2. **签名不同**，给别人装要先卸载旧版。
3. 图集 `UI_TITLEMAIN_01.png.jpg` 经 pngtool 重新编码后比原文件大（约 +130 KB），APK 体积相应增加，条目仍是 STORED。
4. 喇叭图标的**点击热区还在**（控件未删），点那一块仍会切换静音，只是没有图标和反馈。
5. 改 `game.dat` 里的物品 ID 时记得 **+30 偏移**（`QUESTREWARDBASE` 专用），这是踩过的坑。
6. 屏幕逻辑坐标 = 物理像素 2400×1080；`adb screencap` 出的图是 1080×496，点击坐标需按 `2400/1080` 与 `1080/496` 换算。
7. **UI 坐标分两派**：`STATE_DrawMainMenu` 那类会自己乘 `GetScalevalue()`（960×640 基准），
   而 `UIPlay_Create` 这类直接用 `SGL_GetScreenWidth/Height`（物理像素）。改坐标前先确认目标函数属于哪一派，
   否则会差一个缩放倍（魅族 20 上是 1.6875）。详见 `SO_REVERSE.md` §7.2b。
