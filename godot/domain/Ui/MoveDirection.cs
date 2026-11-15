using System;
using Inotia4.Domain.World;

namespace Inotia4.Domain.Ui;

public static class MoveDirection
{
    public static Vec2 Snap(Vec2 axis)
    {
        if (axis.X == 0f && axis.Y == 0f) return Vec2.Zero;

        var degrees = MathF.Atan2(axis.Y, axis.X) * 180f / MathF.PI;
        if (degrees >= -45f && degrees < 45f) return new Vec2(1f, 0f);
        if (degrees >= 45f && degrees < 135f) return new Vec2(0f, 1f);
        if (degrees >= -135f && degrees < -45f) return new Vec2(0f, -1f);
        return new Vec2(-1f, 0f);
    }

    public static bool IsCardinal(MoveControlStyle style) =>
        style is MoveControlStyle.Dpad or MoveControlStyle.FourWay;
}
