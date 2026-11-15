# inotia4 开发规范

**《艾诺迪亚4》的逆向重写**：规则、内容与素材都从原版 APK（`inotia4/`）逆出来，用 Godot 4.5 + C# 重写。代码在 `godot/`，内容与素材在 `public/`。

## 项目状态

- **唯一开发线：`godot/`（C#）**。现状、运行方式与自检开关清单见 `godot/ARCHITECTURE.md`。
- **行为依据只有两个来源**：`inotia4/` 里的逆向记录（原包字节、反汇编、字段语义），和 `godot/tests` 里的回归用例。**任何「与原版一致」的结论都要写明依据：是逆向记录里的哪一条，或哪份回归用例。**
- 决策记录在 `docs/adr/`（`0001` 单线与存档兼容、`0002` 项目定位与素材边界）。

## 沟通

面向使用者的解释里避免直接使用未经解释的内部简称。先用日常语言说清它解决什么问题，再出现技术词。

- 「同值短路」要说成「值没变就直接跳过后面的写入」。
- 「增量更新」要说成「只改变化的部分，没变的不重建」。
- 「常驻包」「IR」这类词，第一次出现必须紧跟一句人话解释。
- 代码、测试和提交信息可以用准确的技术词；面向使用者的文档要先让人看懂。

## 1. 用 Godot 的方式做，不要自己造引擎已有的东西

按这个顺序找解法：

1. **Godot 内置能力**：节点与场景、`_PhysicsProcess`、`AnimatedSprite2D`／`AnimationPlayer`、`Camera2D`、`TileMapLayer`、`Control` 容器布局、`Tween`、`AudioStreamPlayer`。不要手写每帧循环去替代物理步进、视口剔除或补间。
2. **本项目现有实现**：`godot/scripts/` 里的场景与界面、`godot/domain/` 里的规则。改之前先看同一块怎么写的（`WorldScene` 的表驱动、`Bag` 的原子操作、`CombatController` 的动作管线）。
3. **Godot 4.5 官方文档**。引擎版本以 `godot/project.godot` 为准；升级引擎时同步更新本文档与 `README.md`。

**分层是硬约束**：

- `godot/domain/` 只放纯 C# 规则，**不引用 Godot 类型**——这样 `godot/tests` 能直接跑，不用开引擎。
- `godot/scripts/` 放场景、界面、渲染与接线。规则写进场景就跑不了自测。

## 2. 先定义游戏语义，再写代码

代码里只出现有语义的名字，不出现解析过程的中间产物。

| 别这样写 | 要这样写 |
|---|---|
| `pos = new Vec2(-rec.u5, -rec.u7)` | `worldPosition = new Vec2(-rec.tileX, -rec.tileY)` |
| `if ((cell & 0x3FFF) != 0)` / `flags >> 14` | `GetTileIndex(cell)`、`IsFlippedHorizontally(cell)` |
| `if (idx == 0x2484) alpha = 0` | 解码时算出 `transparentIndex`，渲染只读这个字段 |
| `paletteSetId = MAPINFOBASE[map].Byte[3]` | 导出阶段就展开成 `world.TilePalette` |

边界保持一致：

- **工程自包含**。运行、测试与构建只读本仓库的 `godot/` 与 `public/`。`inotia4/` 是逆向资料，不是运行时依赖。
- **剧情独立创作**。`public/content/campaign.json` 是剧情、任务与角色摆放的源文件；新增字段先定义游戏含义、单位和消费方式，内容加载与校验共用 `godot/domain/Content/` 的校验器。
- **名字用游戏里的概念**。动画叫 `walk-east`、`idle-south`，地图属性叫 `blocked`、`exits`、`entities`，剧情行为叫 `dialogue`、`choice`。
- **存档兼容上一版**。当前代码必须能读回最近一次发布的存档格式。改了存档结构或校验口径就把 `SAVE_FORMAT` 加一，同时在升级链里补上旧版到新版的迁移与旧档用例。格式号目前是 **43**（`godot/domain/SaveGame.cs` 的 `SaveFormat`，升级链已铺到 33）。

## 3. 测试与验证

```bash
dotnet build godot/Inotia4.csproj                              # 必须 0 错误 0 警告
dotnet run --project godot/tests/Inotia4.Domain.Tests.csproj   # 域自测，当前 348 项
```

- **把警告当错误对待**：新增警告要么改掉，要么说明为什么留。修可空警告要改成真实的空引用闸门（`is null` 提前返回、`?.`、`TryGetValue`），**不要用 `!` 压**。
- **规则改动必须带用例**：写进 `godot/tests/`，纯 C#、不依赖引擎。348 项全绿才算完成。
- **内容改动跑域自测即可**：里面含内容形状、跨文件引用与素材引用的校验。
- **场景与表现改动**跑对应的无头自检（`--autoplay`、`--exitcheck`、`--controlcheck`、`--skillplaycheck` 等，清单见 `godot/ARCHITECTURE.md`）。
- 全量走查（`--storyaudit`、主线 walkthrough）耗时较长，只在相关改动时跑。

每修复一个独立问题，完成代码/内容修正及必要验证后单独创建 Git 提交，提交说明写清修复行为和验证结果。

## 4. 构建与运行

| 场景 | 做法 |
|---|---|
| 开发 | 用 Godot 编辑器打开 `godot/project.godot`（4.5.1 mono） |
| 无头自检 | `"<Godot>/Godot_v4.5.1-stable_mono_win64_console.exe" --path godot --headless -- --autoplay --exitcheck` |
| 编译 | `dotnet build godot/Inotia4.csproj` |
| Android 导出 | 先 `python godot/tools/pack_game_data.py` 打内容包，再用 `godot/export_presets.cfg` 导出；产物在 `godot/build/`（不入库） |
| 域自测 | `dotnet run --project godot/tests/Inotia4.Domain.Tests.csproj` |

**运行时直接读 `public/`**：`godot/scripts/Application/ContentRoot.cs` 依次找「项目目录」和「`../public`」。所以 `public/` 必须与 `godot/` 同级。该目录不入库（内容与素材由使用者从原版 APK 导出，见 README），本地开发时须自行准备——内容是 436 份 JSON，素材是 11,556 张 PNG，全量预导入进 Godot 会让首包体积和导入时间都不可接受。

**装到手机上靠一个内容包**：Android 上 `res://` 在 APK 里，不是文件路径，`System.IO.File` 一律读不到。所以导出前先把 `public/` 打成 `godot/game_data.zip`（`godot/tools/pack_game_data.py`），随包分发；首次启动由 `ContentRoot` 解压到 `user://game_data`，之后所有读取照旧走文件系统，`ContentLoader`／`TextureCache` 一行都不用改。

这个 zip 必须原样存进 APK（不能二次压缩），否则 Android 上无法随机定位、`ZipReader` 打不开。配置在 Android 构建模板的 `aaptOptions { noCompress "zip" }`，构建模板重装会冲掉，打包脚本每次都会检查并补上。

## 5. 内容

- 源文件是 `public/content/`：`campaign.json`（9.5MB 剧情与任务）、`maps/`、`items.json`、`battle.json`、`drops.json`、`audio.json`、`wardrobe.json` 等。
- `npc-functions.json` 不是手写的：由 `inotia4/tools/export_npc_functions.py` 从 `game.dat` 的 `NPCFUNCBASE`／`NPCINFOBASE`／`NPCFUNCLINKBASE` 还原（功能名、三参数、NPC→功能绑定）。NPC 的 `characterId`（`npc-39`）就是表下标。要加一条新交互，先在导出脚本的 `BEHAVIORS` 里写 `kind`，再在 `NpcFunctionCatalog` 里登记这个 kind，最后在 `WorldScene.RunNpcFunction` 里分派——三处缺一处，加载或测试就会报出来。
- `fusion.json` 同样不是手写的：由 `inotia4/tools/export_fusion_recipes.py` 从 `game.dat` 的 `RECIPEBASE`（配方）＋`MIXTUREBASE`（材料）还原，共 69 条、按 5 个菜单分类。表结构见 `inotia4/docs/SO_REVERSE.md` §17。导出时会校验「69 条配方的材料区间正好铺满 189 条材料表」，任何字段读错都会直接报错。
- `wardrobe.json` 的 `poses` 也是从包里还原的：由 `inotia4/tools/export_wardrobe_poses.py` 读 `i_player` 的动画段生成（段号 = pose 号，层 11 字节，字段表见 `inotia4/docs/COMBO_FX_REVERSE.md` §13）。`--check` 会拿现有的 220 条逐字段回归（八项全对才算链路没坏），`--fill <段号>` 补缺条目；改这个脚本前后都要跑一次 `--check`。
- 改内容后跑域自测，校验器会拦字段形状、跨文件引用和素材路径。
- `public/content/debug/start.json`（GM 现场表，1.6MB）由导出流程生成，**生成器已下线**：要改就手改，改完跑域自测确认还能解析。
- 内容格式说明：`docs/story-content.md`、`docs/audio.md`（命令部分已失效，字段定义仍然有效）。

## 6. 目录导航

```text
godot/                 唯一开发线（Godot 4.5 + C#）
  project.godot        引擎配置（stretch 模式、输入映射）
  domain/              纯 C# 规则：内容、角色、战斗、剧情、世界、存档
  scripts/             场景与界面：Scenes、Ui、Application、Rendering、Debug
  tests/               域自测控制台项目（dotnet run 直接跑）
  docs/                验收记录
  ARCHITECTURE.md       能力现状、自检开关清单、运行方式
public/                内容与素材（运行时按需读取，不是构建输入）
  content/             436 份内容 JSON
  assets/              图片与音频
inotia4/               原版 APK 逆向：工具、文档、反汇编笔记（不是运行时依赖）
docs/                  adr/（决策记录）、内容格式说明
```

## 7. 别做的事

- `public/` 是运行时依赖，但**不入库**：别把它提交进去（`.gitignore` 已排除）。
- 别删 `inotia4/`——原版逆向是「行为为什么是这样」的唯一一手材料。
- 别把规则写进 `godot/scripts/`（引擎类型跑不了自测），也别在 `godot/domain/` 里引用 Godot。
- 别在没有依据的情况下声称「与原版一致」：依据要么是 `inotia4/docs/` 的逆向结论，要么是 `godot/tests` 的回归用例。
