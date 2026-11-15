using Inotia4.Domain.World;

namespace Inotia4.Domain.Combat;

public static class RearTeleportRules
{
    private const int GridSize = 8;

    private const int Gap = 8;

    private static readonly string[] Directions = ["south", "west", "north", "east"];

    public static Vec2? Plan(
        Combatant caster,
        Combatant target,
        Func<Vec2, bool> canStand,
        Func<Vec2, bool> canReach,
        Rect? mapBounds = null)
    {
        if (target.Facing is null) return null;

        var facing = Array.IndexOf(Directions, target.Facing);
        if (facing < 0) return null;

        var left = target.Position.X + target.Bounds.X;
        var top = target.Position.Y + target.Bounds.Y;
        var right = left + target.Bounds.Width;
        var bottom = top + target.Bounds.Height;
        var opposite = (facing + 2) % 4;

        foreach (var turn in new[] { 0, 1, 3, 2 })
        {
            var direction = Directions[(opposite + turn) % 4];
            var region = direction switch
            {
                "north" => (Left: left, Right: right,
                    Top: top - caster.Bounds.Y - caster.Bounds.Height - Gap, Bottom: top - Gap),
                "south" => (Left: left, Right: right,
                    Top: bottom - Gap, Bottom: bottom - caster.Bounds.Y + Gap),
                "west" => (Left: left + caster.Bounds.X - Gap, Right: left - Gap,
                    Top: top, Bottom: bottom),
                _ => (Left: right - Gap, Right: right + caster.Bounds.X + caster.Bounds.Width + Gap,
                    Top: top, Bottom: bottom),
            };
            var preferred = direction switch
            {
                "north" => new Vec2(target.Position.X, region.Top),
                "south" => new Vec2(target.Position.X, region.Bottom),
                "west" => new Vec2(region.Left, target.Position.Y),
                _ => new Vec2(region.Right, target.Position.Y),
            };

            var firstColumn = Math.Max(
                (int)MathF.Floor((mapBounds?.X ?? 0f) / GridSize),
                (int)MathF.Floor(region.Left / GridSize));
            var lastColumn = Math.Min(
                mapBounds is { } bounds ? (int)MathF.Floor((bounds.X + bounds.Width) / GridSize) : int.MaxValue,
                (int)MathF.Floor(region.Right / GridSize));
            if (firstColumn > lastColumn) continue;

            var row = (int)MathF.Floor(preferred.Y / GridSize);
            var column = (int)MathF.Floor(preferred.X / GridSize);
            var attempts = direction is "north" or "south" ? 1 : lastColumn - firstColumn + 1;

            for (var attempt = 0; attempt < attempts; attempt += 1)
            {
                var candidate = new Vec2(column * GridSize, row * GridSize);
                if (canStand(candidate))
                {
                    if (canReach(candidate)) return candidate;
                    break;
                }

                column = direction == "east"
                    ? Math.Max(firstColumn, column - 1)
                    : column >= lastColumn ? firstColumn : column + 1;
            }
        }

        return null;
    }
}
