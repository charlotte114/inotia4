using System;
using System.Collections.Generic;
using Inotia4.Domain.Content;
using Inotia4.Domain.World;

namespace Inotia4.Domain.Combat;

public static class SkillGeometry
{
    public static (Vec2 Position, bool Arrived) PositionTransitionStep(Vec2 start, Vec2 target, int stepPx,
        int completedSteps, int switchFrame) =>
        completedSteps >= switchFrame
            ? (target, true)
            : BasicAttackRules.ProjectileStep(start, target, stepPx);

    /// <summary>
    /// 召唤落点按八像素网格逐个试，挑第一个能站人的位置。
    /// 找不到就返回 <c>null</c>：落点必须由调用方另找一个合法位置，不再拿主人坐标冒充落点。
    /// </summary>
    public static Vec2? PlanSummonSpot(Vec2 position, CollisionBox box, string facing, SummonPlacement placement,
        Func<Vec2, bool> canStand)
    {
        var directions = new[] { "south", "west", "north", "east" };
        var start = Array.IndexOf(directions, facing);
        if (start < 0) start = 0;

        var gap = placement.SearchGapPx;
        var extent = placement.SearchExtentPx;

        foreach (var turn in new[] { 0, 1, 3, 2 })
        {
            var direction = (start + turn) % 4;
            for (var forward = 0f; forward <= extent; forward += 8f)
            {
                for (var side = -extent; side <= extent; side += 8f)
                {
                    var point = direction switch
                    {
                        0 => new Vec2(position.X + side, position.Y + box.Y + box.Height + gap + forward),
                        1 => new Vec2(position.X + box.X - gap - forward, position.Y + side),
                        2 => new Vec2(position.X + side, position.Y + box.Y - gap - forward),
                        _ => new Vec2(position.X + box.X + box.Width + gap + forward, position.Y + side),
                    };
                    if (canStand(point)) return point;
                }
            }
        }

        return null;
    }
}
