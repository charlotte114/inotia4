using System;

namespace Inotia4.Domain.Combat;

public static class ActionStateRules
{
    public static bool PassesChanceThreshold(int threshold, Func<double> random) =>
        threshold == 1000 || (int)Math.Floor(random() * 999) + 1 < threshold;

    public static (bool Applies, int DurationMs) ResolveActionState(StateCombatUnit unit, int rank,
        Func<double> random)
    {
        if (unit.Ranks.Count == 0) return (false, 0);

        var index = Math.Min(unit.Ranks.Count - 1, Math.Max(0, rank - 1));
        var values = unit.Ranks[index];
        return (PassesChanceThreshold(values.ChanceThreshold, random), values.DurationMs);
    }
}
