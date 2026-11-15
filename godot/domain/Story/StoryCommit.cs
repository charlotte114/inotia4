using System;
using System.Collections.Generic;
using System.Linq;
using Inotia4.Domain.Content;

namespace Inotia4.Domain.Story;

/// <summary>
/// 舞台快照里的一个演员：身份、落点与朝向。
///
/// <see cref="Id"/> 以 <c>entity:</c> 开头表示这个演员顶替了某个世界实体的旧身份，
/// 后半段就是那个实体的 id——收尾时要把旧 id 撤掉，否则同一张图上会留下两份。
/// </summary>
public sealed record StoryCommitActor(
    string Id,
    string? WorldEntityId,
    float X,
    float Y,
    string Facing,
    bool Visible,
    bool Unpositioned = false,
    bool Removed = false);

public sealed record StoryCommitCastMember(string? MemberId, string? CharacterId);

public sealed record StoryCommitStage(
    string Map,
    float X,
    float Y,
    string Facing,
    bool RetainPlayerPosition,
    IReadOnlyList<StoryCommitActor> Actors,
    IReadOnlyList<StoryCommitCastMember> Cast);

public sealed record StoryCommitResult(
    IReadOnlyList<StoryWorldCommand> Commands,
    IReadOnlyList<StoryWorldCommand> Incremental,
    string? CompletionFlag);

public static class StoryCommit
{
    public static StoryCommitResult Commands(
        IReadOnlyList<StoryWorldCommand> commands,
        StoryWorldState world,
        IReadOnlyList<StoryEntityState> changes,
        StoryCommitStage? stage,
        string? runStatus,
        string? eventId = null,
        Func<string, bool>? hasPartyMember = null)
    {
        hasPartyMember ??= _ => true;

        // 原版的 `commands` 在 flatMap 回调里读到的仍是未展开的原数组，
        // 所以「后面还有没有同图落位」要在原始顺序上判断。
        var source = commands as IReadOnlyList<StoryWorldCommand> ?? commands.ToList();
        var explicitTravel = source.Any(command => command is TravelCommand);
        var result = new List<StoryWorldCommand>(source.Count + changes.Count + 2);
        var incremental = new List<StoryWorldCommand>();

        for (var index = 0; index < source.Count; index++)
        {
            if (source[index] is not PlaceCommand place || stage is null)
            {
                result.Add(source[index]);
                continue;
            }

            var map = place.MapFromStage ? stage.Map : place.Entity.Map ?? string.Empty;
            if (map != stage.Map)
            {
                result.Add(place);
                continue;
            }

            var actor = stage.Actors.FirstOrDefault(candidate => candidate.WorldEntityId == place.Entity.Id);
            var dirty = changes.Any(entity => entity.Id == place.Entity.Id && entity.Map == map);
            var laterPlacement = false;
            for (var later = index + 1; later < source.Count; later++)
            {
                if (source[later] is not PlaceCommand next || next.Entity.Id != place.Entity.Id) continue;
                var laterMap = next.MapFromStage ? stage.Map : next.Entity.Map ?? string.Empty;
                if (laterMap != map) continue;
                laterPlacement = true;
                break;
            }

            var projected = dirty && actor is not null && !actor.Unpositioned && !actor.Removed && !laterPlacement
                ? place with
                {
                    Entity = place.Entity with
                    {
                        X = actor.X, Y = actor.Y, Facing = actor.Facing, Visible = actor.Visible,
                    },
                }
                : place;

            if (actor?.Id.StartsWith("entity:", StringComparison.Ordinal) != true)
            {
                result.Add(projected);
                if (projected != place) incremental.Add(projected);
                continue;
            }

            var previousId = actor.Id["entity:".Length..];
            if (previousId == place.Entity.Id)
            {
                result.Add(projected);
                if (projected != place) incremental.Add(projected);
                continue;
            }

            var existing = world.Entity(previousId);
            if (existing?.Map is not null && existing.Map != map)
            {
                result.Add(projected);
                if (projected != place) incremental.Add(projected);
                continue;
            }

            result.Add(new ToggleEntityCommand("remove", previousId));
            result.Add(projected);
            incremental.Add(new ToggleEntityCommand("remove", previousId));
            incremental.Add(projected);
        }

        // 舞台同时点名了队伍成员和人物编号，说明这个 NPC 已经由队伍系统接管。
        // 收尾时把同图的地图副本隐藏，否则回到正式地图会看到队伍成员和旧 NPC 两份。
        if (stage is not null)
        {
            foreach (var member in stage.Cast)
            {
                if (string.IsNullOrEmpty(member.MemberId) || string.IsNullOrEmpty(member.CharacterId)) continue;
                if (!hasPartyMember(member.MemberId)) continue;
                var removal = new RemoveCharacterCommand(member.CharacterId, stage.Map, true);
                result.Add(removal);
                incremental.Add(removal);
            }
        }

        var bindings = new List<StoryWorldCommand>();
        foreach (var entity in changes)
        {
            if (string.IsNullOrEmpty(entity.Map) || string.IsNullOrEmpty(entity.Actor) ||
                entity.X is null || entity.Y is null)
                throw new InvalidOperationException("绑定剧情角色缺少地图与位置");

            var existing = world.Entity(entity.Id);
            if (existing?.Removed == true) continue;
            if (existing?.Map is not null && existing.Map != entity.Map) continue;

            var persistent = existing?.Map is not null && existing.Actor is not null && existing.MapVisit is null;
            bindings.Add(new PlaceCommand(
                entity with { Authority = entity.Authority ?? PlacementAuthority.Of(entity) },
                VisitScope: !persistent));
        }

        result.InsertRange(0, bindings);

        var current = world.CurrentLocation;
        var moved = stage is not null &&
                    (current is null || stage.Map != current.Map || stage.X != current.X ||
                     stage.Y != current.Y || stage.Facing != current.Facing);

        if (runStatus is "completed" or "waiting" && stage is not null && !explicitTravel &&
            (!stage.RetainPlayerPosition || moved))
        {
            var travel = new TravelCommand(new StoryLocation
            {
                Map = stage.Map, X = stage.X, Y = stage.Y, Facing = stage.Facing,
            });
            result.Add(travel);
            incremental.Add(travel);
        }

        var completionFlag = eventId is not null && runStatus == "completed" ? eventId : null;

        var ordered = new List<StoryWorldCommand>(bindings.Count + incremental.Count);
        ordered.AddRange(bindings);
        ordered.AddRange(incremental);

        return new StoryCommitResult(result, ordered, completionFlag);
    }
}
