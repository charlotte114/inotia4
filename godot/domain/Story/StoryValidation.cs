using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Inotia4.Domain.Content;
using Inotia4.Domain.World;

namespace Inotia4.Domain.Story;

public static class StoryValidation
{
    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException($"剧情引用无效：{message}");
    }

    public static HashSet<string> ReferenceMaps(CampaignContent campaign)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);

        void Add(string? id)
        {
            if (!string.IsNullOrEmpty(id)) ids.Add(id);
        }

        Add(campaign.Start.Map);
        foreach (var placement in Placements(campaign)) Add(placement.Map);
        foreach (var trigger in campaign.EventTable.Triggers) Add(trigger.Map);

        foreach (var action in Actions(campaign))
        {
            var type = StoryType.Of(action);
            if (type == "stage")
            {
                Add(StoryType.Text(action, "map"));
            }
            else if (type == "world-object" && action.TryGetProperty("command", out var command))
            {
                var kind = StoryType.Text(command, "kind");
                if (kind == "place" && command.TryGetProperty("entity", out var entity))
                    Add(StoryType.Text(entity, "map"));
                else if (kind is "remove-character" or "actor-visibility" or "enemy-availability")
                    Add(StoryType.Text(command, "map"));
            }
        }

        foreach (var script in campaign.Scripts.Values)
        foreach (var node in script.Nodes)
        {
            if (node.Exit.Encounter.ValueKind == JsonValueKind.Object) Add(StoryType.Text(node.Exit.Encounter, "map"));

            foreach (var command in node.Commands)
            {
                var type = StoryType.Of(command);
                if (type is "travel" or "encounter")
                    Add(StoryType.Text(type == "travel" ? Destination(command) : Nested(command, "encounter"), "map"));
                else if (type == "world") CollectWorldMap(command, Add);
            }
        }

        return ids;
    }

    /// <summary>
    /// 逐条核对剧情引用。
    ///
    /// <paramref name="maps"/> 必须已经包含 <see cref="ReferenceMaps"/> 里的每一张图；
    /// 缺图本身就会被当成错误报出来，而不是静默跳过。
    /// </summary>
    public static void ValidateReferences(
        CampaignContent campaign, WorldIndex world, BattleContent battle,
        IReadOnlyDictionary<string, MapDefinition> maps, ItemCatalog items)
    {
        foreach (var id in ReferenceMaps(campaign))
            Ensure(world.Maps.Any(entry => entry.Id == id) && maps.ContainsKey(id), $"地图不存在 {id}");

        var placements = Placements(campaign).ToArray();

        // 世界实体：剧情摆放 + 每张图的静态摆放。判定按「id 在本图出现过」，
        // 因为同一扇门的编号可以出现在多张图里，跨图绑定才是错。
        var entities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var placement in placements) entities.Add(placement.Id);
        foreach (var map in maps.Values)
        foreach (var entity in map.Entities)
            entities.Add(entity.Id);

        var entityMaps = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        void Remember(string id, string map)
        {
            if (id.Length == 0) return;
            if (!entityMaps.TryGetValue(id, out var homes)) entityMaps[id] = homes = new HashSet<string>(StringComparer.Ordinal);
            homes.Add(map);
        }

        foreach (var map in maps.Values)
        foreach (var entity in map.Entities)
            Remember(entity.Id, map.Id);
        foreach (var placement in placements) Remember(placement.Id, placement.Map);

        void CheckEntityMap(string id, string map, string message)
        {
            if (!entityMaps.TryGetValue(id, out var homes)) return;
            Ensure(homes.Contains(map), $"{message}：{id} 在 {string.Join("/", homes)}，舞台在 {map}");
        }

        var actors = world.Actors.Keys.ToHashSet(StringComparer.Ordinal);
        var enemies = battle.Enemies.Select(enemy => enemy.Id).ToHashSet(StringComparer.Ordinal);
        var exits = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var map in maps.Values)
        foreach (var exit in map.Exits)
            exits[StoryExitId(map.Id, exit)] = map.Id;

        var actions = Actions(campaign).ToArray();
        var rawCommands = campaign.Scripts.Values.SelectMany(script => script.Nodes).SelectMany(node => node.Commands).ToArray();
        var worldCommands = new List<StoryWorldCommand>();
        var sink = new CollectingSink(worldCommands);
        var skipped = new List<string>();
        foreach (var command in rawCommands)
            if (StoryType.Of(command) == "world")
                WorldCommands.Apply(command, sink, skipped);
        foreach (var command in worldCommands)
            if (command is PlaceCommand place)
                entities.Add(place.Entity.Id);

        var characters = new HashSet<string>(StringComparer.Ordinal);

        void RememberCharacter(string? id)
        {
            if (!string.IsNullOrEmpty(id)) characters.Add(id);
        }

        foreach (var placement in placements) RememberCharacter(placement.CharacterId);
        foreach (var map in maps.Values)
        foreach (var entity in map.Entities)
            RememberCharacter(entity.CharacterId);
        foreach (var command in worldCommands)
            if (command is PlaceCommand place)
                RememberCharacter(place.Entity.CharacterId);
        foreach (var action in actions)
            if (StoryType.Of(action) == "stage" && Cast(action) is { } cast)
                foreach (var member in cast.EnumerateArray())
                    RememberCharacter(StoryType.Text(member, "characterId"));
        foreach (var quest in campaign.Quests)
        foreach (var change in quest.TargetChanges ?? [])
        {
            var goal = quest.Goals.FirstOrDefault(goal => goal.Id == change.Goal);
            RememberCharacter(goal?.CharacterId);
        }

        foreach (var action in actions)
            if (StoryType.Of(action) == "appear" && StoryType.Text(action, "worldEntityId") is { Length: > 0 } id)
                Ensure(entities.Contains(id), $"出场实体不存在 {id}");

        void Point(string? mapId, float? x, float? y, string owner)
        {
            maps.TryGetValue(mapId ?? string.Empty, out var map);
            Ensure(map is not null && x is { } px && y is { } py && px >= 0 && px < map.Width && py >= 0 && py < map.Height,
                $"位置超出地图 {owner}");
        }

        var quests = campaign.Quests.Select(quest => quest.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var map in maps.Values)
        foreach (var spawn in map.QuestItemSpawns)
        {
            Ensure(quests.Contains(spawn.Quest), $"地图任务物品引用任务不存在 {spawn.Quest}");
            Ensure(items.Items.ContainsKey(spawn.Item), $"地图任务物品不存在 {spawn.Item}");
            Point(map.Id, spawn.X, spawn.Y, spawn.Id);
        }

        void Encounter(StoryEncounter? encounter)
        {
            Ensure(encounter is not null, "遭遇内容缺失");
            if (encounter is null) return;

            Ensure(encounter.Spawns.Count > 0, $"遭遇需要敌人 {encounter.Id}");
            foreach (var spawn in encounter.Spawns)
            {
                Ensure(enemies.Contains(spawn.EnemyId), $"遭遇敌人不存在 {spawn.EnemyId}");
                if (encounter.MapFromStage != true)
                    Point(encounter.Map, spawn.X, spawn.Y, $"{encounter.Id}/{spawn.Id}");
            }
        }

        void Condition(JsonElement condition)
        {
            if (condition.ValueKind != JsonValueKind.Object) return;

            var type = StoryType.Text(condition, "type");
            switch (type)
            {
                case "all":
                case "any":
                    if (condition.TryGetProperty("conditions", out var list) && list.ValueKind == JsonValueKind.Array)
                        foreach (var child in list.EnumerateArray())
                            Condition(child);
                    return;
                case "not":
                    if (condition.TryGetProperty("condition", out var inner)) Condition(inner);
                    return;
                case "world":
                    break;
                default:
                    return;
            }

            var key = StoryType.Text(condition, "key");
            var value = condition.TryGetProperty("value", out var raw) ? ScalarText(raw) : string.Empty;
            switch (key)
            {
                case "map":
                    Ensure(world.Maps.Any(map => map.Id == value), $"条件地图不存在 {value}");
                    break;
                case "near-actor":
                    Ensure(actors.Contains(value), $"条件人物不存在 {value}");
                    break;
                case "near-entity":
                    Ensure(entities.Contains(value), $"条件实体不存在 {value}");
                    break;
                default:
                    if (key.StartsWith("region:", StringComparison.Ordinal))
                    {
                        var eventId = key["region:".Length..];
                        Ensure(campaign.EventTable.Triggers.Any(trigger => trigger.Id == eventId && trigger.Source == "region"),
                            $"条件区域不存在 {key}");
                    }

                    break;
            }
        }

        Ensure(actors.Contains(campaign.Start.Actor), $"主角不存在 {campaign.Start.Actor}");
        Point(campaign.Start.Map, campaign.Start.X, campaign.Start.Y, "start");

        foreach (var placement in placements)
        {
            Ensure(actors.Contains(placement.Actor), $"人物不存在 {placement.Actor}");
            Point(placement.Map, placement.X, placement.Y, placement.Id);
            Condition(placement.Condition);
        }

        foreach (var quest in campaign.Quests)
        {
            if (quest.Giver is { Length: > 0 } giver) Ensure(actors.Contains(giver), $"任务发布人物不存在 {giver}");
            if (quest.Delivery is { Length: > 0 } delivery) Ensure(actors.Contains(delivery), $"任务交付对象不存在 {delivery}");
            if (quest.GiverCharacterId is { Length: > 0 } giverId)
                Ensure(characters.Contains(giverId), $"任务发布人物身份不存在 {giverId}");
            if (quest.DeliveryCharacterId is { Length: > 0 } deliveryId)
                Ensure(characters.Contains(deliveryId), $"任务交付人物身份不存在 {deliveryId}");
            Condition(quest.AvailableWhen);

            foreach (var change in quest.TargetChanges ?? [])
            {
                Ensure(enemies.Contains(change.EnemyId), $"任务变身敌人不存在 {change.EnemyId}");
                Ensure(actors.Contains(change.Actor), $"任务变身人物不存在 {change.Actor}");
                Ensure(quest.Goals.Any(goal => goal.Id == change.Goal && goal.Kind == "interact" &&
                                               goal.Target == change.Actor),
                    $"任务变身目标不匹配 {quest.Id}");
            }

            foreach (var target in quest.DescriptionTargets ?? [])
            {
                Ensure(actors.Contains(target.Target) || entities.Contains(target.Target),
                    $"任务说明人物不存在 {target.Target}");
                if (target.CharacterId is { Length: > 0 } characterId)
                    Ensure(characters.Contains(characterId), $"任务说明人物身份不存在 {characterId}");
            }

            foreach (var goal in quest.Goals)
            {
                if (goal.CharacterId is { Length: > 0 } characterId)
                    Ensure(characters.Contains(characterId), $"任务目标人物身份不存在 {characterId}");
                if (goal.Kind == "defeat") Ensure(enemies.Contains(goal.Target), $"任务敌人不存在 {goal.Target}");
                if (goal.Kind == "talk")
                    Ensure(actors.Contains(goal.Target) || entities.Contains(goal.Target), $"任务对话人物不存在 {goal.Target}");
            }
        }

        foreach (var trigger in campaign.EventTable.Triggers)
        {
            if (trigger.NearCharacterId is { Length: > 0 } characterId)
                Ensure(characters.Contains(characterId), $"近身人物身份不存在 {characterId}");
            Condition(trigger.Condition);
            if (trigger.Entity is { Length: > 0 } entity) Ensure(entities.Contains(entity), $"事件实体不存在 {entity}");
            if (trigger.Region is { } region && trigger.Map.Length > 0 && maps.TryGetValue(trigger.Map, out var map))
                Ensure(region.X < map.Width && region.Y < map.Height &&
                       region.X + region.Width > 0 && region.Y + region.Height > 0,
                    $"区域没有覆盖地图 {trigger.Id}");
        }

        foreach (var (scriptId, script) in campaign.Scripts)
        foreach (var node in script.Nodes)
        {
            if (node.Exit.Type == "branch") Condition(node.Exit.Condition);
            if (node.Exit.Type == "waitEncounter")
            {
                Ensure(node.Exit.Outcomes?.Won is not null, $"遭遇缺少胜利出口 {node.Id}");
                Encounter(ParseEncounter(node.Exit.Encounter, $"{scriptId}/{node.Id}"));
            }

            // 舞台第一幕的地图：`appear` 与 `mapFromStage` 的摆放会把实体写回这张图，
            // 跨图时会把原图的 NPC 抹掉，所以要求实体原本就在这张图。
            var stage = node.Presentation.FirstOrDefault(action => StoryType.Of(action) == "stage");
            var stageMap = stage.ValueKind == JsonValueKind.Object && !StoryType.Bool(stage, "mapFromWorld")
                ? StoryType.Text(stage, "map")
                : null;

            if (stageMap is { Length: > 0 })
                foreach (var action in node.Presentation)
                {
                    var type = StoryType.Of(action);
                    if (type == "appear" && StoryType.Text(action, "worldEntityId") is { Length: > 0 } entityId)
                        CheckEntityMap(entityId, stageMap, "出场实体跨越地图");
                    if (type == "world-object" && action.TryGetProperty("command", out var command) &&
                        StoryType.Text(command, "kind") == "place" && StoryType.Bool(command, "mapFromStage") &&
                        command.TryGetProperty("entity", out var entity))
                        CheckEntityMap(StoryType.Text(entity, "id"), stageMap, "演出摆放跨越地图");
                }

            foreach (var stageAction in node.Presentation.Where(action => StoryType.Of(action) == "stage"))
            {
                var map = StoryType.Text(stageAction, "map");
                var cast = Cast(stageAction);
                if (cast is null) continue;

                var bindings = new List<string>();
                foreach (var member in cast.Value.EnumerateArray())
                {
                    var enemyId = StoryType.Text(member, "enemyId");
                    if (enemyId.Length > 0) Ensure(enemies.Contains(enemyId), $"舞台敌人不存在 {enemyId}");

                    var worldEntityId = StoryType.Text(member, "worldEntityId");
                    if (worldEntityId.Length > 0) bindings.Add(worldEntityId);
                }

                Ensure(bindings.Count == bindings.Distinct(StringComparer.Ordinal).Count(),
                    $"同一舞台重复绑定世界实体 {node.Id}");

                foreach (var id in bindings)
                {
                    Ensure(entities.Contains(id), $"舞台世界实体不存在 {id}");
                    CheckEntityMap(id, map, "舞台绑定跨越地图");
                }
            }
        }

        foreach (var command in rawCommands)
        {
            var type = StoryType.Of(command);
            if (type == "talk")
            {
                var target = StoryType.Text(command, "target");
                Ensure(actors.Contains(target) || entities.Contains(target), $"对话命令人物不存在 {target}");
            }
            else if (type == "travel")
            {
                var destination = Destination(command);
                Point(StoryType.Text(destination, "map"), StoryType.Float(destination, "x"),
                    StoryType.Float(destination, "y"), "travel");
            }
            else if (type == "encounter")
            {
                Encounter(ParseEncounter(Nested(command, "encounter"), "命令"));
            }
        }

        foreach (var command in worldCommands)
        {
            switch (command)
            {
                case PlaceCommand place:
                    Ensure(place.Entity.Actor is { Length: > 0 } actor && actors.Contains(actor),
                        $"放置人物不存在 {place.Entity.Actor}");
                    if (!place.MapFromStage)
                        Point(place.Entity.Map, place.Entity.X, place.Entity.Y, place.Entity.Id);
                    break;
                case ToggleEntityCommand toggle:
                    Ensure(entities.Contains(toggle.Id) ||
                           (toggle.Id.StartsWith("quest-npc-located-", StringComparison.Ordinal) &&
                            actors.Contains(toggle.Id["quest-npc-located-".Length..])),
                        $"操作实体不存在 {toggle.Id}");
                    break;
                case EnemyAvailabilityCommand availability:
                    Ensure(enemies.Contains(availability.EnemyId), $"地图怪物不存在 {availability.EnemyId}");
                    break;
                case RemoveEnemyCommand remove:
                    Ensure(enemies.Contains(remove.EnemyId), $"移除敌人不存在 {remove.EnemyId}");
                    break;
                case ExitCommand exit:
                    Ensure(exits.ContainsKey(exit.Id), $"操作出口不存在 {exit.Id}");
                    break;
                case EnemyLocationCommand location:
                {
                    Ensure(enemies.Contains(location.EnemyId), $"登记敌人不存在 {location.EnemyId}");
                    if (location.Location is { } spot)
                        Point(spot.Map, spot.X, spot.Y, location.EnemyId);
                    break;
                }
                case ActorAppearanceCommand appearance:
                    Ensure(actors.Contains(appearance.Actor), $"外观人物不存在 {appearance.Actor}");
                    Ensure(actors.Contains(appearance.Appearance), $"替换外观不存在 {appearance.Appearance}");
                    break;
                case EncounterCommand encounterCommand:
                    Encounter(encounterCommand.Encounter);
                    break;
                case TravelCommand travel:
                    Point(travel.Destination.Map, travel.Destination.X, travel.Destination.Y, "travel");
                    break;
            }
        }
    }

    private static IEnumerable<ActorPlacement> Placements(CampaignContent campaign) =>
        campaign.PlacementTable.Concat(campaign.EntryPlacements);

    private static IEnumerable<JsonElement> Actions(CampaignContent campaign)
    {
        foreach (var script in campaign.Scripts.Values)
        foreach (var node in script.Nodes)
        foreach (var action in node.Presentation)
        foreach (var flat in Flatten(action))
            yield return flat;
    }

    private static IEnumerable<JsonElement> Flatten(JsonElement action)
    {
        if (StoryType.Of(action) != "parallel" ||
            !action.TryGetProperty("actions", out var actions) || actions.ValueKind != JsonValueKind.Array)
        {
            yield return action;
            yield break;
        }

        foreach (var child in actions.EnumerateArray())
        foreach (var flat in Flatten(child))
            yield return flat;
    }

    private static JsonElement? Cast(JsonElement stage) =>
        stage.TryGetProperty("cast", out var cast) && cast.ValueKind == JsonValueKind.Array ? cast : null;

    private static JsonElement Destination(JsonElement command) =>
        command.TryGetProperty("destination", out var destination) ? destination : default;

    private static JsonElement Nested(JsonElement command, string name) =>
        command.TryGetProperty(name, out var nested) ? nested : default;

    private static StoryEncounter? ParseEncounter(JsonElement raw, string owner)
    {
        if (raw.ValueKind != JsonValueKind.Object) return null;

        try
        {
            return raw.Deserialize<StoryEncounter>(SaveGameStore.JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"剧情引用无效：遭遇内容不完整 {owner}", exception);
        }
    }

    public static string StoryExitId(string mapId, MapExit exit) =>
        exit.Id ??
        string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{mapId}:{exit.Destination}:{exit.X}:{exit.Y}:{exit.Width}:{exit.Height}");

    private static void CollectWorldMap(JsonElement command, Action<string> add)
    {
        if (!command.TryGetProperty("command", out var body)) return;

        switch (StoryType.Text(body, "kind"))
        {
            case "place":
                if (body.TryGetProperty("entity", out var entity)) add(StoryType.Text(entity, "map"));
                break;
            case "remove-character":
            case "actor-visibility":
            case "enemy-availability":
                add(StoryType.Text(body, "map"));
                break;
            case "travel":
                add(StoryType.Text(Nested(body, "destination"), "map"));
                break;
            case "enemy-location":
                if (body.TryGetProperty("location", out var location) && location.ValueKind == JsonValueKind.Object)
                    add(StoryType.Text(location, "map"));
                break;
            case "encounter":
                add(StoryType.Text(Nested(body, "encounter"), "map"));
                break;
        }
    }

    private static string ScalarText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => string.Empty,
    };

    private sealed class CollectingSink(List<StoryWorldCommand> commands) : IWorldCommandSink
    {
        public void Apply(StoryWorldCommand command) => commands.Add(command);

        public void DropItem(string map, float x, float y, string item, int quantity)
        {
        }
    }
}
