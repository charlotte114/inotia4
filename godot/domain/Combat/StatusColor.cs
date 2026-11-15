namespace Inotia4.Domain.Combat;

public static class StatusColorRules
{
    /// <summary>
    /// 按逻辑帧取当前颜色并展开成颜色矩阵。
    ///
    /// <paramref name="logicalFrame"/> 决定用第几项颜色（增益色按帧轮播）；
    /// <paramref name="hitFlash"/> 是受击闪白：先把亮度抬一档（原版 GRP_SetGamma(50)）再叠着色；
    /// <paramref name="blackBackdrop"/> 表示这一层是叠加／滤色混合——那两种混合以黑色为单位元，
    /// 矩阵必须保持 0 → 0，否则整块黑色背景会被抬成灰白色。
    /// </summary>
    public static float[] Matrix(IReadOnlyList<BuffColor> colors, int logicalFrame,
        bool hitFlash = false, bool blackBackdrop = false)
    {
        if (colors.Count == 0) return Identity();

        var color = colors[Math.Max(0, logicalFrame) % colors.Count];

        if (blackBackdrop)
        {
            var gain = hitFlash ? 1.5f : 1f;
            return
            [
                gain * (1f + color.Red / 255f), 0f, 0f, 0f, 0f,
                0f, gain * (1f + color.Green / 255f), 0f, 0f, 0f,
                0f, 0f, gain * (1f + color.Blue / 255f), 0f, 0f,
                0f, 0f, 0f, 1f, 0f,
            ];
        }

        var matrix = new float[]
        {
            1f - color.Red / 255f, 0f, 0f, 0f, color.Red,
            0f, 1f - color.Green / 255f, 0f, 0f, color.Green,
            0f, 0f, 1f - color.Blue / 255f, 0f, color.Blue,
            0f, 0f, 0f, 1f, 0f,
        };

        // GRP_SetGamma(50)：c + ((256-c)*128 >> 8)，之后才是增益着色。
        if (hitFlash)
            foreach (var (scale, offset) in new[] { (0, 4), (6, 9), (12, 14) })
            {
                matrix[offset] += 128f * matrix[scale];
                matrix[scale] *= 0.5f;
            }

        return matrix;
    }

    public static float[] Identity() =>
    [
        1f, 0f, 0f, 0f, 0f,
        0f, 1f, 0f, 0f, 0f,
        0f, 0f, 1f, 0f, 0f,
        0f, 0f, 0f, 1f, 0f,
    ];
}
