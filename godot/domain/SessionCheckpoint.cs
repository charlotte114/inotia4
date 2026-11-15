using System.Text.Json;

namespace Inotia4.Domain;

public sealed record CheckpointLocation(string Map, double X, double Y);

public sealed class CheckpointSnapshot<TWorld>
{
    public required SessionState Session { get; init; }

    public required CheckpointLocation Location { get; init; }

    public required TWorld World { get; init; }
}

public sealed class SessionCheckpoint<TWorld>
{
    private CheckpointSnapshot<TWorld>? _snapshot;

    public bool Available => _snapshot is not null;

    public CheckpointSnapshot<TWorld>? Peek() => _snapshot is null ? null : Clone(_snapshot);

    /// <summary>
    /// 记一份检查点。
    ///
    /// 这里**不再对整份快照做一次 JSON 深拷贝**：<see cref="Session.CaptureState"/> 返回的本来就是
    /// 一套新建的状态（各子系统的 <c>CaptureState</c> 都造新对象、集合逐条复制），
    /// 场景交进来的世界状态同样是刚反序列化出来的——调用方此后不会再改它们，
    /// 存档口径要验的东西在进出存档时已经验过。实测一次进图检查点 185ms，
    /// 其中 119ms 花在这一次多余的深拷贝上。
    ///
    /// <see cref="Restore"/> 仍然克隆：恢复之后这份检查点还要反复用（反复阵亡回退），
    /// 灌回会话的对象会被玩法继续改。隔离留在了真正需要隔离的那一头。
    /// </summary>
    public void Capture(Session session, CheckpointLocation location, TWorld world) =>
        _snapshot = new CheckpointSnapshot<TWorld>
        {
            Session = session.CaptureState(),
            Location = location,
            World = world,
        };

    public (CheckpointLocation Location, TWorld World)? Restore(Session session)
    {
        if (_snapshot is null) return null;
        var restored = Clone(_snapshot);
        session.RestoreState(restored.Session);
        return (restored.Location, restored.World);
    }

    public void Clear() => _snapshot = null;

    private static T Clone<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, SaveGameStore.JsonOptions),
            SaveGameStore.JsonOptions) ?? throw new InvalidOperationException("检查点深拷贝失败");
}
