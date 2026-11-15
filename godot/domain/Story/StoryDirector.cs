using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Inotia4.Domain.Story;

public readonly record struct HealthChange(bool Handled, string? ScriptId, string? CapturedEnemy = null);

public enum StoryStepKind
{
    Presentation,

    /// <summary>剧情战斗：视图层打完之后必须调 <see cref="StoryDirector.ResolveEncounter"/> 续走。</summary>
    Encounter,

    End,
}

public sealed class StoryStep
{
    private StoryStep(StoryStepKind kind, JsonElement item, string? note, EncounterPlan? encounter)
    {
        Kind = kind;
        Item = item;
        Note = note;
        Encounter = encounter;
    }

    public StoryStepKind Kind { get; }

    public JsonElement Item { get; }

    public EncounterPlan? Encounter { get; }

    public string? Note { get; }

    public string Type => Kind == StoryStepKind.Presentation ? StoryType.Of(Item) : "end";

    public static StoryStep Presentation(JsonElement item) =>
        new(StoryStepKind.Presentation, item, null, null);

    public static StoryStep EncounterStep(EncounterPlan plan) =>
        new(StoryStepKind.Encounter, default, null, plan);

    public static StoryStep End(string? note = null) => new(StoryStepKind.End, default, note, null);
}

public sealed class StoryDirector
{
    private const int MaxNodeHops = 512;

    private readonly CampaignContent _campaign;
    private readonly StoryCommandExecutor _executor;
    private readonly Session? _session;
    private readonly Dictionary<string, Dictionary<string, StoryNode>> _index = new();
    private readonly HashSet<string> _unknownConditions = new();

    /// <summary>这个遭遇实例 id 是否已经被登记过。同一段剧本被两个触发器各跑一次时得自己避让，
    /// 否则剧情世界会报「遭遇实例已经存在」。没有运行实例表可查时（域测试里），
    /// 第一场直接用基础 id，之后的按序号往后排。</summary>
    private readonly Func<string, bool>? _encounterExists;

    private StoryScript? _script;
    private StoryNode? _node;
    private int _indexInNode;

    private readonly StoryRunTable _runs = new();

    private bool _active;

    private string? _chosen;

    private StoryExit? _pendingEncounter;

    private string? _pendingEncounterId;

    private bool _skipping;

    /// <summary>
    /// 选项或确认面板正开着。这期间不给跳过，跳过按钮不出现，
    /// 否则玩家会把还没选的面板一起跳掉。
    /// </summary>
    private bool _modal;

    public StoryDirector(
        CampaignContent campaign, QuestJournal? journal = null,
        Character.Bag? inventory = null, Session? session = null,
        IWorldCommandSink? worldSink = null, Func<string, bool>? encounterExists = null)
    {
        _campaign = campaign;
        _session = session;
        Journal = journal ?? new QuestJournal();
        _executor = new StoryCommandExecutor(Journal, inventory, session, campaign, worldSink);
        _encounterExists = encounterExists;
    }

    public QuestJournal Journal { get; }

    /// <summary>
    /// 节点提交点：每到「这一节点的演出播完、命令也跑完」时触发一次。
    /// 世界场景挂它来刷新死亡回退的检查点——不然玩家在剧情里挂掉只能退回进图那一刻。
    /// 域测试里没人接，等于空操作。
    /// </summary>
    public Action? NodeCommitted { get; set; }

    public StoryRunSnapshot CaptureRuns() => _runs.Capture();

    public void RestoreRuns(StoryRunSnapshot? snapshot) => _runs.Restore(snapshot);

    public bool Active
    {
        get => _active;
        private set
        {
            if (_active && !value) _runs.Complete();
            _active = value;
        }
    }

    public StoryRunTable Runs => _runs;

    public string EventStatusOf(string eventId) => _runs.StatusOf(eventId);

    /// <summary>
    /// 读档前把导演清空：上一份存档里演到一半的运行不能带进新现场，
    /// 否则续播会被 <see cref="Active"/> 挡住，玩家会卡在一段永远不会推进的演出里。
    /// </summary>
    public void Reset()
    {
        _active = false;
        _script = null;
        _node = null;
        _indexInNode = 0;
        _pendingEncounter = null;
        _pendingEncounterId = null;
        _chosen = null;
        _skipping = false;
        _modal = false;
        ScriptId = null;
        StageMap = null;
        _runs.Restore(null);
    }

    public StoryCursorState? CaptureCursor()
    {
        if (!Active) return null;
        var script = ScriptId ?? string.Empty;
        var node = NodeId ?? string.Empty;
        if (script.Length == 0 || node.Length == 0) return null;

        return new StoryCursorState
        {
            Script = script,
            Node = node,
            EventId = _runs.Current?.EventId,
            EncounterId = _pendingEncounterId,
        };
    }

    public bool Resume(StoryCursorState cursor, out string? encounterId)
    {
        encounterId = null;
        _chosen = null;
        _pendingEncounter = null;
        _pendingEncounterId = null;
        _skipping = false;
        _modal = false;

        Enter(cursor.Script, cursor.Node);
        _runs.Begin(cursor.Script, NodeId ?? string.Empty, cursor.EventId,
            cursor.EventId ?? cursor.Script);

        if (cursor.EncounterId is not { Length: > 0 } saved) return true;
        if (_node?.Exit is not { Type: "waitEncounter" } exit) return false;

        var plan = BuildEncounter(exit.Encounter, saved);
        _pendingEncounter = exit;
        _pendingEncounterId = plan.Id;
        _runs.Waiting(true);
        encounterId = plan.Id;
        return true;
    }

    public string? ScriptId { get; private set; }

    public string? NodeId => _node?.Id;

    public string? CurrentMap { get; set; }

    public string? StageMap { get; set; }

    public string? PendingEncounterId => _pendingEncounterId;

    public IReadOnlyList<string> CommandLog => _executor.Applied;

    public IReadOnlyList<StoryWorldCommand> WorldLog => _executor.WorldLog;

    /// <summary>因为系统未接而跳过的命令类型。</summary>
    public IReadOnlyCollection<string> SkippedCommands => _executor.Skipped;

    public IReadOnlyCollection<string> UnknownConditions => _unknownConditions;

    public void Start(string scriptId, string? eventId = null)
    {
        _chosen = null;
        _pendingEncounter = null;
        _pendingEncounterId = null;
        _runs.Waiting(false);
        // 每段剧本都是新的一次播放：上一段按过的跳过不能带过来。
        _skipping = false;
        _modal = false;
        Enter(scriptId, null);
        _runs.Begin(scriptId, NodeId ?? string.Empty, eventId, eventId ?? scriptId);
    }

    public bool Skipping => _skipping;

    public bool CanSkip => Active && !_modal;

    public event Action? Skipped;

    public void Skip()
    {
        if (!CanSkip || _skipping) return;
        _skipping = true;
        Skipped?.Invoke();
    }

    public void BeginChoice()
    {
        _skipping = false;
        _modal = true;
    }

    public void EndChoice() => _modal = false;

    public void BeginConfirm() => _modal = true;

    public void EndConfirm() => _modal = false;

    /// <summary>
    /// 记录玩家的选择。演出项里的 <c>choice</c> 展示选项，出口里的 <c>choice</c> 用它查 <c>cases</c>；
    /// 两者之间隔着「节点演完」，所以只能先存着。
    /// </summary>
    public void Choose(string optionValue) => _chosen = optionValue;

    public bool ResolveEncounter(bool won)
    {
        if (_pendingEncounter is null) return false;

        var exit = _pendingEncounter;
        var id = _pendingEncounterId;
        _pendingEncounter = null;
        _pendingEncounterId = null;

        if (id is not null)
            _executor.ApplyWorld(new EncounterResultCommand(id, won ? "won" : "lost"));

        var target = won ? exit.Outcomes?.Won : exit.Outcomes?.Lost;
        if (target?.Script is null || target.Node is null)
        {
            Active = false;
            return false;
        }

        Enter(target.Script, target.Node);
        return true;
    }

    public StoryStep Next()
    {
        var hops = 0;

        while (Active)
        {
            if (_node is null) break;

            if (_indexInNode < _node.Presentation.Length)
                return StoryStep.Presentation(_node.Presentation[_indexInNode++]);

            _executor.ApplyBatch(_node.Commands.Where(command => StoryType.Of(command) != "world"));

            // 节点提交点：这一节点的演出已经播完、命令也都落地了，世界场景据此刷新
            // 「死亡回退」的检查点——不然玩家在剧情里挂掉只能退回进图那一刻，整段进度白走。
            NodeCommitted?.Invoke();

            if (++hops > MaxNodeHops)
            {
                Active = false;
                return StoryStep.End($"节点跳转超过 {MaxNodeHops} 次，疑似自环");
            }

            var exit = _node.Exit;
            switch (exit.Type)
            {
                case "next":
                    if (exit.Target?.Script is null || exit.Target.Node is null)
                    {
                        Active = false;
                        return StoryStep.End($"{_node.Id} 的 next 出口缺 target");
                    }

                    Enter(exit.Target.Script, exit.Target.Node);
                    continue;

                case "end":
                    Active = false;
                    return StoryStep.End();

                case "branch":
                {
                    var taken = Evaluate(exit.Condition) ? exit.Then : exit.Otherwise;
                    if (taken?.Script is null || taken.Node is null)
                    {
                        Active = false;
                        return StoryStep.End($"{_node.Id} 的 branch 没有可走的去处");
                    }

                    Enter(taken.Script, taken.Node);
                    continue;
                }

                case "choice":
                {
                    if (exit.Cases.Count == 0)
                    {
                        Active = false;
                        return StoryStep.End($"{_node.Id} 的 choice 没有落点");
                    }

                    var key = _chosen is not null && exit.Cases.ContainsKey(_chosen)
                        ? _chosen
                        : exit.Cases.Keys.First();

                    _chosen = null;
                    if (exit.Choice is not null) Journal.RecordChoice(exit.Choice, key);
                    var picked = exit.Cases[key];
                    if (picked.Script is null || picked.Node is null)
                    {
                        Active = false;
                        return StoryStep.End($"{_node.Id} 的 choice 落点 {key} 不完整");
                    }

                    Enter(picked.Script, picked.Node);
                    continue;
                }

                case "waitEncounter":
                {
                    var plan = BuildEncounter(exit.Encounter);
                    _executor.ApplyWorld(new EncounterCommand(new StoryEncounter
                    {
                        Id = plan.Id,
                        DefinitionId = plan.DefinitionId,
                        Map = plan.Map,
                        Status = "active",
                        Spawns = plan.Spawns
                            .Select(spawn => new StoryEncounterSpawn
                            {
                                Id = spawn.Id, EnemyId = spawn.EnemyId, X = spawn.X, Y = spawn.Y,
                            })
                            .ToList(),
                    }));

                    _pendingEncounter = exit;
                    _pendingEncounterId = plan.Id;
                    _runs.Waiting(true);
                    return StoryStep.EncounterStep(plan);
                }

                default:
                    Active = false;
                    return StoryStep.End($"出口 {exit.Type} 还未实现（{_node.Id}）");
            }
        }

        Active = false;
        return StoryStep.End();
    }

    /// <summary>
    /// 判定分支条件。条件求值只有这一份，剧本出口、任务可见性、NPC 摆放共用它
    /// （见 <see cref="StoryCondition"/>）。遇到不认识的就当不成立并记下来——
    /// 宁可走「否则」分支也不要假装条件成立把剧情带错。
    /// </summary>
    public bool Evaluate(JsonElement condition) =>
        StoryCondition.Evaluate(condition, Journal, name => _unknownConditions.Add(name));

    /// <summary>
    /// 伤害跨过 30% 生命值（或致死一击）时的剧情门。
    ///
    /// 先尝试把任务怪换成捕捉目标；换不成再看有没有 <c>health-below-30:&lt;敌人&gt;</c> 的战斗事件。
    /// 被接管时调用方不该再把它当普通击杀处理。
    /// </summary>
    public HealthChange HealthChanged(
        string enemy, float before, float after, float maximum, Session.EnemySource? source = null)
    {
        if (_session is null || after >= before || maximum <= 0) return new HealthChange(false, null);

        var threshold = (int)Math.Truncate(maximum * 3 / 10);

        var capture = after <= 0 || (before > threshold && after <= threshold);
        if (source is { } origin && capture && _session.WeakenQuestTarget(enemy, origin))
            return new HealthChange(true, null, origin.Id);

        var key = "health-below-30:" + enemy;
        var currentMap = _session.StoryWorld.CurrentLocation?.Map;
        var trigger = _campaign.EventTable.Triggers.FirstOrDefault(candidate =>
            candidate.Source == "battle" && candidate.Key == key &&
            (candidate.Map.Length == 0 || string.IsNullOrEmpty(currentMap) || candidate.Map == currentMap) &&
            (candidate.Condition.ValueKind != JsonValueKind.Object || Evaluate(candidate.Condition)));
        if (trigger is null) return new HealthChange(false, null);

        if (Journal.EventDone(trigger.Id) && !trigger.Repeat) return new HealthChange(false, null);
        if (Journal.EventStarted(trigger.Id) && !Journal.EventDone(trigger.Id))
            return new HealthChange(true, null);
        if (before <= threshold || after > threshold) return new HealthChange(false, null);

        return new HealthChange(true, trigger.Script);
    }

    private static EncounterPlan ParseEncounter(JsonElement encounter)
    {
        var spawns = new List<EncounterSpawn>();

        if (encounter.ValueKind == JsonValueKind.Object &&
            encounter.TryGetProperty("spawns", out var list) &&
            list.ValueKind == JsonValueKind.Array)
        {
            foreach (var spawn in list.EnumerateArray())
                spawns.Add(new EncounterSpawn(
                    StoryType.Text(spawn, "id"),
                    StoryType.Text(spawn, "enemyId"),
                    StoryType.Float(spawn, "x"),
                    StoryType.Float(spawn, "y")));
        }

        return new EncounterPlan
        {
            Id = StoryType.Text(encounter, "id"),
            Map = StoryType.Text(encounter, "map"),
            Spawns = spawns,
        };
    }

    private EncounterPlan BuildEncounter(JsonElement raw, string? reuseId = null)
    {
        var plan = ParseEncounter(raw);
        var definitionId = plan.Id;
        var map = plan.Map;
        var fromStage = StoryType.Bool(raw, "mapFromStage");
        if (fromStage) map = StageMap ?? CurrentMap ?? map;

        return new EncounterPlan
        {
            Id = reuseId ?? UniqueEncounterId($"{ScriptId}:{_node?.Id}:{definitionId}"),
            DefinitionId = definitionId,
            Map = map,
            Spawns = plan.Spawns,
        };
    }

    private string UniqueEncounterId(string baseId)
    {
        if (_encounterExists is null) return baseId;

        var id = baseId;
        for (var serial = 2; _encounterExists(id); serial += 1)
            id = $"{baseId}#{serial}";

        return id;
    }

    private void Enter(string scriptId, string? nodeId)
    {
        var script = _campaign.GetScript(scriptId);
        _script = script;
        ScriptId = scriptId;

        var wanted = nodeId ?? script.Entry;
        var node = FindNode(script, wanted)
                   ?? throw new KeyNotFoundException($"剧本 {scriptId} 里没有节点 {wanted}");

        // world 命令（place/移除/可见性/travel）在演出**之前**生效：
        // 先把人摆到位，appear/walk 才有对象和坐标。
        // 这类命令也不参与「演完再执行」的批次，否则演员永远站在默认位置上。
        // 摆位同样走整批事务：一条失败整批不落地，不会摆出一半人马。
        _executor.ApplyBatch(node.Commands.Where(command => StoryType.Of(command) == "world"));

        _node = node;
        _indexInNode = 0;
        Active = true;
        _runs.MoveTo(scriptId, node.Id);
    }

    private StoryNode? FindNode(StoryScript script, string nodeId)
    {
        if (!_index.TryGetValue(ScriptId!, out var nodes))
        {
            nodes = new Dictionary<string, StoryNode>(script.Nodes.Length);
            foreach (var node in script.Nodes) nodes[node.Id] = node;
            _index[ScriptId!] = nodes;
        }

        return nodes.GetValueOrDefault(nodeId);
    }
}

public static class StoryType
{
    public static string Of(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty("type", out var type) &&
        type.ValueKind == JsonValueKind.String
            ? type.GetString() ?? string.Empty
            : string.Empty;

    public static string Text(JsonElement element, string key, string fallback = "") =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(key, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    public static int Int(JsonElement element, string key, int fallback = 0) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(key, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number)
            ? number
            : fallback;

    public static float Float(JsonElement element, string key, float fallback = 0f) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(key, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetSingle(out var number)
            ? number
            : fallback;

    public static bool Bool(JsonElement element, string key, bool fallback = false) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(key, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;
}
