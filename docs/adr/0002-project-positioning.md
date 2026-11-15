# 项目定位与素材边界

本仓库（项目名 **inotia4**）是《艾诺迪亚4》（1.2.9）的**逆向重写**：规则、内容与素材都从原包（`inotia4/`）逆出来，用 Godot 4.5 + C# 写成，只开发 `godot/` 这一条线。

## 边界

- **`public/` 是运行时依赖，不能删。** `godot/scripts/Application/ContentRoot.cs` 直接找 `../public` 读 `content/` 与 `assets/`，删掉游戏起不来。素材共 11,556 张 PNG，不进 Godot 导入管线，按需解码。
- **`inotia4/` 是一手材料，不是运行时依赖。** 原包字节、反汇编与导出工具都在这里，回答「行为为什么是这样」。
- **「与原版一致」的依据只有两个**：`inotia4/docs/` 的逆向记录，和 `godot/tests` 的回归用例。两者都没有，就不许这么说。

## Consequences

- 内容改动跑域自测即可覆盖形状、跨文件引用与素材引用校验。
- `public/content/debug/start.json`（GM 现场表，1.6MB）没有生成器：要改只能手改，改完跑域自测确认还能解析。
- 存档只保证本实现相邻版本可读（见 [0001](0001-godot-single-track-and-save-compat.md)）。
