# inotia4 素材清单与可用性

2026-11-08 盘点。回答一个问题：**这个文件夹里的东西，哪些能推进 `godot/` 重写还没解决的点？**

现状对照：连打三模板内容未还原（历史台账第四节第 17 条，台账已于 2026-11-13 删除、
结论查 git 历史：`git show e02739b:godot/REWRITE_SELF_CHECK.md`）是当前
唯一「表现已接、内容靠假设」的玩法差异；其余都与 so 逻辑无关或已修。

---

## 一、最有价值的三件事

### 1. 连击模板 → 挥击特效的顺序（当前唯一的「假设」项）

`godot` 里三模板按 **east（横斩）→ south（下斩）→ north（上斩）** 取片段，顺序是按素材推的。
要实锤，读 so 里这两条链：

- `ACTDATA_GetActUnit` @0x940c0（68B）：ACTUNIT 记录定位。已反汇编确认：
  记录大小从全局表读（`ldrb r4,[r3]`），行号乘法 `mla r0, r4, r0, r6`，
  记录区基址在全局指针里——ACTUNIT 21B/条、动画引用 @7/@9（前轮已证）。
- `CHAR_MakeDefaultAttack` @0x9a000（280B）：普攻动作 id 的来源。已反汇编确认：
  默认动作 5；`char+9 == 1` 时改由**武器表**算——`u16[char+0xA]（武器 id）× 记录大小 + 0x25`
  查表得动作 id，`bics` 后下限钳到 5。也就是说**三模板的特效差异如果存在，
  消费点在 `CHAR_Draw` @0x9bbc0（2016B）**——普攻渲染时按 ACTUNIT 字段放挥击特效。
  下一步：在 `CHAR_Draw` 里找特效 id 的读取偏移（@11..@20 之间的某个 u16），
  对 actions 5/6/7 的 ACTUNIT 逐字节对比，差异字段就是特效/刀光槽。

工具：`tools/armdis.py`（改 `PATH` 指向本仓库的 so）、`re_calls.py` 查调用图、
`re_xref.py` 查字符串引用。capstone 5 + pyelftools 已装。

### 2. `i_player.dat` 姿态/动画记录（模板 2/3 的人物动画）

`tools/re_pack.py` 的解析器**直接可用**（改 `RES` 路径），4399 段全部解开（841,904 B）。
本轮按头部 7 个 u16 分了组（`86 0b | 10 00 | dc 03 | f0 00 | 00 00 | 39 00 | 94 00`，
和恰好 = 4399）：

| 段区间 | 条数 | 推断类型 | 依据 |
|---|---|---|---|
| 0–2949 | 2950 | 精灵（部件图） | 记录头 `81 w w h h …`＋图集偏移 `84 24`（0x2484） |
| 2950–2965 | 16 | 帧组 | `07` 分隔的 u16 id 列表 |
| 2966–3953 | 988 | 动画 | `14` 开头、每帧 `01 00 0a 64 …` 变长记录 |
| 3954–4193 | 240 | 调色板 | `0c 00 64 XX 0d 00 …` 8B 记录 |
| 4194–4250 | 57 | costume | `06 00 + 6×u16 动画 id` |
| 4251–4398 | 148 | mani | 粒子/合成，`09 84 24 …` |

对应 `SNASYS_LoadSprite/LoadFrame/LoadAnimation/LoadCostume/LoadPalette/LoadMani`
（0xc6e88/0xc6f60/0xc7034/0xc74d0/0xc75a4/0xc7824），`SNASYS_ReadObjectCountAll` @0xc6720
连读 7 个 u16 与头部吻合。**下一步**：反汇编 `LoadAnimation`/`LoadCostume`（各 136B）
拿到记录布局，按 `wardrobe.json` 的 pose 格式倒出全部动画——双手剑 pose-4073/74/75
之后的缺口组（4076–4084）与长剑缺口组就在其中，补上即可还原模板 2/3 的人物动作。
注意：pose id（4067 等）不是段内明文 u16（已全库扫描排除），id 是导出时按记录序号分配的。

复现一行（路径相对本仓库）：

```python
import sys; sys.path.insert(0, r'inotia4\tools')
import re_pack as R
R.RES = r'inotia4\_apk\assets\common\game_res'
payload, count, start, delta, base, segs = R.segments('i_player')   # 4399 段全解
```

### 3. `game.dat` 100 张子表 ↔ `public/content` 八份目录对拍

`tools/inotia_dat.py`（容器拆分 + 定长表 + 字符串表 + 回写）是现成的。
战斗数值（伤害公式系数、状态命中率）、EVTCMDBASE 78 条命令参数、
TEXTDATABASE（第 73 张表）都能机器比对，用来验证 `godot/domain` 的公式移植是否逐系数一致。
SO_REVERSE §6 的存档分块/加密说明可用来做「原包存档 → Godot 读档」的兼容验证。

---

## 二、其余可用项（按需取用）

| 素材 | 对实现的用处 |
|---|---|
| `data/sym_by_module.txt`（6376 符号） | 找任何玩法的实现入口：`grep CHAR_／SNASYS_／ACTDATA_` |
| `docs/SO_REVERSE.md` | 主循环（0x92650）、表加载链、UI 缩放口径（7.2b，改 UI 坐标必看）、已确认常量（§14） |
| `docs/APK_FILES.md` | 2081 个资源文件的用途表（找素材时先查它） |
| `data/survey_game_res.txt`（549 文件形态） | 定位某个玩法的数据在哪个 `.dat`、什么记录长 |
| `tools/re_map.py` | `m*.dat` 地图解析；尾部 2–49B 未解字节疑为事件 id 列表（`MAP_AddEventID` 0xB5xxx）——可与我们地图事件的加载做交叉验证 |
| `tools/re_pack_probe.py` | 穷举未知偏移表的方法论，遇到新表先抄它 |
| `tools/re_strings.py`／`re_mutf8.py` | 文案与 id 的对应（TEXTDATABASE 校验） |
| `rects.json` | UI 控件坐标，与 `ui-skins` 目录对拍 |
| 两个 APK | `…_cat.apk` 是基线（勿动），`libgame.orig.so` 是反汇编源 |

## 三、本轮验证记录

- `re_pack.py` 逻辑对本仓库路径直接可用：i_player.dat 4399 段全解、无失败段。
- 反汇编链路（capstone thumb）跑通：`SNASYS_LoadCostume`、`ACTDATA_GetActUnit`、
  `CHAR_MakeDefaultAttack` 均已出码（结论见上文一.1/一.2）。
- 未动 `libgame.so`（只读 orig），未改 APK。

---

## 四、2026-11-08 追加：普攻刀光的机制结论（反汇编实锤）

按「第 1 步：拆 CHAR_Draw 实锤三模板顺序」执行，结论**推翻了此前的假设前提**：

1. `CHAR_AddActEffect` @0x9c8bc：从 `EFFECTINFOBASE`（表 62，659×7B：`i16 动画id @0、
   标志 @2、i8 @3、i8 @4`）按索引取特效，挂在 `char+0x284` 链表上，由 `CHAR_Draw`
   尾部循环 `SNASYS_DrawAnimationAsPaletteID` 画出；锚点是**角色自身**（或标志指定的屏幕中心）。
2. `CHAR_ProcessAction` @0xa1fda：按 ACTDATABASE 记录的 **@0x18／@0x1a 两个 i16 槽**
   （-1 = 无）各派发一个特效；全表 192 条只有 39 条用了槽——**动作 5/6/7（普攻三模板）
   与 8/9/10（武器姿态变体）全是 -1**，即原版普攻**不派发任何独立刀光特效**。
3. `CHAR_DrawEx` @0x99810：人物动画是「动画 → 帧 → 多图层」的合成器
   （`UTIL_GetDirectionFrameEx` 做朝向→帧映射，图层按 costume 部件取精灵）。
   `public/content/wardrobe.json` 的普攻姿态（pose-4073/74/75）只有
   body／coat／head／two-handed（武器）部件——**没有刀光图层**。

**结论**：原版的挥砍「刀光」不在独立特效里，而是**烘在人物攻击动画的帧图层里**
（三模板 = 三套攻击动画，所以连打时刀光/挥砍画面随平 A 次数变化）。移植里独立的
`sword-swing` 循环特效（#91／#93）是结构性近似——这就是「方向看着不对」的根源。

**修正后的路线**：第 2 步（解析 `SNASYS_LoadAnimation` @0xc7034 / `MANI_Create` @0xc0e34
与 i_player.dat 的动画记录布局，补出 4076-4084 等缺口姿态，**连同刀光图层**）完成后，
把普攻的独立挥击特效拆掉，让刀光随动画回来。在此之前保留现有近似。

本轮新增实锤：`CHAR_GetDefaultAttack` @0x957fa 是 thunk（直接读 `char+0x264`）；
`CHAR_MakeDefaultAttack` @0x9a000 默认动作 5、`char+9==1` 时按武器表
`u16[char+0xA]×size+0x25` 查动作 id；ACTUNIT 对齐后 @7/@9 = 动画引用对
（U[5]/[6]/[7] = [447,448]/[449,450]/[451,452] ✓）；ACTUNITBASE／ACTDATABASE
容器各有 6B 头。`game.dat` 容器可用 `inotia_dat.split_container` 按序号取表
（ACTDATABASE=28、ACTUNITBASE=29、ACTTRANSMITBASE=30、EFFECTINFOBASE=62）。
