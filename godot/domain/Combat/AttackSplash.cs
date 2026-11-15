using Inotia4.Domain.World;

namespace Inotia4.Domain.Combat;

public static class AttackSplash
{
    public readonly record struct SplashSelection(Vec2 Center, string? TargetId, bool ConsumedGaze);

    private static bool HostileVisible(Combatant source, Combatant target) =>
        source.Team != target.Team && (target.States is null || !target.States.Contains("stealthed"));

    public static SplashSelection SelectCenter(
        Combatant source,
        IReadOnlyList<Combatant> candidates,
        int rangeUnits,
        string? currentTargetId = null,
        string? gazeTargetId = null,
        bool retainCurrentTarget = false)
    {
        var current = candidates.FirstOrDefault(target => target.Id == currentTargetId);
        var consumedGaze = gazeTargetId is not null && !(retainCurrentTarget && current is not null);
        Combatant? target = consumedGaze
            ? candidates.FirstOrDefault(value => value.Id == gazeTargetId)
            : current;

        if (target is null && !consumedGaze && current is null)
        {
            var score = 0f;
            var origin = new Vec2((int)source.Position.X, (int)source.Position.Y);
            foreach (var candidate in candidates.Take(100))
            {
                if (!HostileVisible(source, candidate)) continue;
                var position = new Vec2((int)candidate.Position.X, (int)candidate.Position.Y);
                var value = BasicAttackRules.TargetScore(origin, position, source.Facing ?? "south", rangeUnits * 8f);
                if (value > score)
                {
                    target = candidate;
                    score = value;
                }
            }
        }

        if (target is not null &&
            BasicAttackRules.AttackDistance(source.Position, source.Bounds, target.Position, target.Bounds) <= rangeUnits)
            return new SplashSelection(target.Position, target.Id, consumedGaze);

        return new SplashSelection(source.Position, null, consumedGaze);
    }

    public static List<Combatant> Targets(
        Combatant source, Vec2 center, IReadOnlyList<Combatant> candidates, int rangeUnits)
    {
        var selected = new List<Combatant>();
        foreach (var candidate in candidates.Take(100))
        {
            if (!HostileVisible(source, candidate) ||
                BasicAttackRules.TargetDistance(center, candidate.Position) / 8 > rangeUnits)
                continue;

            selected.Add(candidate);
            if (selected.Count == SkillUnitSequence.MaxSkillTargets) break;
        }

        return selected;
    }
}
