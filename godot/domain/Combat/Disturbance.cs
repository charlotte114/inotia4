using System.Collections.Generic;
using Inotia4.Domain.World;

namespace Inotia4.Domain.Combat;

public static class Disturbance
{
    public interface IActor : IPlacedRect
    {
        string Id { get; }

        bool SameCamp { get; }

        bool OwnSummon { get; }

        int EffectiveDefense { get; }
    }

    /// <summary>
    /// 扰乱候选：遍历活动同阵营对象（排除自己），先过矩形格距这一关（格距 +2 ≤ 64），
    /// 遇到自己拥有的召唤物立刻选它；否则保留有效防御**严格高于**自己且最高者，相等保留先遇到的。
    /// </summary>
    public static string? Candidate(IActor self, IReadOnlyList<IActor> actors, int rangeUnits = 64)
    {
        IActor? best = null;
        foreach (var actor in actors)
        {
            if (actor.Id == self.Id || !actor.SameCamp) continue;
            if (RectDistance.Between(self, actor) + 2 > rangeUnits) continue;
            if (actor.OwnSummon) return actor.Id;
            if (actor.EffectiveDefense <= self.EffectiveDefense) continue;
            if (best is null || actor.EffectiveDefense > best.EffectiveDefense) best = actor;
        }

        return best?.Id;
    }
}
