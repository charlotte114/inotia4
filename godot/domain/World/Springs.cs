using System;
using System.Collections.Generic;
using System.Linq;

namespace Inotia4.Domain.World;

public sealed record SpringVisitsState(string? Map, IReadOnlyList<string> Used);

/// <summary>
/// 一次地图访问里喝空过的泉水：喝过一次就记下来，画面上这口泉水换成水井的样子。
/// 记录按「每次进图重置」走——换图才清空，重新进同一张图时地图对象会重建，
/// 所以这份记录要跟着沿用，走过的泉水不该重新长满。
/// </summary>
public sealed class SpringVisits
{
    private string? _map;
    private readonly HashSet<string> _used = new(StringComparer.Ordinal);

    public string? MapId => _map;

    public SpringVisitsState CaptureState() => new(_map, _used.ToList());

    public void RestoreState(SpringVisitsState state)
    {
        _map = state.Map;
        _used.Clear();
        foreach (var id in state.Used) _used.Add(id);
    }

    /// <summary>换图才清；同图重建继续沿用（走过的地方不该重新长满）。</summary>
    public void EnterMap(string map)
    {
        if (map == _map) return;
        _map = map;
        _used.Clear();
    }

    public bool IsEmpty(string map, string entity) => _map == map && _used.Contains(entity);

    public void Drain(string map, string entity)
    {
        if (map != _map) return;
        _used.Add(entity);
    }
}
