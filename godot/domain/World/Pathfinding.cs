namespace Inotia4.Domain.World;

public static class PathGrid
{
    public const int Size = 8;
}

/// <summary>世界能回答的两个问题：可站范围多大、某个点能不能站。</summary>
public interface IStandability
{
    Rect Bounds { get; }

    bool CanStand(Vec2 point);
}

public interface IPathQuery
{
    Rect Bounds { get; }

    bool CanStand(Vec2 point);

    bool IsGoal(Vec2 point);

    float Heuristic(Vec2 point);
}

public enum PathStatus
{
    Pending,
    Unreachable,
    Found,
}

public sealed class PathResult
{
    public PathStatus Status { get; init; }

    public IReadOnlyList<Vec2> Path { get; init; } = Array.Empty<Vec2>();

    public int Expanded { get; init; }
}

/// <summary>
/// 八邻接 A*，带节点预算：
/// 每步只展开有限个节点，预算用完返回 <see cref="PathStatus.Pending"/>，
/// 调用方下一帧接着推进——**不能把预算耗尽当成无路**。
/// </summary>
public sealed class PathSearch
{
    private static readonly (int Dx, int Dy)[] Directions =
    [
        (0, -1), (-1, 0), (1, 0), (0, 1),
        (-1, -1), (1, -1), (-1, 1), (1, 1),
    ];

    private sealed class Node
    {
        public required string Key;
        public required Vec2 Point;
        public required float Cost;
        public required float Score;
        public Node? Parent;
        public bool IsOrigin;
    }

    private readonly IPathQuery _query;
    private readonly Rect _bounds;
    private readonly Dictionary<string, bool> _standability = new();
    private readonly Dictionary<string, float> _costs = new();
    private readonly List<Node> _heap = [];
    private readonly Node _origin;
    private readonly float _column;
    private readonly float _row;
    private readonly bool _aligned;

    private PathStatus _completed = PathStatus.Pending;
    private IReadOnlyList<Vec2> _completedPath = Array.Empty<Vec2>();

    public PathSearch(Vec2 start, IPathQuery query)
    {
        _query = query;
        _bounds = query.Bounds;

        _column = (start.X - _bounds.X) / PathGrid.Size;
        _row = (start.Y - _bounds.Y) / PathGrid.Size;
        _aligned = IsWholeNumber(_column) && IsWholeNumber(_row);

        _origin = new Node
        {
            Key = _aligned ? KeyOf(start) : "start",
            Point = start,
            Cost = 0f,
            Score = query.Heuristic(start),
            IsOrigin = true,
        };

        _costs[_origin.Key] = 0f;
        Push(_origin);
    }

    public bool IsComplete => _completed != PathStatus.Pending;

    public PathResult Step(int maxExpanded)
    {
        if (maxExpanded < 0) throw new ArgumentOutOfRangeException(nameof(maxExpanded), "寻路节点预算必须是非负整数");
        if (IsComplete) return Result(0);

        var expanded = 0;

        while (_heap.Count > 0 && expanded < maxExpanded)
        {
            var current = Pop();
            expanded++;

            if (current.Cost != _costs[current.Key]) continue;

            if (_query.IsGoal(current.Point))
            {
                var path = new List<Vec2>();
                for (var node = current; node.Parent is not null; node = node.Parent) path.Add(node.Point);
                path.Reverse();

                _completed = PathStatus.Found;
                _completedPath = path;
                return Result(expanded);
            }

            foreach (var point in Neighbors(current))
            {
                var dx = point.X - current.Point.X;
                var dy = point.Y - current.Point.Y;

                // 斜行时两个相邻方向都要通，否则会从障碍角上穿过去。
                if (dx != 0f && dy != 0f &&
                    (!CanStand(new Vec2(point.X, current.Point.Y)) || !CanStand(new Vec2(current.Point.X, point.Y))))
                    continue;

                if (!InBounds(point) || !Pathfinding.CanTraverse(current.Point, point, CanStand)) continue;

                var cost = current.Cost + MathF.Sqrt(dx * dx + dy * dy);
                var key = KeyOf(point);
                if (cost >= GetCost(key)) continue;

                _costs[key] = cost;
                Push(new Node
                {
                    Key = key,
                    Point = point,
                    Cost = cost,
                    Score = cost + _query.Heuristic(point),
                    Parent = current,
                });
            }
        }

        if (_heap.Count == 0) _completed = PathStatus.Unreachable;
        return Result(expanded);
    }

    private IEnumerable<Vec2> Neighbors(Node current)
    {
        if (current.IsOrigin && !_aligned)
        {
            foreach (var column in Corners(_column))
            {
                foreach (var row in Corners(_row))
                    yield return new Vec2(_bounds.X + column * PathGrid.Size, _bounds.Y + row * PathGrid.Size);
            }

            yield break;
        }

        foreach (var (dx, dy) in Directions)
            yield return new Vec2(current.Point.X + dx * PathGrid.Size, current.Point.Y + dy * PathGrid.Size);
    }

    private static IEnumerable<int> Corners(float value)
    {
        var lower = (int)MathF.Floor(value);
        var upper = (int)MathF.Ceiling(value);
        yield return lower;
        if (upper != lower) yield return upper;
    }

    private bool CanStand(Vec2 point)
    {
        var key = $"{point.X},{point.Y}";
        if (_standability.TryGetValue(key, out var cached)) return cached;

        var result = _query.CanStand(point);
        _standability[key] = result;
        return result;
    }

    private bool InBounds(Vec2 point) =>
        point.X >= _bounds.X && point.Y >= _bounds.Y &&
        point.X <= _bounds.X + _bounds.Width && point.Y <= _bounds.Y + _bounds.Height;

    private string KeyOf(Vec2 point) =>
        $"{RoundHalfUp((point.X - _bounds.X) / PathGrid.Size)},{RoundHalfUp((point.Y - _bounds.Y) / PathGrid.Size)}";

    private float GetCost(string key) => _costs.TryGetValue(key, out var cost) ? cost : float.PositiveInfinity;

    private PathResult Result(int expanded) => new()
    {
        Status = _completed,
        Path = _completedPath,
        Expanded = expanded,
    };

    private void Push(Node node)
    {
        _heap.Add(node);
        var index = _heap.Count - 1;

        while (index > 0)
        {
            var parent = (index - 1) >> 1;
            if (_heap[parent].Score <= node.Score) break;
            _heap[index] = _heap[parent];
            index = parent;
        }

        _heap[index] = node;
    }

    private Node Pop()
    {
        var first = _heap[0];
        var last = _heap[^1];
        _heap.RemoveAt(_heap.Count - 1);

        if (_heap.Count > 0)
        {
            var index = 0;
            while (index * 2 + 1 < _heap.Count)
            {
                var child = index * 2 + 1;
                if (child + 1 < _heap.Count && _heap[child + 1].Score < _heap[child].Score) child++;
                if (last.Score <= _heap[child].Score) break;
                _heap[index] = _heap[child];
                index = child;
            }

            _heap[index] = last;
        }

        return first;
    }

    private static bool IsWholeNumber(float value) => MathF.Abs(value - MathF.Round(value)) < 1e-6f;

    private static int RoundHalfUp(float value) => (int)MathF.Floor(value + 0.5f);
}

public static class Pathfinding
{
    /// <summary>
    /// 路段沿途检查可站性；斜行还检查两个相邻方向，禁止从障碍角穿过。
    /// 每 4 像素取一个采样点，长距离也不会漏掉中间的墙。
    /// </summary>
    public static bool CanTraverse(Vec2 from, Vec2 to, Func<Vec2, bool> canStand)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var steps = Math.Max(1, (int)MathF.Ceiling(MathF.Max(MathF.Abs(dx), MathF.Abs(dy)) / 4f));

        for (var i = 1; i <= steps; i++)
        {
            var point = new Vec2(from.X + dx * i / steps, from.Y + dy * i / steps);
            var previous = new Vec2(from.X + dx * (i - 1) / steps, from.Y + dy * (i - 1) / steps);

            if (!canStand(point)) return false;
            if (dx != 0f && dy != 0f &&
                (!canStand(new Vec2(point.X, previous.Y)) || !canStand(new Vec2(previous.X, point.Y))))
                return false;
        }

        return true;
    }

    public static IReadOnlyList<Vec2>? Find(Vec2 start, IPathQuery query, int maxExpanded = int.MaxValue)
    {
        var result = new PathSearch(start, query).Step(maxExpanded);
        return result.Status == PathStatus.Found ? result.Path : null;
    }
}

public sealed class GoalQuery : IPathQuery
{
    private readonly IStandability _world;
    private readonly Vec2 _destination;

    public GoalQuery(IStandability world, Vec2 destination)
    {
        _world = world;
        _destination = destination;
    }

    public Rect Bounds => _world.Bounds;

    public bool CanStand(Vec2 point) => _world.CanStand(point);

    public bool IsGoal(Vec2 point) =>
        Distance(point, _destination) <= PathGrid.Size && Pathfinding.CanTraverse(point, _destination, CanStand);

    public float Heuristic(Vec2 point) => MathF.Max(0f, Distance(point, _destination) - PathGrid.Size);

    public static float Distance(Vec2 a, Vec2 b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
