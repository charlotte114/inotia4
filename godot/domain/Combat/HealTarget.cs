namespace Inotia4.Domain.Combat;

public sealed record HealSlot(Combatant Actor, bool Active, bool HasDisplay, int? Score);

public static class HealTarget
{
    public static int Score(int health, int maxHealth, bool hasDealtDamage)
    {
        if (maxHealth == 0) return 0;
        return (int)Math.Truncate(
            Math.Truncate(health * 100.0 / maxHealth) * (hasDealtDamage ? 13 : 10) / 10.0);
    }

    public static Combatant Select(Combatant caster, IReadOnlyList<HealSlot?> slots, int rangeUnits)
    {
        Combatant? best = null;
        var bestScore = int.MaxValue;

        foreach (var slot in slots)
        {
            if (slot is null || !slot.Active || !slot.HasDisplay) continue;
            if (slot.Actor.Team != caster.Team) continue;
            if (slot.Score is not { } score) continue;
            if (RectDistance.Between(caster, slot.Actor) + 2 > rangeUnits) continue;
            if (score < bestScore)
            {
                best = slot.Actor;
                bestScore = score;
            }
        }

        return best ?? caster;
    }
}
