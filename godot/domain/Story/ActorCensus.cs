using System;
using System.Collections.Generic;
using System.Linq;

namespace Inotia4.Domain.Story;

public static class ActorCensus
{
    public static string InstanceKey(SceneActorSaved actor)
    {
        if (!string.IsNullOrEmpty(actor.WorldEntityId)) return $"world:{actor.WorldEntityId}";
        if (!string.IsNullOrEmpty(actor.MemberId)) return $"member:{actor.MemberId}";
        if (!string.IsNullOrEmpty(actor.EnemyKey)) return $"enemy:{actor.EnemyKey}";
        return $"story:{actor.Id}";
    }

    public static Dictionary<string, List<SceneActorSaved>> DuplicateVisibleActors(
        IReadOnlyList<SceneActorSaved> actors)
    {
        var groups = new Dictionary<string, List<SceneActorSaved>>();
        foreach (var actor in actors)
        {
            if (!actor.Visible || actor.Removed == true || actor.StageHidden == true) continue;
            var key = InstanceKey(actor);
            if (!groups.TryGetValue(key, out var same)) groups[key] = same = [];
            same.Add(actor);
        }

        foreach (var key in groups.Where(pair => pair.Value.Count < 2).Select(pair => pair.Key).ToList())
            groups.Remove(key);
        return groups;
    }

    public static void AssertUnique(IReadOnlyList<SceneActorSaved> actors, string context)
    {
        var duplicates = DuplicateVisibleActors(actors);
        if (duplicates.Count == 0) return;
        var detail = string.Join("；", duplicates.Select(pair =>
            $"{pair.Key} → {string.Join(", ", pair.Value.Select(actor => actor.Id))}"));
        throw new InvalidOperationException($"{context}演员实例重复：{detail}");
    }
}
