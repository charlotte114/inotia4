using System;
using Inotia4.Domain.Content;
using Inotia4.Domain.World;

namespace Inotia4.Domain.Combat;

public static class EnemyEngagement
{
    public static int AwarenessRangeUnits(EnemyBehavior behavior, Rect? bounds, int sightBonusUnits = 0)
    {
        var ordinary = Math.Max(0, behavior.AwarenessRangeUnits) + sightBonusUnits;
        if (behavior.Engagement != "map-wide") return ordinary;

        if (bounds is null) throw new InvalidOperationException("全图参战的敌人缺少地图边界");

        var area = (int)Math.Ceiling((bounds.Value.Width + bounds.Value.Height) / 8.0) + 2;
        return Math.Max(ordinary, area);
    }
}
