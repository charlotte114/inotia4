using System;
using System.Collections.Generic;
using System.Linq;
using Inotia4.Domain.Content;

namespace Inotia4.Domain.World;

public readonly record struct NavigationShape(CollisionBox? Collision)
{
    public static NavigationShape Of(CollisionBox? box) => new(box);
}

public interface INavigationActor
{
    /// <summary>实体身份。场景每次查询都重新包一层，所以身份不能靠包装对象的引用。</summary>
    object Key { get; }

    NavigationShape Shape { get; }

    Vec2 Position { get; }

    bool Enabled { get; }

    bool Party { get; }

    bool Ghost { get; }

    bool StrictTerrain { get; }
}

public sealed record NavigationIgnore(object Key, bool Party = true, bool Ghost = false) : INavigationActor
{
    public NavigationShape Shape => default;

    public Vec2 Position => default;

    public bool Enabled => false;

    public bool StrictTerrain => false;
}

public readonly record struct NavigationPlacement(INavigationActor Actor, Vec2 Position, bool? Enabled = null)
{
    public bool EffectiveEnabled => Enabled ?? Actor.Enabled;
}

public readonly record struct NavigationEviction(INavigationActor Actor, Vec2 Position, bool Enabled);

public sealed class NavigationWorld
{
    private const float TerrainRadius = 4f;

    private const float CollisionEpsilon = 1e-4f;

    private const int PlacementSearchBudget = 512;

    private static readonly (int Dx, int Dy)[] Directions =
    [
        (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1),
    ];

    private readonly CollisionGrid _grid;

    private readonly Func<IEnumerable<INavigationActor>> _actors;

    /// <summary>地形连通块编号：每个 8 像素格点一个标签，-1 表示还没算，0 表示不可站。</summary>
    private int[]? _labels;

    private int _labelColumns;

    private int _labelRows;

    public NavigationWorld(string name, CollisionGrid grid, Func<IEnumerable<INavigationActor>> actors)
    {
        Name = name;
        _grid = grid;
        _actors = actors;
    }

    public string Name { get; }

    public Rect Bounds => _grid.Bounds;

    /// <summary>脚点能不能站（地形与地图边界）。</summary>
    public bool TerrainFree(Vec2 point)
    {
        var r = TerrainRadius;
        var bounds = Bounds;
        if (point.X - r < bounds.X - CollisionEpsilon || point.Y - r < bounds.Y - CollisionEpsilon ||
            point.X + r > bounds.X + bounds.Width + CollisionEpsilon ||
            point.Y + r > bounds.Y + bounds.Height + CollisionEpsilon)
            return false;

        return _grid.CanStandOnTerrain(point.X, point.Y, r);
    }

    /// <summary>
    /// 整个身体能不能放在这里：地形上整盒不压阻挡格，
    /// 且不与别人的碰撞盒重叠。同阵营（队伍）彼此不占位。
    /// </summary>
    public bool CanPlaceBody(NavigationShape shape, Vec2 point, INavigationActor? ignore = null,
        IReadOnlyList<NavigationPlacement>? placements = null)
    {
        if (shape.Collision is not { } box) return true;

        var rect = BodyAt(box, point);
        return _grid.CanStandInRect(rect) && !BlockedByActors(rect, ignore, placements);
    }

    public bool BodyTerrainFree(NavigationShape shape, Vec2 point) =>
        shape.Collision is not { } box || _grid.CanStandInRect(BodyAt(box, point));

    public bool CanStandAt(NavigationShape shape, Vec2 point, INavigationActor? ignore = null,
        IReadOnlyList<NavigationPlacement>? placements = null) =>
        TerrainFree(point) &&
        (shape.Collision is not { } box || !BlockedByActors(BodyAt(box, point), ignore, placements));

    public IReadOnlyList<Vec2>? Path(NavigationShape shape, Vec2 from, Vec2 to,
        INavigationActor? ignore = null, int maxExpanded = int.MaxValue)
    {
        bool Free(Vec2 point) => CanStandAt(shape, point, ignore);

        if (!Free(to)) return null;

        var path = Pathfinding.Find(from, new ActorPathQuery(Bounds, Free, to), maxExpanded);
        if (path is null) return null;
        if (path.Count == 0 || (path[^1] - to).Length > 0.01f)
            return [.. path, to];
        return path;
    }

    private bool BlockedByActors(Rect rect, INavigationActor? ignore,
        IReadOnlyList<NavigationPlacement>? placements = null)
    {
        var ignoreGhost = ignore?.Ghost == true;
        foreach (var actor in _actors())
        {
            if (ignore is not null && SameActor(actor.Key, ignore.Key)) continue;
            var placed = Replacement(actor, placements);
            if (placed is { } entry)
            {
                if (!entry.EffectiveEnabled) continue;
            }
            else if (!actor.Enabled) continue;

            if (ignoreGhost || actor.Ghost) continue;
            if (ignore is not null && ignore.Party && actor.Party) continue;

            if (actor.Shape.Collision is not { } other) continue;
            var point = placed is { } spot ? spot.Position : actor.Position;
            if (Overlaps(BodyAt(other, point), rect)) return true;
        }

        return false;
    }

    private static NavigationPlacement? Replacement(INavigationActor actor,
        IReadOnlyList<NavigationPlacement>? placements)
    {
        if (placements is null) return null;
        foreach (var placement in placements)
            if (SameActor(placement.Actor.Key, actor.Key)) return placement;
        return null;
    }

    /// <summary>
    /// 两处包装是不是同一个实体。场景每次查询都重新包一层，所以身份只能比
    /// <see cref="INavigationActor.Key"/>：节点按引用，字符串（主角的战斗 id）按值。
    /// </summary>
    private static bool SameActor(object left, object right) =>
        ReferenceEquals(left, right) ||
        (left is string a && right is string b && string.Equals(a, b, StringComparison.Ordinal));

    private sealed class ActorPathQuery(Rect bounds, Func<Vec2, bool> canStand, Vec2 goal) : IPathQuery
    {
        public Rect Bounds => bounds;

        public bool CanStand(Vec2 point) => canStand(point);

        public bool IsGoal(Vec2 point) =>
            (point - goal).Length <= PathGrid.Size && Pathfinding.CanTraverse(point, goal, canStand);

        public float Heuristic(Vec2 point) => MathF.Max(0f, (point - goal).Length - PathGrid.Size);
    }

    public Vec2? NearestBody(NavigationShape shape, Vec2 origin, INavigationActor? ignore = null,
        IReadOnlyList<NavigationPlacement>? placements = null) =>
        shape.Collision is null ? null : Scan(origin, point => CanPlaceBody(shape, point, ignore, placements));

    public Vec2? NearestSettledBody(NavigationShape shape, Vec2 origin, INavigationActor? ignore = null,
        IReadOnlyList<NavigationPlacement>? placements = null) =>
        shape.Collision is null
            ? null
            : Scan(origin, point => TerrainFree(point) && CanPlaceBody(shape, point, ignore, placements));

    /// <summary>
    /// 就近脚点落点：地形可站且不与实体重叠。找不到就抛错——
    /// 调用方要么给这张图安排一条路，要么本来就不该在这里放人。
    /// </summary>
    public Vec2 Nearest(NavigationShape shape, Vec2 origin, INavigationActor? ignore = null,
        IReadOnlyList<NavigationPlacement>? placements = null) =>
        Scan(origin, point => TerrainFree(point) && CanPlace(shape, point, ignore, placements))
        ?? throw new InvalidOperationException($"地图没有可通行区域：{Name}");

    /// <summary>
    /// 就近可走到、且整盒站得住的落点：
    /// <paramref name="preferred"/> 本身合法、又能沿地形走到时原样返回；
    /// 否则在可达范围里按路径长度找最近的整盒点。
    /// </summary>
    public Vec2? NearestReachableBody(NavigationShape shape, Vec2 origin, Vec2? preferred = null,
        INavigationActor? ignore = null)
    {
        if (shape.Collision is null) return null;

        if (preferred is { } want && CanPlaceBody(shape, want, ignore) && TerrainRoute(origin, want))
            return want;

        var landing = (Vec2?)null;
        var query = new PlacementQuery(Bounds, TerrainFree, point =>
        {
            if (!CanPlaceBody(shape, point, ignore)) return false;
            landing = point;
            return true;
        });

        var result = new PathSearch(origin, query).Step(PlacementSearchBudget);
        // 预算耗尽与真的不可达要分开：前者说明落点可能只是很远，交给调用方回退。
        return result.Status == PathStatus.Found ? landing : null;
    }

    /// <summary>
    /// 地形连通性：两点能不能只靠地形走过去。
    /// 一张图里地形不变，所以按 8 像素格点预计算连通块编号，查询只比编号。
    /// </summary>
    public bool TerrainRoute(Vec2 from, Vec2 to)
    {
        if (!TerrainFree(from) || !TerrainFree(to)) return false;

        var fromLabels = LabelsAt(from);
        var toLabels = LabelsAt(to);
        if (fromLabels.Count > 0 && toLabels.Count > 0)
        {
            foreach (var label in fromLabels)
                if (toLabels.Contains(label)) return true;

            return false;
        }

        return Pathfinding.Find(from, new ActorPathQuery(Bounds, TerrainFree, to), PlacementSearchBudget) is not null;
    }

    public bool CanPlace(NavigationShape shape, Vec2 point, INavigationActor? ignore = null,
        IReadOnlyList<NavigationPlacement>? placements = null)
    {
        if (shape.Collision is not { } box) return true;
        if (!TerrainFree(point)) return false;

        return !BlockedByActors(BodyAt(box, point), ignore, placements);
    }

    public bool CanPlaceAll(IReadOnlyList<NavigationPlacement> placements)
    {
        foreach (var placement in placements)
        {
            if (!placement.EffectiveEnabled) continue;
            if (!CanPlace(placement.Actor.Shape, placement.Position, placement.Actor, placements)) return false;
        }

        return true;
    }

    /// <summary>
    /// 只校验位置真正变了的落位。地图自带实体（火把、泉水、
    /// 宝箱、按阶段显隐的人物）保持原位时，位置来自地图数据本身，本来就允许贴在墙上、
    /// 摆在阻挡格或与相邻实体重叠；重放它们不该失败。队伍单位（<see cref="INavigationActor.StrictTerrain"/>）
    /// 例外：位置没变或当前隐藏也要求整盒落点成立，堵住「隐藏时写入非法坐标，再显示出来就永久卡墙」。
    /// </summary>
    public bool CanSettleAll(IReadOnlyList<NavigationPlacement> placements)
    {
        foreach (var placement in placements)
        {
            var strict = placement.Actor.StrictTerrain;
            if (!placement.EffectiveEnabled && !strict) continue;
            if (!strict && At(placement.Actor.Position, placement.Position)) continue;

            if (strict)
            {
                if (!TerrainFree(placement.Position)) return false;
                if (!CanPlaceBody(placement.Actor.Shape, placement.Position, placement.Actor, placements)) return false;
                continue;
            }

            if (!CanPlace(placement.Actor.Shape, placement.Position, placement.Actor, placements)) return false;
        }

        return true;
    }

    /// <summary>
    /// 权威落位：<paramref name="placements"/> 的位置必须成立，
    /// 被它们压住的现有角色就近让开。
    ///
    /// 不在 <paramref name="placements"/> 里的现有角色一律让位；目标角色本身只有在
    /// <paramref name="displaceable"/>（主角这类权威性低于剧情角色的）里时才让位。
    /// 剧情把人物登记到正式地图时用它——位置来自内容，玩家的走动或转场落点不该把它顶掉，
    /// 也不该让整组落位失败。
    /// </summary>
    public IReadOnlyList<NavigationEviction> EvictionsFor(IReadOnlyList<NavigationPlacement> placements,
        IReadOnlySet<object>? displaceable = null)
    {
        // 现读一份实体表：挪开一个之后，后面的判定要按它的新位置算，所以用本地副本推进。
        var settled = new List<NavigationPlacement>(placements);
        var evicted = new List<NavigationEviction>();

        foreach (var actor in _actors().ToList())
        {
            if (!actor.Enabled) continue;

            var index = IndexOf(settled, actor.Key);
            if (index >= 0 && displaceable?.Contains(actor.Key) != true) continue;

            var point = index >= 0 ? settled[index].Position : actor.Position;
            var strict = actor.StrictTerrain;

            if (strict
                    ? TerrainFree(point) && CanPlaceBody(actor.Shape, point, actor, settled)
                    : CanPlace(actor.Shape, point, actor, settled)) continue;

            // 队伍单位要让位的点也必须过整盒 + 脚点两关，否则收尾校验仍会判它非法。
            var landing = strict ? NearestSettledBody(actor.Shape, point, actor, settled) : SafeNearest(actor.Shape, point, actor, settled);
            if (landing is not { } spot) spot = point;

            if (index >= 0) settled[index] = new NavigationPlacement(actor, spot, true);
            else settled.Add(new NavigationPlacement(actor, spot, true));
            evicted.Add(new NavigationEviction(actor, spot, true));
        }

        return evicted;
    }

    /// <summary>就近落点；地图上完全没有可站区域时返回 <c>null</c>（挤开不该因此中断）。</summary>
    private Vec2? SafeNearest(NavigationShape shape, Vec2 origin, INavigationActor? ignore,
        IReadOnlyList<NavigationPlacement>? placements)
    {
        try { return Nearest(shape, origin, ignore, placements); }
        catch (InvalidOperationException) { return null; }
    }

    private static bool At(Vec2 a, Vec2 b) => MathF.Abs(a.X - b.X) < 1e-4f && MathF.Abs(a.Y - b.Y) < 1e-4f;

    private static int IndexOf(List<NavigationPlacement> placements, object key)
    {
        for (var index = 0; index < placements.Count; index++)
            if (SameActor(placements[index].Actor.Key, key)) return index;
        return -1;
    }

    /// <summary>
    /// 就近扫描：起点可站就原样返回；否则按半格步长遍历全图栅格，
    /// 取离起点欧氏距离最近的合法点，同距时先扫到的那格赢（y 在外、x 在内的行优先）。
    ///
    /// 之前这里按 8 像素环形扩散取环内第一个命中点：同一环里斜角排在正交之前、左上排在右下
    /// 之前，落点会系统性偏向主角左上方——空地上主角正上方的合法点会输给更远的左上斜角，
    /// 与既定选点口径不一致（规格票 03 的落点对拍抓出来的：要按全图距离取最近）。
    /// </summary>
    private Vec2? Scan(Vec2 origin, Func<Vec2, bool> accept)
    {
        if (accept(origin)) return origin;

        var bounds = Bounds;
        var step = Math.Max(4, _grid.TileSize / 2);
        Vec2? best = null;
        var bestDistance = float.MaxValue;

        for (var y = bounds.Y + step / 2f; y < bounds.Y + bounds.Height; y += step)
        for (var x = bounds.X + step / 2f; x < bounds.X + bounds.Width; x += step)
        {
            var dx = x - origin.X;
            var dy = y - origin.Y;
            var distance = dx * dx + dy * dy;
            if (distance >= bestDistance || !accept(new Vec2(x, y))) continue;
            best = new Vec2(x, y);
            bestDistance = distance;
        }

        return best;
    }

    private static Rect BodyAt(CollisionBox box, Vec2 point) =>
        new(point.X + box.X, point.Y + box.Y, box.Width, box.Height);

    private static bool Overlaps(Rect a, Rect b) =>
        a.X < b.X + b.Width - CollisionEpsilon && a.X + a.Width > b.X + CollisionEpsilon &&
        a.Y < b.Y + b.Height - CollisionEpsilon && a.Y + a.Height > b.Y + CollisionEpsilon;

    private List<int> LabelsAt(Vec2 point)
    {
        var result = new List<int>(4);
        var size = PathGrid.Size;
        var col = (int)MathF.Floor(point.X / size);
        var row = (int)MathF.Floor(point.Y / size);

        EnsureLabels();
        foreach (var (dc, dr) in new[] { (0, 0), (1, 0), (0, 1), (1, 1) })
        {
            var c = col + dc;
            var r = row + dr;
            if (c < 0 || r < 0 || c >= _labelColumns || r >= _labelRows) continue;

            var label = _labels![r * _labelColumns + c];
            if (label <= 0) continue;

            var corner = new Vec2(c * size, r * size);
            if (!Pathfinding.CanTraverse(point, corner, TerrainFree)) continue;
            if (!result.Contains(label)) result.Add(label);
        }

        return result;
    }

    private void EnsureLabels()
    {
        if (_labels is not null) return;

        var size = PathGrid.Size;
        var bounds = Bounds;
        _labelColumns = Math.Max(1, (int)MathF.Ceiling(bounds.Width / size));
        _labelRows = Math.Max(1, (int)MathF.Ceiling(bounds.Height / size));

        var labels = new int[_labelColumns * _labelRows];
        for (var index = 0; index < labels.Length; index += 1)
        {
            var c = index % _labelColumns;
            var r = index / _labelColumns;
            labels[index] = TerrainFree(new Vec2(c * size, r * size)) ? -1 : 0;
        }

        var next = 1;
        var queue = new Queue<(int C, int R)>();
        for (var start = 0; start < labels.Length; start += 1)
        {
            if (labels[start] != -1) continue;

            var label = next++;
            labels[start] = label;
            queue.Clear();
            queue.Enqueue((start % _labelColumns, start / _labelColumns));

            while (queue.Count > 0)
            {
                var (c, r) = queue.Dequeue();
                var from = new Vec2(c * size, r * size);
                foreach (var (dx, dy) in Directions)
                {
                    var nc = c + dx;
                    var nr = r + dy;
                    if (nc < 0 || nr < 0 || nc >= _labelColumns || nr >= _labelRows) continue;

                    var target = nr * _labelColumns + nc;
                    if (labels[target] != -1) continue;

                    var to = new Vec2(nc * size, nr * size);
                    if (!Pathfinding.CanTraverse(from, to, TerrainFree)) continue;

                    labels[target] = label;
                    queue.Enqueue((nc, nr));
                }
            }
        }

        _labels = labels;
    }

    private sealed class PlacementQuery(Rect bounds, Func<Vec2, bool> canStand, Func<Vec2, bool> isGoal) : IPathQuery
    {
        public Rect Bounds => bounds;

        public bool CanStand(Vec2 point) => canStand(point);

        public bool IsGoal(Vec2 point) => isGoal(point);

        /// <summary>按路径长度找最近的点，所以不做任何方向上的估计。</summary>
        public float Heuristic(Vec2 point) => 0f;
    }
}
