using System;

namespace Inotia4.Domain.Story;

/// <summary>
/// 带作用域的演员实例引用。
///
/// 地图实体编号只在单图唯一，所以 world 必须带地图；敌人实例只在一次战斗/遭遇里唯一，
/// 所以 combat 带地图与遭遇作用域；剧情替身只在一次演出运行里唯一。
/// alias 只是本次演出的绑定名，不属于引用本身。
/// </summary>
public abstract record ActorRef
{
    public sealed record World(string Map, string EntityId) : ActorRef;

    public sealed record Party(string MemberId) : ActorRef;

    public sealed record Combat(string Map, string? EncounterId, string EnemyKey) : ActorRef;

    public sealed record Story(string RunId, string InstanceId) : ActorRef;

    public static string Key(ActorRef reference) => reference switch
    {
        World world => $"world|{world.Map}|{world.EntityId}",
        Party party => $"party|{party.MemberId}",
        Combat combat => $"combat|{combat.Map}|{combat.EncounterId ?? string.Empty}|{combat.EnemyKey}",
        Story story => $"story|{story.RunId}|{story.InstanceId}",
        _ => throw new ArgumentOutOfRangeException(nameof(reference)),
    };
}

public sealed record ActorRefContext(string Map, string? RunId = null, string? EncounterId = null);

public static class ActorRefs
{
    public static ActorRef FromState(SceneActorSaved actor, ActorRefContext context)
    {
        if (!string.IsNullOrEmpty(actor.MemberId)) return new ActorRef.Party(actor.MemberId);
        if (!string.IsNullOrEmpty(actor.EnemyKey))
            return new ActorRef.Combat(context.Map, context.EncounterId, actor.EnemyKey);
        if (!string.IsNullOrEmpty(actor.WorldEntityId))
            return new ActorRef.World(context.Map, actor.WorldEntityId);
        return new ActorRef.Story(context.RunId ?? "scene", actor.Id);
    }
}
