using Inotia4.Domain.Content;

namespace Inotia4.Domain.Effects;

public sealed record LayerDraw(string Kind, string? Blend, string[] Operations)
{
    public bool IsBackdrop => Kind == "backdrop";
}

/// <summary>
/// 特效颜色与混合的公共判断：每层该按哪种方式画、哪些运算必须读背景。
///
/// 一帧可以挂一串滤镜：运算按顺序作用在「背景通道」和「源通道」上，透明度是链末的最后一步。
/// 这里不碰引擎，渲染器与内容校验共用同一份判断。
/// </summary>
public static class EffectColors
{
    public const int FormatVersion = 1;

    /// <summary>滤镜运算名，按 <c>SNASYS_ApplyFrameFilter</c> 的位序排列。</summary>
    public static readonly string[] Operations =
        ["multiply", "add", "screen", "hard-light", "color-dodge", "overlay"];

    public static readonly string[] DirectBlends = [EffectBlends.Normal, EffectBlends.Multiply, EffectBlends.Screen, EffectBlends.Add];

    /// <summary>
    /// <c>backdrop</c> 层在没有背景合成路径时用的混合方式。
    ///
    /// 这两个运算的结果取决于背景：一个按背景截顶、一个按背景分支，离线折不出精确的编码；读背景的合成
    /// 还没接通，所以先按最接近的相加画，颜色偏亮的部分记为差异。
    /// </summary>
    public static string BackdropApproximation(string[] operations) =>
        operations.Contains("multiply") ? EffectBlends.Multiply : EffectBlends.Add;

    public static LayerDraw LayerDrawOf(EffectLayer layer)
    {
        if (layer.Blend != EffectBlends.Backdrop)
            return new LayerDraw("blend", layer.Blend, []);

        if (layer.Operations.Length == 0)
            throw new InvalidOperationException($"backdrop 层没有可作用的运算：{layer.Key}");

        return new LayerDraw("backdrop", null, layer.Operations);
    }

    public static string? LayerProblem(EffectClip clip, EffectLayer layer)
    {
        if (clip.Version != FormatVersion)
            return $"特效契约版本不匹配：{layer.Key} 是 {clip.Version}，运行时支持 {FormatVersion}";

        foreach (var operation in layer.Operations)
            if (!Operations.Contains(operation))
                return $"未核实的颜色运算：{layer.Key} {operation}";

        if (layer.Blend == EffectBlends.Backdrop)
            return layer.Operations.Length > 0 ? null : $"backdrop 层没有可作用的运算：{layer.Key}";

        return DirectBlends.Contains(layer.Blend) ? null : $"未核实的绘制方式：{layer.Key} {layer.Blend}";
    }

    public static List<string> Problems(IReadOnlyDictionary<string, StoryEffectEntry> effects)
    {
        var problems = new List<string>();
        foreach (var (name, effect) in effects)
        foreach (var clip in effect.Clips.Values)
        foreach (var layer in clip.Layers)
        {
            var problem = LayerProblem(clip, layer);
            if (problem is not null) problems.Add($"{name}: {problem}");
        }

        return problems;
    }
}
