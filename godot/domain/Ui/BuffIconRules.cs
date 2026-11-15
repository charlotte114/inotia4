using System;

namespace Inotia4.Domain.Ui;

/// <summary>
/// 状态图标：摆放位置与闪烁节奏。
///
/// 图标排在面板左下角、依次向左：第一枚的下边缘齐面板底边，左边缘在面板左缘左侧半个图标宽处
/// （所以右边缘落在面板左缘右移半个图标宽，图标骑在左缘上），之后每枚再往左挪一个间距。
/// 坐标是面板的局部坐标（原点在面板左上角），面板缩放时图标跟着缩放。
/// </summary>
public static class BuffIconRules
{
    public const int BlinkWindowFrames = 30;

    public const int BlinkPeriodFrames = 4;

    public static (float X, float Y) Position(float size, float step, float panelBottom, int index) =>
        (-size / 2f - index * step, panelBottom - size);

    public static double RemainingFrames(double remainingMs, int logicFrameMs) =>
        Math.Max(0, Math.Ceiling(remainingMs / Math.Max(1, logicFrameMs)));

    /// <summary>
    /// 这一帧该不该藏起来闪烁：只剩最后 30 帧时，每 4 帧一个明暗周期（奇数周期隐藏）。
    /// 隐藏只是不画，槽位保留——后面的图标不会往前挤。
    /// </summary>
    public static bool Blinking(double remainingFrames, long tick) =>
        remainingFrames <= BlinkWindowFrames && tick / BlinkPeriodFrames % 2 == 1;
}
