using Godot;

namespace Inotia4.Rendering;

/// <summary>
/// <c>screen</c>（滤色）混合的着色器实现。
///
/// Godot 的 <see cref="CanvasItemMaterial"/> 只有 Mix／Add／Sub／Mul，没有 Screen；
/// 而屏幕混合按 <c>1 − (1 − 背景)(1 − 源)</c> 算，亮区不会像加法那样一片糊白，
/// 这个差别在光晕与技能特效上是看得出来的。
/// 内容里 759 处用了 <c>screen</c>（火把／路灯的光晕层、技能特效用得最多），
/// 所以这里用一段 <c>canvas_item</c> 着色器读屏实现，而不是拿加法近似。
///
/// 写法要点：
/// <list type="bullet">
/// <item>读 <c>screen_texture</c>（Godot 会自动插一次 backbuffer 拷贝）；</item>
/// <item>输出 alpha 恒为 1，让默认混合直接覆盖——但颜色已经按源 alpha 与背景插值过，
/// 源透明处结果就等于背景，视觉上是原样透出；</item>
/// <item>同一帧里多处 screen 图层读到的是同一份背景快照，彼此不会叠加进对方的背景——
/// 逐对象硬件混合不会有这个问题，这里会有细微差别，但比加法近似小得多。</item>
/// </list>
/// </summary>
public static class ScreenBlend
{
    /// <summary>
    /// 屏幕混合的片元着色器。状态染色的那份材质也要这一段，
    /// 所以公式只在这里写一次（见 <see cref="StatusColorMaterial"/>）。
    /// </summary>
    public const string Body = """
    void fragment() {
        vec4 source = COLOR;
        if (source.a <= 0.001) discard;
        vec3 backdrop = texture(screen_texture, SCREEN_UV).rgb;
        vec3 lit = 1.0 - (1.0 - backdrop) * (1.0 - source.rgb);
        COLOR = vec4(mix(backdrop, lit, source.a), 1.0);
    }
    """;

    public const string Sampler = "uniform sampler2D screen_texture : hint_screen_texture, repeat_disable, filter_nearest;";

    public const string Code = $"""
    shader_type canvas_item;

    {Sampler}

    {Body}
    """;

    private static Shader? _shader;

    public static ShaderMaterial Material() =>
        new() { Shader = _shader ??= new Shader { Code = Code } };

    public static bool IsScreen(Material? material) =>
        material is ShaderMaterial shader && shader.Shader is not null && shader.Shader == _shader;
}
