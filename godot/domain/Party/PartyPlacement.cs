using Inotia4.Domain.World;

namespace Inotia4.Domain.Party;

/// <summary>
/// 给上阵队员在主角身边找一个能放下整个身体、且从主角身边走得到的落点。
/// 判定次序（照反汇编里那段二十来行的落位逻辑）：
///
/// <list type="number">
/// <item>先按整盒找最近的点：整盒判定不查脚点，而落位还要求脚点可站，
/// 所以候选点要用「地形可站」复核一遍。</item>
/// <item>有整盒候选时再查它与主角的地形连通性；
/// 被墙隔开时它会按可行走路径找最近的整盒点。</item>
/// <item>地图连整盒空间都没有时退回脚点口径；完全没有可行走区域时返回 <c>null</c>，
/// 由调用方决定报错还是保持原位。</item>
/// </list>
/// </summary>
public static class PartyPlacement
{
    public static Vec2? ChoosePartyLanding(NavigationWorld navigation, NavigationShape shape, Vec2 origin,
        INavigationActor? ignore = null)
    {
        bool Settle(Vec2 point) => navigation.TerrainFree(point) && navigation.CanPlaceBody(shape, point, ignore);

        var preferred = navigation.NearestBody(shape, origin, ignore);
        if (preferred is { } first)
        {
            var reachable = navigation.NearestReachableBody(shape, origin, first, ignore);
            if (reachable is { } spot && Settle(spot)) return spot;
            if (Settle(first)) return first;
        }

        try
        {
            return navigation.Nearest(shape, origin, ignore);
        }
        catch (System.InvalidOperationException)
        {
            return null;
        }
    }
}
