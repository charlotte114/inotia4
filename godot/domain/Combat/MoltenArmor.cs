using System;
using System.Collections.Generic;
using System.Linq;
using Inotia4.Domain.World;

namespace Inotia4.Domain.Combat;

public static class MoltenArmor
{
    public readonly record struct DefenseSource(string Kind, int PhysicalAttack, int Defense, int DefenseRate);

    public static int ReflectionDamage(DefenseSource source, int ratePerThousand)
    {
        var basis = source.Kind == "monster"
            ? Math.Max(0, source.PhysicalAttack)
            : Fighter.EffectiveDefense(Math.Max(0, source.Defense), Math.Max(0, source.DefenseRate));
        return (int)Math.Truncate(basis * (double)Math.Max(0, ratePerThousand) / 1000);
    }

    public static int? RollSplash(int reflection, int chanceThreshold, Func<double> random) =>
        1 + (int)Math.Floor(random() * 999) < chanceThreshold ? (int)Math.Truncate(reflection / 2.0) : null;

    public static List<Combatant> SplashTargets(Combatant source, Vec2 center,
        IReadOnlyList<Combatant> candidates, string attackerId) =>
        AttackSplash.Targets(source, center, candidates, 4)
            .Where(target => target.Id != attackerId).ToList();
}
