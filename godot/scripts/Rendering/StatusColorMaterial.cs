using Godot;
using Inotia4.Domain.Combat;

namespace Inotia4.Rendering;

/// <summary>
/// 增益着色用的材质：给角色图层套一层颜色矩阵（中毒泛绿、冻结泛蓝这类整体偏色）。
///
/// Godot 没有现成的颜色矩阵滤镜，
/// 所以写成一段 <c>canvas_item</c> 着色器：把矩阵的三行系数与偏移当 uniform 传进去。
///
/// 着色器同时承担混合方式：<c>render_mode</c> 按这一层的混合挑
/// <c>blend_mix</c>／<c>blend_add</c>／<c>blend_mul</c>，
/// 与 <see cref="BlendMaterials"/> 那张表一一对应（Godot 的一个节点只能有一份材质，
/// 颜色矩阵和混合方式必须写在同一份材质里）。
/// </summary>
public static class StatusColorMaterial
{
    public static ShaderMaterial Create(string blend)
    {
        var material = new ShaderMaterial { Shader = new Shader { Code = ShaderFor(blend) } };
        Apply(material, null);
        return material;
    }

    public static void Apply(ShaderMaterial material, float[]? matrix)
    {
        var values = matrix ?? StatusColorRules.Identity();
        material.SetShaderParameter("row_r", new Vector3(values[0], values[1], values[2]));
        material.SetShaderParameter("row_g", new Vector3(values[5], values[6], values[7]));
        material.SetShaderParameter("row_b", new Vector3(values[10], values[11], values[12]));
        material.SetShaderParameter("offset", new Vector3(values[4], values[9], values[14]));
    }

    public static bool IsBlackBackdrop(string blend) => blend is "add" or "screen";

    private static string RenderModeFor(string blend) => blend switch
    {
        "add" => "blend_add",
        "multiply" => "blend_mul",
        _ => "blend_mix",
    };

    private static string ShaderFor(string blend) => blend == "screen"
        ? $$"""
shader_type canvas_item;

{{ScreenBlend.Sampler}}

uniform vec3 row_r;
uniform vec3 row_g;
uniform vec3 row_b;
uniform vec3 offset;

void fragment() {
    vec4 source = COLOR;
    vec3 tinted = vec3(dot(source.rgb, row_r) + offset.x,
                       dot(source.rgb, row_g) + offset.y,
                       dot(source.rgb, row_b) + offset.z);
    vec3 backdrop = texture(screen_texture, SCREEN_UV).rgb;
    vec3 lit = 1.0 - (1.0 - backdrop) * (1.0 - tinted);
    COLOR = vec4(mix(backdrop, lit, source.a), 1.0);
}
"""
        : $$"""
shader_type canvas_item;
render_mode {{RenderModeFor(blend)}};

uniform vec3 row_r;
uniform vec3 row_g;
uniform vec3 row_b;
uniform vec3 offset;

void fragment() {
    vec4 source = COLOR;
    COLOR = vec4(dot(source.rgb, row_r) + offset.x,
                 dot(source.rgb, row_g) + offset.y,
                 dot(source.rgb, row_b) + offset.z,
                 source.a);
}
""";
}
