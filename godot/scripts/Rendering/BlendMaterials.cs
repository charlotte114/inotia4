using System;
using Godot;

namespace Inotia4.Rendering;

/// <summary>
/// 混合方式到 Godot 材质的映射。
///
/// 人物图层、场景物件与特效共用这几档混合方式：
/// <c>normal</c>／<c>multiply</c>／<c>screen</c>／<c>add</c> 各有对应，
/// <c>color-dodge</c> 没有，直接报错（内容里出现它就该当成素材问题查）。
///
/// Godot 的 <see cref="CanvasItemMaterial"/> 有 <c>Add</c> 与 <c>Mul</c>，但没有 <c>Screen</c>。
/// 常驻图层（人物、景物）的 <c>screen</c> 走 <see cref="ScreenBlend"/> 那支读屏着色器，
/// 公式与正常的滤色混合一致；<b>一次性特效</b>不读屏、按叠加近似——见 <see cref="For"/> 的说明。
///
/// <c>color-dodge</c> 一律报错：不能悄悄画成普通混合，把问题藏起来。
/// </summary>
public static class BlendMaterials
{
    /// <summary>
    /// 按混合名取材质。<c>normal</c> 返回 <c>null</c>（走默认画法），
    /// <c>color-dodge</c> 直接抛错。
    ///
    /// <paramref name="transient"/> 表示这个精灵是「出现几帧就销毁」的一次性特效
    /// （挥击弧光、命中冲击、伤害数字）。这类精灵<b>不能</b>挂读屏着色器：
    /// 它们频繁增删，读屏要的屏幕拷贝常拿到错乱或残缺的一份，结果是精灵整块矩形
    /// 把下面的画面（主角、身边的阻挡物）刷成一片空白——表现就是「身边的东西缺一块」。
    /// 历史台账 #61 记过这件事（#77 的同类问题在 backdrop 滤镜链上已经踩过一次；台账已删、查 git 历史）。
    /// 常驻图层（人物、火把光晕、景物）照旧走读屏，画面差别本来就出在那里。
    /// </summary>
    public static Material? For(string blend, bool transient = false) => blend switch
    {
        "normal" => null,
        "add" => new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add },
        "multiply" => new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Mul },
        "screen" when transient => new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add },
        "screen" => ScreenBlend.Material(),
        "color-dodge" => throw new InvalidOperationException($"颜色混合模式没有 Godot 对应：{blend}"),
        _ => null,
    };
}
