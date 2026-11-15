# inotia4

**《艾诺迪亚4》的逆向重写。** 从原版 APK 1.2.9 逆出规则与数据结构，用 **Godot 4.5（C#）** 重写成可玩的原生版本，目标平台是桌面与 Android。代码在 `godot/`，逆向资料与导出工具在 `inotia4/`。

行为以原版为准：判定口径、随机数消费次序、存档结构都按逆向出来的实现写，不按手感猜。逆向过程与结论记在 `inotia4/docs/`。

## 环境要求

- **Godot 4.5.1（.NET/mono 版）**：编辑器与导出都要 mono 版本
- **.NET SDK 9**：`dotnet --version` 能出结果
- Windows / Linux 桌面，或 Android 真机

## 准备内容与素材

运行时读的内容与素材（`public/`）**不在本仓库内**：它们由使用者从自己持有的原版 APK 导出，涉及版权，不随仓库分发。

仓库里带了导出工具（`inotia4/tools/`）。把原版 APK 解出的资源放到 `inotia4/_apk/`，然后执行：

```bash
python inotia4/tools/export_npc_functions.py
python inotia4/tools/export_fusion_recipes.py
python inotia4/tools/export_ui_sprites.py
python inotia4/tools/export_wardrobe_poses.py
```

产物落在 `public/content/` 与 `public/assets/`。目录结构需要 `public/` 与 `godot/` 同级。

> 没有 `public/` 时，编译与域自测仍可运行；启动游戏会因找不到内容而报错，这是预期行为。

## 快速开始

```bash
dotnet build godot/Inotia4.csproj
```

然后用 Godot 编辑器打开 `godot/project.godot` 按 F5 运行。

内容与素材是运行时按需读磁盘的：`godot/scripts/Application/ContentRoot.cs` 会去找 `godot/` 同级的 `public/`。

## 常用命令

```bash
dotnet build godot/Inotia4.csproj                                # 编译，应为 0 错误 0 警告
dotnet run --project godot/tests/Inotia4.Domain.Tests.csproj     # 域自测，当前 334 项全绿
"<Godot>/Godot_v4.5.1-stable_mono_win64_console.exe" --path godot --headless -- --autoplay --exitcheck
```

无头自检的开关清单（`--autoplay`、`--controlcheck`、`--skillplaycheck`、`--storyaudit` 等）见 [godot/ARCHITECTURE.md](godot/ARCHITECTURE.md)。

## 目录结构

```text
godot/                 Godot 4.5 + C# 工程（唯一开发线）
  domain/              纯 C# 规则：内容、角色、战斗、剧情、世界、存档
  scripts/             场景与界面：Scenes、Ui、Application、Rendering、Debug
  tests/               域自测控制台项目，dotnet run 直接跑，不开引擎
  docs/                验收记录
  ARCHITECTURE.md       能力现状、自检开关、运行方式
public/                内容与素材（运行时按需读取）
  content/             436 份内容 JSON（campaign 9.5MB、maps、items、battle…）
  assets/              图片与音频
inotia4/               原版 APK 逆向：工具、文档、反汇编笔记（不参与运行）
docs/                  adr/（决策记录）、agents/（协作规则）、内容格式说明
```

## 内容怎么写

- `public/content/campaign.json` 是剧情、任务、事件与角色摆放的源文件。
- `public/content/world.json`、`maps/`、`battle.json`、`drops.json`、`audio.json` 等保存世界、战斗、掉落与音频数据。
- `public/assets/` 存图片与音频，路径由内容 JSON 引用。
- 字段写法见 [剧情与任务创作](docs/story-content.md)；改完跑域自测——内容形状、跨文件引用与素材引用都在那里校验。

## 验证与验收

- 规则改动：写进 `godot/tests/`，`dotnet run` 全绿。
- 场景与表现改动：跑对应无头自检；画面与手感由人工验收。
- 按项目约定，不主动启动游戏或浏览器自动化；界面和真机效果由用户验收。

## Android

用 `godot/export_presets.cfg` 导出，产物在 `godot/build/`（不入库）。导出前会临时把 `public/` 的内容与素材复制进包，仓库里不会留下副本。
