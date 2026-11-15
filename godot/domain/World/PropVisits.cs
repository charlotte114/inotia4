using System.Collections.Generic;
using System.Linq;

namespace Inotia4.Domain.World;

public sealed record PropVisitsState(IReadOnlyList<string> Consumed);

/// <summary>
/// 用一次就消失的摆放：地上插着的剑被拔走之后，同一张图的同一个实体不再出现。
///
/// 和宝箱的 <c>Consumed</c> 是两件事——宝箱记录还带着本次访问的宝箱怪，
/// 这里只是一个跨访问保留的「已消耗」表，所以单独一份，互不干扰。
/// </summary>
public sealed class PropVisits
{
    private readonly HashSet<string> _consumed = [];

    public IReadOnlyCollection<string> Consumed => _consumed;

    private static string Key(string map, string entity) => $"{map}:{entity}";

    public bool IsConsumed(string map, string entity) => _consumed.Contains(Key(map, entity));

    public bool Consume(string map, string entity) => _consumed.Add(Key(map, entity));

    public PropVisitsState CaptureState() => new(_consumed.ToList());

    public void RestoreState(PropVisitsState state)
    {
        _consumed.Clear();
        foreach (var key in state.Consumed ?? []) _consumed.Add(key);
    }
}
