using System;

namespace Inotia4.Domain.Ui;

public static class GaugeRules
{
    public static float Width(int value, int max, float width)
    {
        if (max <= 0) return 0f;
        var ratio = (float)value / max;
        return MathF.Floor(width * Math.Clamp(ratio, 0f, 1f));
    }
}
