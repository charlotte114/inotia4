using System;
using System.Collections.Generic;

namespace Inotia4.Domain.Combat;

public enum SkillUnitOutcome
{
    Success,
    Failure,

    Skipped,
}

public sealed record ResolvedSkillUnit(int CueIndex, string TargetId, SkillUnitOutcome Outcome);

public interface ISkillUnitSequencePort
{
    IReadOnlyList<string> Targets(int cueIndex);

    void Prepare(int cueIndex)
    {
    }

    SkillUnitOutcome Unit(int cueIndex, string targetId);

    void Resolved(ResolvedSkillUnit result)
    {
    }
}

public static class SkillUnitSequence
{
    public const int MaxSkillTargets = 16;

    public static List<ResolvedSkillUnit> Resolve(IReadOnlyList<ICombatUnit> units, ISkillUnitSequencePort port)
    {
        var failedTargets = new List<string>();
        var results = new List<ResolvedSkillUnit>();
        for (var cueIndex = 0; cueIndex < units.Count; cueIndex += 1)
        {
            var unit = units[cueIndex];
            var bypassFailure = unit is SetAttributeUnit or ClearAttributeUnit;
            var targets = port.Targets(cueIndex);
            var limit = Math.Min(targets.Count, MaxSkillTargets);
            port.Prepare(cueIndex);
            for (var index = 0; index < limit; index += 1)
            {
                var targetId = targets[index];
                var outcome = !bypassFailure && failedTargets.Contains(targetId)
                    ? SkillUnitOutcome.Skipped
                    : port.Unit(cueIndex, targetId);

                if (outcome == SkillUnitOutcome.Failure && failedTargets.Count < MaxSkillTargets)
                    failedTargets.Add(targetId);

                var result = new ResolvedSkillUnit(cueIndex, targetId, outcome);
                results.Add(result);
                port.Resolved(result);
            }
        }

        return results;
    }
}
