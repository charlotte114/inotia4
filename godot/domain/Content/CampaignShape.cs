using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Inotia4.Domain.Combat;
using static Inotia4.Domain.Content.ContentShape;

namespace Inotia4.Domain.Content;

/// <summary>
/// <c>campaign.json</c> 的形状校验：剧情脚本的节点图、任务与任务组、剧情事件、
/// 地图摆放与地图进入时的摆放、剧情敌人，以及它们各自引用的条件、世界命令、
/// 演出动作这几套递归结构。
///
/// 剧情内容是三棵递归树（条件、命令、演出）加一张节点图；写错的表现是「走到那一步
/// 没反应」「条件永远不成立」「剧本演到一半没有下一节点」——运行时多半静默跳过，
/// 反查不到字段。所以这里在加载期一次全拒，把错误挡在内容目录外面。
///
/// 内嵌调试目录（<c>debugStory</c>）走同一条闸：形状在 Campaign 那张字段表里校验，
/// 跨文件引用（现场表提到的任务、事件、摆放是否真存在）在
/// <see cref="ValidateCampaign"/> 末尾交 <see cref="Inotia4.Domain.Story.StoryDebug"/> 接手。
/// 正式内容不带这个字段，GM 模式从独立文件 <c>content/debug/start.json</c> 加载。
///
/// 条件、摆放与刷怪点的形状与地图文件同源（<see cref="WorldShape"/> 的
/// <see cref="WorldShape.Condition"/>／<see cref="WorldShape.EntityFields"/>／
/// <see cref="WorldShape.EnemySpawnFields"/>），不重复维护。
/// </summary>
public static class CampaignShape
{
    private static readonly string[] Facings = WorldShape.Facings;

    private static readonly string[] EquipmentSlots =
        ["head", "hands", "back", "body", "feet", "main-hand", "off-hand", "neck", "ring", "accessory"];

    private static readonly string[] PoseNames =
    [
        "stand", "fall-down", "heavy-blow", "recovery", "lie-down", "lie-side",
        "sway", "cry", "look-around", "pick-up", "give", "demon-lord-attack", "demon-lord-skill", "skill-2",
    ];

    private static readonly string[] TriggerSources =
    [
        "start", "map-entry", "region", "interaction", "quest", "inventory", "choice", "battle", "near", "event",
    ];

    private static readonly ShapeCheck Facing = OneOf(Facings);

    private static readonly ShapeCheck DebugStory = (value, path) =>
        Inotia4.Domain.Story.StoryDebug.CheckShape(
            System.Text.Json.JsonSerializer.SerializeToElement(value.Node), path);

    private static readonly ShapeCheck Strings = ArrayOf(Text);

    private static readonly ShapeCheck NonNegative = (value, path) =>
    {
        Number(value, path);
        TryDouble(value.Node, out var number);
        Require(number >= 0, path, "需要非负数");
    };

    private static readonly ShapeCheck Opacity = (value, path) =>
    {
        NonNegative(value, path);
        TryDouble(value.Node, out var number);
        Require(number <= 1, path, "透明度不得大于一");
    };

    private static readonly Dictionary<string, ShapeCheck> LineFields = Fields(
        ("speaker", Text), ("text", Text), ("actor", Nullable(Text)),
        ("portrait", Optional(Nullable(Text))), ("presentation", Optional(OneOf("screen"))));

    private static readonly ShapeCheck Dialogue = Object(Merge(
        LineFields, Fields(("type", OneOf("dialogue")))));

    private static readonly ShapeCheck Choice = (value, path) =>
    {
        Object(Fields(
            ("type", OneOf("choice")),
            ("prompt", Text),
            ("description", Optional(Text)),
            ("options", ArrayOf(Object(Fields(
                ("label", Text), ("value", Text),
                ("lines", Optional(ArrayOf(Object(LineFields))))))))))(value, path);
        Require(value.Node?["options"] is JsonArray { Count: > 0 }, path, "选项不能为空");
    };

    private static readonly ShapeCheck PartyAction = Object(Fields(
        ("type", OneOf("party-join", "party-leave")), ("companion", Text)));

    private static readonly ShapeCheck Member = Object(Fields(
        ("id", Text), ("actor", Text), ("characterId", Optional(Text)), ("enemyId", Optional(Text)),
        ("memberId", Optional(Text)), ("binding", Optional(OneOf("world"))),
        ("worldEntityId", Optional(Text)), ("inheritPosition", Optional(Flag)), ("unpositioned", Optional(Flag)),
        ("x", Number), ("y", Number), ("facing", Facing), ("visible", Flag)));

    private static readonly ShapeCheck WorldCommand = BuildWorldCommand();

    private static readonly Dictionary<string, ShapeCheck> PerformanceChecks = BuildPerformanceChecks();

    private static readonly ShapeCheck Performance = (value, path) =>
    {
        Require(value.Present && value.Node is JsonObject, path, "需要演出动作");
        var type = Str(((JsonObject)value.Node!)["type"]);
        Require(type is not null && PerformanceChecks.ContainsKey(type), path, $"未定义演出动作 {type}");
        PerformanceChecks[type!](value, path);
    };

    private static readonly ShapeCheck Action = (value, path) =>
    {
        Require(value.Present && value.Node is JsonObject, path, "需要剧情动作");
        var kind = Str(((JsonObject)value.Node!)["type"]);
        var check = kind switch
        {
            "dialogue" => Dialogue,
            "choice" => Choice,
            "party-join" or "party-leave" => PartyAction,
            _ => Performance,
        };
        check(value, path);
    };

    private static readonly ShapeCheck Presentation = (value, path) =>
    {
        var type = value.Node is JsonObject node ? Str(node["type"]) : null;
        if (type == "quest-popup")
        {
            Object(Fields(("type", OneOf("quest-popup")),
                ("kind", OneOf("start", "complete")), ("quest", Text)))(value, path);
            return;
        }

        Action(value, path);
        Require(type is not "party-join" and not "party-leave", path, "队伍变化通过游戏命令提交");
    };

    private static readonly ShapeCheck QuestFact = Object(Fields(
        ("kind", OneOf("talk", "interact", "defeat", "recruit")),
        ("target", Text), ("characterId", Optional(Text)), ("quantity", Optional(Count)),
        ("occurrence", Object(Fields(("source", Text), ("sequence", Count))))));

    private static readonly ShapeCheck QuestCommand = (value, path) =>
    {
        Require(value.Present && value.Node is JsonObject, path, "需要任务命令");
        var type = Str(((JsonObject)value.Node!)["type"]);
        ShapeCheck check = type switch
        {
            "refresh" => Object(Fields(("type", OneOf("refresh")))),
            "fact" => Object(Fields(("type", OneOf("fact")), ("fact", QuestFact))),
            "reset-side-quests" => Object(Fields(("type", OneOf("reset-side-quests")))),
            "accept" => Object(Fields(
                ("type", OneOf("accept")), ("quest", Text), ("cycle", Optional(Integer)),
                ("source", Optional(OneOf("interaction", "script"))))),
            _ => Object(Fields(
                ("type", OneOf("complete", "script-ready", "script-complete", "reset")),
                ("quest", Text), ("cycle", Optional(Integer)))),
        };
        check(value, path);
    };

    private static readonly ShapeCheck Encounter = Object(Fields(
        ("id", Text), ("definitionId", Optional(Text)), ("map", Text), ("mapFromStage", Optional(Flag)),
        ("status", OneOf("prepared", "active", "won", "lost")),
        ("spawns", ArrayOf(Object(WorldShape.EnemySpawnFields)))));

    private static readonly ShapeCheck Destination = Object(Fields(
        ("map", Text), ("x", Number), ("y", Number), ("facing", Facing)));

    private static readonly ShapeCheck StoryCommand = BuildStoryCommand();

    private static readonly ShapeCheck NodeRef = Object(Fields(("script", Text), ("node", Text)));

    private static readonly ShapeCheck NodeExit = (value, path) =>
    {
        Require(value.Present && value.Node is JsonObject, path, "需要节点出口");
        var type = Str(((JsonObject)value.Node!)["type"]);
        var checks = new Dictionary<string, ShapeCheck>(StringComparer.Ordinal)
        {
            ["next"] = Object(Fields(("type", OneOf("next")), ("target", NodeRef))),
            ["branch"] = Object(Fields(
                ("type", OneOf("branch")), ("condition", WorldShape.Condition),
                ("then", NodeRef), ("otherwise", NodeRef))),
            ["call"] = Object(Fields(("type", OneOf("call")), ("target", NodeRef), ("then", NodeRef))),
            ["choice"] = Object(Fields(
                ("type", OneOf("choice")), ("choice", Text), ("cases", Record(NodeRef)),
                ("otherwise", Optional(NodeRef)))),
            ["waitEncounter"] = Object(Fields(
                ("type", OneOf("waitEncounter")), ("encounter", Encounter), ("outcomes", Record(NodeRef)))),
            ["defer"] = Object(Fields(("type", OneOf("defer")))),
            ["end"] = Object(Fields(("type", OneOf("end")))),
        };
        Require(type is not null && checks.ContainsKey(type), path, $"未定义节点出口 {type}");
        checks[type!](value, path);
    };

    private static readonly ShapeCheck StorySpot = Object(Fields(
        ("map", Text), ("x", Number), ("y", Number), ("source", Text)));

    private static readonly ShapeCheck StoryPlace = Variant("type", new Dictionary<string, ShapeCheck>(StringComparer.Ordinal)
    {
        ["fixed"] = Object(Fields(
            ("type", OneOf("fixed")), ("map", Text), ("x", Number), ("y", Number), ("source", Text))),
        ["alternatives"] = Object(Fields(
            ("type", OneOf("alternatives")), ("spots", ArrayOf(StorySpot)))),
        ["current"] = Object(Fields(("type", OneOf("current")))),
        ["unavailable"] = Object(Fields(("type", OneOf("unavailable")), ("reason", Text))),
    });

    private static readonly ShapeCheck Placement = Object(Merge(
        WorldShape.EntityFields,
        Fields(("map", Text), ("facing", Optional(Facing)))));

    private static readonly ShapeCheck CampaignEnemy = Object(Merge(
        WorldShape.EnemySpawnFields,
        Fields(("map", Text), ("activeQuest", Text), ("rewardItem", Text))));

    private static readonly ShapeCheck StoryEvent = Object(Fields(
        ("id", Text), ("source", OneOf(TriggerSources)), ("script", Text),
        ("key", Optional(Text)), ("entity", Optional(Text)), ("map", Optional(Text)),
        ("place", Optional(StoryPlace)), ("near", Optional(Text)), ("nearCharacterId", Optional(Text)),
        ("region", Optional(Object(WorldShape.BoxFields))),
        ("condition", Optional(WorldShape.Condition)),
        ("priority", Optional(Integer)), ("repeat", Optional(Flag))));

    private static readonly ShapeCheck Quest = Object(Fields(
        ("id", Text), ("title", Text), ("description", Text),
        ("giver", Optional(Text)), ("giverCharacterId", Optional(Text)),
        ("delivery", Optional(Text)), ("deliveryCharacterId", Optional(Text)),
        ("places", Optional(Object(Fields(
            ("available", Optional(StoryPlace)), ("active", Optional(StoryPlace)),
            ("ready", Optional(StoryPlace)))))),
        ("guidance", Object(Fields(
            ("available", Text), ("active", Text), ("counterLabel", Optional(Text))))),
        ("targetChanges", Optional(ArrayOf(Object(Fields(
            ("goal", Text), ("enemyId", Text), ("actor", Text),
            ("removeAfterInteraction", Flag), ("changedText", Text), ("interactText", Text)))))),
        ("descriptionTargets", Optional(ArrayOf(Object(Fields(
            ("target", Text), ("characterId", Optional(Text)), ("label", Optional(Text))))))),
        ("goals", ArrayOf(Object(Fields(
            ("id", Text), ("kind", OneOf("talk", "item", "interact", "defeat", "recruit")),
            ("target", Text), ("characterId", Optional(Text)), ("quantity", Count), ("label", Optional(Text)))))),
        ("availableWhen", Optional(WorldShape.Condition)),
        ("completion", OneOf("talk", "all", "story", "internal")),
        ("hidden", Optional(Flag)), ("repeat", Optional(Flag)),
        ("onAcceptItems", Optional(ArrayOf(Object(Fields(("item", Text), ("quantity", Count)))))),
        ("handoverItems", Optional(ArrayOf(Object(Fields(
            ("item", Text), ("quantity", Count), ("removal", Optional(OneOf("quantity", "all-matching")))))))),
        ("itemCompletion", Optional(OneOf("all", "equipped-item-shortcut"))),
        ("acceptance", Optional(OneOf("automatic", "optional"))),
        ("rewards", Optional(Object(Fields(
            ("experience", Integer), ("money", Integer),
            ("items", ArrayOf(Object(Fields(
                ("item", Text), ("quantity", Count), ("classIds", Optional(Strings)))))))))),
        ("offerScript", Optional(Nullable(Text))),
        ("completeScript", Optional(Nullable(Text))),
        ("progressText", Text), ("completeText", Text)));

    private static readonly ShapeCheck Campaign = Object(Fields(
        ("version", (value, path) => Require(TryDouble(value.Node, out var version) && version == 2,
            path, "剧情内容需要版本 2")),
        ("contentVersion", Count),
        ("start", Object(Fields(
            ("map", Text), ("x", Number), ("y", Number), ("actor", Text),
            ("facing", Facing), ("script", Text)))),
        ("scripts", Record(Object(Fields(
            ("entry", Text),
            ("nodes", ArrayOf(Object(Fields(
                ("id", Text),
                ("presentation", ArrayOf(Presentation)),
                ("commands", ArrayOf(StoryCommand)),
                ("stage", Optional(PerformanceChecks["stage"])),
                ("exit", NodeExit))))))))),
        ("questGroups", ArrayOf(Object(Fields(
            ("id", Text), ("title", Text), ("kind", OneOf("main", "side")), ("questIds", Strings))))),
        ("quests", ArrayOf(Quest)),
        ("placements", ArrayOf(Placement)),
        ("mapEntryPlacements", Optional(ArrayOf(Placement))),
        ("enemyPlacements", Optional(ArrayOf(CampaignEnemy))),
        ("events", ArrayOf(StoryEvent)),
        ("debugStory", Optional(DebugStory)),
        ("exitRules", Optional(ArrayOf(Object(Fields(
            ("id", Text), ("map", Text), ("condition", WorldShape.Condition))))))));

    public static void ValidateCampaign(JsonNode? root)
    {
        Campaign(new ShapeValue(root), "campaign");
        if (root is not JsonObject campaign) return;
        CheckCampaign(campaign);
        CheckStoryGraph(campaign);
        if (campaign["debugStory"] is not null)
        {
            var content = System.Text.Json.JsonSerializer.Deserialize<Story.CampaignContent>(root)
                ?? throw new System.IO.InvalidDataException("内容无效 campaign: 调试现场表无法物化");
            var catalog = Story.StoryDebug.Parse(
                System.Text.Json.JsonSerializer.SerializeToElement(campaign["debugStory"]));
            Story.StoryDebug.ValidateStoryDebug(content, catalog);
        }
    }

    private static void CheckCampaign(JsonObject campaign)
    {
        var questIds = Ids(campaign["quests"]);
        var eventIds = Ids(campaign["events"]);

        var grouped = new HashSet<string>(StringComparer.Ordinal);
        if (campaign["questGroups"] is JsonArray groups)
        {
            foreach (var group in groups)
            {
                var id = Str(group?["id"]) ?? string.Empty;
                if (group?["questIds"] is not JsonArray members || members.Count == 0)
                    throw Error(group is null ? "campaign.questGroups" : id, "任务组需要至少一个任务");
                foreach (var member in members)
                {
                    var questId = Str(member) ?? string.Empty;
                    Require(grouped.Add(questId), "campaign.questGroups.questIds", "标识重复");
                    Require(questIds.Contains(questId), id, $"任务不存在 {questId}");
                }
            }
        }

        if (campaign["quests"] is JsonArray quests)
            foreach (var quest in quests.OfType<JsonObject>())
            {
                var id = Str(quest["id"]) ?? string.Empty;
                var hidden = IsTrue(quest["hidden"]);
                var completion = Str(quest["completion"]) ?? string.Empty;
                var visible = !hidden && completion != "internal";
                Require(id.Length > 0 && (!visible || grouped.Contains(id)),
                    id.Length > 0 ? id : "campaign.quests", "任务缺少标识或所属分组");
                var goals = quest["goals"] as JsonArray ?? [];
                Unique(goals.Select(goal => Str(goal?["id"]) ?? string.Empty), $"{id}.goals");
                Require(completion != "all" || goals.Count > 0, id, "任务需要完成目标");
                foreach (var goal in goals)
                    Require(!string.IsNullOrEmpty(Str(goal?["id"])) && !string.IsNullOrEmpty(Str(goal?["target"])),
                        id, "任务目标标识为空");
                if (Str(quest["itemCompletion"]) == "equipped-item-shortcut")
                    Require(goals.Count > 0 && goals.All(goal => Str(goal?["kind"]) == "item"),
                        id, "穿戴例外只用于有序物品目标");
            }

        Unique(PlacementIds(campaign).Select(entity => Str(entity?["id"]) ?? string.Empty),
            "campaign.placements");
        if (campaign["enemyPlacements"] is JsonArray enemies)
            Unique(enemies.Select(enemy => Str(enemy?["id"]) ?? string.Empty), "campaign.enemyPlacements");
        if (campaign["exitRules"] is JsonArray exits)
            Unique(exits.Select(exit => Str(exit?["id"]) ?? string.Empty), "campaign.exitRules");

        var choices = new HashSet<string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(eventIds, StringComparer.Ordinal);
        var conditions = new List<(string Owner, JsonObject? Value)>();
        var placementIds = PlacementIds(campaign).Select(entity => Str(entity?["id"]) ?? string.Empty).ToHashSet(StringComparer.Ordinal);
        var scripts = campaign["scripts"] as JsonObject;

        if (scripts is not null)
        {
            foreach (var (name, script) in scripts)
            {
                if (script?["nodes"] is not JsonArray nodes) continue;
                foreach (var node in nodes.OfType<JsonObject>())
                {
                    if (node["commands"] is JsonArray commands) CollectCommandMarks(commands, choices, flags, questIds);
                    if (node["exit"] is JsonObject exit)
                    {
                        if (Str(exit["type"]) == "choice" && Str(exit["choice"]) is { Length: > 0 } choice) choices.Add(choice);
                        if (Str(exit["type"]) == "branch")
                            conditions.Add(($"{name}/{Str(node["id"])}", exit["condition"] as JsonObject));
                    }

                    if (node["presentation"] is JsonArray presentation)
                        foreach (var action in presentation.OfType<JsonObject>())
                            if (Str(action["type"]) == "quest-popup")
                                Require(questIds.Contains(Str(action["quest"]) ?? string.Empty),
                                    name, "提示任务不存在");
                }
            }
        }

        if (campaign["quests"] is JsonArray questList)
            foreach (var quest in questList.OfType<JsonObject>())
            {
                var id = Str(quest["id"]) ?? string.Empty;
                conditions.Add((id, quest["availableWhen"] as JsonObject));
                foreach (var key in new[] { "offerScript", "completeScript" })
                    if (Str(quest[key]) is { Length: > 0 } scriptId)
                        Require(scripts is not null && scripts.ContainsKey(scriptId), id, $"剧情缺失 {scriptId}");
            }

        if (campaign["events"] is JsonArray events)
            foreach (var item in events.OfType<JsonObject>())
            {
                var id = Str(item["id"]) ?? string.Empty;
                conditions.Add((id, item["condition"] as JsonObject));
                if (Str(item["source"]) == "region")
                    Require(item["region"] is JsonObject && Str(item["map"]) is { Length: > 0 },
                        id, "区域事件缺少地图和矩形");
                if (Str(item["entity"]) is { Length: > 0 } entity)
                    Require(placementIds.Contains(entity), id, "事件交互对象不存在");
            }

        foreach (var entity in PlacementIds(campaign))
        {
            conditions.Add((Str(entity?["id"]) ?? string.Empty, entity?["condition"] as JsonObject));
            if (Str(entity?["afterInteractScript"]) is { Length: > 0 } scriptId)
                Require(campaign["scripts"] is JsonObject table && table.ContainsKey(scriptId),
                    Str(entity?["id"]) ?? "campaign.placements", $"交互剧情缺失 {scriptId}");
        }

        if (campaign["exitRules"] is JsonArray exitRules)
            foreach (var rule in exitRules.OfType<JsonObject>())
                conditions.Add((Str(rule["id"]) ?? string.Empty, rule["condition"] as JsonObject));

        foreach (var (owner, value) in conditions)
        {
            if (value is null) continue;
            var refs = CollectReferences(value);
            foreach (var id in refs.quests) Require(questIds.Contains(id), owner, $"条件任务不存在 {id}");
            foreach (var id in refs.events) Require(eventIds.Contains(id), owner, $"条件事件不存在 {id}");
            foreach (var id in refs.choices) Require(choices.Contains(id), owner, $"条件选择不存在 {id}");
            foreach (var id in refs.flags) Require(flags.Contains(id), owner, $"条件标记没有来源 {id}");
            foreach (var key in refs.world)
                Require(key is "map" or "near-actor" or "near-entity" or "interaction" ||
                        (key.StartsWith("region:", StringComparison.Ordinal) && eventIds.Contains(key[7..])),
                    owner, $"世界条件没有来源 {key}");
        }

        CheckQuestCycles(campaign, questIds);

        if (campaign["enemyPlacements"] is JsonArray enemyPlacements)
            foreach (var enemy in enemyPlacements.OfType<JsonObject>())
                Require(questIds.Contains(Str(enemy["activeQuest"]) ?? string.Empty),
                    Str(enemy["id"]) ?? "campaign.enemyPlacements", "任务敌人引用了不存在的任务");
    }

    private static void CollectCommandMarks(
        JsonArray commands, HashSet<string> choices, HashSet<string> flags, HashSet<string> questIds)
    {
        foreach (var command in commands.OfType<JsonObject>())
        {
            var type = Str(command["type"]);
            if (type == "flag") flags.Add(Str(command["flag"]) ?? string.Empty);
            if (type == "choice") choices.Add(Str(command["choice"]) ?? string.Empty);
            if (type == "world" && command["command"] is JsonObject world)
            {
                if (Str(world["kind"]) == "flag") flags.Add(Str(world["id"]) ?? string.Empty);
                if (Str(world["kind"]) == "choice") choices.Add(Str(world["id"]) ?? string.Empty);
            }

            if (type == "quest" && command["command"] is JsonObject quest && Str(quest["quest"]) is { Length: > 0 } questId)
                Require(questIds.Contains(questId), "story.command", $"命令任务不存在 {questId}");
        }
    }

    /// <summary>任务前置的强依赖闭包：非 internal 任务不能通过前置互相等死。</summary>
    private static void CheckQuestCycles(JsonObject campaign, HashSet<string> questIds)
    {
        JsonObject? QuestOf(string id) => (campaign["quests"] as JsonArray)?
            .OfType<JsonObject>().FirstOrDefault(quest => Str(quest["id"]) == id);

        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        void Visit(string id)
        {
            if (visited.Contains(id)) return;
            Require(!visiting.Contains(id), id, "任务前置条件形成无入口循环");
            visiting.Add(id);
            var definition = QuestOf(id);
            if (definition is not null && Str(definition["completion"]) != "internal")
                foreach (var dependency in Mandatory(definition["availableWhen"] as JsonObject))
                    Visit(dependency);
            visiting.Remove(id);
            visited.Add(id);
        }

        foreach (var id in questIds) Visit(id);
    }

    private static HashSet<string> Mandatory(JsonObject? condition)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (condition is null) return result;
        switch (Str(condition["type"]))
        {
            case "quest":
                if (Str(condition["status"]) is "accepted" or "active" or "ready" or "completed" or "settled")
                    result.Add(Str(condition["quest"]) ?? string.Empty);
                break;
            case "all":
                foreach (var child in condition["conditions"] as JsonArray ?? [])
                    result.UnionWith(Mandatory(child as JsonObject));
                break;
            case "any":
                var children = (condition["conditions"] as JsonArray ?? [])
                    .Select(child => Mandatory(child as JsonObject)).ToArray();
                if (children.Length > 0)
                    foreach (var id in children[0])
                        if (children.All(set => set.Contains(id))) result.Add(id);
                break;
        }

        return result;
    }

    private static (HashSet<string> quests, HashSet<string> events, HashSet<string> choices,
        HashSet<string> flags, HashSet<string> world) CollectReferences(JsonObject condition)
    {
        var quests = new HashSet<string>(StringComparer.Ordinal);
        var events = new HashSet<string>(StringComparer.Ordinal);
        var choices = new HashSet<string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        var world = new HashSet<string>(StringComparer.Ordinal);

        void Visit(JsonObject value)
        {
            switch (Str(value["type"]))
            {
                case "all" or "any":
                    foreach (var child in value["conditions"] as JsonArray ?? [])
                        if (child is JsonObject nested) Visit(nested);
                    break;
                case "not":
                    if (value["condition"] is JsonObject negated) Visit(negated);
                    break;
                case "quest": quests.Add(Str(value["quest"]) ?? string.Empty); break;
                case "event": events.Add(Str(value["event"]) ?? string.Empty); break;
                case "choice": choices.Add(Str(value["choice"]) ?? string.Empty); break;
                case "flag": flags.Add(Str(value["flag"]) ?? string.Empty); break;
                case "world": world.Add(Str(value["key"]) ?? string.Empty); break;
            }
        }

        Visit(condition);
        return (quests, events, choices, flags, world);
    }

    private static IEnumerable<JsonNode?> PlacementIds(JsonObject campaign)
    {
        foreach (var entity in campaign["placements"] as JsonArray ?? []) yield return entity;
        foreach (var entity in campaign["mapEntryPlacements"] as JsonArray ?? []) yield return entity;
    }

    private static HashSet<string> Ids(JsonNode? array) =>
        (array as JsonArray ?? []).Select(node => Str(node?["id"]) ?? string.Empty).ToHashSet(StringComparer.Ordinal);

    private static InvalidDataException Error(string path, string message) => new($"内容无效 {path}: {message}");

    private static bool IsTrue(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.True;

    private static bool IsFalse(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.False;

    private static void CheckStoryGraph(JsonObject campaign)
    {
        if (campaign["scripts"] is not JsonObject scripts) return;

        var nodes = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var (script, definition) in scripts)
        {
            if (definition?["nodes"] is not JsonArray list || list.Count == 0)
                throw Error($"campaign.scripts.{script}.nodes", "剧情没有节点");

            foreach (var node in list.OfType<JsonObject>())
            {
                var id = $"{script}\n{Str(node["id"])}";
                Require(!string.IsNullOrEmpty(Str(node["id"])) && !nodes.ContainsKey(id),
                    $"campaign.scripts.{script}", $"节点重复或为空：{Str(node["id"])}");
                nodes[id] = node;

                var choices = (node["presentation"] as JsonArray ?? [])
                    .OfType<JsonObject>().Where(action => Str(action["type"]) == "choice").ToArray();
                foreach (var choice in choices)
                {
                    var options = choice["options"] as JsonArray ?? [];
                    Require(options.Count > 0, $"campaign.scripts.{script}/{Str(node["id"])}", "选项为空");
                    Unique(options.Select(option => Str(option?["value"]) ?? string.Empty),
                        $"campaign.scripts.{script}/{Str(node["id"])}");
                }

                Require(choices.Length <= 1 && (choices.Length == 0 || Str(node["exit"]?["type"]) == "choice"),
                    $"campaign.scripts.{script}/{Str(node["id"])}", "选择需要独立节点与分支出口");

                var exitType = Str(node["exit"]?["type"]);
                if (exitType == "choice")
                {
                    Require(choices.Length == 1, $"campaign.scripts.{script}/{Str(node["id"])}", "分支缺少选择表现");
                    var optionValues = (choices.Length > 0 ? choices[0]["options"] as JsonArray : null)?
                        .Select(option => Str(option?["value"]) ?? string.Empty).ToHashSet(StringComparer.Ordinal) ?? [];
                    var cases = node["exit"]?["cases"] as JsonObject ?? new JsonObject();
                    foreach (var (value, _) in cases)
                        Require(optionValues.Contains(value),
                            $"campaign.scripts.{script}/{Str(node["id"])}", "选项与分支出口不匹配");
                    var otherwise = node["exit"]?["otherwise"] is not null;
                    Require(otherwise || optionValues.IsSubsetOf(cases.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal)),
                        $"campaign.scripts.{script}/{Str(node["id"])}", "选项与分支出口不匹配");
                }

                if (exitType == "choice")
                    Require((node["exit"]?["cases"] as JsonObject)?.Count > 0,
                        $"campaign.scripts.{script}/{Str(node["id"])}", "分支为空");
                if (exitType == "waitEncounter")
                    Require((node["exit"]?["outcomes"] as JsonObject)?.Count > 0,
                        $"campaign.scripts.{script}/{Str(node["id"])}", "战斗结果为空");
            }

            var entry = Str(definition["entry"]);
            Require(entry is not null && list.OfType<JsonObject>().Any(node => Str(node["id"]) == entry),
                $"campaign.scripts.{script}", "入口不存在");
        }

        foreach (var (id, node) in nodes)
        {
            var exit = node["exit"] as JsonObject;
            foreach (var target in ExitTargets(exit))
            {
                var key = $"{Str(target?["script"])}\n{Str(target?["node"])}";
                Require(nodes.ContainsKey(key), $"剧情节点 {id}", $"引用了不存在的节点 {Str(target?["script"])}/{Str(target?["node"])}");
            }
        }

        var events = new HashSet<string>(StringComparer.Ordinal);
        if (campaign["events"] is JsonArray eventList)
            foreach (var item in eventList.OfType<JsonObject>())
            {
                var id = Str(item["id"]) ?? string.Empty;
                Require(id.Length > 0 && events.Add(id), "campaign.events", $"剧情事件标识重复或为空：{id}");
                Require(scripts.ContainsKey(Str(item["script"]) ?? string.Empty),
                    $"campaign.events.{id}", $"剧情事件引用了未知脚本 {Str(item["script"])}");
            }

        var terminating = nodes.Where(pair =>
                pair.Value["exit"] is JsonObject exit &&
                Str(exit["type"]) is "end" or "defer" or "waitEncounter")
            .Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var (id, node) in nodes)
                if (!terminating.Contains(id) &&
                    ExitTargets(node["exit"] as JsonObject).Any(target =>
                        terminating.Contains($"{Str(target?["script"])}\n{Str(target?["node"])}")))
                {
                    terminating.Add(id);
                    changed = true;
                }
        }

        foreach (var id in nodes.Keys)
            Require(terminating.Contains(id), $"剧情节点 {id}", "无法退出循环或等待战斗");
    }

    private static IEnumerable<JsonObject?> ExitTargets(JsonObject? exit)
    {
        if (exit is null) yield break;
        switch (Str(exit["type"]))
        {
            case "next": yield return exit["target"] as JsonObject; break;
            case "call":
                yield return exit["target"] as JsonObject;
                yield return exit["then"] as JsonObject;
                break;
            case "branch":
                yield return exit["then"] as JsonObject;
                yield return exit["otherwise"] as JsonObject;
                break;
            case "choice":
                foreach (var (_, target) in exit["cases"] as JsonObject ?? new JsonObject()) yield return target as JsonObject;
                if (exit["otherwise"] is JsonObject otherwise) yield return otherwise;
                break;
            case "waitEncounter":
                foreach (var (_, target) in exit["outcomes"] as JsonObject ?? new JsonObject()) yield return target as JsonObject;
                break;
        }
    }

    private static ShapeCheck BuildWorldCommand()
    {
        var entity = Object(Fields(
            ("id", Text), ("map", Text), ("actor", Text), ("characterId", Optional(Text)),
            ("authority", Optional(OneOf("registered", "npc", "story", "static"))),
            ("x", Number), ("y", Number), ("facing", Optional(Facing)),
            ("visible", Optional(Flag)), ("removed", Optional(Flag)), ("hint", Optional(Text))));

        return Variant("kind", new Dictionary<string, ShapeCheck>(StringComparer.Ordinal)
        {
            ["place"] = Object(Fields(
                ("kind", OneOf("place")), ("scope", Optional(OneOf("visit"))),
                ("mapFromStage", Optional(Flag)), ("entity", entity))),
            ["show"] = Object(Fields(("kind", OneOf("show")), ("id", Text))),
            ["hide"] = Object(Fields(("kind", OneOf("hide")), ("id", Text))),
            ["remove"] = Object(Fields(("kind", OneOf("remove")), ("id", Text))),
            ["remove-character"] = Object(Fields(
                ("kind", OneOf("remove-character")), ("characterId", Text), ("map", Text),
                ("mapFromStage", Optional(Flag)), ("all", Flag))),
            ["actor-visibility"] = Object(Fields(
                ("kind", OneOf("actor-visibility")), ("map", Text), ("actor", Text),
                ("characterId", Optional(Text)), ("visible", Flag))),
            ["enemy-availability"] = Object(Fields(
                ("kind", OneOf("enemy-availability")), ("map", Text), ("enemyId", Text), ("available", Flag))),
            ["exit"] = Object(Fields(("kind", OneOf("exit")), ("id", Text), ("enabled", Flag))),
            ["flag"] = Object(Fields(("kind", OneOf("flag")), ("id", Text), ("value", Flag))),
            ["choice"] = Object(Fields(("kind", OneOf("choice")), ("id", Text), ("value", Text))),
            ["remove-enemy"] = Object(Fields(
                ("kind", OneOf("remove-enemy")), ("enemyId", Text), ("map", Optional(Text)), ("all", Flag))),
            ["enemy-location"] = Object(Fields(
                ("kind", OneOf("enemy-location")), ("enemyId", Text),
                ("location", Nullable(Object(Fields(("map", Text), ("x", Number), ("y", Number))))),
                ("scope", Optional(OneOf("visit"))),
                ("respawn", Optional((value, path) =>
                    Require(IsFalse(value.Node), path, "剧情敌人不可刷新"))),
                ("mapFromStage", Optional(Flag)))),
            ["actor-appearance"] = Object(Fields(
                ("kind", OneOf("actor-appearance")), ("actor", Text), ("appearance", Text))),
            ["party-buff"] = Object(Fields(
                ("kind", OneOf("party-buff")), ("buff", OneOf(BattleSemantics.BuffKeys)), ("strength", Integer))),
            ["travel"] = Object(Fields(("kind", OneOf("travel")), ("destination", Destination))),
            ["encounter"] = Object(Fields(("kind", OneOf("encounter")), ("encounter", Encounter))),
            ["encounter-result"] = Object(Fields(
                ("kind", OneOf("encounter-result")), ("id", Text), ("result", OneOf("won", "lost")))),
        });
    }

    private static ShapeCheck BuildStoryCommand()
    {
        ShapeCheck memberValue = (value, path) =>
        {
            if (Str(value.Node) == "full") return;
            Integer(value, path);
        };

        return Variant("type", new Dictionary<string, ShapeCheck>(StringComparer.Ordinal)
        {
            ["talk"] = Object(Fields(("type", OneOf("talk")), ("target", Text))),
            ["interaction"] = Object(Fields(
                ("type", OneOf("interaction")), ("target", Text), ("instance", Text))),
            ["quest"] = Object(Fields(("type", OneOf("quest")), ("command", QuestCommand))),
            ["item-give"] = Object(Fields(
                ("type", OneOf("item-give")), ("item", Text), ("quantity", Count))),
            ["item-take"] = Object(Fields(
                ("type", OneOf("item-take")), ("item", Text), ("quantity", Count),
                ("allowPartial", Optional(Flag)))),
            ["item-drop"] = Object(Fields(
                ("type", OneOf("item-drop")), ("item", Text), ("quantity", Count),
                ("map", Optional(Text)), ("x", Number), ("y", Number))),
            ["money-give"] = Object(Fields(("type", OneOf("money-give")), ("amount", Count))),
            ["money-take"] = Object(Fields(
                ("type", OneOf("money-take")), ("amount", Count), ("allowPartial", Optional(Flag)))),
            ["flag"] = Object(Fields(
                ("type", OneOf("flag")), ("flag", Text), ("value", Optional(Flag)))),
            ["choice"] = Object(Fields(("type", OneOf("choice")), ("choice", Text), ("value", Text))),
            ["party-join"] = PartyAction,
            ["party-leave"] = PartyAction,
            ["health"] = Object(Fields(("type", OneOf("health")), ("member", Text), ("value", memberValue))),
            ["mana"] = Object(Fields(("type", OneOf("mana")), ("member", Text), ("value", memberValue))),
            ["party-control"] = Object(Fields(("type", OneOf("party-control")), ("member", Text))),
            ["equip"] = Object(Fields(
                ("type", OneOf("equip")), ("member", Text), ("item", Text),
                ("slot", OneOf(EquipmentSlots)), ("create", Optional(Flag)), ("ifPresent", Optional(Flag)))),
            ["unequip-destroy"] = Object(Fields(
                ("type", OneOf("unequip-destroy")), ("member", Text), ("item", Text),
                ("ifPresent", Optional(Flag)))),
            ["world"] = Object(Fields(("type", OneOf("world")), ("command", WorldCommand))),
            ["travel"] = Object(Fields(("type", OneOf("travel")), ("destination", Destination))),
            ["encounter"] = Object(Fields(("type", OneOf("encounter")), ("encounter", Encounter))),
        });
    }

    private static Dictionary<string, ShapeCheck> BuildPerformanceChecks()
    {
        return new Dictionary<string, ShapeCheck>(StringComparer.Ordinal)
        {
            ["instant-message"] = Object(Fields(("type", OneOf("instant-message")), ("text", Text))),
            ["stage"] = Object(Fields(
                ("type", OneOf("stage")), ("mapFromWorld", Optional(Flag)), ("resetActors", Optional(Flag)),
                ("map", Text), ("x", Number), ("y", Number), ("facing", Facing), ("playerVisible", Flag),
                ("retainPlayerPosition", Optional(Flag)), ("cast", ArrayOf(Member)),
                ("camera", Optional(Object(Fields(("x", Number), ("y", Number))))),
                ("curtain", Optional(Object(Fields(("opacity", Opacity), ("color", Integer))))),
                ("audio", Optional(Object(Fields(
                    ("music", Nullable(Text)), ("ambience", Nullable(Text)))))))),
            ["appear"] = Object(Fields(
                ("type", OneOf("appear")), ("target", Text), ("x", Number), ("y", Number),
                ("facing", Facing), ("mode", Optional(OneOf("reuse", "create"))),
                ("worldEntityId", Optional(Text)))),
            ["world-object"] = Object(Fields(
                ("type", OneOf("world-object")), ("target", Optional(Text)),
                ("mode", Optional(OneOf("reuse", "create"))),
                ("command", (value, path) =>
                {
                    WorldCommand(value, path);
                    Require(Str((value.Node as JsonObject)?["kind"]) is "place" or "show" or "hide" or "remove" or "remove-character",
                        path, "需要场景物件命令");
                }))),
            ["show"] = Object(Fields(("type", OneOf("show")), ("target", Text))),
            ["leave"] = Object(Fields(("type", OneOf("leave")), ("target", Text))),
            ["walk"] = Object(Fields(
                ("type", OneOf("walk")), ("target", Text), ("x", Number), ("y", Number),
                ("speedPxPerSecond", Positive), ("followCamera", Optional(Flag)), ("ensure", Optional(Flag)))),
            ["face"] = Object(Fields(
                ("type", OneOf("face")), ("target", Text), ("facing", Facing), ("ensure", Optional(Flag)))),
            ["pose"] = Object(Fields(
                ("type", OneOf("pose")), ("target", Text),
                ("pose", OneOf(["idle", "attack", .. PoseNames])),
                ("durationMs", NonNegative), ("hold", Optional(Flag)))),
            ["effect"] = Object(Fields(("type", OneOf("effect")), ("target", Text), ("effect", Text))),
            ["location-effect"] = Object(Fields(
                ("type", OneOf("location-effect")), ("effect", Text), ("x", Number), ("y", Number))),
            ["map-effect"] = Object(Fields(("type", OneOf("map-effect")), ("enabled", Flag))),
            ["sound"] = Object(Fields(("type", OneOf("sound")), ("sound", Text))),
            ["music"] = Object(Fields(("type", OneOf("music")), ("sound", Nullable(Text)))),
            ["refresh-appearance"] = Object(Fields(("type", OneOf("refresh-appearance")))),
            ["stop-audio"] = Object(Fields(("type", OneOf("stop-audio")))),
            ["camera"] = Object(Fields(
                ("type", OneOf("camera")), ("target", Text), ("motion", OneOf("cut", "pan")))),
            ["camera-point"] = Object(Fields(
                ("type", OneOf("camera-point")), ("x", Number), ("y", Number), ("motion", OneOf("cut", "pan")))),
            ["fade"] = Object(Fields(
                ("type", OneOf("fade")), ("opacity", Opacity), ("color", Integer), ("durationMs", NonNegative))),
            ["wait"] = Object(Fields(("type", OneOf("wait")), ("durationMs", NonNegative))),
            ["parallel"] = (value, path) =>
            {
                Object(Fields(("type", OneOf("parallel")), ("actions", ArrayOf((child, childPath) =>
                {
                    Performance(child, childPath);
                    Require(Str((child.Node as JsonObject)?["type"]) != "stage", childPath, "切图必须串行");
                }))))(value, path);

                var targets = Targets(value.Node as JsonObject);
                Require(targets.Count == targets.ToHashSet(StringComparer.Ordinal).Count,
                    path, "并行动作重复控制同一个人物或镜头");
            },
        };
    }

    private static List<string> Targets(JsonObject? action)
    {
        var result = new List<string>();
        if (action is null) return result;
        var type = Str(action["type"]);
        switch (type)
        {
            case "parallel":
                foreach (var child in action["actions"] as JsonArray ?? [])
                    result.AddRange(Targets(child as JsonObject));
                break;
            case "world-object":
            {
                var command = action["command"] as JsonObject;
                var kind = Str(command?["kind"]);
                if (kind == "place")
                {
                    if (Str(action["mode"]) == "create")
                        result.Add($"entity:{Str(command?["entity"]?["id"])}");
                    else
                        result.Add(Str(action["target"]) ??
                                   $"character:{Str(command?["entity"]?["characterId"]) ?? Str(command?["entity"]?["id"])}");
                }
                else if (kind == "remove-character")
                    result.Add($"character:{Str(command?["characterId"])}");
                else
                    result.Add($"entity:{Str(command?["id"])}");
                break;
            }
            case "camera" or "camera-point": result.Add("camera"); break;
            case "fade": result.Add("fade"); break;
            case "music": result.Add("music"); break;
            case "walk":
                if (IsTrue(action["followCamera"]))
                    result.AddRange([Str(action["target"]) ?? string.Empty, "camera"]);
                else goto default;
                break;
            default:
                if (action.ContainsKey("target") && type != "effect") result.Add(Str(action["target"]) ?? string.Empty);
                break;
        }

        return result;
    }
}
