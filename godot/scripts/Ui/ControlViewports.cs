using System;
using Godot;
using Inotia4.Domain.Ui;

namespace Inotia4.Ui;

/// <summary>
/// 操作区计算用的视口：窗口尺寸加四周的安全区内缩。
///
/// 内缩取的是系统给的安全区（刘海屏、圆角、手势条那圈不可遮挡的边）；Godot 侧用
/// <see cref="DisplayServer.GetDisplaySafeArea"/> 减去屏幕范围得到这组数字。
/// 只有窗口铺满整块屏幕（全屏／移动端）时内缩才有意义——桌面窗口居中时，
/// 「屏幕到窗口的距离」不是安全区，按 0 处理。
/// </summary>
public static class ControlViewports
{
    public static ControlViewport Current(Control node)
    {
        var size = node.GetViewportRect().Size;
        var (left, right, top, bottom) = Insets();
        return new ControlViewport(size.X, size.Y, left, right, top, bottom);
    }

    private static (float Left, float Right, float Top, float Bottom) Insets()
    {
        if (DisplayServer.GetName() == "headless") return (0f, 0f, 0f, 0f);

        var safe = DisplayServer.GetDisplaySafeArea();
        if (safe.Size.X <= 0 || safe.Size.Y <= 0) return (0f, 0f, 0f, 0f);

        var window = DisplayServer.WindowGetPosition();
        var size = DisplayServer.WindowGetSize();
        var screenIndex = DisplayServer.WindowGetCurrentScreen();
        var screen = DisplayServer.ScreenGetSize(screenIndex);

        if (size.X < screen.X - 2 || size.Y < screen.Y - 2) return (0f, 0f, 0f, 0f);

        var left = Math.Max(0, safe.Position.X - window.X);
        var top = Math.Max(0, safe.Position.Y - window.Y);
        var right = Math.Max(0, window.X + size.X - (safe.Position.X + safe.Size.X));
        var bottom = Math.Max(0, window.Y + size.Y - (safe.Position.Y + safe.Size.Y));
        return (left, right, top, bottom);
    }
}
