using Inotia4.Domain.World;

namespace Inotia4.Domain.Combat;

public class SkillUnitStart
{
    public required string CasterId { get; init; }

    public required SkillPlayback Skill { get; init; }

    public required int CueIndex { get; init; }
}

public sealed class SkillUnitHit : SkillUnitStart
{
    public required string TargetId { get; init; }

    public PreparedDamage? PreparedDamage { get; set; }
}

public sealed class SkillExecution
{
    public required string CasterId { get; init; }

    public required SkillPlayback Skill { get; init; }

    public required IReadOnlyList<SkillEffectCue> Cues { get; init; }

    public Vec2? Origin { get; init; }

    public string? SelectedTargetId { get; init; }
}

public sealed class SkillExecutionPort
{
    public required ICombatPort Port { get; init; }

    public Func<string?>? CurrentTargetId { get; init; }

    public Action<string>? SelectTarget { get; init; }

    public Action<SkillUnitHit>? Resolved { get; init; }
}

public static class CombatUnitExecution
{
    public static List<ResolvedSkillUnit> ExecuteSkillUnits(SkillExecution request, SkillExecutionPort port)
    {
        var casterId = request.CasterId;
        var cues = request.Cues;
        var skill = request.Skill.Level is null ? request.Skill with { Level = 1 } : request.Skill;
        var world = port.Port;
        var position = request.Origin ?? world.Actor(casterId)?.Position;
        var origin = position;
        var selectedTargetId = request.SelectedTargetId;

        var targets = new List<string>();
        PreparedDamage? preparedDamage = null;
        var results = new List<ResolvedSkillUnit>();

        void Work()
        {
            var units = new List<ICombatUnit>(cues.Count);
            for (var index = 0; index < cues.Count; index += 1)
                units.Add(skill.Action.CombatUnits is { } catalog && index < catalog.Count
                    ? catalog[index]
                    : new SimpleCombatUnit { Effect = "visual" });

            results = SkillUnitSequence.Resolve(units, new SequencePort
            {
                Targets = cueIndex => SelectTargets(cueIndex),
                Prepare = cueIndex => preparedDamage = world.PrepareUnit(new SkillUnitStart
                {
                    CasterId = casterId, Skill = skill, CueIndex = cueIndex,
                }),
                Unit = (cueIndex, targetId) => world.Actor(targetId) is null
                    ? SkillUnitOutcome.Skipped
                    : world.Unit(new SkillUnitHit
                    {
                        CasterId = casterId,
                        Skill = skill,
                        CueIndex = cueIndex,
                        TargetId = targetId,
                        PreparedDamage = preparedDamage,
                    }),
                Resolved = result =>
                {
                    var hit = new SkillUnitHit
                    {
                        CasterId = casterId,
                        Skill = skill,
                        CueIndex = result.CueIndex,
                        TargetId = result.TargetId,
                        PreparedDamage = preparedDamage,
                    };
                    world.UnitFeedback(hit, result.Outcome, cues[result.CueIndex]);
                    if (result.Outcome != SkillUnitOutcome.Skipped) port.Resolved?.Invoke(hit);
                },
            });
        }

        world.RunUnitSequence(Work);
        return results;

        IReadOnlyList<string> SelectTargets(int cueIndex)
        {
            var cue = cues[cueIndex];
            if (cue.TargetSelection == "reuse-previous") return targets;

            var caster = world.Actor(casterId);
            if (caster is null || origin is null)
            {
                targets = [];
                return targets;
            }

            var source = caster with { Position = origin.Value };
            if (cue.Target == "target-area")
            {
                var id = port.CurrentTargetId?.Invoke() ?? selectedTargetId;
                var current = id is null ? null : world.Actor(id);
                var candidates = world.Candidates();
                var pool = current is not null ? new List<Combatant> { current } : candidates;
                var range = (int)MathF.Floor(cue.TargetRangePx / 8);
                var selection = AttackSplash.SelectCenter(caster, pool, range, current?.Id);
                if (selection.TargetId is not null) port.SelectTarget?.Invoke(selection.TargetId);
                var center = selection.TargetId is not null ? selection.Center : origin.Value;
                targets = AttackSplash.Targets(caster, center, candidates, range).Select(actor => actor.Id).ToList();
                return targets;
            }

            var selected = selectedTargetId is null ? null : world.Actor(selectedTargetId);
            var slots = cue.Target is "party" or "party-ally" ? world.PartySlots() : null;
            var resolved = cue.Target == "none"
                ? [caster.Id]
                : CueTargets.Resolve(cue, source, selected, world.Candidates(), slots,
                        world.RangeBonusUnits(casterId), world.HealScore, world.HealCandidates())
                    .Take(SkillUnitSequence.MaxSkillTargets)
                    .Select(actor => actor.Id)
                    .ToList();
            targets = resolved;
            return targets;
        }
    }

    /// <summary>把闭包收成一个具名实现：这里要求显式接口，不能直接传一组 lambda。</summary>
    private sealed class SequencePort : ISkillUnitSequencePort
    {
        public required Func<int, IReadOnlyList<string>> Targets { get; init; }

        public required Action<int> Prepare { get; init; }

        public required Func<int, string, SkillUnitOutcome> Unit { get; init; }

        public required Action<ResolvedSkillUnit> Resolved { get; init; }

        IReadOnlyList<string> ISkillUnitSequencePort.Targets(int cueIndex) => Targets(cueIndex);

        void ISkillUnitSequencePort.Prepare(int cueIndex) => Prepare(cueIndex);

        SkillUnitOutcome ISkillUnitSequencePort.Unit(int cueIndex, string targetId) => Unit(cueIndex, targetId);

        void ISkillUnitSequencePort.Resolved(ResolvedSkillUnit result) => Resolved(result);
    }
}
