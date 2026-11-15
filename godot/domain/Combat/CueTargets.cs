using Inotia4.Domain.World;

namespace Inotia4.Domain.Combat;

/// <summary>
/// 技能触发项（cue）的选人：按触发项写的目标类型，从场上角色里挑出实际受影响的那些人。
/// 这一层只读施法者、当前目标与候选表，不碰控制器状态，所以单独成文件。
/// </summary>
public static class CueTargets
{
    public readonly record struct AreaSelection(List<Combatant> Targets, string? SelectedTargetId);

    public static AreaSelection ResolveTargetArea(
        Combatant caster,
        Combatant? current,
        IReadOnlyList<Combatant> candidates,
        int rangeUnits,
        Vec2? fallbackCenter = null)
    {
        var pool = current is not null ? new List<Combatant> { current } : candidates.ToList();
        var selection = AttackSplash.SelectCenter(caster, pool, rangeUnits, current?.Id);
        var center = selection.TargetId is not null ? selection.Center : fallbackCenter ?? caster.Position;
        return new AreaSelection(AttackSplash.Targets(caster, center, candidates, rangeUnits), selection.TargetId);
    }

    public static List<Combatant> Resolve(
        SkillEffectCue cue,
        Combatant caster,
        Combatant? selected,
        IReadOnlyList<Combatant> candidates,
        IReadOnlyList<PartyBuffSlot?>? partySlots = null,
        int rangeBonusUnits = 0,
        Func<string, int?>? healScore = null,
        IReadOnlyList<HealSlot>? healCandidates = null) =>
        Resolve(cue.Target, cue.TargetRangePx, cue.TargetRangeMetric, caster, selected, candidates,
            partySlots, rangeBonusUnits, healScore, healCandidates);

    public static List<Combatant> Resolve(
        string target,
        float targetRangePx,
        string? targetRangeMetric,
        Combatant caster,
        Combatant? selected,
        IReadOnlyList<Combatant> candidates,
        IReadOnlyList<PartyBuffSlot?>? partySlots = null,
        int rangeBonusUnits = 0,
        Func<string, int?>? healScore = null,
        IReadOnlyList<HealSlot>? healCandidates = null)
    {
        if (rangeBonusUnits > 0) targetRangePx += rangeBonusUnits * 8;

        var slots = partySlots ?? [];

        if (target == "self") return [caster];
        if (target == "none") return [];
        if (target == "party") return PartyBuffTargets.Resolve(caster, slots, (int)MathF.Floor(targetRangePx / 8));

        if (target == "party-ally")
            return PartyBuffTargets.Resolve(caster, slots, (int)MathF.Floor(targetRangePx / 8))
                .Where(actor => actor.Id != caster.Id).ToList();

        if (target == "ally-heal")
        {
            var pool = healCandidates ?? BuildHealSlots(slots, healScore);
            return [HealTarget.Select(caster, pool, (int)MathF.Floor(targetRangePx / 8))];
        }

        if (target == "target-area")
            return ResolveTargetArea(caster, selected, candidates, (int)MathF.Floor(targetRangePx / 8)).Targets;

        if (selected is not null && !CombatController.CanTargetCombatant(caster, selected)) selected = null;

        var center = caster.Position;
        var rangeUnits = (int)MathF.Floor(targetRangePx / 8);
        bool WithinRange(Combatant actor) => targetRangeMetric == "octagonal-grid"
            ? BasicAttackRules.TargetDistance(center, actor.Position) / 8 <= rangeUnits
            : MathF.Sqrt(MathF.Pow(center.X - actor.Position.X, 2f) + MathF.Pow(center.Y - actor.Position.Y, 2f)) <=
              targetRangePx;

        if (target is "current" or "enemy") return selected is not null ? [selected] : [];

        var allies = target == "ally";
        return candidates
            .Where(actor => (allies ? actor.Team == caster.Team : actor.Team != caster.Team) &&
                            CombatController.CanTargetCombatant(caster, actor) && WithinRange(actor))
            .ToList();
    }

    private static List<HealSlot> BuildHealSlots(IReadOnlyList<PartyBuffSlot?> slots, Func<string, int?>? healScore)
    {
        var pool = new List<HealSlot>();
        foreach (var slot in slots)
        {
            if (slot is null) continue;
            var score = healScore?.Invoke(slot.Actor.Id);
            if (score is not { } value) continue;
            pool.Add(new HealSlot(slot.Actor, slot.Active, slot.HasDisplay, value));
        }

        return pool;
    }
}
