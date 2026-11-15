using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Inotia4.Domain.Content;

namespace Inotia4.Domain.Story;

public sealed record StoryEntityState
{
    public required string Id { get; init; }

    public string? Map { get; init; }

    public string? Actor { get; init; }

    public string? CharacterId { get; init; }

    public string? Authority { get; init; }

    public int? MapVisit { get; init; }

    public float? X { get; init; }

    public float? Y { get; init; }

    public string? Facing { get; init; }

    public bool? Visible { get; init; }

    public bool? Removed { get; init; }

    public string? Hint { get; init; }

    public string? Objective { get; init; }
}

public sealed record StoryEncounterSpawn
{
    public required string Id { get; init; }

    public required string EnemyId { get; init; }

    public float X { get; init; }

    public float Y { get; init; }
}

public sealed record StoryEncounter
{
    public required string Id { get; init; }

    public string? DefinitionId { get; init; }

    public required string Map { get; init; }

    public bool? MapFromStage { get; init; }

    public string Status { get; init; } = "prepared";

    public List<StoryEncounterSpawn> Spawns { get; init; } = [];
}

public sealed record StoryLocation
{
    public required string Map { get; init; }

    public float X { get; init; }

    public float Y { get; init; }

    public string Facing { get; init; } = "south";
}

public sealed record StoryEnemyRemoval
{
    public required string EnemyId { get; init; }

    public string? Map { get; init; }

    public bool All { get; init; }
}

public sealed record StoryEnemyLocation
{
    public required string Id { get; init; }

    public required string EnemyId { get; init; }

    public required string Map { get; init; }

    public float X { get; init; }

    public float Y { get; init; }

    public int? MapVisit { get; init; }

    public bool? Respawn { get; init; }
}

public sealed record StoryPartyBuff
{
    public required string Buff { get; init; }

    public int Strength { get; init; }
}

public sealed class StoryWorldSnapshot
{
    public List<StoryEntityState> Entities { get; set; } = [];

    public Dictionary<string, bool> Exits { get; set; } = [];

    public List<string> Flags { get; set; } = [];

    public Dictionary<string, string> Choices { get; set; } = [];

    public List<StoryEncounter> Encounters { get; set; } = [];

    public StoryLocation? Location { get; set; }

    public int MapVisit { get; set; }

    public List<StoryEnemyRemoval>? EnemyRemovals { get; set; }

    public List<StoryEnemyLocation>? EnemyLocations { get; set; }

    public Dictionary<string, string>? Appearances { get; set; }

    public List<StoryPartyBuff>? PartyBuffs { get; set; }
}

public abstract record StoryWorldCommand;

public sealed record RemoveEnemyCommand(string EnemyId, string? Map, bool All) : StoryWorldCommand;

public sealed record EnemyLocationCommand(
    string EnemyId, StoryLocation? Location, bool VisitScope = false, bool NoRespawn = false)
    : StoryWorldCommand;

public sealed record ActorAppearanceCommand(string Actor, string Appearance) : StoryWorldCommand;

public sealed record PartyBuffCommand(string Buff, int Strength) : StoryWorldCommand;

public sealed record PlaceCommand(
    StoryEntityState Entity, bool VisitScope = false, bool MapFromStage = false) : StoryWorldCommand;

public sealed record RemoveCharacterCommand(string CharacterId, string Map, bool All) : StoryWorldCommand;

public sealed record ToggleEntityCommand(string Kind, string Id) : StoryWorldCommand;

public sealed record ActorVisibilityCommand(string Map, string Actor, string? CharacterId, bool Visible)
    : StoryWorldCommand;

public sealed record EnemyAvailabilityCommand(string Map, string EnemyId, bool Available) : StoryWorldCommand;

public sealed record ExitCommand(string Id, bool Enabled) : StoryWorldCommand;

public sealed record FlagCommand(string Id, bool Value) : StoryWorldCommand;

public sealed record ChoiceCommand(string Id, string Value) : StoryWorldCommand;

public sealed record TravelCommand(StoryLocation Destination) : StoryWorldCommand;

public sealed record EncounterCommand(StoryEncounter Encounter) : StoryWorldCommand;

public sealed record EncounterResultCommand(string Id, string Result) : StoryWorldCommand;

public static class PlacementAuthority
{
    public const string Registered = "registered";

    public const string Npc = "npc";

    public const string Story = "story";

    public const string Static = "static";

    public static string Of(string id, string? authority) =>
        !string.IsNullOrEmpty(authority) ? authority : Decode(id);

    public static string Of(MapEntity entity) => Of(entity.Id, entity.Authority);

    public static string Of(StoryEntityState entity) => Of(entity.Id, entity.Authority);

    private static string Decode(string id)
    {
        if (id.StartsWith("quest-npc-located-", StringComparison.Ordinal)) return Registered;
        if (id.StartsWith("quest-npc-", StringComparison.Ordinal)) return Npc;
        if (id.StartsWith("story-", StringComparison.Ordinal) ||
            id.StartsWith("quest-target:", StringComparison.Ordinal)) return Story;
        return Static;
    }
}

///
/// 它记的是「剧情把世界改成了什么样」：谁被摆到哪、谁被撤下、哪些敌人禁用或换位、
/// 出口开关、旗标与选择、队伍状态、遭遇战进度、当前转场落点。场景每次进图都从这份状态
/// 投影出一份地图人物名单，所以读档、换图、回图看到的世界与剧情推进到的地方一致。
/// </summary>
public sealed class StoryWorldState
{
    private readonly Dictionary<string, StoryEntityState> _entities = [];
    private readonly Dictionary<string, bool> _exits = [];
    private readonly HashSet<string> _flags = [];
    private readonly Dictionary<string, string> _choices = [];
    private readonly Dictionary<string, StoryEncounter> _encounters = [];
    private StoryLocation? _location;
    private int _mapVisit;
    private List<StoryEnemyRemoval> _enemyRemovals = [];
    private readonly Dictionary<string, StoryEnemyLocation> _enemyLocations = [];
    private readonly Dictionary<string, string> _appearances = [];
    private List<StoryPartyBuff> _partyBuffs = [];

    private int _revision;

    public IReadOnlyList<StoryEnemyRemoval> PendingEnemyRemovals => _enemyRemovals.ToArray();

    public void ConsumeEnemyRemovals() => _enemyRemovals = [];

    public int Revision => _revision;

    public IReadOnlyList<StoryEnemyLocation> EnemySpawns(string map) =>
        _enemyLocations.Values.Where(value => value.Map == map).ToArray();

    public string ActorAppearance(string actor) => _appearances.GetValueOrDefault(actor) ?? actor;

    public IReadOnlyList<StoryPartyBuff> PendingPartyBuffs => _partyBuffs.ToArray();

    public void ConsumePartyBuffs(int count)
    {
        if (count <= 0) return;
        _partyBuffs.RemoveRange(0, Math.Min(count, _partyBuffs.Count));
    }

    public StoryLocation? CurrentLocation => _location;

    public int Visit => _mapVisit;

    public bool HasFlag(string id) => _flags.Contains(id);

    public IReadOnlyCollection<string> FlagIds => _flags;

    public string? Choice(string id) => _choices.GetValueOrDefault(id);

    public Dictionary<string, string> ChoiceValues() => new(_choices);

    public Dictionary<string, (string Status, string? DefinitionId)> EncounterStatuses() =>
        _encounters.ToDictionary(pair => pair.Key, pair => (pair.Value.Status, pair.Value.DefinitionId));

    public StoryEntityState? Entity(string id) => _entities.GetValueOrDefault(id);

    public StoryEncounter? Encounter(string id) =>
        _encounters.TryGetValue(id, out var encounter) ? CloneEncounter(encounter) : null;

    public IReadOnlyList<StoryEncounter> ActiveEncounters =>
        _encounters.Values.Where(encounter => encounter.Status == "active").Select(CloneEncounter).ToArray();

    public static string StoryExitId(string map, MapExit exit) =>
        !string.IsNullOrEmpty(exit.Id)
            ? exit.Id!
            : $"{map}:{exit.Destination}:{exit.X:0.#}:{exit.Y:0.#}:{exit.Width:0.#}:{exit.Height:0.#}";

    public bool ExitEnabled(string map, MapExit exit) =>
        _exits.GetValueOrDefault(StoryExitId(map, exit), true);

    public bool ActorVisible(string map, string actor, string? characterId) =>
        !_flags.Contains(AbsentActorKey(map, characterId ?? actor));

    public bool EnemyAvailable(string map, string enemyId) =>
        !_flags.Contains(AbsentEnemyKey(map, enemyId));

    private static string AbsentActorKey(string map, string character) =>
        $"{{\"absent-actor\",\"{map}\",\"{character}\"}}";

    private static string AbsentEnemyKey(string map, string enemyId) =>
        $"{{\"absent-enemy\",\"{map}\",\"{enemyId}\"}}";

    public List<MapEntity> ProjectEntities(
        string map, IReadOnlyList<MapEntity> baseEntities,
        IReadOnlyList<MapEntity>? definitions = null, int? visit = null)
    {
        visit ??= _mapVisit;
        var projected = new Dictionary<string, MapEntity>();

        // 本图上已经登记过的角色（按人物编号，没有编号就按角色资源）：
        // 它们的静态副本要让位给登记副本，否则会重叠成两个同名 NPC。
        var located = _entities.Values
            .Where(entity => PlacementAuthority.Of(entity) == PlacementAuthority.Registered &&
                             entity.Map == map && entity.Removed != true &&
                             (entity.MapVisit is null || entity.MapVisit == visit))
            .Select(entity => entity.CharacterId ?? entity.Actor)
            .ToHashSet();

        foreach (var entity in baseEntities)
        {
            if (PlacementAuthority.Of(entity) != PlacementAuthority.Registered &&
                located.Contains(entity.CharacterId ?? entity.Actor)) continue;

            var stored = _entities.GetValueOrDefault(entity.Id);
            // 过场副本（mapVisit 不等于本次访问）在这一号访问里已经作废，不能拿它覆盖静态摆放。
            var over = stored is not null && stored.MapVisit is not null && stored.MapVisit != visit
                ? null
                : stored;

            if (over?.Removed == true) continue;
            if (over?.Map is { Length: > 0 } overMap && overMap != map) continue;
            if (!ActorVisible(map, over?.Actor ?? entity.Actor, over?.CharacterId ?? entity.CharacterId)) continue;

            projected[entity.Id] = Merge(entity, over);
        }

        var definitionTable = definitions ?? baseEntities;
        foreach (var entity in _entities.Values)
        {
            if (entity.Removed == true) continue;
            if (entity.Map != map) continue;
            if (entity.MapVisit is not null && entity.MapVisit != visit) continue;
            if (PlacementAuthority.Of(entity) != PlacementAuthority.Registered &&
                located.Contains(entity.CharacterId ?? entity.Actor)) continue;
            if (string.IsNullOrEmpty(entity.Actor)) continue;
            if (!ActorVisible(map, entity.Actor!, entity.CharacterId)) continue;
            if (entity.X is null || entity.Y is null) continue;
            if (projected.ContainsKey(entity.Id)) continue;

            var definition = definitionTable.FirstOrDefault(candidate => candidate.Id == entity.Id);
            projected[entity.Id] = MergeFromDefinition(definition, entity);
        }

        return projected.Values.ToList();
    }

    public bool Visible(string id, int? visit = null)
    {
        visit ??= _mapVisit;
        var value = _entities.GetValueOrDefault(id);
        if (value?.MapVisit is not null && value.MapVisit != visit) return true;
        return value?.Removed != true && value?.Visible != false;
    }

    public bool IsPlaced(string id) => _entities.ContainsKey(id);

    public void Apply(StoryWorldCommand command)
    {
        _revision += 1;

        switch (command)
        {
            case RemoveEnemyCommand remove:
                _enemyRemovals.Add(new StoryEnemyRemoval
                {
                    EnemyId = remove.EnemyId,
                    Map = remove.Map,
                    All = remove.All,
                });
                return;

            case EnemyLocationCommand locate:
            {
                if (locate.Location is { } location)
                {
                    _enemyLocations[locate.EnemyId] = new StoryEnemyLocation
                    {
                        Id = "story-located-" + locate.EnemyId,
                        EnemyId = locate.EnemyId,
                        Map = location.Map,
                        X = location.X,
                        Y = location.Y,
                        MapVisit = locate.VisitScope
                            ? _mapVisit + (location.Map != _location?.Map ? 1 : 0)
                            : null,
                        Respawn = locate.NoRespawn ? false : null,
                    };
                }
                else
                {
                    _enemyLocations.Remove(locate.EnemyId);
                }

                return;
            }

            case ActorAppearanceCommand appearance:
                _appearances[appearance.Actor] = appearance.Appearance;
                return;

            case PartyBuffCommand buff:
                _partyBuffs.Add(new StoryPartyBuff { Buff = buff.Buff, Strength = buff.Strength });
                return;

            case PlaceCommand place:
            {
                var entity = place.Entity;
                if (string.IsNullOrEmpty(entity.Id) || string.IsNullOrEmpty(entity.Map) ||
                    string.IsNullOrEmpty(entity.Actor) ||
                    entity.X is null || entity.Y is null ||
                    !float.IsFinite(entity.X.Value) || !float.IsFinite(entity.Y.Value))
                    throw new InvalidOperationException("剧情角色位置无效");

                if (PlacementAuthority.Of(entity) == PlacementAuthority.Registered &&
                    !string.IsNullOrEmpty(entity.CharacterId))
                {
                    foreach (var (id, other) in _entities.ToArray())
                        if (PlacementAuthority.Of(other) == PlacementAuthority.Story &&
                            other.CharacterId == entity.CharacterId &&
                            other.Map == entity.Map && other.Removed != true)
                            _entities.Remove(id);
                }

                var previous = _entities.GetValueOrDefault(entity.Id);
                // 常驻登记必须显式清掉旧的访问寿命：同一实例可能先由演出按 visit 登记，
                // 后续命令再把它永久摆出来；只靠合并会把旧的 mapVisit 留下来。
                _entities[entity.Id] = new StoryEntityState
                {
                    Id = entity.Id,
                    Map = entity.Map,
                    Actor = entity.Actor,
                    CharacterId = entity.CharacterId ?? previous?.CharacterId,
                    Authority = PlacementAuthority.Of(entity),
                    MapVisit = place.VisitScope
                        ? _mapVisit + (entity.Map != _location?.Map ? 1 : 0)
                        : null,
                    X = entity.X,
                    Y = entity.Y,
                    Facing = entity.Facing ?? previous?.Facing,
                    Visible = entity.Visible ?? previous?.Visible,
                    Removed = false,
                    Hint = entity.Hint ?? previous?.Hint,
                    Objective = entity.Objective ?? previous?.Objective,
                };

                if (entity.Visible != false && !string.IsNullOrEmpty(entity.CharacterId))
                    _flags.Remove(AbsentActorKey(entity.Map!, entity.CharacterId!));
                return;
            }

            case RemoveCharacterCommand remove:
            {
                // 有位置登记时，这个角色在本图只有一个正式副本；旧存档遗留的过场副本一起撤下，
                // 否则登记副本先被移除后，过场副本会重新露出来。
                var registered = _entities.Values.Any(entity =>
                    PlacementAuthority.Of(entity) == PlacementAuthority.Registered &&
                    entity.CharacterId == remove.CharacterId &&
                    entity.Map == remove.Map && entity.Removed != true);

                foreach (var entity in _entities.Values)
                {
                    if (entity.CharacterId != remove.CharacterId ||
                        entity.Map != remove.Map || entity.Removed == true) continue;

                    _entities[entity.Id] = entity with { Removed = true };
                    if (!remove.All && !registered) break;
                }

                if (!string.IsNullOrEmpty(remove.CharacterId))
                {
                    var remaining = _entities.Values.Any(entity =>
                        entity.CharacterId == remove.CharacterId &&
                        entity.Map == remove.Map && entity.Removed != true);
                    if (!remaining) _flags.Add(AbsentActorKey(remove.Map, remove.CharacterId));
                }

                return;
            }

            case ToggleEntityCommand toggle:
            {
                var previous = _entities.GetValueOrDefault(toggle.Id);
                _entities[toggle.Id] = toggle.Kind switch
                {
                    "remove" => (previous ?? new StoryEntityState { Id = toggle.Id }) with
                    {
                        Id = toggle.Id,
                        Removed = true,
                    },
                    _ => (previous ?? new StoryEntityState { Id = toggle.Id }) with
                    {
                        Id = toggle.Id,
                        Visible = toggle.Kind == "show",
                        Removed = false,
                    },
                };
                return;
            }

            case ActorVisibilityCommand visibility:
                SetPresentFlag(AbsentActorKey(visibility.Map, visibility.CharacterId ?? visibility.Actor),
                    visibility.Visible);
                return;

            case EnemyAvailabilityCommand availability:
                SetPresentFlag(AbsentEnemyKey(availability.Map, availability.EnemyId), availability.Available);
                return;

            case ExitCommand exitCommand:
                _exits[exitCommand.Id] = exitCommand.Enabled;
                return;

            case FlagCommand flag:
                if (flag.Value) _flags.Add(flag.Id);
                else _flags.Remove(flag.Id);
                return;

            case ChoiceCommand choice:
                _choices[choice.Id] = choice.Value;
                return;

            case TravelCommand travel:
            {
                var destination = travel.Destination;
                if (string.IsNullOrEmpty(destination.Map) ||
                    !float.IsFinite(destination.X) || !float.IsFinite(destination.Y))
                    throw new InvalidOperationException("剧情转场位置无效");

                if (_location?.Map != destination.Map) _mapVisit += 1;

                foreach (var (id, entity) in _entities.ToArray())
                    if (entity.MapVisit is not null && entity.MapVisit != _mapVisit)
                        _entities.Remove(id);
                foreach (var (id, enemy) in _enemyLocations.ToArray())
                    if (enemy.MapVisit is not null && enemy.MapVisit != _mapVisit)
                        _enemyLocations.Remove(id);

                _location = destination with { };
                return;
            }

            case EncounterCommand encounter:
            {
                ValidateEncounter(encounter.Encounter);
                if (_encounters.ContainsKey(encounter.Encounter.Id))
                    throw new InvalidOperationException(
                        "剧情遭遇实例已经存在：" + encounter.Encounter.Id);
                _encounters[encounter.Encounter.Id] = CloneEncounter(encounter.Encounter);
                return;
            }

            case EncounterResultCommand result:
            {
                if (!_encounters.TryGetValue(result.Id, out var encounter))
                    throw new InvalidOperationException("剧情遭遇不存在：" + result.Id);
                if (encounter.Status is "won" or "lost")
                {
                    if (encounter.Status != result.Result)
                        throw new InvalidOperationException("剧情遭遇结果冲突：" + result.Id);
                    return;
                }

                _encounters[result.Id] = encounter with { Status = result.Result };
                return;
            }

            default:
                throw new InvalidOperationException("未知剧情世界命令");
        }
    }

    private void SetPresentFlag(string key, bool present)
    {
        if (present) _flags.Remove(key);
        else _flags.Add(key);
    }

    private static MapEntity Merge(MapEntity entity, StoryEntityState? over)
    {
        if (over is null) return entity;

        return new MapEntity
        {
            Id = entity.Id,
            Actor = over.Actor ?? entity.Actor,
            X = over.X ?? entity.X,
            Y = over.Y ?? entity.Y,
            Flip = entity.Flip,
            CharacterId = over.CharacterId ?? entity.CharacterId,
            Appear = entity.Appear,
            Downgrade = entity.Downgrade,
            Authority = over.Authority ?? entity.Authority,
            Hint = over.Hint ?? entity.Hint,
            Objective = over.Objective ?? entity.Objective,
            // 原版是 `{...entity, ...over}` 的展开：其余字段跟着静态摆放走，不因为被剧情改过就丢。
            Collectible = entity.Collectible,
            AfterInteractScript = entity.AfterInteractScript,
            Spring = entity.Spring,
        };
    }

    private static MapEntity MergeFromDefinition(MapEntity? definition, StoryEntityState entity) => new()
    {
        Id = entity.Id,
        Actor = entity.Actor!,
        X = entity.X ?? 0f,
        Y = entity.Y ?? 0f,
        Flip = definition?.Flip ?? false,
        CharacterId = entity.CharacterId,
        Appear = definition?.Appear,
        Downgrade = definition?.Downgrade,
        Authority = PlacementAuthority.Of(entity),
        Hint = entity.Hint ?? definition?.Hint,
        Objective = entity.Objective ?? definition?.Objective,
        Collectible = definition?.Collectible ?? false,
        AfterInteractScript = definition?.AfterInteractScript,
        Spring = definition?.Spring ?? false,
    };

    public StoryWorldSnapshot CaptureState() => new()
    {
        Entities = _entities.Values.ToList(),
        Exits = new Dictionary<string, bool>(_exits),
        Flags = _flags.ToList(),
        Choices = new Dictionary<string, string>(_choices),
        Encounters = _encounters.Values.Select(CloneEncounter).ToList(),
        Location = _location,
        MapVisit = _mapVisit,
        EnemyRemovals = _enemyRemovals.ToList(),
        EnemyLocations = _enemyLocations.Values.ToList(),
        Appearances = new Dictionary<string, string>(_appearances),
        PartyBuffs = _partyBuffs.ToList(),
    };

    public static StoryWorldSnapshot PrepareRestore(StoryWorldSnapshot state)
    {
        var result = new StoryWorldSnapshot
        {
            Entities = state.Entities.ToList(),
            Exits = new Dictionary<string, bool>(state.Exits),
            Flags = state.Flags.ToList(),
            Choices = new Dictionary<string, string>(state.Choices),
            Encounters = state.Encounters.Select(CloneEncounter).ToList(),
            Location = state.Location,
            MapVisit = state.MapVisit,
            EnemyRemovals = state.EnemyRemovals?.ToList(),
            EnemyLocations = state.EnemyLocations?.ToList(),
            Appearances = state.Appearances is null ? null : new Dictionary<string, string>(state.Appearances),
            PartyBuffs = state.PartyBuffs?.ToList(),
        };

        foreach (var removal in result.EnemyRemovals ?? [])
            if (string.IsNullOrEmpty(removal.EnemyId))
                throw new InvalidOperationException("剧情敌人移除无效");

        foreach (var location in result.EnemyLocations ?? [])
        {
            if (string.IsNullOrEmpty(location.Id) || string.IsNullOrEmpty(location.EnemyId) ||
                string.IsNullOrEmpty(location.Map) ||
                !float.IsFinite(location.X) || !float.IsFinite(location.Y))
                throw new InvalidOperationException("剧情敌人位置无效");
            if (location.Respawn is not null && location.Respawn != false ||
                location.MapVisit is < 0)
                throw new InvalidOperationException("剧情临时敌人寿命无效");
        }

        foreach (var buff in result.PartyBuffs ?? [])
            if (string.IsNullOrEmpty(buff.Buff) || buff.Strength < 0)
                throw new InvalidOperationException("剧情队伍状态无效");

        if (result.MapVisit < 0) throw new InvalidOperationException("剧情地图访问版本无效");

        if (result.Entities.Any(entity => string.IsNullOrEmpty(entity.Id)) ||
            result.Entities.Select(entity => entity.Id).Distinct().Count() != result.Entities.Count)
            throw new InvalidOperationException("剧情角色标识重复或无效");
        if (result.Encounters.Select(encounter => encounter.Id).Distinct().Count() != result.Encounters.Count)
            throw new InvalidOperationException("剧情遭遇标识重复或无效");

        foreach (var entity in result.Entities)
        {
            if (entity.CharacterId is { Length: 0 }) throw new InvalidOperationException("剧情人物身份无效");
            if (entity.Authority is { Length: > 0 } authority &&
                authority is not (PlacementAuthority.Registered or PlacementAuthority.Npc
                    or PlacementAuthority.Story or PlacementAuthority.Static))
                throw new InvalidOperationException("剧情角色来源无效");
            if (entity.MapVisit is < 0) throw new InvalidOperationException("剧情临时物件访问版本无效");
            if (entity.X is { } x && !float.IsFinite(x) ||
                entity.Y is { } y && !float.IsFinite(y))
                throw new InvalidOperationException("剧情角色快照位置无效");
            if (entity.Facing is { Length: > 0 } facing && facing is not ("north" or "south" or "east" or "west"))
                throw new InvalidOperationException("剧情角色快照朝向无效");
        }

        if (result.Location is { } location2 &&
            (string.IsNullOrEmpty(location2.Map) || !float.IsFinite(location2.X) ||
             !float.IsFinite(location2.Y) ||
             location2.Facing is not ("north" or "south" or "east" or "west")))
            throw new InvalidOperationException("剧情世界快照落点无效");

        if (result.Flags.Any(string.IsNullOrEmpty) ||
            result.Flags.Distinct().Count() != result.Flags.Count)
            throw new InvalidOperationException("剧情世界快照标记无效");
        if (result.Exits.Keys.Any(string.IsNullOrEmpty) || result.Choices.Keys.Any(string.IsNullOrEmpty))
            throw new InvalidOperationException("剧情世界快照标记无效");

        foreach (var encounter in result.Encounters) ValidateEncounter(encounter);

        return result;
    }

    public void RestoreState(StoryWorldSnapshot state)
    {
        _revision += 1;
        var restored = PrepareRestore(state);

        _entities.Clear();
        foreach (var entity in restored.Entities) _entities[entity.Id] = entity;

        _exits.Clear();
        foreach (var (id, value) in restored.Exits) _exits[id] = value;

        _flags.Clear();
        foreach (var flag in restored.Flags) _flags.Add(flag);

        _choices.Clear();
        foreach (var (id, value) in restored.Choices) _choices[id] = value;

        _encounters.Clear();
        foreach (var encounter in restored.Encounters) _encounters[encounter.Id] = encounter;

        _location = restored.Location;
        _mapVisit = restored.MapVisit;
        _partyBuffs = restored.PartyBuffs ?? [];
        _enemyRemovals = restored.EnemyRemovals ?? [];

        _appearances.Clear();
        foreach (var (actor, appearance) in restored.Appearances ?? []) _appearances[actor] = appearance;

        _enemyLocations.Clear();
        foreach (var location in restored.EnemyLocations ?? []) _enemyLocations[location.EnemyId] = location;
    }

    // 其余状态记录只有不可变标量；遭遇的生成点列表还需单独复制。
    private static StoryEncounter CloneEncounter(StoryEncounter encounter) =>
        encounter with { Spawns = encounter.Spawns.ToList() };

    private static void ValidateEncounter(StoryEncounter encounter)
    {
        if (string.IsNullOrEmpty(encounter.Id) ||
            encounter.Status is not ("prepared" or "active" or "won" or "lost") ||
            string.IsNullOrEmpty(encounter.Map) ||
            encounter.Spawns.Select(spawn => spawn.Id).Distinct().Count() != encounter.Spawns.Count ||
            encounter.Spawns.Any(spawn => string.IsNullOrEmpty(spawn.Id) ||
                                          string.IsNullOrEmpty(spawn.EnemyId) ||
                                          !float.IsFinite(spawn.X) || !float.IsFinite(spawn.Y)))
            throw new InvalidOperationException("剧情遭遇快照无效");
    }
}
