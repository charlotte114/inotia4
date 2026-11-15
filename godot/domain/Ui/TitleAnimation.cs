using System;
using System.Collections.Generic;

namespace Inotia4.Domain.Ui;

/// <summary>
/// 标题页背景的一层振荡，六组结构里的一组（整份依据记在
/// <c>inotia4/docs/SO_REVERSE.md</c> §19，六组的常量见 <see cref="TitleAnimation"/>）。
///
/// 每组是一个小状态机：<c>int 透明度</c>（0..100 的整数刻度，不是浮点）、
/// <c>int 计步器</c>（0..359）、<c>u8 方向</c>（0 或 1）。
///
/// 每帧先按「方向」决定增量，加到透明度上，再按两条边界回写方向：
/// 透明度 &gt; 100 就清零并把方向置 0；透明度 ≤ 阈值则把方向置 1。
/// 所以「方向」不是升降开关，而是**决定这一步用哪个增量**——
/// A/C 两组两种增量都是正的（只涨不落，是锯齿），B/D/E/F 两组是 ±1。
/// </summary>
public sealed class TitleLayer
{
    public const int Full = 100;

    /// <summary>计步器到顶就归零的周期（原版 0x168）。</summary>
    public const int StepPeriod = 360;

    public int Opacity { get; private set; }

    public int Step { get; private set; }

    public int Direction { get; private set; }

    public int Threshold { get; }

    public int StepWhenZero { get; }

    public int StepWhenOne { get; }

    public bool AlwaysRise { get; }

    public TitleLayer(int opacity, int step, int direction, int threshold,
        int stepWhenZero, int stepWhenOne, bool alwaysRise)
    {
        if (threshold is < 0 or > Full) throw new ArgumentOutOfRangeException(nameof(threshold));
        Opacity = Math.Clamp(opacity, 0, Full);
        Step = step % StepPeriod;
        Direction = direction == 0 ? 0 : 1;
        Threshold = threshold;
        StepWhenZero = stepWhenZero;
        StepWhenOne = stepWhenOne;
        AlwaysRise = alwaysRise;
    }

    public void Advance()
    {
        Step = (Step + 1) % StepPeriod;

        Opacity += Direction == 0 ? StepWhenZero : StepWhenOne;

        if (Opacity > Full)
        {
            // 撞顶不是回弹，是直接清零（绘制那句取补数当 alpha：100 时补数是 0，
            // 也就是最暗——所以「透明度」这个名字在原版里其实反着用）。
            Opacity = 0;
            Direction = 0;
        }
        else if (Opacity <= Threshold)
        {
            if (Opacity < 0) Opacity = 0;   // 防御：只在给了越界初相位时才会出现
            Direction = 1;
        }
    }

    /// <summary>
    /// 交给绘制的 alpha：<c>(100 - 透明度) / 100</c>。原版的透明度是反着用的，
    /// 撞到 100 时补数为 0，画面最暗，所以这里要取补数才等于绘制时的 alpha。
    /// </summary>
    public float Alpha => (Full - Opacity) / (float)Full;
}

/// <summary>
/// 标题页那六层振荡（依据见 <c>inotia4/docs/SO_REVERSE.md</c> §19.2）。
///
/// 六块的常量全部来自反汇编的置数段与各块的读写偏移（<c>ldrb</c>／<c>strb</c>）。
/// 注意 A、C 两块与其余四块的语义不同：A/C 的两种增量都是正的（只涨不落），
/// B/D/E/F 才是 ±1——这个区别是从各自的立即数读出来的，
/// 直接当成「涨 2 落 1」会把锯齿做成来回摆。
/// </summary>
public sealed class TitleAnimation
{
    /// <summary>
    /// 每层的定义：<c>(初始透明度, 初始计步, 初始方向, 阈值, 方向0增量, 方向1增量, 只涨不落)</c>。
    ///
    /// 初值取 `0xD7A28`–`0xD7A60` 的置数段（2026-11-12 重核）：
    /// 透明度写 `[+0x38]/[+0x3C]/[+0x40]/[+0x44]/[+0x48]/[+0x4C]`，值是
    /// 60／50／60／30／50／40；计步器写 `[+0x24]/[+0x2C]/[+0x28]/[+0x34]/[+0x20]/[+0x30]`，
    /// 值是 0／90／90／180／0／180；六个方向字节 `[+0x55]..[+0x50]` 都置 1。
    ///
    /// **初值不是全 0**：六层的初始透明度本来就各不相同，靠这一点错开相位。
    /// 早先这里全写 0，六层就会同步明灭（`计步器`只写不读，错不开相位）。
    /// </summary>
    public static readonly (int Opacity, int Step, int Direction, int Threshold,
        int StepWhenZero, int StepWhenOne, bool AlwaysRise)[] LayerSpecs =
    [
        //  A 0xD7A80  +0x38/+0x24  阈值 0x3B  初值 60/0     mvneq #1 / movne #2 → 只涨
        (60, 0, 1, 59, +1, +2, true),
        //  B 0xD7AE6  +0x3C/+0x2C  阈值 0x31  初值 50/90    moveq #-1 / movne #1 → 来回
        (50, 90, 1, 49, -1, +1, false),
        //  C 0xD7B4E  +0x40/+0x28  阈值 0x3B  初值 60/90    mvneq #1 / movne #2 → 只涨
        (60, 90, 1, 59, +1, +2, true),
        //  D 0xD7BB2  +0x44/+0x34  阈值 0x1D  初值 30/180   moveq #-1 / movne #1 → 来回
        (30, 180, 1, 29, -1, +1, false),
        //  E 0xD7C32  +0x48/+0x20  阈值 0x31  初值 50/0     moveq #-1 / movne #1 → 来回
        (50, 0, 1, 49, -1, +1, false),
        //  F 0xD7CB8  +0x4C/+0x30  阈值 0x27  初值 40/180   moveq #-1 / movne #1 → 来回
        (40, 180, 1, 39, -1, +1, false),
    ];

    private readonly TitleLayer[] _layers;

    public TitleAnimation()
    {
        _layers = new TitleLayer[LayerSpecs.Length];
        for (var i = 0; i < LayerSpecs.Length; i++)
        {
            var spec = LayerSpecs[i];
            _layers[i] = new TitleLayer(spec.Opacity, spec.Step, spec.Direction,
                spec.Threshold, spec.StepWhenZero, spec.StepWhenOne, spec.AlwaysRise);
        }
    }

    public IReadOnlyList<TitleLayer> Layers => _layers;

    public void Advance()
    {
        foreach (var layer in _layers) layer.Advance();
    }

    public float AlphaOf(int index) => _layers[index].Alpha;
}
