using System;
using System.Collections.Generic;
using System.Linq;
using Inotia4.Domain.Character;

namespace Inotia4.Domain.Story;

public enum QuestStatus
{
    /// <summary>条件没满足（或内部任务），任务板上不该出现。</summary>
    Locked,

    Available,

    Active,

    Ready,

    Completed,
}

public interface IStoryWorld
{
    QuestStatus StatusOf(string questId);

    bool EventDone(string eventId);

    string? ChoiceOf(string choiceId);

    bool Flag(string name);
}

public sealed class QuestJournal : IStoryWorld, IStoryConditionContext
{
    private readonly Dictionary<string, bool> _flags = new();
    private readonly HashSet<string> _eventsStarted = [];
    private readonly HashSet<string> _eventsDone = [];
    private readonly Dictionary<string, string> _choices = new();

    public QuestJournal(CampaignContent? campaign = null)
    {
        Campaign = campaign;
        Book = new QuestBook(
            campaign?.Quests ?? [],
            BuildContext,
            campaign?.SideQuestIds ?? new HashSet<string>());
    }

    public CampaignContent? Campaign { get; }

    public QuestBook Book { get; }

    public Bag? Bag { get; set; }

    public Session? Session { get; set; }

    public Func<string?>? ClassIdProvider { get; set; }

    public string? LastRejection { get; private set; }

    public Action<string>? UnknownCondition { get; set; }

    public QuestPlan Apply(params QuestCommand[] commands)
    {
        var plan = Book.Plan(commands);

        // 同一轮结算不能发两次（判据是「任务 + 轮次」已有回执）：先整批判完，
        // 再动背包与回执账，免得中途抛错留下半批。
        foreach (var settlement in plan.Rewards)
            if (HasReceipt(settlement.Quest, settlement.Cycle))
                throw new InvalidOperationException("任务奖励已结算");

        foreach (var item in plan.ItemRemovals)
        {
            var held = Bag?.Count(item.Item) ?? 0;
            if (held < item.Quantity)
                throw new InvalidOperationException($"剧情物品 {item.Item} 不足");
        }

        if (Bag is not null)
        {
            foreach (var item in plan.ItemRemovals) Bag.Remove(item.Item, item.Quantity);
            foreach (var item in plan.ItemGrants) Bag.Add(item.Item, item.Quantity);
        }

        foreach (var settlement in plan.Rewards)
        {
            Session?.GrantReward(settlement.Reward.Experience, settlement.Reward.Money);
            RecordReceipt(settlement);
        }

        Book.Commit(plan);
        return plan;
    }

    public void Refresh() => Apply(QuestCommand.Refresh());

    public bool Accept(string questId, string? source = null)
    {
        var before = StatusOf(questId);
        if (before is QuestStatus.Active or QuestStatus.Ready) return false;
        return TryApply([QuestCommand.Accept(questId, source: source)]);
    }

    public bool Ready(string questId) => TryApply([QuestCommand.ScriptReady(questId)]);

    /// <summary>交付结算。只能从「可交付」结算，否则拒绝。</summary>
    public bool Complete(string questId) => TryApply([QuestCommand.Complete(questId)]);

    /// <summary>
    /// 交付结算：收走交付物、发放奖励，返回这次的结算回执。
    /// 调用方要先确认状态是「可交付」，否则任务簿会拒绝。
    /// </summary>
    public QuestSettlement? Settle(string questId)
    {
        var plan = Apply(QuestCommand.Complete(questId));
        return plan.Rewards.FirstOrDefault();
    }

    public bool Reset(string questId) => TryApply([QuestCommand.Reset(questId)]);

    private readonly List<QuestSettlement> _receipts = [];
    private readonly Dictionary<string, int> _receiptIndex = new(StringComparer.Ordinal);

    private static string ReceiptKey(string quest, int cycle) => quest + "\0" + cycle;

    public IReadOnlyList<QuestSettlement> Receipts => _receipts;

    public bool HasReceipt(string questId, int cycle) => _receiptIndex.ContainsKey(ReceiptKey(questId, cycle));

    private void RecordReceipt(QuestSettlement settlement)
    {
        var key = ReceiptKey(settlement.Quest, settlement.Cycle);
        if (_receiptIndex.ContainsKey(key)) throw new InvalidOperationException("任务奖励已结算");
        _receiptIndex[key] = _receipts.Count;
        _receipts.Add(settlement);
    }

    public RewardReceipt? RewardReceiptFor(string questId, int? cycle = null)
    {
        if (cycle is { } exact)
            return _receiptIndex.TryGetValue(ReceiptKey(questId, exact), out var at) ? _receipts[at].Reward : null;

        for (var index = _receipts.Count - 1; index >= 0; index--)
            if (_receipts[index].Quest == questId) return _receipts[index].Reward;
        return null;
    }

    public List<QuestReceiptState> CaptureReceipts() => [.. _receipts.Select(settlement => new QuestReceiptState
    {
        Quest = settlement.Quest,
        Cycle = settlement.Cycle,
        Experience = settlement.Reward.Experience,
        Money = settlement.Reward.Money,
        Items = [.. settlement.Reward.Items.Select(item => new QuestReceiptItemState
        {
            Item = item.Item,
            Quantity = item.Quantity,
        })],
    })];

    /// <summary>
    /// 读档用：先验再填。校验口径：任务必须在内容里、
    /// 「任务 + 轮次」不重复、轮次不超过该任务已完成的轮数、奖励数值都是非负整数、
    /// 物品在目录里（目录为空时跳过物品这一道校验，不拿空目录把所有旧档判坏）。
    /// </summary>
    public void RestoreReceipts(IReadOnlyList<QuestReceiptState>? states)
    {
        _receipts.Clear();
        _receiptIndex.Clear();
        if (states is null) return;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var state in states)
        {
            var key = ReceiptKey(state.Quest, state.Cycle);
            var known = Campaign?.FindQuest(state.Quest) ?? null;
            if (known is null || !seen.Add(key) || state.Cycle < 0)
                throw new InvalidOperationException("检查点奖励记录无效");
            if (state.Cycle > Book.CompletedCycles(state.Quest))
                throw new InvalidOperationException("检查点奖励与任务周期不一致");
            if (state.Experience < 0 || state.Money < 0)
                throw new InvalidOperationException("检查点奖励内容无效");

            var receipt = new RewardReceipt { Experience = state.Experience, Money = state.Money };
            foreach (var item in state.Items)
            {
                if (item.Item.Length == 0 || item.Quantity <= 0 ||
                    (Bag?.Catalog.HasDefinitions == true && !Bag.Catalog.Has(item.Item)))
                    throw new InvalidOperationException("检查点奖励内容无效");
                receipt.Items.Add(new RewardItem(item.Item, item.Quantity));
            }

            _receiptIndex[key] = _receipts.Count;
            _receipts.Add(new QuestSettlement(state.Quest, state.Cycle, receipt));
        }
    }

    public QuestPlan EmitFact(string kind, string target, string source, string? characterId = null,
        int? quantity = null)
    {
        var sequence = Book.FactSequence(source) + 1;
        return Apply(QuestCommand.EmitFact(new QuestFact
        {
            Kind = kind,
            Target = target,
            CharacterId = characterId,
            Quantity = quantity,
            Occurrence = new QuestFactOrigin(source, sequence),
        }));
    }

    private bool TryApply(QuestCommand[] commands)
    {
        try
        {
            Apply(commands);
            LastRejection = null;
            return true;
        }
        catch (InvalidOperationException error)
        {
            LastRejection = error.Message;
            return false;
        }
    }

    public QuestStatus StatusOf(string questId) => Book.Status(questId);

    public bool Accepted(string questId) => Book.Accepted(questId);

    public bool Visible(string questId) => Book.Visible(questId);

    public int CompletedCycles(string questId) => Book.CompletedCycles(questId);

    public int GoalCount(string questId, string goalId) =>
        Book.State(questId)?.GoalCount(goalId) ?? 0;

    public IReadOnlyList<string> RunningQuestIds => Book.RunningQuestIds;

    public List<(string Kind, string Target)> WaitingGoals(string target, string? characterId = null)
    {
        var goals = new List<(string Kind, string Target)>();
        var campaign = Campaign;
        if (campaign is null) return goals;

        foreach (var quest in campaign.Quests)
        {
            if (StatusOf(quest.Id) != QuestStatus.Active) continue;
            foreach (var goal in quest.Goals)
            {
                if (goal.Kind is not ("talk" or "interact")) continue;
                // 目标与事实都写了人物编号时按它比对，否则按 target 比对。
                var matches = characterId is not null && goal.CharacterId is not null
                    ? goal.CharacterId == characterId
                    : goal.Target == target;
                if (matches) goals.Add((goal.Kind, goal.Target));
            }
        }

        return goals;
    }

    public IReadOnlyDictionary<string, QuestStatus> Entries =>
        Book.ProgressEntries()
            .Where(pair => pair.Value.Status != QuestStatus.Locked)
            .ToDictionary(pair => pair.Key, pair => pair.Value.Status);

    public StoryWorldState? StoryWorld { get; set; }

    public bool Flag(string name) => StoryWorld?.HasFlag(name) ?? (_flags.TryGetValue(name, out var value) && value);

    public void SetFlag(string name, bool value)
    {
        if (StoryWorld is not null) StoryWorld.Apply(new FlagCommand(name, value));
        else _flags[name] = value;
    }

    public IReadOnlyDictionary<string, bool> Flags => _flags;

    public void MarkEventStarted(string eventId) => _eventsStarted.Add(eventId);

    public bool EventStarted(string eventId) => _eventsStarted.Contains(eventId);

    public void MarkEventDone(string eventId) => _eventsDone.Add(eventId);

    public bool EventDone(string eventId) => _eventsDone.Contains(eventId);

    /// <summary>
    /// 存档用：把事件进度收成两份可序列化的名单（按 id 排序，写档稳定、便于对拍）。
    /// 会话层没有运行中的剧情对象，所以这份进度要自己进快照。
    /// </summary>
    public StoryEventState CaptureEvents() => new()
    {
        Started = [.. _eventsStarted.OrderBy(id => id, StringComparer.Ordinal)],
        Done = [.. _eventsDone.OrderBy(id => id, StringComparer.Ordinal)],
    };

    /// <summary>
    /// 读档用：先清空再填。旧档没有这段就是空名单——不能沿用读档前那份进程内标记，
    /// 否则重开的存档会带着上一次运行的「已演过」记录，一次性事件反而演不出来。
    /// </summary>
    public void RestoreEvents(StoryEventState? state)
    {
        _eventsStarted.Clear();
        _eventsDone.Clear();
        if (state is null) return;
        foreach (var id in state.Started)
            if (!string.IsNullOrEmpty(id)) _eventsStarted.Add(id);
        foreach (var id in state.Done)
            if (!string.IsNullOrEmpty(id)) _eventsDone.Add(id);
    }

    public void RestartEvent(string eventId)
    {
        _eventsStarted.Remove(eventId);
        _eventsDone.Remove(eventId);
    }

    public void RecordChoice(string choiceId, string value)
    {
        if (StoryWorld is not null) StoryWorld.Apply(new ChoiceCommand(choiceId, value));
        else _choices[choiceId] = value;
    }

    public string? ChoiceOf(string choiceId) =>
        StoryWorld?.Choice(choiceId) ?? _choices.GetValueOrDefault(choiceId);

    QuestStatus IStoryConditionContext.QuestStatus(string id) => Book.Status(id);

    public string EventStatus(string id) =>
        _eventsDone.Contains(id) ? "completed" : _eventsStarted.Contains(id) ? "running" : "idle";

    public int QuestCompletedCycles(string id) => Book.CompletedCycles(id);

    public string? Choice(string id) => ChoiceOf(id);

    public object? World(string key) => key == "map" ? StoryWorld?.CurrentLocation?.Map : null;

    private readonly Dictionary<string, QuestDefinition?> _slotCache = new();
    private (int Journal, int Bag) _slotStamp = (-1, -1);

    public QuestDefinition? SlotFor(string actor, string? characterId)
    {
        if (Campaign is null) return null;

        var stamp = (Book.Version, Bag?.Revision ?? -1);
        if (stamp != _slotStamp)
        {
            _slotStamp = stamp;
            _slotCache.Clear();
        }

        var key = actor + "\0" + (characterId ?? string.Empty);
        if (!_slotCache.TryGetValue(key, out var cached))
        {
            cached = NpcInteraction.QuestSlot(Campaign, this, actor, characterId);
            _slotCache[key] = cached;
        }

        return cached;
    }

    public QuestContext BuildContext() => new()
    {
        StatusOf = id => Book is null ? QuestStatus.Locked : Book.Status(id),
        CompletedCyclesOf = id => Book is null ? 0 : Book.CompletedCycles(id),
        EventStatusOf = EventStatus,
        ChoiceOf = ChoiceOf,
        FlagOf = Flag,
        WorldOf = World,
        ItemCount = item => Bag?.Count(item) ?? 0,
        EquippedItemCount = item => Bag?.WornInstances.Values
            .Count(entry => entry.ItemId == item) ?? 0,
        ClassId = ClassIdProvider?.Invoke(),
        Unknown = UnknownCondition,
    };

    public static string Name(QuestStatus status) => status switch
    {
        QuestStatus.Locked => "锁定",
        QuestStatus.Available => "未接",
        QuestStatus.Active => "进行中",
        QuestStatus.Ready => "可交付",
        QuestStatus.Completed => "已完成",
        _ => status.ToString(),
    };

    public static QuestStatus? Parse(string text) => text switch
    {
        "locked" => QuestStatus.Locked,
        "available" => QuestStatus.Available,
        "active" => QuestStatus.Active,
        "ready" => QuestStatus.Ready,
        "completed" => QuestStatus.Completed,
        _ => null,
    };
}

public sealed class StoryEventState
{
    public List<string> Started { get; set; } = [];

    public List<string> Done { get; set; } = [];
}
