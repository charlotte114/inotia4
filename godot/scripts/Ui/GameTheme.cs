using Godot;

namespace Inotia4.Ui;

/// <summary>
/// 全局主题：给所有界面一个能画中文的字体。
///
/// Godot 内置字体不含 CJK，直接写中文会变成一排空白方块。原版是网页版，
/// 中文能靠系统字体兜底；导出到 Android 没有这层兜底，必须自带字体。
/// 这里用 <see cref="SystemFont"/> 按名字向操作系统要字体：桌面命中微软雅黑／苹方，
/// Android 命中 Noto Sans CJK。既不用打包几十兆字体文件，也避开字体授权问题。
/// </summary>
public static class GameTheme
{
    private static readonly string[] FontNames =
    [
        "Microsoft YaHei UI",
        "Microsoft YaHei",
        "PingFang SC",
        "Noto Sans CJK SC",
        "Noto Sans SC",
        "Source Han Sans SC",
        "WenQuanYi Micro Hei",
        "sans-serif",
    ];

    public static Theme Create(int baseFontSize = 16)
    {
        var font = new SystemFont
        {
            FontNames = FontNames,
            AllowSystemFallback = true,
        };

        return new Theme
        {
            DefaultFont = font,
            DefaultFontSize = baseFontSize,
        };
    }

    public static void Apply(Node node, int baseFontSize = 16)
    {
        if (node.GetTree().Root is { } root) root.Theme = Create(baseFontSize);
    }
}
