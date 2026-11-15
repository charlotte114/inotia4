using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inotia4.Domain.Story;

public sealed class StoryDebugCatalog
{
    [JsonPropertyName("schema")] public int? Schema { get; init; }

    [JsonPropertyName("meta")] public StoryDebugMeta? Meta { get; init; }

    [JsonPropertyName("tasks")] public StoryDebugTask[] Tasks { get; init; } = [];

    [JsonPropertyName("gaps")] public StoryDebugGap[]? Gaps { get; init; }
}

public sealed class StoryDebugMeta
{
    [JsonPropertyName("generator")] public string? Generator { get; init; }

    [JsonPropertyName("interpreterVersion")] public int InterpreterVersion { get; init; }

    [JsonPropertyName("semanticsVersion")] public int SemanticsVersion { get; init; }

    [JsonPropertyName("sourceHash")] public string SourceHash { get; init; } = string.Empty;

    [JsonPropertyName("generatedAt")] public string GeneratedAt { get; init; } = string.Empty;

    [JsonPropertyName("sources")] public Dictionary<string, string>? Sources { get; init; }

    [JsonPropertyName("coverage")] public StoryDebugCoverage? Coverage { get; init; }
}

public sealed class StoryDebugCoverage
{
    [JsonPropertyName("eligible")] public int Eligible { get; init; }

    [JsonPropertyName("verified")] public int Verified { get; init; }

    [JsonPropertyName("gaps")] public int Gaps { get; init; }

    [JsonPropertyName("excludedNoIntake")] public int ExcludedNoIntake { get; init; }

    [JsonPropertyName("excludedUnreachableEvents")] public int ExcludedUnreachableEvents { get; init; }

    [JsonPropertyName("unreachableNodes")] public int UnreachableNodes { get; init; }

    [JsonPropertyName("byGapKind")] public Dictionary<string, int>? ByGapKind { get; init; }
}

public sealed class StoryDebugTask
{
    [JsonPropertyName("quest")] public string Quest { get; init; } = string.Empty;

    [JsonPropertyName("phase")] public string Phase { get; init; } = string.Empty;

    [JsonPropertyName("entry")] public StoryDebugEntry Entry { get; init; } = new();

    [JsonPropertyName("scripts")] public string[]? Scripts { get; init; }

    [JsonPropertyName("start")] public StoryDebugStartTable? Start { get; init; }

    [JsonPropertyName("play")] public StoryDebugPlay? Play { get; init; }

    [JsonPropertyName("accept")] public StoryDebugAccept? Accept { get; init; }

    [JsonPropertyName("sourceHash")] public string? SourceHash { get; init; }

    [JsonPropertyName("basis")] public JsonElement? Basis { get; init; }
}

public sealed class StoryDebugEntry
{
    [JsonPropertyName("type")] public string Type { get; init; } = "fixed";

    [JsonPropertyName("map")] public string Map { get; init; } = string.Empty;

    [JsonPropertyName("x")] public double X { get; init; }

    [JsonPropertyName("y")] public double Y { get; init; }

    [JsonPropertyName("source")] public string Source { get; init; } = string.Empty;
}

public sealed class StoryDebugAccept
{
    [JsonPropertyName("event")] public string? Event { get; init; }

    [JsonPropertyName("script")] public string? Script { get; init; }

    [JsonPropertyName("node")] public string? Node { get; init; }

    [JsonPropertyName("actor")] public string? Actor { get; init; }

    [JsonPropertyName("giver")] public string? Giver { get; init; }
}

public sealed class StoryDebugPlay
{
    [JsonPropertyName("script")] public string Script { get; init; } = string.Empty;

    [JsonPropertyName("node")] public string? Node { get; init; }

    [JsonPropertyName("event")] public string? Event { get; init; }

    [JsonPropertyName("cut")] public string Cut { get; init; } = string.Empty;
}

public sealed class StoryDebugStartTable
{
    [JsonPropertyName("quests")] public StoryDebugStartQuest[]? Quests { get; init; }

    [JsonPropertyName("running")] public string[]? Running { get; init; }

    [JsonPropertyName("world")] public StoryDebugWorld? World { get; init; }

    [JsonPropertyName("items")] public StoryDebugItem[]? Items { get; init; }

    [JsonPropertyName("itemReset")] public string[]? ItemReset { get; init; }

    [JsonPropertyName("party")] public StoryDebugParty? Party { get; init; }

    [JsonPropertyName("completedEvents")] public StoryDebugCompletedEvent[]? CompletedEvents { get; init; }

    [JsonPropertyName("occurrenceSequences")] public JsonElement[]? OccurrenceSequences { get; init; }

    [JsonPropertyName("factSequences")] public JsonElement[]? FactSequences { get; init; }

    [JsonPropertyName("requiresProfile")] public StoryDebugRequiresProfile? RequiresProfile { get; init; }
}

public sealed class StoryDebugStartQuest
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;

    [JsonPropertyName("status")] public string Status { get; init; } = string.Empty;

    [JsonPropertyName("cycle")] public int? Cycle { get; init; }

    [JsonPropertyName("completedCycles")] public int? CompletedCycles { get; init; }

    [JsonPropertyName("goals")] public Dictionary<string, int>? Goals { get; init; }

    [JsonPropertyName("e")] public int? E { get; init; }
}

public sealed class StoryDebugWorld
{
    [JsonPropertyName("location")] public StoryDebugLocation Location { get; init; } = new();

    [JsonPropertyName("entities")] public StoryDebugEntity[]? Entities { get; init; }

    [JsonPropertyName("exits")] public JsonElement[]? Exits { get; init; }

    [JsonPropertyName("flags")] public string[]? Flags { get; init; }

    [JsonPropertyName("choices")] public JsonElement[]? Choices { get; init; }

    [JsonPropertyName("encounters")] public StoryDebugEncounter[]? Encounters { get; init; }

    [JsonPropertyName("mapVisit")] public int? MapVisit { get; init; }

    [JsonPropertyName("enemyRemovals")] public StoryDebugEnemyRemoval[]? EnemyRemovals { get; init; }

    [JsonPropertyName("enemyLocations")] public StoryDebugEnemyLocation[]? EnemyLocations { get; init; }

    [JsonPropertyName("appearances")] public JsonElement[]? Appearances { get; init; }

    [JsonPropertyName("partyBuffs")] public StoryDebugPartyBuff[]? PartyBuffs { get; init; }

    [JsonPropertyName("absentActors")] public StoryDebugAbsentActor[]? AbsentActors { get; init; }

    [JsonPropertyName("absentEnemies")] public StoryDebugAbsentEnemy[]? AbsentEnemies { get; init; }
}

public sealed class StoryDebugLocation
{
    [JsonPropertyName("map")] public string Map { get; init; } = string.Empty;

    [JsonPropertyName("x")] public double X { get; init; }

    [JsonPropertyName("y")] public double Y { get; init; }

    [JsonPropertyName("facing")] public string Facing { get; init; } = "south";
}

public sealed class StoryDebugEntity
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;

    [JsonPropertyName("map")] public string? Map { get; init; }

    [JsonPropertyName("actor")] public string? Actor { get; init; }

    [JsonPropertyName("characterId")] public string? CharacterId { get; init; }

    [JsonPropertyName("authority")] public string? Authority { get; init; }

    [JsonPropertyName("mapVisit")] public int? MapVisit { get; init; }

    [JsonPropertyName("x")] public double? X { get; init; }

    [JsonPropertyName("y")] public double? Y { get; init; }

    [JsonPropertyName("facing")] public string? Facing { get; init; }

    [JsonPropertyName("visible")] public bool? Visible { get; init; }

    [JsonPropertyName("removed")] public bool? Removed { get; init; }

    [JsonPropertyName("hint")] public string? Hint { get; init; }
}

public sealed class StoryDebugEncounter
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;

    [JsonPropertyName("definitionId")] public string? DefinitionId { get; init; }

    [JsonPropertyName("map")] public string Map { get; init; } = string.Empty;

    [JsonPropertyName("mapFromStage")] public bool? MapFromStage { get; init; }

    [JsonPropertyName("status")] public string Status { get; init; } = string.Empty;

    [JsonPropertyName("spawns")] public StoryDebugSpawn[]? Spawns { get; init; }
}

public sealed class StoryDebugSpawn
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;

    [JsonPropertyName("enemyId")] public string EnemyId { get; init; } = string.Empty;

    [JsonPropertyName("x")] public double X { get; init; }

    [JsonPropertyName("y")] public double Y { get; init; }
}

public sealed class StoryDebugEnemyRemoval
{
    [JsonPropertyName("enemyId")] public string EnemyId { get; init; } = string.Empty;

    [JsonPropertyName("map")] public string? Map { get; init; }

    [JsonPropertyName("all")] public bool All { get; init; }
}

public sealed class StoryDebugEnemyLocation
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;

    [JsonPropertyName("enemyId")] public string EnemyId { get; init; } = string.Empty;

    [JsonPropertyName("map")] public string Map { get; init; } = string.Empty;

    [JsonPropertyName("x")] public double X { get; init; }

    [JsonPropertyName("y")] public double Y { get; init; }

    [JsonPropertyName("mapVisit")] public int? MapVisit { get; init; }

    [JsonPropertyName("respawn")] public bool? Respawn { get; init; }

    [JsonPropertyName("mapFromStage")] public bool? MapFromStage { get; init; }
}

public sealed class StoryDebugPartyBuff
{
    [JsonPropertyName("buff")] public string Buff { get; init; } = string.Empty;

    [JsonPropertyName("strength")] public int Strength { get; init; }
}

public sealed class StoryDebugAbsentActor
{
    [JsonPropertyName("map")] public string Map { get; init; } = string.Empty;

    [JsonPropertyName("actor")] public string Actor { get; init; } = string.Empty;

    [JsonPropertyName("characterId")] public string? CharacterId { get; init; }
}

public sealed class StoryDebugAbsentEnemy
{
    [JsonPropertyName("map")] public string Map { get; init; } = string.Empty;

    [JsonPropertyName("enemyId")] public string EnemyId { get; init; } = string.Empty;
}

public sealed class StoryDebugItem
{
    [JsonPropertyName("item")] public string Item { get; init; } = string.Empty;

    [JsonPropertyName("quantity")] public int Quantity { get; init; }

    [JsonPropertyName("e")] public int? E { get; init; }
}

public sealed class StoryDebugParty
{
    [JsonPropertyName("join")] public string[]? Join { get; init; }

    [JsonPropertyName("leave")] public string[]? Leave { get; init; }

    [JsonPropertyName("control")] public string? Control { get; init; }
}

public sealed class StoryDebugCompletedEvent
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;

    [JsonPropertyName("e")] public int? E { get; init; }
}

public sealed class StoryDebugRequiresProfile
{
    [JsonPropertyName("items")] public StoryDebugItem[]? Items { get; init; }

    [JsonPropertyName("money")] public int? Money { get; init; }
}

/// <summary>证据不足、不能进入调试目录的任务。</summary>
public sealed class StoryDebugGap
{
    [JsonPropertyName("quest")] public string Quest { get; init; } = string.Empty;

    [JsonPropertyName("title")] public string? Title { get; init; }

    [JsonPropertyName("kind")] public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("detail")] public string Detail { get; init; } = string.Empty;

    [JsonPropertyName("evidence")] public string? Evidence { get; init; }

    [JsonPropertyName("fixHint")] public string? FixHint { get; init; }
}

public static class StoryDebug
{
    private static readonly string[] QuestStatuses = ["locked", "available", "active", "ready", "completed"];
    private static readonly string[] RunningStatuses = ["active", "ready"];
    private static readonly string[] PlayCuts = ["before-opening", "after-accept", "event-end"];
    private static readonly string[] Facings = ["north", "south", "east", "west"];
    private static readonly string[] EncounterStatuses = ["prepared", "active", "won", "lost"];

    public static void CheckShape(JsonElement value, string path)
    {
        CheckFields(value, path, new Dictionary<string, Action<JsonElement, string>>
        {
            ["schema"] = Optional((element, elementPath) =>
                Require(element.ValueKind == JsonValueKind.Number && element.GetInt32() == 1, elementPath, "需要版本 1")),
            ["meta"] = Optional(CheckMeta),
            ["tasks"] = Required(CheckArray(CheckTask)),
            ["gaps"] = Optional(CheckArray(CheckGap)),
        });
    }

    private static void CheckTask(JsonElement value, string path)
    {
        CheckFields(value, path, new Dictionary<string, Action<JsonElement, string>>
        {
            ["quest"] = Required(TextField),
            ["phase"] = Required(OneOf("available", "active")),
            ["entry"] = Required(CheckEntry),
            ["scripts"] = Optional(CheckStrings),
            ["start"] = Optional(CheckStart),
            ["play"] = Optional(CheckPlay),
            ["accept"] = Optional(CheckAccept),
            ["sourceHash"] = Optional(TextField),
            ["basis"] = Optional((element, elementPath) =>
                Require(IsObject(element), elementPath, "需要对象")),
        });
    }

    private static void CheckEntry(JsonElement value, string path)
    {
        CheckFields(value, path, new Dictionary<string, Action<JsonElement, string>>
        {
            ["type"] = Required(OneOf("fixed")),
            ["map"] = Required(TextField),
            ["x"] = Required(NumberField),
            ["y"] = Required(NumberField),
            ["source"] = Required(TextField),
        });
    }

    private static void CheckStart(JsonElement value, string path)
    {
        CheckFields(value, path, new Dictionary<string, Action<JsonElement, string>>
        {
            ["quests"] = Optional(CheckArray((element, elementPath) => CheckFields(element, elementPath,
                new Dictionary<string, Action<JsonElement, string>>
                {
                    ["id"] = Required(TextField),
                    ["status"] = Required(OneOf(QuestStatuses)),
                    ["cycle"] = Optional(IntegerField),
                    ["completedCycles"] = Optional(IntegerField),
                    ["goals"] = Optional(CheckRecord(IntegerField)),
                    ["e"] = Optional(IntegerField),
                }))),
            ["running"] = Optional(CheckStrings),
            ["world"] = Optional(CheckWorld),
            ["items"] = Optional(CheckArray((element, elementPath) => CheckFields(element, elementPath,
                new Dictionary<string, Action<JsonElement, string>>
                {
                    ["item"] = Required(TextField),
                    ["quantity"] = Required(CountField),
                    ["e"] = Optional(IntegerField),
                }))),
            ["itemReset"] = Optional(CheckStrings),
            ["party"] = Optional((element, elementPath) => CheckFields(element, elementPath,
                new Dictionary<string, Action<JsonElement, string>>
                {
                    ["join"] = Optional(CheckStrings),
                    ["leave"] = Optional(CheckStrings),
                    ["control"] = Optional(TextField),
                })),
            ["completedEvents"] = Optional(CheckArray((element, elementPath) => CheckFields(element, elementPath,
                new Dictionary<string, Action<JsonElement, string>>
                {
                    ["id"] = Required(TextField),
                    ["e"] = Optional(IntegerField),
                }))),
            ["occurrenceSequences"] = Optional(CheckSequence),
            ["factSequences"] = Optional(CheckSequence),
            ["requiresProfile"] = Optional((element, elementPath) => CheckFields(element, elementPath,
                new Dictionary<string, Action<JsonElement, string>>
                {
                    ["items"] = Optional(CheckArray((inner, innerPath) => CheckFields(inner, innerPath,
                        new Dictionary<string, Action<JsonElement, string>>
                        {
                            ["item"] = Required(TextField),
                            ["quantity"] = Required(CountField),
                            ["e"] = Optional(IntegerField),
                        }))),
                    ["money"] = Optional(IntegerField),
                })),
        });
    }

    private static void CheckWorld(JsonElement value, string path)
    {
        CheckFields(value, path, new Dictionary<string, Action<JsonElement, string>>
        {
            ["location"] = Required((element, elementPath) => CheckFields(element, elementPath,
                new Dictionary<string, Action<JsonElement, string>>
                {
                    ["map"] = Required(TextField),
                    ["x"] = Required(NumberField),
                    ["y"] = Required(NumberField),
                    ["facing"] = Required(OneOf(Facings)),
                })),
            ["entities"] = Optional(CheckArray((element, elementPath) => CheckFields(element, elementPath,
                new Dictionary<string, Action<JsonElement, string>>
                {
                    ["id"] = Required(TextField),
                    ["map"] = Optional(TextField),
                    ["actor"] = Optional(TextField),
                    ["characterId"] = Optional(TextField),
                    ["authority"] = Optional(OneOf("registered", "npc", "story", "static")),
                    ["mapVisit"] = Optional(IntegerField),
                    ["x"] = Optional(NumberField),
                    ["y"] = Optional(NumberField),
                    ["facing"] = Optional(OneOf(Facings)),
                    ["visible"] = Optional(BooleanField),
                    ["removed"] = Optional(BooleanField),
                    ["hint"] = Optional(TextField),
                }))),
            ["exits"] = Optional(CheckArray(Pair(TextField, BooleanField))),
            ["flags"] = Optional(CheckStrings),
            ["choices"] = Optional(CheckArray(Pair(TextField, TextField))),
            ["encounters"] = Optional(CheckArray((element, elementPath) => CheckFields(element, elementPath,
                new Dictionary<string, Action<JsonElement, string>>
                {
                    ["id"] = Required(TextField),
                    ["definitionId"] = Optional(TextField),
                    ["map"] = Required(TextField),
                    ["mapFromStage"] = Optional(BooleanField),
                    ["status"] = Required(OneOf(EncounterStatuses)),
                    ["spawns"] = Required(CheckArray((spawn, spawnPath) => CheckFields(spawn, spawnPath,
                        new Dictionary<string, Action<JsonElement, string>>
                        {
                            ["id"] = Required(TextField),
                            ["enemyId"] = Required(TextField),
                            ["x"] = Required(NumberField),
                            ["y"] = Required(NumberField),
                        }))),
                }))),
            ["mapVisit"] = Optional(IntegerField),
            ["enemyRemovals"] = Optional(CheckArray((element, elementPath) => CheckFields(element, elementPath,
                new Dictionary<string, Action<JsonElement, string>>
                {
                    ["enemyId"] = Required(TextField),
                    ["map"] = Optional(TextField),
                    ["all"] = Required(BooleanField),
                }))),
            ["enemyLocations"] = Optional(CheckArray((element, elementPath) => CheckFields(element, elementPath,
                new Dictionary<string, Action<JsonElement, string>>
                {
                    ["id"] = Required(TextField),
                    ["enemyId"] = Required(TextField),
                    ["map"] = Required(TextField),
                    ["x"] = Required(NumberField),
                    ["y"] = Required(NumberField),
                    ["mapVisit"] = Optional(IntegerField),
                    ["respawn"] = Optional((inner, innerPath) =>
                        Require(inner.ValueKind == JsonValueKind.False, innerPath, "只能为 false")),
                    ["mapFromStage"] = Optional(BooleanField),
                }))),
            ["appearances"] = Optional(CheckArray(Pair(TextField, TextField))),
            ["partyBuffs"] = Optional(CheckArray((element, elementPath) => CheckFields(element, elementPath,
                new Dictionary<string, Action<JsonElement, string>>
                {
                    ["buff"] = Required(TextField),
                    ["strength"] = Required(IntegerField),
                }))),
            ["absentActors"] = Optional(CheckArray((element, elementPath) => CheckFields(element, elementPath,
                new Dictionary<string, Action<JsonElement, string>>
                {
                    ["map"] = Required(TextField),
                    ["actor"] = Required(TextField),
                    ["characterId"] = Optional(TextField),
                }))),
            ["absentEnemies"] = Optional(CheckArray((element, elementPath) => CheckFields(element, elementPath,
                new Dictionary<string, Action<JsonElement, string>>
                {
                    ["map"] = Required(TextField),
                    ["enemyId"] = Required(TextField),
                }))),
        });
    }

    private static void CheckPlay(JsonElement value, string path)
    {
        CheckFields(value, path, new Dictionary<string, Action<JsonElement, string>>
        {
            ["script"] = Required(TextField),
            ["node"] = Optional(TextField),
            ["event"] = Optional(TextField),
            ["cut"] = Required(OneOf(PlayCuts)),
        });
    }

    private static void CheckAccept(JsonElement value, string path)
    {
        CheckFields(value, path, new Dictionary<string, Action<JsonElement, string>>
        {
            ["event"] = Optional(TextField),
            ["script"] = Optional(TextField),
            ["node"] = Optional(TextField),
            ["actor"] = Optional(TextField),
            ["giver"] = Optional(TextField),
        });
    }

    private static void CheckGap(JsonElement value, string path)
    {
        CheckFields(value, path, new Dictionary<string, Action<JsonElement, string>>
        {
            ["quest"] = Required(TextField),
            ["title"] = Optional(TextField),
            ["kind"] = Required(TextField),
            ["detail"] = Required(TextField),
            ["evidence"] = Optional(TextField),
            ["fixHint"] = Optional(TextField),
        });
    }

    private static void CheckMeta(JsonElement value, string path)
    {
        CheckFields(value, path, new Dictionary<string, Action<JsonElement, string>>
        {
            ["generator"] = Optional(TextField),
            ["interpreterVersion"] = Required(PositiveField),
            ["semanticsVersion"] = Required(PositiveField),
            ["sourceHash"] = Required(TextField),
            ["generatedAt"] = Required(TextField),
            ["sources"] = Optional(CheckRecord(TextField)),
            ["coverage"] = Required((element, elementPath) => CheckFields(element, elementPath,
                new Dictionary<string, Action<JsonElement, string>>
                {
                    ["eligible"] = Required(IntegerField),
                    ["verified"] = Required(IntegerField),
                    ["gaps"] = Required(IntegerField),
                    ["excludedNoIntake"] = Required(IntegerField),
                    ["excludedUnreachableEvents"] = Required(IntegerField),
                    ["unreachableNodes"] = Required(IntegerField),
                    ["byGapKind"] = Required(CheckRecord(IntegerField)),
                })),
        });
    }

    private static void Require(bool condition, string path, string message)
    {
        if (!condition) throw new InvalidDataException($"内容无效 {path}: {message}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static Action<JsonElement, string> Required(Action<JsonElement, string> check) => check;

    private static Action<JsonElement, string> Optional(Action<JsonElement, string> check) =>
        (element, path) =>
        {
            if (element.ValueKind != JsonValueKind.Undefined) check(element, path);
        };

    private static Action<JsonElement, string> CheckArray(Action<JsonElement, string> check) =>
        (element, path) =>
        {
            Require(element.ValueKind == JsonValueKind.Array, path, "需要数组");
            var index = 0;
            foreach (var item in element.EnumerateArray()) check(item, $"{path}[{index++}]");
        };

    private static Action<JsonElement, string> CheckRecord(Action<JsonElement, string> check) =>
        (element, path) =>
        {
            Require(IsObject(element), path, "需要对象");
            foreach (var property in element.EnumerateObject())
                check(property.Value, $"{path}.{property.Name}");
        };

    private static void CheckFields(
        JsonElement value, string path, IReadOnlyDictionary<string, Action<JsonElement, string>> fields)
    {
        Require(IsObject(value), path, "需要对象");
        foreach (var property in value.EnumerateObject())
            Require(fields.ContainsKey(property.Name), $"{path}.{property.Name}", "未定义字段");
        foreach (var field in fields)
            fields[field.Key](TryGet(value, field.Key), $"{path}.{field.Key}");
    }

    private static Action<JsonElement, string> OneOf(params string[] values) =>
        (element, path) => Require(
            element.ValueKind == JsonValueKind.String && values.Contains(element.GetString()), path,
            $"需要 {string.Join('/', values)}");

    private static Action<JsonElement, string> Pair(
        Action<JsonElement, string> first, Action<JsonElement, string> second) =>
        (element, path) =>
        {
            Require(element.ValueKind == JsonValueKind.Array && element.GetArrayLength() == 2, path, "需要二元组");
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                (index == 0 ? first : second)(item, $"{path}[{index}]");
                index++;
            }
        };

    private static Action<JsonElement, string> CheckSequence => CheckArray(Pair(TextField, CountField));

    private static Action<JsonElement, string> CheckStrings => CheckArray(TextField);

    private static void TextField(JsonElement value, string path) =>
        Require(value.ValueKind == JsonValueKind.String, path, "需要文字");

    private static void BooleanField(JsonElement value, string path) =>
        Require(value.ValueKind is JsonValueKind.True or JsonValueKind.False, path, "需要布尔值");

    private static void NumberField(JsonElement value, string path) =>
        Require(value.ValueKind == JsonValueKind.Number, path, "需要有限数字");

    private static void IntegerField(JsonElement value, string path)
    {
        NumberField(value, path);
        var number = value.GetDouble();
        Require(number == Math.Floor(number), path, "需要非负整数");
    }

    private static void CountField(JsonElement value, string path)
    {
        IntegerField(value, path);
        Require(value.GetDouble() > 0, path, "必须是正整数");
    }

    private static void PositiveField(JsonElement value, string path)
    {
        CountField(value, path);
        Require(value.GetDouble() > 0, path, "必须大于零");
    }

    private static bool IsObject(JsonElement value) => value.ValueKind == JsonValueKind.Object;

    private static JsonElement TryGet(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var property)
            ? property
            : default;

    public static StoryDebugCatalog Parse(JsonElement value)
    {
        CheckShape(value, "debugStory");
        return value.Deserialize<StoryDebugCatalog>()
            ?? throw new System.IO.InvalidDataException("内容无效 debugStory: 现场表无法物化");
    }

    public static void ValidateStoryDebug(CampaignContent campaign, StoryDebugCatalog? catalog)
    {
        if (catalog is null) return;
        if (catalog.Schema is { } schema) Require(schema == 1, "campaign.debugStory", "剧情调试目录版本不支持");
        if (catalog.Meta is { } meta) ValidateMeta(meta);
        var quests = campaign.Quests.ToDictionary(quest => quest.Id, quest => quest);
        var events = campaign.Events
            .Select(@event => StoryType.Text(@event, "id"))
            .Where(id => id.Length > 0)
            .ToHashSet();
        var seen = new HashSet<string>();
        foreach (var task in catalog.Tasks)
        {
            if (task.Quest.Length == 0 || !seen.Add(task.Quest))
                throw new InvalidOperationException("剧情调试任务标识无效");
            if (!quests.ContainsKey(task.Quest))
                throw new InvalidOperationException("剧情调试任务引用未知任务");
            if (task.Phase is not "available" and not "active")
                throw new InvalidOperationException("剧情调试任务起点无效");
            var entry = task.Entry;
            if (entry.Type != "fixed" || entry.Map.Length == 0 || !IsFinite(entry.X)
                || !IsFinite(entry.Y) || entry.Source.Length == 0)
                throw new InvalidOperationException("剧情调试任务落点无效");
            foreach (var script in task.Scripts ?? [])
                if (!campaign.Scripts.ContainsKey(script))
                    throw new InvalidOperationException("剧情调试任务引用未知脚本");
            Require(task.Start is not null, $"剧情调试任务 {task.Quest} 缺少现场表");
            Require(task.SourceHash is not null && task.SourceHash.Trim().Length > 0,
                $"剧情调试任务 {task.Quest} 的现场表缺少来源 hash");
            ValidateStartTable(task.Start!, quests, events, $"debug:{task.Quest}");
            if (task.Play is { } play) ValidatePlay(play, campaign, events, task.Quest);
            if (task.Accept is { } accept)
            {
                foreach (var value in new[] { accept.Event, accept.Script, accept.Node, accept.Actor, accept.Giver })
                    Require(value is null || value.Trim().Length > 0,
                        $"剧情调试任务 {task.Quest} 的接取来源无效");
                if (accept.Event is { Length: > 0 } acceptEvent && !events.Contains(acceptEvent))
                    throw new InvalidOperationException($"剧情调试任务 {task.Quest} 的接取事件不存在");
                if (accept.Script is { Length: > 0 } acceptScript &&
                    !campaign.Scripts.ContainsKey(acceptScript))
                    throw new InvalidOperationException($"剧情调试任务 {task.Quest} 的接取脚本不存在");
            }
        }

        var gapSeen = new HashSet<string>();
        foreach (var gap in catalog.Gaps ?? [])
        {
            Require(gap.Quest.Trim().Length > 0 && quests.ContainsKey(gap.Quest),
                $"剧情调试缺口引用未知任务 {gap.Quest}");
            Require(gap.Kind.Trim().Length > 0, $"剧情调试缺口缺少类型 {gap.Quest}");
            Require(gap.Detail.Trim().Length > 0, $"剧情调试缺口缺少说明 {gap.Quest}");
            var key = System.Text.Json.JsonSerializer.Serialize(new[] { gap.Quest, gap.Kind, gap.Detail });
            Require(gapSeen.Add(key), $"剧情调试缺口重复 {gap.Quest}");
        }
    }

    private static void ValidateMeta(StoryDebugMeta meta)
    {
        Require(meta.InterpreterVersion > 0, "剧情调试解释器版本无效");
        Require(meta.SemanticsVersion > 0, "剧情调试语义版本无效");
        Require(meta.SourceHash.Trim().Length > 0, "剧情调试目录缺少来源 hash");
        Require(meta.GeneratedAt.Trim().Length > 0 &&
            DateTimeOffset.TryParse(meta.GeneratedAt, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _), "剧情调试目录生成时间无效");
        if (meta.Coverage is not { } coverage)
            throw new InvalidOperationException("剧情调试覆盖率无效");
        foreach (var (key, value) in new Dictionary<string, int>
                 {
                     ["eligible"] = coverage.Eligible,
                     ["verified"] = coverage.Verified,
                     ["gaps"] = coverage.Gaps,
                     ["excludedNoIntake"] = coverage.ExcludedNoIntake,
                     ["excludedUnreachableEvents"] = coverage.ExcludedUnreachableEvents,
                     ["unreachableNodes"] = coverage.UnreachableNodes,
                 })
            Require(value >= 0, $"剧情调试覆盖率无效：{key}");
        if (meta.Coverage.ByGapKind is null)
            throw new InvalidOperationException("剧情调试缺口分类无效");
        foreach (var (kind, value) in meta.Coverage.ByGapKind)
            Require(kind.Trim().Length > 0 && value >= 0, $"剧情调试缺口分类无效：{kind}");
    }

    private static void ValidatePlay(StoryDebugPlay play, CampaignContent campaign,
        IReadOnlySet<string> events, string owner)
    {
        Require(play.Script.Trim().Length > 0, $"剧情调试任务 {owner} 的开幕演出缺少脚本");
        if (!campaign.Scripts.TryGetValue(play.Script, out var graph))
            throw new InvalidOperationException($"剧情调试任务 {owner} 的开幕演出引用未知脚本 {play.Script}");
        if (play.Node is not null && !graph.Nodes.Any(node => node.Id == play.Node))
            throw new InvalidOperationException($"剧情调试任务 {owner} 的开幕演出引用未知节点 {play.Node}");
        if (play.Event is not null && !events.Contains(play.Event))
            throw new InvalidOperationException($"剧情调试任务 {owner} 的开幕演出引用未知事件 {play.Event}");
        Require(PlayCuts.Contains(play.Cut), $"剧情调试任务 {owner} 的开幕切点无效");
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private static void ValidateStartTable(StoryDebugStartTable start,
        IReadOnlyDictionary<string, QuestDefinition> quests, IReadOnlySet<string> events, string owner)
    {
        var declared = new Dictionary<string, StoryDebugStartQuest>();
        foreach (var entry in start.Quests ?? [])
        {
            Require(entry.Id.Trim().Length > 0, $"{owner}: 现场表任务无效");
            if (!quests.TryGetValue(entry.Id, out var definition))
                throw new InvalidOperationException($"{owner}: 现场表引用未知任务 {entry.Id}");
            Require(!declared.ContainsKey(entry.Id), $"{owner}: 现场表任务重复 {entry.Id}");
            declared.Add(entry.Id, entry);
            Require(QuestStatuses.Contains(entry.Status), $"{owner}: 任务 {entry.Id} 状态无效");
            foreach (var value in new[] { entry.Cycle, entry.CompletedCycles, entry.E })
                Require(value is null || value >= 0, $"{owner}: 任务 {entry.Id} 数值无效");
            foreach (var (goalId, count) in entry.Goals ?? new Dictionary<string, int>())
            {
                var goal = definition.Goals.FirstOrDefault(value => value.Id == goalId);
                Require(goal is not null, $"{owner}: 任务 {entry.Id} 引用未知目标 {goalId}");
                Require(count >= 0 && count <= goal!.Quantity,
                    $"{owner}: 任务 {entry.Id} 目标 {goalId} 进度无效");
            }
        }

        var running = new HashSet<string>();
        foreach (var id in start.Running ?? [])
        {
            Require(id.Trim().Length > 0 && running.Add(id), $"{owner}: 现场表运行池标识无效 {id}");
            if (!declared.TryGetValue(id, out var entry) || !RunningStatuses.Contains(entry.Status))
                throw new InvalidOperationException($"{owner}: 现场表运行池引用未接取任务 {id}");
        }
        foreach (var (id, entry) in declared)
            Require(!RunningStatuses.Contains(entry.Status) || running.Contains(id),
                $"{owner}: 现场表运行池缺少任务 {id}");

        var world = start.World;
        Require(world is not null, $"{owner}: 现场表缺少世界落点");
        var location = world!.Location;
        Require(location.Map.Trim().Length > 0 && IsFinite(location.X) && IsFinite(location.Y),
            $"{owner}: 现场表落点无效");
        Require(Facings.Contains(location.Facing), $"{owner}: 现场表落点朝向无效");
        foreach (var entity in world.Entities ?? [])
        {
            Require(entity.Id.Trim().Length > 0, $"{owner}: 现场表人物标识无效");
            Require(entity.Map is null || entity.Map.Trim().Length > 0,
                $"{owner}: 现场表人物地图无效 {entity.Id}");
            Require(entity.Actor is null || entity.Actor.Trim().Length > 0,
                $"{owner}: 现场表人物角色无效 {entity.Id}");
            Require((entity.X is null || IsFinite(entity.X.Value)) &&
                    (entity.Y is null || IsFinite(entity.Y.Value)),
                $"{owner}: 现场表人物坐标无效 {entity.Id}");
            Require(entity.MapVisit is null || entity.MapVisit >= 0,
                $"{owner}: 现场表人物访问版本无效 {entity.Id}");
        }
        foreach (var flag in world.Flags ?? [])
            Require(flag.Trim().Length > 0, $"{owner}: 现场表标记无效");
        foreach (var removal in world.EnemyRemovals ?? [])
            Require(removal.EnemyId.Trim().Length > 0, $"{owner}: 现场表敌人移除无效");
        foreach (var spawn in world.EnemyLocations ?? [])
            Require(spawn.Id.Trim().Length > 0 && spawn.EnemyId.Trim().Length > 0
                && spawn.Map.Trim().Length > 0 && IsFinite(spawn.X) && IsFinite(spawn.Y),
                $"{owner}: 现场表敌人位置无效");
        foreach (var absent in world.AbsentActors ?? [])
            Require(absent.Map.Trim().Length > 0 && absent.Actor.Trim().Length > 0,
                $"{owner}: 现场表缺场人物无效");
        foreach (var absent in world.AbsentEnemies ?? [])
            Require(absent.Map.Trim().Length > 0 && absent.EnemyId.Trim().Length > 0,
                $"{owner}: 现场表缺场敌人无效");

        var items = new HashSet<string>();
        foreach (var item in start.Items ?? [])
        {
            Require(item.Item.Trim().Length > 0, $"{owner}: 现场表物品无效");
            Require(items.Add(item.Item), $"{owner}: 现场表物品重复 {item.Item}");
            Require(item.Quantity > 0, $"{owner}: 现场表物品数量无效 {item.Item}");
            Require(item.E is null || item.E >= 0, $"{owner}: 现场表物品证据索引无效 {item.Item}");
        }
        var reset = new HashSet<string>();
        foreach (var id in start.ItemReset ?? [])
            Require(id.Trim().Length > 0 && reset.Add(id), $"{owner}: 现场表待清物品无效 {id}");
        if (start.Party is { } party)
        {
            foreach (var id in (party.Join ?? []).Concat(party.Leave ?? []))
                Require(id.Trim().Length > 0, $"{owner}: 现场表伙伴标识无效");
            Require(party.Control is null || party.Control.Trim().Length > 0,
                $"{owner}: 现场表操控伙伴无效");
        }
        var completed = new HashSet<string>();
        foreach (var entry in start.CompletedEvents ?? [])
        {
            Require(entry.Id.Trim().Length > 0, $"{owner}: 现场表完成事件无效");
            if (!events.Contains(entry.Id))
                throw new InvalidOperationException($"{owner}: 现场表完成事件不存在 {entry.Id}");
            Require(completed.Add(entry.Id), $"{owner}: 现场表完成事件重复 {entry.Id}");
            Require(entry.E is null || entry.E >= 0, $"{owner}: 现场表完成事件证据索引无效 {entry.Id}");
        }
        foreach (var (label, sequences) in
                 new[] { ("剧情", start.OccurrenceSequences), ("任务", start.FactSequences) })
        {
            var sources = new HashSet<string>();
            foreach (var sequence in sequences ?? [])
            {
                var parsed = ParseSequencePair(sequence);
                Require(parsed is not null && parsed.Value.Source.Trim().Length > 0 && parsed.Value.Value > 0,
                    $"{owner}: 现场表{label}序号无效");
                Require(sources.Add(parsed!.Value.Source), $"{owner}: 现场表{label}来源重复 {parsed.Value.Source}");
            }
        }
        if (start.RequiresProfile is { } profile)
        {
            foreach (var item in profile.Items ?? [])
            {
                Require(item.Item.Trim().Length > 0 && item.Quantity > 0,
                    $"{owner}: 现场表玩家物品先决条件无效");
                Require(item.E is null || item.E >= 0,
                    $"{owner}: 现场表玩家物品证据索引无效 {item.Item}");
            }
            Require(profile.Money is null || profile.Money >= 0,
                $"{owner}: 现场表玩家金币先决条件无效");
        }
    }

    private static (string Source, int Value)? ParseSequencePair(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != 2) return null;
        var source = element[0];
        var value = element[1];
        if (source.ValueKind != JsonValueKind.String || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var number) || number <= 0) return null;
        return (source.GetString()!, number);
    }

    public static StoryDebugTask DebugTask(CampaignContent campaign, string quest)
    {
        var task = campaign.DebugStory?.Tasks.FirstOrDefault(candidate => candidate.Quest == quest);
        return task ?? throw new InvalidOperationException("这个任务没有调试入口");
    }

    public static StoryDebugGap[] DebugGaps(CampaignContent campaign, string? quest = null) =>
        (campaign.DebugStory?.Gaps ?? []).Where(gap => quest is null || gap.Quest == quest).ToArray();

    public static StoryDebugGap? DebugGapFor(CampaignContent campaign, string quest) =>
        DebugGaps(campaign, quest).FirstOrDefault();

    public static int StoryDebugEvidenceCount(StoryDebugTask task)
    {
        var start = task.Start;
        if (start is null) return 0;
        var count = 0;
        foreach (var entry in start.Quests ?? []) if (entry.E is not null) count += 1;
        foreach (var item in start.Items ?? []) if (item.E is not null) count += 1;
        foreach (var item in start.RequiresProfile?.Items ?? []) if (item.E is not null) count += 1;
        foreach (var @event in start.CompletedEvents ?? []) if (@event.E is not null) count += 1;
        if (task.Basis is { } basis && basis.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "before", "steps", "choices", "battles" })
                if (basis.TryGetProperty(key, out var list) && list.ValueKind == JsonValueKind.Array)
                    count += list.GetArrayLength();
            if (basis.TryGetProperty("opening", out var opening) &&
                opening.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)) count += 1;
            if (basis.TryGetProperty("giver", out var giver) &&
                giver.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)) count += 1;
        }
        return count;
    }
}
