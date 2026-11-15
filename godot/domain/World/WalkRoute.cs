namespace Inotia4.Domain.World;

public enum WalkState
{
    Walking,
    Arrived,
    Failed,
}

/// <summary>
/// 走到指定脚点的路线。
///
/// 两个要点：
/// <list type="bullet">
/// <item>**寻路分帧**。一次完整搜索可能几千个节点，同步跑完会掉帧，
/// 所以每帧只展开固定数量，没搜完就返回「这一帧原地不动」。</item>
/// <item>**被挡就重算**。路上出现新的阻挡（别人走过来、剧情挪了东西）时，
/// 每 250 毫秒重算一次，而不是一路撞墙撞到超时。</item>
/// </list>
/// </summary>
public sealed class WalkRoute
{
    private const int SearchBudgetPerFrame = 128;

    private const int FirstSearchBudget = 256;

    private const float SearchDeadlineMs = 3000f;
    private const float RepathIntervalMs = 250f;
    private const float ArrivalEpsilon = 0.01f;

    private readonly IStandability _world;
    private readonly float _speed;

    private List<Vec2> _path = [];
    private PathSearch? _search;
    private float _elapsed;
    private float _searchStarted;
    private bool _searchIsRepath;
    private float _nextSearch;
    private float _lastProgress;
    private Vec2 _progressPosition;
    private float _timeout;

    public WalkRoute(IStandability world, Vec2 start, Vec2 destination, float speed)
    {
        _world = world;
        _speed = speed;
        Destination = destination;
        _progressPosition = start;
        BeginSearch(start, false);
    }

    public Vec2 Destination { get; }

    public WalkState State { get; private set; } = WalkState.Walking;

    public string? Error { get; private set; }

    public Vec2? Next(Vec2 current, float deltaMs)
    {
        if (State != WalkState.Walking) return null;

        _elapsed += deltaMs;

        if (GoalQuery.Distance(current, Destination) <= ArrivalEpsilon)
        {
            State = WalkState.Arrived;
            return null;
        }

        if (_search is not null)
        {
            if (_elapsed - _searchStarted > SearchDeadlineMs)
            {
                Fail("道路搜索超时");
                return null;
            }

            AdvanceSearch(current);
            if (State != WalkState.Walking) return null;
            if (_search is not null) return null;
        }

        if (_timeout > 0f && _elapsed > _timeout)
        {
            Fail("人物未能到达");
            return null;
        }

        while (_path.Count > 0 && GoalQuery.Distance(current, _path[0]) <= ArrivalEpsilon) _path.RemoveAt(0);

        if (GoalQuery.Distance(current, _progressPosition) >= 0.5f)
        {
            _lastProgress = _elapsed;
            _progressPosition = current;
        }

        var blocked = _path.Count == 0 || !Pathfinding.CanTraverse(current, _path[0], _world.CanStand);
        if (_elapsed >= _nextSearch && (blocked || _elapsed - _lastProgress >= RepathIntervalMs))
        {
            _nextSearch = _elapsed + RepathIntervalMs;
            _lastProgress = _elapsed;
            BeginSearch(current, true);
            return null;
        }

        return _path.Count > 0 && Pathfinding.CanTraverse(current, _path[0], _world.CanStand) ? _path[0] : null;
    }

    private void BeginSearch(Vec2 start, bool repath)
    {
        _searchIsRepath = repath;
        _searchStarted = _elapsed;
        _path.Clear();
        _search = new PathSearch(start, new GoalQuery(_world, Destination));
    }

    private void AdvanceSearch(Vec2 current)
    {
        if (_search is null) return;

        var result = _search.Step(_searchIsRepath ? SearchBudgetPerFrame : FirstSearchBudget);
        if (result.Status == PathStatus.Pending) return;

        _search = null;

        if (result.Status == PathStatus.Unreachable)
        {
            _path.Clear();
            // 首次不可达立即失败；路上临时受阻按原有间隔重试，保留已有的行走超时。
            if (!_searchIsRepath) Fail("道路不可达");
            return;
        }

        var path = new List<Vec2>(result.Path);
        if (path.Count == 0 || GoalQuery.Distance(path[^1], Destination) > ArrivalEpsilon) path.Add(Destination);
        _path = path;

        var previous = current;
        var distance = 0f;
        foreach (var point in path)
        {
            distance += GoalQuery.Distance(previous, point);
            previous = point;
        }

        _timeout = _elapsed + distance / _speed * 1000f + 3000f;

        // 分帧搜索可能已经花掉几百毫秒，从找到路径这一刻重新计时，
        // 否则刚找到就被判成「久未前进」而立刻重算。
        _lastProgress = _elapsed;
    }

    private void Fail(string error)
    {
        State = WalkState.Failed;
        Error = error;
    }
}
