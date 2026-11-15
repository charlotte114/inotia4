using System.Text.Json;
using Inotia4.Domain;
using Inotia4.Domain.Character;
using Inotia4.Domain.Content;
using Inotia4.Domain.Party;
using Inotia4.Domain.Story;
using Inotia4.Domain.World;

namespace Inotia4.Domain.Tests;

/// <summary>
/// 主线走查：不画画面，只用领域层把主线从头走一遍。
///
/// 走法照着玩家实际会做的事：按内容给的 <c>places</c> 移动到发布人 → 交互接任务 →
/// 到目标点打怪／捡东西 → 回交付人交任务；每到一个地方先把该触发的事件跑掉
/// （剧情自己会接任务、发物品、置旗标）。用的全是游戏里那几个类，
/// 所以这里走不通的地方，实际玩到那里也走不通。
///
/// 分轮推进：NPC 一次只给一个任务槽，所以一轮里被别的任务占住的位置，
/// 下一轮再试——这正是玩家多跑一趟的做法。
/// </summary>
internal sealed class MainlineAudit
{
    private readonly CampaignContent _campaign;
    private readonly QuestJournal _journal;
    private readonly Bag _bag;
    private readonly PartyRoster _party;
    private readonly Session _session = new();
    private readonly QuestTracker _tracker;
    private readonly StoryDirector _story;
    private readonly HashSet<string> _earlyAccepts = [];

    private readonly HashSet<string> _touched = [];

    private MainlineAudit(CampaignContent campaign, ItemCatalog items, CharacterStatsCatalog stats,
        CompanionCatalog companions)
    {
        _campaign = campaign;
        var hero = new BattleCharacter("dark-knight", stats, items);
        _bag = hero.Bag;
        _journal = new QuestJournal(campaign) { Bag = _bag, Session = _session };
        _session.Bag = _bag;
        _session.Journal = _journal;
        _tracker = new QuestTracker(campaign, journal: _journal);
        _party = new PartyRoster(hero, stats, items, companions, () => { },
            onRecruited: (itemId, _) => _tracker.ReportRecruit(itemId));
        _story = new StoryDirector(campaign, _journal, _bag, _session, new TravelSink(_session));
    }

    public static int Run(string contentRoot)
    {
        var options = ContentJson();
        var campaign = JsonSerializer.Deserialize<CampaignContent>(
            File.ReadAllText(Path.Combine(contentRoot, "content", "campaign.json")), options)!;
        var items = JsonSerializer.Deserialize<ItemCatalog>(
            File.ReadAllText(Path.Combine(contentRoot, "content", "items.json")), options)!;
        var stats = JsonSerializer.Deserialize<CharacterStatsCatalog>(
            File.ReadAllText(Path.Combine(contentRoot, "content", "character-stats.json")), options)!;
        var companions = JsonSerializer.Deserialize<CompanionCatalog>(
            File.ReadAllText(Path.Combine(contentRoot, "content", "companions.json")), options)!;
        DropsParse(items, contentRoot);

        return new MainlineAudit(campaign, items, stats, companions).Walk();
    }

    private static JsonSerializerOptions ContentJson()
    {
        var options = new JsonSerializerOptions { AllowTrailingCommas = true };
        Inotia4.Domain.Combat.SkillActionJson.Configure(options);
        return options;
    }

    private static void DropsParse(ItemCatalog items, string contentRoot)
    {
        var options = ContentJson();
        var drops = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(contentRoot, "content", "drops.json")),
            new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var battle = JsonSerializer.Deserialize<BattleContent>(
            File.ReadAllText(Path.Combine(contentRoot, "content", "battle.json")), options)!;
        var catalog = Drops.ParseDrops(drops.RootElement, items,
            battle.Enemies.Select(enemy => enemy.Id).ToList());
        Console.WriteLine($"掉落目录：v{catalog.Version}，敌人 {catalog.Enemies!.Count} 条、" +
                          $"池子 {catalog.Pools!.Count} 张、章节带 {catalog.ChapterBands!.Length} 段");
    }

    private int Walk()
    {
        var mains = _campaign.Quests.Where(quest => _campaign.KindOfQuest(quest.Id) == "main").ToList();
        var reasons = new Dictionary<string, string>();

        _session.EnterMap(_campaign.Start.Map, new Vec2(_campaign.Start.X, _campaign.Start.Y), "south");

        if (_campaign.Start.Script is { Length: > 0 } startScript)
        {
            foreach (var trigger in _campaign.EventTable.Triggers)
                if (trigger.Source == "start") _journal.MarkEventStarted(trigger.Id);
            RunScript(startScript);
            Drain();
        }

        var completed = 0;
        var stagnant = 0;

        for (var round = 0; round < 12; round++)
        {
            reasons.Clear();
            var beforeCompleted = completed;
            completed = 0;

            foreach (var quest in mains)
            {
                if (_journal.StatusOf(quest.Id) == QuestStatus.Completed) { completed++; continue; }

                var reason = Attempt(quest, depth: 0);
                if (reason is not null) reasons[quest.Id] = reason;
                else if (_journal.StatusOf(quest.Id) != QuestStatus.Completed) reasons[quest.Id] = "收尾未完成";
            }

            if (completed == mains.Count) break;

            stagnant = completed > beforeCompleted || round == 0 ? 0 : stagnant + 1;
            if (stagnant >= 2) break;
        }

        var stuck = reasons.Select(pair => new Stuck
        {
            Quest = pair.Key,
            Title = _campaign.FindQuest(pair.Key)?.Title ?? string.Empty,
            Reason = pair.Value,
        }).ToList();

        Console.WriteLine();
        Console.WriteLine($"主线任务 {mains.Count} 个：走到已完成 {completed}，卡住 {stuck.Count}");
        var touchedCompleted = mains.Count(quest =>
            _touched.Contains(quest.Id) && _journal.StatusOf(quest.Id) == QuestStatus.Completed);
        Console.WriteLine($"被接上过的主线 {_touched.Count} 个：完成 {touchedCompleted}，其余停在半路");

        var hard = mains.FirstOrDefault(quest =>
            reasons.TryGetValue(quest.Id, out var reason) &&
            (reason.Contains("没有进度上报入口") || reason.Contains("目标都达成") ||
             reason.Contains("只说进度") || reason.Contains("招募目标")));
        Console.WriteLine(hard is null
            ? "没有发现会卡死主线的规则缺陷"
            : $"第一个硬缺陷：{hard.Id} {hard.Title}（{reasons[hard.Id]}）");

        Console.WriteLine($"可用条件没满足就被接取的任务 {_earlyAccepts.Count} 个");
        if (_earlyAccepts.Count > 0)
            Console.WriteLine($"  提前接取：{string.Join("，", _earlyAccepts.Take(8))}");
        Console.WriteLine();

        if (stuck.Count > 0)
        {
            Console.WriteLine("卡住的按原因归类：");
            foreach (var group in stuck.GroupBy(entry => entry.Reason).OrderByDescending(group => group.Count()))
            {
                Console.WriteLine($"  {group.Count(),4}  {group.Key}");
                foreach (var entry in group.Take(3))
                    Console.WriteLine($"          {entry.Quest}  {entry.Title}");
                if (group.Count() > 3) Console.WriteLine($"          ……另外 {group.Count() - 3} 个");
            }

            Console.WriteLine();
            Console.WriteLine("最早卡住的 15 个（按内容顺序）：");
            foreach (var entry in stuck.Take(15))
                Console.WriteLine($"  {entry.Quest,-14} {entry.Title,-26} {entry.Reason}");
        }

        return 0;
    }

    private string? Attempt(QuestDefinition quest, int depth)
    {
        var status = _journal.StatusOf(quest.Id);
        if (status != QuestStatus.Available) _touched.Add(quest.Id);

        if (status == QuestStatus.Available)
        {
            if (quest.Hidden) return "隐藏任务，没有可接入口（应由剧本接取）";

            if (quest.Giver is null && quest.Delivery is null) return "没有发布者（应由剧本接取）";
            if (quest.Giver is null) return "没有发布者（只能由剧本接取）";

            var actor = quest.Giver;

            GoTo(quest, "available");
            Drain();

            if (_journal.StatusOf(quest.Id) != QuestStatus.Available)
                return Attempt(quest, depth + 1);

            var plan = NpcInteraction.Interact(_campaign, _journal, actor, quest.GiverCharacterId,
                quest.Description, quest.ProgressText);

            switch (plan.Kind)
            {
                case InteractionPlan.PlanKind.Accept:
                {
                    var offered = _campaign.FindQuest(plan.QuestId);
                    if (offered is null) return $"发布人给的任务 {plan.QuestId} 不在内容里";

                    if (offered.AvailableWhen.ValueKind == JsonValueKind.Object &&
                        !_story.Evaluate(offered.AvailableWhen))
                        _earlyAccepts.Add(offered.Id);

                    QuestRules.Accept(_campaign, _journal, _bag, offered.Id);
                    Drain();

                    if (offered.Id != quest.Id && depth < 6)
                    {
                        var inner = Attempt(offered, depth + 1);
                        return inner ?? "同一位 NPC 的槽位排在前面的任务";
                    }

                    return requested(quest, depth);
                }

                case InteractionPlan.PlanKind.PlayScript:
                    RunScript(plan.ScriptId!);
                    Drain();
                    return _journal.StatusOf(quest.Id) == QuestStatus.Available
                        ? "接取脚本演完仍未接取"
                        : Attempt(quest, depth + 1);

                case InteractionPlan.PlanKind.Progress:
                    return plan.QuestId == quest.Id
                        ? "同一任务处于进行中，没有接取入口"
                        : $"发布人的槽被 {plan.QuestId} 占着";

                case InteractionPlan.PlanKind.Complete:
                    return "发布人处直接结算（状态错位）";

                default:
                    return "发布人身上没有任务槽（NPC 可能还没摆出来）";
            }
        }

        if (status == QuestStatus.Active)
        {
            GoTo(quest, "active");
            Drain();

            if (_journal.StatusOf(quest.Id) == QuestStatus.Ready) return Attempt(quest, depth + 1);
            if (_journal.StatusOf(quest.Id) == QuestStatus.Completed) return null;

            var why = SatisfyGoals(quest);
            if (why is not null) return why;

            status = _journal.StatusOf(quest.Id);
            if (status == QuestStatus.Ready) return Attempt(quest, depth + 1);
            if (status == QuestStatus.Completed) return null;

            return quest.Completion == "story"
                ? "等待剧情结算（story 类，由脚本命令推进）"
                : "目标都达成，但没有进入可交付";
        }

        if (status == QuestStatus.Ready)
        {
            GoTo(quest, "ready");
            Drain();

            if (_journal.StatusOf(quest.Id) == QuestStatus.Ready) return Deliver(quest, depth);
            return _journal.StatusOf(quest.Id) == QuestStatus.Completed ? null : "交付地点事件改变了任务状态";
        }

        return null;

        string? requested(QuestDefinition current, int level)
        {
            return _journal.StatusOf(current.Id) switch
            {
                QuestStatus.Active => Attempt(current, level + 1),
                QuestStatus.Ready => Attempt(current, level + 1),
                QuestStatus.Completed => null,
                _ => "接取后状态没有变化",
            };
        }
    }

    private string? SatisfyGoals(QuestDefinition quest)
    {
        foreach (var goal in quest.Goals)
        {
            switch (goal.Kind)
            {
                case "defeat":
                    for (var i = _tracker.CountOf(quest.Id, goal.Id); i < goal.Quantity; i++)
                        _tracker.ReportDefeat(goal.Target);
                    break;

                case "item":
                    var have = _bag.Count(goal.Target);
                    if (have < goal.Quantity) _bag.Add(goal.Target, goal.Quantity - have);
                    _tracker.RefreshItemGoals(_bag);
                    break;

                case "talk":
                    for (var i = _tracker.CountOf(quest.Id, goal.Id); i < goal.Quantity; i++)
                        _tracker.ReportTalk(goal.Target, goal.CharacterId);
                    break;

                case "interact":
                    for (var i = _tracker.CountOf(quest.Id, goal.Id); i < goal.Quantity; i++)
                        _tracker.ReportInteract(goal.Target, goal.CharacterId);
                    break;

                case "recruit":
                    try
                    {
                        while (_tracker.CountOf(quest.Id, goal.Id) < goal.Quantity)
                        {
                            if (_bag.Count(goal.Target) == 0) _bag.Add(goal.Target);
                            var emblem = _bag.Entries.Last(entry => entry.ItemId == goal.Target);
                            _party.Recruit(emblem.Id);
                        }
                    }
                    catch (InvalidOperationException error)
                    {
                        return $"招募走不通：{error.Message}";
                    }

                    break;

                default:
                    return $"目标类型 {goal.Kind} 没有进度上报入口";
            }
        }

        return null;
    }

    private string? Deliver(QuestDefinition quest, int depth)
    {
        var actor = quest.Delivery ?? quest.Giver;
        if (actor is null) return "没有交付对象（应由剧本结算）";

        var plan = NpcInteraction.Interact(_campaign, _journal, actor, quest.DeliveryCharacterId,
            quest.ProgressText, quest.ProgressText);

        switch (plan.Kind)
        {
            case InteractionPlan.PlanKind.Complete:
                if (plan.QuestId != quest.Id) return $"交付人结算的是 {plan.QuestId}";
                if (NpcInteraction.Settle(_campaign, _journal, quest.Id) is null)
                    return "交付物品不足，结算被拒";
                Drain();
                return null;

            case InteractionPlan.PlanKind.PlayScript:
                RunScript(plan.ScriptId!);
                Drain();
                if (_journal.StatusOf(quest.Id) == QuestStatus.Completed) return null;
                return depth < 3
                    ? Attempt(quest, depth + 1)
                    : $"交付脚本演完后仍是{QuestJournal.Name(_journal.StatusOf(quest.Id))}";

            case InteractionPlan.PlanKind.Progress:
                return $"交付人只说进度（{quest.Completion} 类任务没进入可交付）";

            default:
                return $"交付人没有任务槽（{plan.Kind}）";
        }
    }

    private void Drain()
    {
        for (var guard = 0; guard < 400; guard++)
        {
            var fired = false;
            foreach (var trigger in _campaign.EventTable.Triggers)
            {
                if (_journal.EventStarted(trigger.Id)) continue;
                if (!PlaceMatches(trigger)) continue;
                if (trigger.Condition.ValueKind == JsonValueKind.Object && !_story.Evaluate(trigger.Condition))
                    continue;

                _journal.MarkEventStarted(trigger.Id);
                RunScript(trigger.Script);
                _journal.MarkEventDone(trigger.Script);
                fired = true;
                break;
            }

            if (!fired) return;
        }
    }

    private bool PlaceMatches(StoryEventTrigger trigger)
    {
        switch (trigger.Source)
        {
            case "start":
                return false;

            case "region":
                if (trigger.Map.Length > 0 && trigger.Map != _session.MapId) return false;
                if (trigger.Region is not { } region) return false;
                const float foot = 8f;
                return _session.Position.X + foot > region.X && _session.Position.X - foot < region.X + region.Width &&
                       _session.Position.Y + foot > region.Y && _session.Position.Y - foot < region.Y + region.Height;

            case "map-entry":
                return trigger.Map.Length == 0 || trigger.Map == _session.MapId;

            case "near":
                foreach (var spot in trigger.Spots)
                    if ((spot.Map.Length == 0 || spot.Map == _session.MapId) &&
                        Dist(_session.Position, spot.Point) <= 96f)
                        return true;
                return trigger.Spots.Count == 0;

            default:
                return true;
        }
    }

    private static float Dist(Vec2 a, Vec2 b) => MathF.Abs(a.X - b.X) + MathF.Abs(a.Y - b.Y);

    private void GoTo(QuestDefinition quest, string slot)
    {
        if (!TryPlace(quest.Places, slot, out var map, out var x, out var y)) return;
        if (map != _session.MapId) _session.EnterMap(map, new Vec2(x, y), "south");
        else _session.Moved(new Vec2(x, y), "south");
    }

    private static bool TryPlace(JsonElement places, string slot, out string map, out float x, out float y)
    {
        map = string.Empty;
        x = 0f;
        y = 0f;

        if (places.ValueKind != JsonValueKind.Object || !places.TryGetProperty(slot, out var place) ||
            place.ValueKind != JsonValueKind.Object)
            return false;

        if (StoryType.Text(place, "type") == "fixed")
        {
            map = StoryType.Text(place, "map");
            x = StoryType.Float(place, "x");
            y = StoryType.Float(place, "y");
            return map.Length > 0;
        }

        if (place.TryGetProperty("spots", out var spots) && spots.ValueKind == JsonValueKind.Array)
        {
            foreach (var spot in spots.EnumerateArray())
            {
                map = StoryType.Text(spot, "map");
                if (map.Length == 0) continue;
                x = StoryType.Float(spot, "x");
                y = StoryType.Float(spot, "y");
                return true;
            }
        }

        return false;
    }

    private void RunScript(string scriptId)
    {
        if (!_campaign.Scripts.ContainsKey(scriptId)) return;

        _story.Start(scriptId);
        for (var guard = 0; guard < 20000; guard++)
        {
            var step = _story.Next();
            if (step.Kind == StoryStepKind.End) break;
            if (step.Kind == StoryStepKind.Encounter) _story.ResolveEncounter(true);
        }

        _tracker.RefreshItemGoals(_bag);
    }

    private sealed class Stuck
    {
        public required string Quest { get; init; }
        public required string Title { get; init; }
        public required string Reason { get; init; }
    }

    private sealed class TravelSink(Session session) : IWorldCommandSink
    {
        public void Apply(StoryWorldCommand command)
        {
            if (command is not TravelCommand travel) return;

            var map = travel.Destination.Map;
            var point = new Vec2(travel.Destination.X, travel.Destination.Y);
            if (map.Length == 0 || map == session.MapId) session.Moved(point, travel.Destination.Facing);
            else session.EnterMap(map, point, travel.Destination.Facing);
        }

        public void DropItem(string map, float x, float y, string item, int quantity) { }
    }
}
