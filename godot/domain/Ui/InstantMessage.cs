using System;

namespace Inotia4.Domain.Ui;

/// <summary>
/// 屏幕即时提示（原版的是 <c>INSTANTMSGSYSTEM_Add</c> 负责挂上去、
/// <c>INSTANTMSG_Draw</c> 负责画出来）。
///
/// 泉水效果、技能放不出来这类「一句话打发」的反馈走这条通道：水平居中、纵向三成屏高，
/// 活 30 个逻辑帧（1.8 秒），最后四帧按 75／50／25 的台阶淡出，然后整条收起。
/// </summary>
public static class InstantMessages
{
    public const int DurationMs = 30 * Combat.CombatClock.LogicFrameMs;

    private const float HeightRatio = 0.3f;

    public const int FontSize = 24;

    public static (float X, float Y) Point(float width, float height) =>
        (MathF.Floor(width / 2f), MathF.Floor(height * HeightRatio));

    /// <summary>
    /// 透明度：剩下的整帧数按每帧 25% 台阶降，0 帧就不画了。
    /// 所以可见的最后三段是 0.75／0.5／0.25。
    /// </summary>
    public static float Alpha(long remainingMs)
    {
        if (remainingMs <= 0) return 0f;
        var frames = (long)Math.Ceiling(remainingMs / (double)Combat.CombatClock.LogicFrameMs);
        return MathF.Min(1f, frames * 25 / 100f);
    }
}
