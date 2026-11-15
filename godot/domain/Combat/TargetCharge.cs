using Inotia4.Domain.World;

namespace Inotia4.Domain.Combat;

public readonly record struct ChargePlan(Vec2 Destination, bool Close);

public static class TargetCharge
{
    private const int MaxPathSearchSteps = 101;

    public static ChargePlan? Plan(
        Combatant caster, Combatant target, SkillCharge charge, Func<Vec2, bool> canStand)
    {
        if (BasicAttackRules.AttackDistance(caster.Position, caster.Bounds, target.Position, target.Bounds) - 2 <
            charge.CloseDistanceUnits)
            return new ChargePlan(caster.Position, true);

        var size = charge.GridSizePx;
        var start = new GridPoint((int)MathF.Floor(caster.Position.X / size),
            (int)MathF.Floor(caster.Position.Y / size));
        var goal = new GridPoint((int)MathF.Floor(target.Position.X / size),
            (int)MathF.Floor(target.Position.Y / size));

        var open = new List<GridStep> { new(start, 0) };
        var costs = new Dictionary<GridPoint, int> { [start] = 0 };

        for (var visited = 0; open.Count > 0 && visited < MaxPathSearchSteps; visited += 1)
        {
            open = open.OrderBy(step => step.Cost + Distance(step.Point, goal)).ToList();
            var current = open[0];
            open.RemoveAt(0);
            if (current.Cost != costs[current.Point]) continue;

            var destination = new Vec2(current.Point.X * size, current.Point.Y * size);
            if (Distance(current.Point, goal) <= charge.StoppingDistanceUnits && canStand(destination))
                return new ChargePlan(destination, false);

            foreach (var (dx, dy) in Neighbours)
            {
                var next = new GridPoint(current.Point.X + dx, current.Point.Y + dy);
                var cost = current.Cost + 1;
                if (cost >= costs.GetValueOrDefault(next, int.MaxValue)) continue;
                if (!canStand(new Vec2(next.X * size, next.Y * size))) continue;
                costs[next] = cost;
                open.Add(new GridStep(next, cost));
            }
        }

        return null;
    }

    private static readonly (int Dx, int Dy)[] Neighbours = [(-1, 0), (0, -1), (0, 1), (1, 0)];

    private readonly record struct GridPoint(int X, int Y);

    private readonly record struct GridStep(GridPoint Point, int Cost);

    private static int Distance(GridPoint a, GridPoint b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);
}
