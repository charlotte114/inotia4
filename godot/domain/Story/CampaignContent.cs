using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inotia4.Domain.Story;

public sealed class CampaignContent
{
    [JsonPropertyName("version")] public int Version { get; init; }

    [JsonPropertyName("start")] public CampaignStart Start { get; init; } = new();

    [JsonPropertyName("scripts")] public Dictionary<string, StoryScript> Scripts { get; init; } = new();

    [JsonPropertyName("quests")] public QuestDefinition[] Quests { get; init; } = [];

    private Dictionary<string, QuestDefinition>? _questIndex;

    public QuestDefinition? FindQuest(string id)
    {
        _questIndex ??= BuildQuestIndex();
        return _questIndex.GetValueOrDefault(id);
    }

    private Dictionary<string, QuestDefinition> BuildQuestIndex()
    {
        var index = new Dictionary<string, QuestDefinition>(Quests.Length);
        foreach (var quest in Quests) index[quest.Id] = quest;
        return index;
    }

    [JsonPropertyName("questGroups")] public JsonElement QuestGroups { get; init; }

    private List<QuestGroupDefinition>? _groupTable;

    public IReadOnlyList<QuestGroupDefinition> GroupTable
    {
        get
        {
            if (_groupTable is not null) return _groupTable;

            _groupTable = [];
            if (QuestGroups.ValueKind == JsonValueKind.Array)
            {
                foreach (var group in QuestGroups.EnumerateArray())
                {
                    var id = StoryType.Text(group, "id");
                    if (id.Length == 0) continue;
                    if (!group.TryGetProperty("questIds", out var ids) || ids.ValueKind != JsonValueKind.Array)
                        continue;

                    var members = new List<string>();
                    foreach (var member in ids.EnumerateArray())
                        if (member.ValueKind == JsonValueKind.String)
                            members.Add(member.GetString()!);

                    _groupTable.Add(new QuestGroupDefinition(
                        id, StoryType.Text(group, "title"), StoryType.Text(group, "kind"), [.. members]));
                }
            }

            return _groupTable;
        }
    }

    private HashSet<string>? _sideQuestIds;

    public IReadOnlySet<string> SideQuestIds
    {
        get
        {
            if (_sideQuestIds is not null) return _sideQuestIds;

            _sideQuestIds = [];
            if (QuestGroups.ValueKind == JsonValueKind.Array)
            {
                foreach (var group in QuestGroups.EnumerateArray())
                {
                    if (StoryType.Text(group, "kind") != "side") continue;
                    if (!group.TryGetProperty("questIds", out var ids) ||
                        ids.ValueKind != JsonValueKind.Array) continue;

                    foreach (var id in ids.EnumerateArray())
                        if (id.ValueKind == JsonValueKind.String)
                            _sideQuestIds.Add(id.GetString()!);
                }
            }

            return _sideQuestIds;
        }
    }

    private Dictionary<string, string>? _questKinds;

    public string KindOfQuest(string questId)
    {
        if (_questKinds is null)
        {
            _questKinds = new Dictionary<string, string>();
            if (QuestGroups.ValueKind == JsonValueKind.Array)
            {
                foreach (var group in QuestGroups.EnumerateArray())
                {
                    var kind = StoryType.Text(group, "kind");
                    if (!group.TryGetProperty("questIds", out var ids) || ids.ValueKind != JsonValueKind.Array)
                        continue;

                    foreach (var id in ids.EnumerateArray())
                        if (id.ValueKind == JsonValueKind.String)
                            _questKinds.TryAdd(id.GetString()!, kind);
                }
            }
        }

        return _questKinds.GetValueOrDefault(questId, "side");
    }

    [JsonPropertyName("events")] public JsonElement[] Events { get; init; } = [];

    [JsonPropertyName("debugStory")] public StoryDebugCatalog? DebugStory { get; init; }

    private StoryEventTable? _eventTable;

    public StoryEventTable EventTable => _eventTable ??= new StoryEventTable(Events);

    [JsonPropertyName("placements")] public JsonElement[] Placements { get; init; } = [];

    [JsonPropertyName("mapEntryPlacements")] public JsonElement MapEntryPlacements { get; init; }

    private ActorPlacement[]? _entryTable;

    public IReadOnlyList<ActorPlacement> EntryPlacements
    {
        get
        {
            if (_entryTable is not null) return _entryTable;
            _entryTable = ParsePlacements(MapEntryPlacements);
            return _entryTable;
        }
    }

    private ActorPlacement[]? _placementTable;

    public IReadOnlyList<ActorPlacement> PlacementTable
    {
        get
        {
            if (_placementTable is not null) return _placementTable;

            _placementTable = ParsePlacements(Placements.AsEnumerable());
            return _placementTable;
        }
    }

    private static ActorPlacement[] ParsePlacements(JsonElement raw) =>
        raw.ValueKind == JsonValueKind.Array ? ParsePlacements(raw.EnumerateArray()) : [];

    private static ActorPlacement[] ParsePlacements(IEnumerable<JsonElement> elements)
    {
        var list = new List<ActorPlacement>();
        foreach (var element in elements)
        {
            if (element.ValueKind != JsonValueKind.Object) continue;

            var placement = new ActorPlacement
            {
                Id = StoryType.Text(element, "id"),
                Actor = StoryType.Text(element, "actor"),
                CharacterId = element.TryGetProperty("characterId", out var characterId) &&
                              characterId.ValueKind == JsonValueKind.String
                    ? characterId.GetString()
                    : null,
                Map = StoryType.Text(element, "map"),
                X = StoryType.Float(element, "x"),
                Y = StoryType.Float(element, "y"),
                Facing = element.TryGetProperty("facing", out var facing) &&
                         facing.ValueKind == JsonValueKind.String
                    ? facing.GetString()
                    : null,
                AfterInteractScript = element.TryGetProperty("afterInteractScript", out var afterInteract) &&
                                      afterInteract.ValueKind == JsonValueKind.String
                    ? afterInteract.GetString()
                    : null,
                Condition = element.TryGetProperty("condition", out var condition) ? condition : default,
            };

            if (placement.Actor.Length > 0 && placement.Map.Length > 0) list.Add(placement);
        }

        return list.ToArray();
    }

    public StoryScript GetScript(string id) =>
        Scripts.TryGetValue(id, out var script)
            ? script
            : throw new KeyNotFoundException($"剧本不存在：{id}");
}

public sealed class CampaignStart
{
    [JsonPropertyName("map")] public string Map { get; init; } = string.Empty;

    [JsonPropertyName("x")] public float X { get; init; }

    [JsonPropertyName("y")] public float Y { get; init; }

    [JsonPropertyName("actor")] public string Actor { get; init; } = string.Empty;

    [JsonPropertyName("facing")] public string Facing { get; init; } = "south";

    [JsonPropertyName("script")] public string? Script { get; init; }
}

public sealed class StoryScript
{
    [JsonPropertyName("entry")] public string Entry { get; init; } = string.Empty;

    [JsonPropertyName("nodes")] public StoryNode[] Nodes { get; init; } = [];
}

public sealed class StoryNode
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;

    [JsonPropertyName("presentation")] public JsonElement[] Presentation { get; init; } = [];

    [JsonPropertyName("commands")] public JsonElement[] Commands { get; init; } = [];

    [JsonPropertyName("exit")] public StoryExit Exit { get; init; } = new();
}

public sealed class StoryExit
{
    [JsonPropertyName("type")] public string Type { get; init; } = "end";

    [JsonPropertyName("target")] public StoryTarget? Target { get; init; }

    [JsonPropertyName("condition")] public JsonElement Condition { get; init; }

    [JsonPropertyName("then")] public StoryTarget? Then { get; init; }

    [JsonPropertyName("otherwise")] public StoryTarget? Otherwise { get; init; }

    [JsonPropertyName("choice")] public string? Choice { get; init; }

    [JsonPropertyName("cases")] public Dictionary<string, StoryTarget> Cases { get; init; } = new();

    [JsonPropertyName("encounter")] public JsonElement Encounter { get; init; }

    [JsonPropertyName("outcomes")] public EncounterOutcomes? Outcomes { get; init; }

    [JsonPropertyName("branches")] public JsonElement[] Branches { get; init; } = [];
}

public sealed class StoryTarget
{
    [JsonPropertyName("script")] public string? Script { get; init; }

    [JsonPropertyName("node")] public string? Node { get; init; }
}

public sealed record QuestGroupDefinition(string Id, string Title, string Kind, string[] QuestIds);

public sealed class QuestGuidance
{
    [JsonPropertyName("available")] public string Available { get; init; } = string.Empty;

    [JsonPropertyName("active")] public string Active { get; init; } = string.Empty;

    [JsonPropertyName("counterLabel")] public string? CounterLabel { get; init; }
}

public sealed class QuestDescriptionTarget
{
    [JsonPropertyName("target")] public string Target { get; init; } = string.Empty;

    [JsonPropertyName("characterId")] public string? CharacterId { get; init; }

    [JsonPropertyName("label")] public string? Label { get; init; }
}

public sealed class QuestTargetChange
{
    [JsonPropertyName("goal")] public string Goal { get; init; } = string.Empty;

    [JsonPropertyName("enemyId")] public string EnemyId { get; init; } = string.Empty;

    [JsonPropertyName("actor")] public string Actor { get; init; } = string.Empty;

    [JsonPropertyName("removeAfterInteraction")] public bool RemoveAfterInteraction { get; init; }

    [JsonPropertyName("changedText")] public string ChangedText { get; init; } = string.Empty;

    [JsonPropertyName("interactText")] public string InteractText { get; init; } = string.Empty;
}

public sealed class QuestDefinition
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;

    [JsonPropertyName("title")] public string Title { get; init; } = string.Empty;

    [JsonPropertyName("description")] public string Description { get; init; } = string.Empty;

    [JsonPropertyName("delivery")] public string? Delivery { get; init; }

    [JsonPropertyName("deliveryCharacterId")] public string? DeliveryCharacterId { get; init; }

    [JsonPropertyName("giver")] public string? Giver { get; init; }

    [JsonPropertyName("giverCharacterId")] public string? GiverCharacterId { get; init; }

    [JsonPropertyName("offerScript")] public string? OfferScript { get; init; }

    [JsonPropertyName("completeScript")] public string? CompleteScript { get; init; }

    [JsonPropertyName("progressText")] public string? ProgressText { get; init; }

    [JsonPropertyName("guidance")] public QuestGuidance? Guidance { get; init; }

    [JsonPropertyName("descriptionTargets")] public QuestDescriptionTarget[]? DescriptionTargets { get; init; }

    [JsonPropertyName("hidden")] public bool Hidden { get; init; }

    /// <summary>
    /// 可接取条件：条件满足了任务才从「锁定」转成「可接」，
    /// 没满足的任务不该出现在 NPC 的任务槽里；求值在任务簿刷新时走 <c>StoryCondition</c>。
    /// </summary>
    [JsonPropertyName("availableWhen")] public JsonElement AvailableWhen { get; init; }

    [JsonPropertyName("goals")] public QuestGoal[] Goals { get; init; } = [];

    [JsonPropertyName("targetChanges")] public QuestTargetChange[]? TargetChanges { get; init; }

    [JsonPropertyName("rewards")] public QuestRewards? Rewards { get; init; }

    [JsonPropertyName("onAcceptItems")] public QuestHandover[] OnAcceptItems { get; init; } = [];

    [JsonPropertyName("handoverItems")] public QuestHandover[] HandoverItems { get; init; } = [];

    [JsonPropertyName("completion")] public string Completion { get; init; } = "all";

    [JsonPropertyName("repeat")] public bool Repeat { get; init; }

    [JsonPropertyName("places")] public JsonElement Places { get; init; }

    [JsonPropertyName("itemCompletion")] public string ItemCompletion { get; init; } = "all";
}

public sealed class QuestGoal
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;

    [JsonPropertyName("kind")] public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("target")] public string Target { get; init; } = string.Empty;

    /// <summary>
    /// 目标与事实都写了它时按它比对（同一个 actor 摆多处，只有一处算数）；
    /// 否则按 <see cref="Target"/> 比对。
    /// </summary>
    [JsonPropertyName("characterId")] public string? CharacterId { get; init; }

    [JsonPropertyName("quantity")] public int Quantity { get; init; } = 1;

    [JsonPropertyName("label")] public string? Label { get; init; }
}

public sealed class QuestHandover
{
    [JsonPropertyName("item")] public string Item { get; init; } = string.Empty;

    [JsonPropertyName("quantity")] public int Quantity { get; init; } = 1;

    [JsonPropertyName("removal")] public string Removal { get; init; } = "exact";
}

public sealed class QuestRewards
{
    [JsonPropertyName("experience")] public int Experience { get; init; }

    [JsonPropertyName("money")] public int Money { get; init; }

    [JsonPropertyName("items")] public QuestItem[] Items { get; init; } = [];
}

public sealed class QuestItem
{
    [JsonPropertyName("item")] public string Item { get; init; } = string.Empty;

    [JsonPropertyName("quantity")] public int Quantity { get; init; } = 1;

    [JsonPropertyName("classIds")] public string[] ClassIds { get; init; } = [];
}
