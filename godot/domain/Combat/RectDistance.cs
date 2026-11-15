using Inotia4.Domain.Content;
using Inotia4.Domain.World;

namespace Inotia4.Domain.Combat;

public interface IPlacedRect
{
    Vec2 Position { get; }

    CollisionBox Bounds { get; }
}

public static class RectDistance
{
    public const int PixelsPerDistanceUnit = 8;

    public static int Between(IPlacedRect a, IPlacedRect b)
    {
        var aLeft = a.Position.X + a.Bounds.X;
        var aTop = a.Position.Y + a.Bounds.Y;
        var bLeft = b.Position.X + b.Bounds.X;
        var bTop = b.Position.Y + b.Bounds.Y;

        var gapX = AxisGap(aLeft, aLeft + a.Bounds.Width, bLeft, bLeft + b.Bounds.Width);
        var gapY = AxisGap(aTop, aTop + a.Bounds.Height, bTop, bTop + b.Bounds.Height);

        return Floor(gapY / PixelsPerDistanceUnit) + Floor(gapX / PixelsPerDistanceUnit);
    }

    private static float AxisGap(float aMin, float aMax, float bMin, float bMax) =>
        MathF.Max(0f, MathF.Max(aMin - bMax, bMin - aMax));

    private static int Floor(float value) => (int)MathF.Floor(value);
}
