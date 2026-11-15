namespace Inotia4.Domain.Combat;

public sealed record PartyBuffSlot(Combatant Actor, bool Active, bool HasDisplay);

public static class PartyBuffTargets
{
    public const int ActivePartySlotCount = 3;

    public static List<Combatant> Resolve(
        Combatant caster, IReadOnlyList<PartyBuffSlot?> slots, int rangeUnits)
    {
        var result = new List<Combatant>();
        foreach (var slot in slots.Take(ActivePartySlotCount))
        {
            if (slot is null || !slot.Active || !slot.HasDisplay) continue;
            if (slot.Actor.Team != caster.Team) continue;
            if (rangeUnits >= 1 &&
                (int)Math.Floor(BasicAttackRules.TargetDistance(caster.Position, slot.Actor.Position) / 8.0) > rangeUnits)
                continue;
            result.Add(slot.Actor);
        }

        return result;
    }
}
