using Godot;
using Inotia4.Domain.Story;

namespace Inotia4.Rendering;

public static class MapEffectTone
{
    public static bool Enabled { get; private set; }

    public static bool SetEnabled(bool enabled)
    {
        if (Enabled == enabled) return false;
        Enabled = enabled;
        return true;
    }

    public static void Reset() => Enabled = false;

    private static readonly float[] Matrix =
    [
        17710f / 65536f, 34730f / 65536f, 6440f / 65536f, 0f, 0f,
        16940f / 65536f, 33220f / 65536f, 6160f / 65536f, 0f, 0f,
        17710f / 65536f, 34730f / 65536f, 6440f / 65536f, 0f, 0f,
        0f, 0f, 0f, 1f, 0f,
    ];

    public static ShaderMaterial CreateMaterial()
    {
        var shader = new Shader
        {
            Code = ShaderCode,
        };

        var material = new ShaderMaterial { Shader = shader };
        material.SetShaderParameter("row_r", new Vector3(Matrix[0], Matrix[1], Matrix[2]));
        material.SetShaderParameter("row_g", new Vector3(Matrix[5], Matrix[6], Matrix[7]));
        material.SetShaderParameter("row_b", new Vector3(Matrix[10], Matrix[11], Matrix[12]));
        return material;
    }

    /// <summary>
    /// 逐像素乘同一组系数。Godot 的 canvas_item 着色器拿到的 <c>COLOR</c> 已是乘过顶点色的
    /// 纹理采样，所以这里直接按矩阵重算 RGB，alpha 原样透传（矩阵第四行是 [0,0,0,1,0]）。
    /// </summary>
    private const string ShaderCode = """
shader_type canvas_item;

uniform vec3 row_r;
uniform vec3 row_g;
uniform vec3 row_b;

void fragment() {
    vec3 source = COLOR.rgb;
    COLOR.rgb = vec3(dot(source, row_r), dot(source, row_g), dot(source, row_b));
}
""";
}
