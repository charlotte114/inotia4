using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Inotia4.Domain.Story;

/// <summary>
/// 一条任务的进度：状态、周期、完成过的周期数、每个目标的计数。
///
/// 条目一旦建成就不可变（靠 <c>init</c> 写只读）：
/// 改状态一律换一个新对象，所以未改动的条目可以在计划之间、在预演沙盒与真实任务簿之间
/// 按引用共享，不必克隆。
/// </summary>
public sealed record QuestProgress
{
    private static readonly IReadOnlyDictionary<string, int> NoGoals = new Dictionary<string, int>();

    public QuestStatus Status { get; init; } = QuestStatus.Locked;

    public int Cycle { get; init; }

    public int CompletedCycles { get; init; }

    public IReadOnlyDictionary<string, int> Goals { get; init; } = NoGoals;

    public int GoalCount(string goalId) => Goals.TryGetValue(goalId, out var value) ? value : 0;
}

public sealed class QuestBookState
{
    public const int CurrentVersion = 5;

    public int Version { get; init; } = CurrentVersion;

    public List<KeyValuePair<string, QuestProgress>> Quests { get; init; } = [];

    public List<string> RunningQuestIds { get; init; } = [];

    public List<KeyValuePair<string, int>> FactSequences { get; init; } = [];

    public List<string> DebugPins { get; init; } = [];

    public QuestBookState Clone()
    {
        var quests = new List<KeyValuePair<string, QuestProgress>>(Quests.Count);
        foreach (var (id, progress) in Quests)
            quests.Add(new KeyValuePair<string, QuestProgress>(id, new QuestProgress
            {
                Status = progress.Status,
                Cycle = progress.Cycle,
                CompletedCycles = progress.CompletedCycles,
                Goals = new Dictionary<string, int>(progress.Goals),
            }));

        return new QuestBookState
        {
            Version = Version,
            Quests = quests,
            RunningQuestIds = [.. RunningQuestIds],
            FactSequences = [.. FactSequences],
            DebugPins = [.. DebugPins],
        };
    }
}

/// <summary>批次回滚基线：只记状态根，条目不可变所以不需要复制。</summary>
public sealed class QuestBookCheckpoint
{
    internal Dictionary<string, QuestProgress> States { get; init; } = [];

    internal List<string> RunningIds { get; init; } = [];

    internal Dictionary<string, int> FactSequences { get; init; } = [];

    internal HashSet<string> DebugPins { get; init; } = [];
}

public sealed class QuestFact
{
    public required string Kind { get; init; }

    public required string Target { get; init; }

    public string? CharacterId { get; init; }

    public int? Quantity { get; init; }

    public required QuestFactOrigin Occurrence { get; init; }
}

/// <summary>事实来源与序号。序号必须单调递增，同一来源重复的旧序号会被忽略。</summary>
public sealed record QuestFactOrigin(string Source, int Sequence);

public enum QuestCommandKind
{
    Accept,
    Complete,
    ScriptReady,
    ScriptComplete,
    Reset,
    Fact,
    PinActive,
    UnpinActive,
    ResetSideQuests,
    Refresh,
}

public sealed class QuestCommand
{
    public required QuestCommandKind Kind { get; init; }

    public string Quest { get; init; } = string.Empty;

    public int? Cycle { get; init; }

    public string? Source { get; init; }

    public QuestFact? Fact { get; init; }

    public static QuestCommand Accept(string quest, int? cycle = null, string? source = null) =>
        new() { Kind = QuestCommandKind.Accept, Quest = quest, Cycle = cycle, Source = source };

    public static QuestCommand Complete(string quest, int? cycle = null) =>
        new() { Kind = QuestCommandKind.Complete, Quest = quest, Cycle = cycle };

    public static QuestCommand ScriptReady(string quest, int? cycle = null) =>
        new() { Kind = QuestCommandKind.ScriptReady, Quest = quest, Cycle = cycle };

    public static QuestCommand ScriptComplete(string quest, int? cycle = null) =>
        new() { Kind = QuestCommandKind.ScriptComplete, Quest = quest, Cycle = cycle };

    public static QuestCommand Reset(string quest) =>
        new() { Kind = QuestCommandKind.Reset, Quest = quest };

    public static QuestCommand EmitFact(QuestFact fact) =>
        new() { Kind = QuestCommandKind.Fact, Fact = fact };

    public static QuestCommand PinActive(string quest) =>
        new() { Kind = QuestCommandKind.PinActive, Quest = quest };

    public static QuestCommand UnpinActive(string quest) =>
        new() { Kind = QuestCommandKind.UnpinActive, Quest = quest };

    public static QuestCommand ResetSideQuests() => new() { Kind = QuestCommandKind.ResetSideQuests };

    public static QuestCommand Refresh() => new() { Kind = QuestCommandKind.Refresh };
}

public sealed class RewardReceipt
{
    public int Experience { get; init; }

    public int Money { get; init; }

    public List<RewardItem> Items { get; } = [];
}

public sealed record RewardItem(string Item, int Quantity);

public sealed record QuestSettlement(string Quest, int Cycle, RewardReceipt Reward);

public sealed class QuestPlan
{
    internal Dictionary<string, QuestProgress> States { get; init; } = [];

    internal List<string> RunningIds { get; init; } = [];

    internal Dictionary<string, int> FactSequences { get; init; } = [];

    internal HashSet<string> DebugPins { get; init; } = [];

    public required int BaseRevision { get; init; }

    public required bool Changed { get; init; }

    public required IReadOnlyList<RewardItem> ItemGrants { get; init; }

    public required IReadOnlyList<RewardItem> ItemRemovals { get; init; }

    public required IReadOnlyList<QuestSettlement> Rewards { get; init; }

    public QuestBookState NextState() => new()
    {
        Version = QuestBookState.CurrentVersion,
        Quests = [.. States],
        RunningQuestIds = [.. RunningIds],
        FactSequences = [.. FactSequences],
        DebugPins = [.. DebugPins.OrderBy(id => id, StringComparer.Ordinal)],
    };
}

public sealed class QuestBook
{
    private readonly Dictionary<string, QuestDefinition> _definitions;
    private readonly Func<QuestContext> _context;
    private readonly IReadOnlySet<string> _sideQuests;

    private Dictionary<string, QuestProgress> _states;
    private List<string> _runningIds;
    private Dictionary<string, int> _factSequences;
    private HashSet<string> _debugPins;
    private int _revision;

    private readonly Dictionary<QuestPlan, object> _plans = [];

    public QuestBook(
        IReadOnlyList<QuestDefinition> definitions,
        Func<QuestContext> context,
        IReadOnlySet<string> sideQuests,
        QuestBookState? state = null,
        QuestBook? shareFrom = null)
        : this(PrepareDefinitions(definitions), context, sideQuests, state, shareFrom)
    {
    }

    private QuestBook(
        Dictionary<string, QuestDefinition> definitions,
        Func<QuestContext> context,
        IReadOnlySet<string> sideQuests,
        QuestBookState? state,
        QuestBook? shareFrom)
    {
        _context = context;
        _sideQuests = sideQuests;
        _definitions = definitions;

        // 派生沙盒：与来源共享同一份不可变状态根，不克隆、也不跑初始 refresh。
        if (shareFrom is not null)
        {
            _states = shareFrom._states;
            _runningIds = [.. shareFrom._runningIds];
            _factSequences = new Dictionary<string, int>(shareFrom._factSequences);
            _debugPins = [.. shareFrom._debugPins];
            _revision = shareFrom._revision;
            return;
        }

        _states = [];
        _runningIds = [];
        _factSequences = [];
        _debugPins = [];
        foreach (var (id, quest) in _definitions) _states[id] = InitialProgress(quest);

        if (state is not null)
        {
            InstallTrustedState(state);
            return;
        }

        Commit(Plan(QuestCommand.Refresh()));
    }

    public int Version => _revision;

    public QuestDefinition? Definition(string id) => _definitions.GetValueOrDefault(id);

    public QuestProgress? State(string id) => _states.GetValueOrDefault(id);

    public IReadOnlyDictionary<string, QuestProgress> ProgressEntries() => _states;

    public IReadOnlyList<KeyValuePair<string, int>> FactSequenceEntries() => [.. _factSequences];

    public QuestStatus Status(string id) => _states.TryGetValue(id, out var state) ? state.Status : QuestStatus.Locked;

    public bool Pinned(string id) => _debugPins.Contains(id);

    public void PinDebug(string id) => Commit(Plan(QuestCommand.PinActive(id)));

    public void UnpinDebug(string id) => Commit(Plan(QuestCommand.UnpinActive(id)));

    public bool Accepted(string id) => Status(id) is QuestStatus.Active or QuestStatus.Ready or QuestStatus.Completed;

    public int CompletedCycles(string id) => _states.TryGetValue(id, out var state) ? state.CompletedCycles : 0;

    public bool Visible(string id) => _definitions.TryGetValue(id, out var quest) && QuestIsVisible(quest);

    public IReadOnlyList<string> RunningQuestIds => [.. _runningIds];

    public static bool QuestIsVisible(QuestDefinition quest) =>
        !quest.Hidden && quest.Completion != "internal";

    public QuestBookState CaptureState() => new()
    {
        Version = QuestBookState.CurrentVersion,
        Quests = [.. _states],
        RunningQuestIds = [.. _runningIds],
        FactSequences = [.. _factSequences],
        DebugPins = [.. _debugPins.OrderBy(id => id, StringComparer.Ordinal)],
    };

    public int FactSequence(string source) => _factSequences.GetValueOrDefault(source);

    public QuestBookCheckpoint Checkpoint() => new()
    {
        States = _states,
        RunningIds = [.. _runningIds],
        FactSequences = new Dictionary<string, int>(_factSequences),
        DebugPins = [.. _debugPins],
    };

    public void RollbackTo(QuestBookCheckpoint checkpoint)
    {
        _states = checkpoint.States;
        _runningIds = [.. checkpoint.RunningIds];
        _factSequences = new Dictionary<string, int>(checkpoint.FactSequences);
        _debugPins = [.. checkpoint.DebugPins];
        _revision++;
    }

    public QuestPlan Plan(QuestCommand command) => Plan([command]);

    public QuestPlan Plan(IReadOnlyList<QuestCommand> commands)
    {
        var states = new Dictionary<string, QuestProgress>(_states);
        var runningIds = new List<string>(_runningIds);
        var factSequences = new Dictionary<string, int>(_factSequences);
        var debugPins = new HashSet<string>(_debugPins);
        var context = _context();
        var dirty = new HashSet<string>();

        void Write(string id, QuestStatus? status = null, int? cycle = null, int? completedCycles = null,
            IReadOnlyDictionary<string, int>? goals = null)
        {
            var current = states[id];
            var merged = new QuestProgress
            {
                Status = status ?? current.Status,
                Cycle = cycle ?? current.Cycle,
                CompletedCycles = completedCycles ?? current.CompletedCycles,
                Goals = goals ?? current.Goals,
            };
            if (SameProgress(current, merged)) return;
            states[id] = merged;
            dirty.Add(id);
        }

        var grants = new Dictionary<string, int>();
        var removals = new Dictionary<string, int>();
        var balances = new Dictionary<string, int>();
        var rewards = new List<QuestSettlement>();

        void RemoveRunning(string id)
        {
            var index = runningIds.IndexOf(id);
            if (index < 0) return;
            var last = runningIds[^1];
            runningIds.RemoveAt(runningIds.Count - 1);
            if (index < runningIds.Count) runningIds[index] = last;
        }

        int Count(string item) =>
            balances.TryGetValue(item, out var value) ? value : context.ItemCount(item);

        static void Add(Dictionary<string, int> map, string item, int quantity) =>
            map[item] = map.GetValueOrDefault(item) + quantity;

        void Grant(string item, int quantity)
        {
            Add(grants, item, quantity);
            balances[item] = Count(item) + quantity;
        }

        var conditionContext = new QuestContext
        {
            StatusOf = id => states.TryGetValue(id, out var state) ? state.Status : QuestStatus.Locked,
            CompletedCyclesOf = id => states.TryGetValue(id, out var state) ? state.CompletedCycles : 0,
            EventStatusOf = context.EventStatusOf,
            ChoiceOf = context.ChoiceOf,
            FlagOf = context.FlagOf,
            WorldOf = context.WorldOf,
            ItemCount = Count,
            EquippedItemCount = context.EquippedItemCount,
            ClassId = context.ClassId,
            Unknown = context.Unknown,
        };

        void Refresh()
        {
            // 解锁可能依赖同一批里另一个任务变成可接，所以要多跑几轮直到不再翻转。
            for (var pass = 0; pass <= states.Count; pass++)
            {
                var availabilityChanged = false;
                foreach (var (id, quest) in _definitions)
                {
                    var state = states[id];
                    var available = quest.Completion != "internal" &&
                                    StoryCondition.Evaluate(quest.AvailableWhen, conditionContext, context.Unknown);

                    var status = state.Status;
                    if (status == QuestStatus.Available && !available)
                    {
                        status = QuestStatus.Locked;
                        availabilityChanged = true;
                    }
                    else if (status == QuestStatus.Locked && available)
                    {
                        status = QuestStatus.Available;
                        availabilityChanged = true;
                    }

                    Dictionary<string, int>? draft = null;
                    if (status is QuestStatus.Active or QuestStatus.Ready && quest.Completion == "all")
                    {
                        foreach (var goal in quest.Goals)
                        {
                            if (goal.Kind != "item") continue;
                            var value = Math.Min(goal.Quantity, Count(goal.Target));
                            if (draft is null && state.Goals.TryGetValue(goal.Id, out var held) && held == value)
                                continue;
                            draft ??= new Dictionary<string, int>(state.Goals);
                            draft[goal.Id] = value;
                        }

                        var goals = (IReadOnlyDictionary<string, int>?)draft ?? state.Goals;
                        var ready = true;
                        foreach (var goal in quest.Goals)
                        {
                            if (goals.GetValueOrDefault(goal.Id) >= goal.Quantity) continue;

                            if (goal.Kind == "item" && quest.ItemCompletion == "equipped-item-shortcut" &&
                                (context.EquippedItemCount?.Invoke(goal.Target) ?? 0) > 0)
                                break;

                            ready = false;
                            break;
                        }

                        status = ready ? QuestStatus.Ready : QuestStatus.Active;

                        if (debugPins.Contains(id)) status = QuestStatus.Active;
                    }

                    var nextGoals = (IReadOnlyDictionary<string, int>?)draft ?? state.Goals;
                    if (status == state.Status && ReferenceEquals(nextGoals, state.Goals)) continue;
                    Write(id, status, goals: nextGoals);
                }

                if (!availabilityChanged) break;
            }
        }

        void Settle(QuestDefinition quest, QuestProgress state)
        {
            var required = new Dictionary<string, int>();
            foreach (var item in quest.HandoverItems)
            {
                var amount = item.Removal == "all-matching" ? Count(item.Item) : item.Quantity;
                Add(required, item.Item, amount);
            }

            foreach (var (item, quantity) in required)
                if (Count(item) < quantity)
                    throw new InvalidOperationException($"交付物品 {item} 不足");

            foreach (var (item, quantity) in required)
            {
                balances[item] = Count(item) - quantity;
                Add(removals, item, quantity);
            }

            var reward = quest.Rewards;
            var receipt = new RewardReceipt
            {
                Experience = reward?.Experience ?? 0,
                Money = Economy.MoneyGain(reward?.Money ?? 0),
            };
            foreach (var item in reward?.Items ?? [])
            {
                if (item.ClassIds.Length > 0 &&
                    (context.ClassId is null || !item.ClassIds.Contains(context.ClassId))) continue;
                receipt.Items.Add(new RewardItem(item.Item, item.Quantity));
            }

            rewards.Add(new QuestSettlement(quest.Id, state.Cycle, receipt));
            foreach (var item in receipt.Items) Grant(item.Item, item.Quantity);

            RemoveRunning(quest.Id);
            if (quest.Repeat)
            {
                Write(quest.Id, status: QuestStatus.Locked, cycle: state.Cycle + 1,
                    completedCycles: state.CompletedCycles + 1, goals: InitialProgress(quest).Goals);
            }
            else
            {
                Write(quest.Id, status: QuestStatus.Completed,
                    completedCycles: state.CompletedCycles + 1);
            }
        }

        // 结算/重置命令先解除调试钉住，再让 refresh 按真实目标重算状态；
        // 否则 complete 看到的是钉住的进行中，永远过不了可交付检查。
        foreach (var command in commands)
        {
            if (command.Kind == QuestCommandKind.ResetSideQuests)
            {
                foreach (var id in debugPins.Where(_sideQuests.Contains).ToList()) debugPins.Remove(id);
            }
            else if (command.Kind is QuestCommandKind.Complete or QuestCommandKind.ScriptReady
                     or QuestCommandKind.ScriptComplete or QuestCommandKind.Reset)
            {
                debugPins.Remove(command.Quest);
            }
        }

        Refresh();

        foreach (var command in commands)
        {
            switch (command.Kind)
            {
                case QuestCommandKind.Refresh:
                    Refresh();
                    continue;

                case QuestCommandKind.Fact:
                {
                    var fact = command.Fact ?? throw new InvalidOperationException("任务事实缺少内容");
                    var (source, sequence) = (fact.Occurrence.Source, fact.Occurrence.Sequence);
                    if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(fact.Target))
                        throw new InvalidOperationException("任务事实缺少来源或目标");
                    if (sequence < 1) throw new InvalidOperationException("事实序号无效");
                    if ((fact.Quantity ?? 1) < 1) throw new InvalidOperationException("事实数量无效");

                    if (sequence <= factSequences.GetValueOrDefault(source)) continue;
                    factSequences[source] = sequence;

                    foreach (var (id, quest) in _definitions)
                    {
                        var state = states[id];
                        if (state.Status != QuestStatus.Active || quest.Completion != "all") continue;

                        Dictionary<string, int>? draft = null;
                        foreach (var goal in quest.Goals)
                        {
                            if (goal.Kind != fact.Kind) continue;

                            // 目标与事实都写了 characterId 时按它比，否则按 target 比。
                            var matches = fact.CharacterId is not null && goal.CharacterId is not null
                                ? goal.CharacterId == fact.CharacterId
                                : goal.Target == fact.Target;
                            if (!matches) continue;

                            var value = Math.Min(goal.Quantity,
                                state.Goals.GetValueOrDefault(goal.Id) + (fact.Quantity ?? 1));
                            if (draft is null && state.Goals.TryGetValue(goal.Id, out var held) && held == value)
                                continue;
                            draft ??= new Dictionary<string, int>(state.Goals);
                            draft[goal.Id] = value;
                        }

                        if (draft is not null) Write(id, goals: draft);
                    }

                    Refresh();
                    continue;
                }

                case QuestCommandKind.ResetSideQuests:
                {
                    for (var index = 0; index < runningIds.Count;)
                    {
                        var id = runningIds[index];
                        if (!_sideQuests.Contains(id))
                        {
                            index++;
                            continue;
                        }

                        var quest = _definitions[id];
                        var state = states[id];
                        foreach (var item in quest.HandoverItems)
                        {
                            var held = Count(item.Item);
                            var quantity = item.Removal == "all-matching" ? held : Math.Min(held, item.Quantity);
                            if (quantity == 0) continue;
                            balances[item.Item] = held - quantity;
                            Add(removals, item.Item, quantity);
                        }

                        Write(id, status: QuestStatus.Locked, cycle: state.Cycle + 1,
                            goals: InitialProgress(quest).Goals);
                        RemoveRunning(id);
                    }

                    Refresh();
                    continue;
                }

                case QuestCommandKind.PinActive:
                {
                    if (!_definitions.TryGetValue(command.Quest, out var pinned) ||
                        !states.TryGetValue(command.Quest, out var pinnedState))
                        throw new InvalidOperationException($"未知任务 {command.Quest}");
                    if (pinned.Completion != "all") throw new InvalidOperationException("只有目标计数任务能钉住进行中");
                    if (pinnedState.Status is not (QuestStatus.Active or QuestStatus.Ready))
                        throw new InvalidOperationException("只能钉住已接取的任务");

                    if (debugPins.Add(command.Quest)) Refresh();
                    continue;
                }

                case QuestCommandKind.UnpinActive:
                {
                    if (!_definitions.ContainsKey(command.Quest))
                        throw new InvalidOperationException($"未知任务 {command.Quest}");
                    if (debugPins.Remove(command.Quest)) Refresh();
                    continue;
                }
            }

            if (!_definitions.TryGetValue(command.Quest, out var currentQuest) ||
                !states.TryGetValue(command.Quest, out var currentState))
                throw new InvalidOperationException($"未知任务 {command.Quest}");
            if (command.Cycle is not null && command.Cycle != currentState.Cycle)
                throw new InvalidOperationException($"任务 {command.Quest} 周期已改变");

            switch (command.Kind)
            {
                case QuestCommandKind.Accept:
                {
                    var quest = currentQuest;
                    var state = currentState;
                    if (quest.Completion == "internal" && command.Source != "script")
                        throw new InvalidOperationException("内部任务只能由剧情授予");
                    if (state.Status == QuestStatus.Active) break;
                    if (state.Status == QuestStatus.Ready)
                    {
                        Write(quest.Id, status: QuestStatus.Active);
                        break;
                    }

                    var goals = state.Status == QuestStatus.Completed
                        ? InitialProgress(quest).Goals
                        : state.Goals;
                    if (state.Status != QuestStatus.Completed && state.Status != QuestStatus.Available &&
                        !(quest.Completion == "internal" && command.Source == "script" &&
                          state.Status == QuestStatus.Locked))
                        throw new InvalidOperationException("任务尚不可接取");

                    var cycle = state.Cycle + (state.Status == QuestStatus.Completed ? 1 : 0);
                    Write(quest.Id, status: QuestStatus.Active, cycle: cycle, goals: goals);
                    if (!runningIds.Contains(quest.Id)) runningIds.Add(quest.Id);

                    foreach (var item in quest.OnAcceptItems) Grant(item.Item, item.Quantity);

                    if (quest.Completion == "talk") Write(quest.Id, status: QuestStatus.Ready);
                    break;
                }

                case QuestCommandKind.Complete:
                    if (currentState.Status != QuestStatus.Ready)
                        throw new InvalidOperationException("任务目标尚未达成");
                    Settle(currentQuest, currentState);
                    break;

                case QuestCommandKind.ScriptReady:
                {
                    var quest = currentQuest;
                    var state = currentState;
                    if (state.Status == QuestStatus.Ready) break;
                    if (quest.Completion == "all") throw new InvalidOperationException("常规任务通过目标进度达成");

                    var started = state.Status is not (QuestStatus.Active or QuestStatus.Ready);
                    var goals = started ? InitialProgress(quest).Goals : state.Goals;
                    if (started && !runningIds.Contains(quest.Id)) runningIds.Add(quest.Id);

                    var cycle = state.Cycle + (state.Status == QuestStatus.Completed ? 1 : 0);
                    Write(quest.Id, status: QuestStatus.Ready, cycle: cycle, goals: goals);
                    break;
                }

                case QuestCommandKind.ScriptComplete:
                    if (currentState.Status == QuestStatus.Completed) break;
                    Settle(currentQuest, currentState);
                    break;

                case QuestCommandKind.Reset:
                    Write(currentQuest.Id, status: QuestStatus.Locked, cycle: currentState.Cycle + 1,
                        goals: InitialProgress(currentQuest).Goals);
                    RemoveRunning(currentQuest.Id);
                    break;
            }

            Refresh();
        }

        var changed = !SameList(runningIds, _runningIds) ||
                      !SameNumbers(factSequences, _factSequences) ||
                      !SameStringSet(debugPins, _debugPins) ||
                      dirty.Any(id => !SameProgress(states[id], _states.GetValueOrDefault(id)));
        var delta = new Dictionary<string, int>();
        foreach (var item in grants.Keys.Union(removals.Keys))
            delta[item] = grants.GetValueOrDefault(item) - removals.GetValueOrDefault(item);

        var plan = new QuestPlan
        {
            States = states,
            RunningIds = runningIds,
            FactSequences = factSequences,
            DebugPins = debugPins,
            BaseRevision = _revision,
            Changed = changed,
            ItemGrants = [.. delta.Where(pair => pair.Value > 0)
                .Select(pair => new RewardItem(pair.Key, pair.Value))],
            ItemRemovals = [.. delta.Where(pair => pair.Value < 0)
                .Select(pair => new RewardItem(pair.Key, -pair.Value))],
            Rewards = rewards,
        };

        _plans[plan] = true;
        return plan;
    }

    public void Commit(QuestPlan plan)
    {
        if (!_plans.Remove(plan)) throw new InvalidOperationException("任务计划已失效");
        if (plan.BaseRevision != _revision) throw new InvalidOperationException("任务计划已失效");
        if (!plan.Changed) return;

        // 直接接管计划那份容器：条目不可变，所以没有「谁还会再写它」的问题。
        _states = plan.States;
        _runningIds = plan.RunningIds;
        _factSequences = plan.FactSequences;
        _debugPins = plan.DebugPins;
        _revision++;
    }

    /// <summary>
    /// 派生一个共享同一份状态根的沙盒：不克隆状态、也不跑初始 refresh。两边都可以照常
    /// 计划/提交——写入是换条目而不是改条目，所以预演不会碰到来源簿。
    /// </summary>
    public QuestBook Fork(Func<QuestContext> context) =>
        new(_definitions, context, _sideQuests, state: null, shareFrom: this);

    public void Adopt(QuestBook other)
    {
        if (!ReferenceEquals(other._definitions, _definitions))
            throw new InvalidOperationException("任务定义不同，不能接管状态");

        _states = other._states;
        _runningIds = [.. other._runningIds];
        _factSequences = new Dictionary<string, int>(other._factSequences);
        _debugPins = [.. other._debugPins];
        _revision++;
    }

    /// <summary>
    /// 安装一份**由本类 <see cref="CaptureState"/> 产出、且出自同一份任务定义**的状态。
    /// 外部输入——读档、检查点、回滚——必须走 <see cref="RestoreState"/>，那里的校验是防篡改的。
    /// </summary>
    public void InstallTrustedState(QuestBookState state)
    {
        if (state.Version != QuestBookState.CurrentVersion)
            throw new InvalidOperationException("任务快照版本不支持");

        var states = state.Quests.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var id in states.Keys)
            if (!_definitions.ContainsKey(id))
                throw new InvalidOperationException($"任务快照引用未知任务 {id}");

        foreach (var (id, quest) in _definitions)
            if (!states.ContainsKey(id)) states[id] = InitialProgress(quest);

        _states = states;
        _runningIds = [.. state.RunningQuestIds];
        _factSequences = state.FactSequences.ToDictionary(pair => pair.Key, pair => pair.Value);
        _debugPins = [.. state.DebugPins];
        _revision++;
    }

    public void RestoreState(QuestBookState state)
    {
        if (state.Version != QuestBookState.CurrentVersion)
            throw new InvalidOperationException("任务快照版本不支持");

        var input = state.Clone();
        if (input.Quests.Select(pair => pair.Key).Distinct().Count() != input.Quests.Count)
            throw new InvalidOperationException("任务快照标识重复");

        var states = input.Quests.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var (id, progress) in states)
        {
            if (!_definitions.TryGetValue(id, out var quest))
                throw new InvalidOperationException($"任务快照引用未知任务 {id}");
            if (progress.Status is not (QuestStatus.Locked or QuestStatus.Available or QuestStatus.Active
                or QuestStatus.Ready or QuestStatus.Completed))
                throw new InvalidOperationException("任务快照状态无效");
            if (progress.Cycle < 0 || progress.CompletedCycles < 0)
                throw new InvalidOperationException("任务周期无效");

            foreach (var goal in progress.Goals.Keys)
                if (!quest.Goals.Any(value => value.Id == goal))
                    throw new InvalidOperationException($"任务快照引用未知目标 {goal}");

            foreach (var goal in quest.Goals)
            {
                if (!progress.Goals.TryGetValue(goal.Id, out var count))
                    throw new InvalidOperationException($"任务快照缺少目标 {goal.Id}");
                if (count < 0) throw new InvalidOperationException("任务目标进度无效");
                if (count > goal.Quantity) throw new InvalidOperationException("任务目标进度超出数量");
            }
        }

        foreach (var (id, quest) in _definitions)
            if (!states.ContainsKey(id)) states[id] = InitialProgress(quest);

        if (input.RunningQuestIds.Distinct().Count() != input.RunningQuestIds.Count)
            throw new InvalidOperationException("任务运行池标识重复或结构无效");

        var running = new HashSet<string>(input.RunningQuestIds);
        foreach (var id in running)
            if (!states.TryGetValue(id, out var runningState) ||
                runningState.Status is not (QuestStatus.Active or QuestStatus.Ready))
                throw new InvalidOperationException($"任务运行池引用无效：{id}");

        foreach (var (id, progress) in states)
            if (progress.Status is QuestStatus.Active or QuestStatus.Ready && !running.Contains(id))
                throw new InvalidOperationException($"任务运行池缺少任务：{id}");

        if (input.FactSequences.Select(pair => pair.Key).Distinct().Count() != input.FactSequences.Count)
            throw new InvalidOperationException("任务事实来源重复");
        foreach (var (source, sequence) in input.FactSequences)
        {
            if (string.IsNullOrEmpty(source)) throw new InvalidOperationException("任务事实来源为空");
            if (sequence < 1) throw new InvalidOperationException("事实序号无效");
        }

        var pins = input.DebugPins.Distinct().ToList();
        if (pins.Count != input.DebugPins.Count) throw new InvalidOperationException("任务快照调试钉住无效");
        foreach (var id in pins)
        {
            if (!_definitions.TryGetValue(id, out var pinnedQuest) ||
                !states.TryGetValue(id, out var pinnedState))
                throw new InvalidOperationException($"任务快照调试钉住引用未知任务 {id}");
            if (pinnedQuest.Completion != "all")
                throw new InvalidOperationException("任务快照调试钉住只适用于目标计数任务");
            if (pinnedState.Status is not (QuestStatus.Active or QuestStatus.Ready))
                throw new InvalidOperationException($"任务快照调试钉住引用未接取任务 {id}");
        }

        _states = states;
        _runningIds = [.. input.RunningQuestIds];
        _factSequences = input.FactSequences.ToDictionary(pair => pair.Key, pair => pair.Value);
        _debugPins = [.. pins];
        _revision++;
    }

    private static Dictionary<string, QuestDefinition> PrepareDefinitions(IReadOnlyList<QuestDefinition> definitions)
    {
        var prepared = new Dictionary<string, QuestDefinition>(definitions.Count);
        foreach (var quest in definitions)
        {
            if (string.IsNullOrEmpty(quest.Id)) throw new InvalidOperationException("任务标识为空");
            if (!prepared.TryAdd(quest.Id, quest)) throw new InvalidOperationException("任务标识重复");
        }

        var questIds = new HashSet<string>(prepared.Keys);
        foreach (var quest in prepared.Values)
        {
            if (quest.Goals.Select(goal => goal.Id).Distinct().Count() != quest.Goals.Length)
                throw new InvalidOperationException($"任务 {quest.Id} 目标标识重复");
            if (quest.Completion == "all" && quest.Goals.Length == 0)
                throw new InvalidOperationException($"任务 {quest.Id} 缺少完成目标");
            if (quest.ItemCompletion == "equipped-item-shortcut" &&
                (quest.Completion != "all" || quest.Goals.Any(goal => goal.Kind != "item")))
                throw new InvalidOperationException("装备判定捷径只适用于物品目标任务");
            if (quest.Completion == "talk" && quest.Goals.Length > 0)
                throw new InvalidOperationException("对话任务的说明对象应放在 descriptionTargets");

            foreach (var goal in quest.Goals)
            {
                if (string.IsNullOrEmpty(goal.Id) || string.IsNullOrEmpty(goal.Target))
                    throw new InvalidOperationException($"任务 {quest.Id} 目标标识为空");
                if (goal.Quantity < 1) throw new InvalidOperationException("目标数量无效");
            }

            foreach (var item in quest.OnAcceptItems)
                CheckItem(item.Item, item.Quantity);
            foreach (var item in quest.HandoverItems)
                CheckItem(item.Item, item.Quantity);
            foreach (var item in quest.Rewards?.Items ?? [])
                CheckItem(item.Item, item.Quantity);

            if (quest.Rewards is { Experience: < 0 } or { Money: < 0 })
                throw new InvalidOperationException("任务奖励无效");

            var referenced = new HashSet<string>();
            StoryCondition.CollectQuestRefs(quest.AvailableWhen, referenced);
            foreach (var id in referenced)
                if (!questIds.Contains(id))
                    throw new InvalidOperationException($"任务 {quest.Id} 引用未知任务 {id}");
        }

        return prepared;
    }

    private static void CheckItem(string item, int quantity)
    {
        if (string.IsNullOrEmpty(item)) throw new InvalidOperationException("物品标识为空");
        if (quantity < 1) throw new InvalidOperationException("物品数量无效");
    }

    private static QuestProgress InitialProgress(QuestDefinition quest) => new()
    {
        Status = QuestStatus.Locked,
        Goals = quest.Goals.ToDictionary(goal => goal.Id, _ => 0),
    };

    private static bool SameProgress(QuestProgress? a, QuestProgress? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        if (a.Status != b.Status || a.Cycle != b.Cycle || a.CompletedCycles != b.CompletedCycles) return false;
        if (a.Goals.Count != b.Goals.Count) return false;
        foreach (var (key, value) in a.Goals)
            if (!b.Goals.TryGetValue(key, out var other) || other != value) return false;
        return true;
    }

    private static bool SameList(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count) return false;
        for (var index = 0; index < a.Count; index++)
            if (a[index] != b[index]) return false;
        return true;
    }

    private static bool SameNumbers(IReadOnlyDictionary<string, int> a, IReadOnlyDictionary<string, int> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var (key, value) in a)
            if (!b.TryGetValue(key, out var other) || other != value) return false;
        return true;
    }

    private static bool SameStringSet(IReadOnlySet<string> a, IReadOnlySet<string> b) =>
        a.Count == b.Count && a.All(b.Contains);
}

public sealed class QuestContext : IStoryConditionContext
{
    public Func<string, QuestStatus> StatusOf { get; init; } = _ => QuestStatus.Locked;

    public Func<string, int> CompletedCyclesOf { get; init; } = _ => 0;

    public Func<string, string> EventStatusOf { get; init; } = _ => "idle";

    public Func<string, string?> ChoiceOf { get; init; } = _ => null;

    public Func<string, bool> FlagOf { get; init; } = _ => false;

    public Func<string, object?> WorldOf { get; init; } = _ => null;

    public Func<string, int> ItemCount { get; init; } = _ => 0;

    public Func<string, int>? EquippedItemCount { get; init; }

    public string? ClassId { get; init; }

    public Action<string>? Unknown { get; init; }

    QuestStatus IStoryConditionContext.QuestStatus(string id) => StatusOf(id);

    public int QuestCompletedCycles(string id) => CompletedCyclesOf(id);

    public string EventStatus(string id) => EventStatusOf(id);

    public string? Choice(string id) => ChoiceOf(id);

    public bool Flag(string id) => FlagOf(id);

    public object? World(string key) => WorldOf(key);
}
