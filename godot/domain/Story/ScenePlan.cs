using System;
using System.Collections.Generic;
using System.Linq;

namespace Inotia4.Domain.Story;

public sealed class PlannedActor
{
    public required ActorRef Ref { get; init; }

    public required string RefKey { get; init; }

    public required string ActorId { get; init; }

    public required SceneActorSaved Actor { get; init; }

    public IReadOnlyList<string> Aliases { get; init; } = [];
}

/// <summary>
/// 从当前快照派生的只读演出计划：
/// 不修改快照，也不写回世界。可见实例按引用键分组，每份状态必须只有一个可见演员。
/// </summary>
public sealed class ScenePlan
{
    public required string Map { get; init; }

    public IReadOnlyList<PlannedActor> Actors { get; init; } = [];

    public IReadOnlyDictionary<string, IReadOnlyList<PlannedActor>> Visible { get; init; } =
        new Dictionary<string, IReadOnlyList<PlannedActor>>();

    public IReadOnlyDictionary<string, string> Bindings { get; init; } =
        new Dictionary<string, string>();
}

public static class ScenePlans
{
    public static ScenePlan Plan(
        SceneSnapshotSaved snapshot,
        IReadOnlyList<(string Alias, string ActorId)>? bindings = null,
        string? runId = null)
    {
        var aliasesByActor = new Dictionary<string, List<string>>();
        var bindingMap = new Dictionary<string, string>();
        foreach (var (alias, actorId) in bindings ?? [])
        {
            bindingMap[alias] = actorId;
            if (!aliasesByActor.TryGetValue(actorId, out var aliases)) aliasesByActor[actorId] = aliases = [];
            aliases.Add(alias);
        }

        var context = new ActorRefContext(snapshot.Map, runId);
        var actors = snapshot.Actors.Select(actor =>
        {
            var reference = ActorRefs.FromState(actor, context);
            return new PlannedActor
            {
                Ref = reference,
                RefKey = ActorRef.Key(reference),
                ActorId = actor.Id,
                Actor = actor,
                Aliases = aliasesByActor.TryGetValue(actor.Id, out var list) ? [.. list] : [],
            };
        }).ToList();

        var visible = new Dictionary<string, List<PlannedActor>>();
        foreach (var planned in actors)
        {
            if (!planned.Actor.Visible || planned.Actor.Removed == true || planned.Actor.StageHidden == true)
                continue;
            if (!visible.TryGetValue(planned.RefKey, out var same)) visible[planned.RefKey] = same = [];
            same.Add(planned);
        }

        return new ScenePlan
        {
            Map = snapshot.Map,
            Actors = actors,
            Visible = visible.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<PlannedActor>)pair.Value),
            Bindings = bindingMap,
        };
    }

    public static void Assert(ScenePlan plan)
    {
        var duplicates = plan.Visible.Where(pair => pair.Value.Count > 1).ToList();
        if (duplicates.Count > 0)
        {
            var detail = string.Join("；", duplicates.Select(pair =>
                $"{pair.Key} → {string.Join(", ", pair.Value.Select(actor => actor.ActorId))}"));
            throw new InvalidOperationException($"场景计划演员实例重复：{detail}");
        }

        var owner = new Dictionary<string, string>();
        foreach (var (alias, actorId) in plan.Bindings)
        {
            if (plan.Actors.All(actor => actor.ActorId != actorId))
                throw new InvalidOperationException($"场景计划绑定缺失：{alias} → {actorId}");
            if (owner.TryGetValue(actorId, out var previous))
                throw new InvalidOperationException($"场景计划演员重复绑定：{previous} 与 {alias} → {actorId}");
            owner[actorId] = alias;
        }
    }
}

public sealed record SceneActorDelta
{
    public required ActorRef Ref { get; init; }

    public required string Kind { get; init; }

    public SceneActorSaved? Before { get; init; }

    public SceneActorSaved? After { get; init; }
}

public sealed record SceneDelta
{
    public required string Map { get; init; }

    public required IReadOnlyList<SceneActorDelta> Changes { get; init; }
}

public static class SceneDiffs
{
    private static bool Changed(SceneActorSaved before, SceneActorSaved after) =>
        before.Actor != after.Actor || before.X != after.X || before.Y != after.Y ||
        before.Facing != after.Facing || before.Visible != after.Visible ||
        (before.Removed == true) != (after.Removed == true) ||
        (before.StageHidden == true) != (after.StageHidden == true) ||
        (before.Unpositioned == true) != (after.Unpositioned == true);

    public static SceneDelta Diff(SceneSnapshotSaved? before, SceneSnapshotSaved after, string? runId = null)
    {
        var previous = new List<(string Key, SceneActorSaved Actor)>();
        if (before is not null)
        {
            var context = new ActorRefContext(before.Map, runId);
            foreach (var actor in before.Actors)
            {
                var key = ActorRef.Key(ActorRefs.FromState(actor, context));
                if (previous.Any(pair => pair.Key == key))
                    throw new InvalidOperationException($"场景差量基线演员实例重复：{key}");
                previous.Add((key, actor));
            }
        }

        var changes = new List<SceneActorDelta>();
        var current = new HashSet<string>();
        var afterContext = new ActorRefContext(after.Map, runId);
        foreach (var actor in after.Actors)
        {
            var reference = ActorRefs.FromState(actor, afterContext);
            var key = ActorRef.Key(reference);
            if (!current.Add(key)) throw new InvalidOperationException($"场景差量演员实例重复：{key}");
            var old = previous.FirstOrDefault(pair => pair.Key == key);
            if (old.Actor is null) changes.Add(new SceneActorDelta { Ref = reference, Kind = "appear", After = actor });
            else if (Changed(old.Actor, actor))
                changes.Add(new SceneActorDelta
                { Ref = reference, Kind = "update", Before = old.Actor, After = actor });
        }

        foreach (var (key, actor) in previous)
        {
            if (current.Contains(key)) continue;
            changes.Add(new SceneActorDelta
            {
                Ref = ActorRefs.FromState(actor, new ActorRefContext(before!.Map, runId)),
                Kind = "remove",
                Before = actor,
            });
        }

        return new SceneDelta { Map = after.Map, Changes = changes };
    }
}
