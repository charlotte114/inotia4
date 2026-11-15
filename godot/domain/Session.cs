using System;
using System.Collections.Generic;
using System.Linq;
using Inotia4.Domain.Character;
using Inotia4.Domain.World;

namespace Inotia4.Domain;

/// <summary>
/// 一次游戏的会话状态：人在哪张图、站在哪、朝哪、过了多久。
///
/// 这个对象是一局游戏的总账本：任务、背包、队伍、战斗时钟都挂在它下面。
/// 这里先只收「跨图必须留住」的那部分——切一次图，人物位置和朝向不能丢，
/// 否则每换一张图都像重新开了一局。战斗、背包、任务会按依赖顺序陆续搬进来。
///
/// 这个类不引用引擎，可以脱离画面测试。
/// </summary>
public sealed class Session
{
    public string MapId { get; private set; } = string.Empty;

    public Vec2 Position { get; private set; }

    public string Facing { get; private set; } = "south";

    public long GameTimeMs { get; private set; }

    /// <summary>
    /// 第几次进入当前地图。进入**别的地图**才加一；同图内重建（切镜头、重载）
    /// 不算新访问，否则地图上的限时刷新会被反复重置。
    /// </summary>
    public int Visit { get; private set; }

    public int Money { get; private set; }

    public int Experience => Hero?.Player.Experience ?? _experience;

    private int _experience;

    public Bag? Bag { get; set; }

    public ShopVisits Shops { get; } = new();

    public int PlayerLevel
    {
        get => Hero?.Level ?? _playerLevel;
        set => _playerLevel = value;
    }

    private int _playerLevel = 1;

    public Character.BattleCharacter? Hero { get; set; }

    public Party.PartyRoster? Party { get; set; }

    public Story.QuestJournal? Journal
    {
        get => _journal;
        set
        {
            _journal = value;
            if (value is not null) value.StoryWorld = StoryWorld;
        }
    }

    private Story.QuestJournal? _journal;

    public Func<Story.StoryRunSnapshot?>? CaptureStoryRuns { get; set; }

    public Action<Story.StoryRunSnapshot?>? ApplyStoryRuns { get; set; }

    public Story.StoryRunSnapshot? PendingStoryRuns { get; private set; }

    public Story.StoryRunSnapshot? TakePendingStoryRuns()
    {
        var pending = PendingStoryRuns;
        PendingStoryRuns = null;
        return pending;
    }

    public Character.StorySkillProgression? Skills { get; set; }

    public GroundLootStore GroundLoot { get; } = new();

    public LootPity LootPity { get; } = new();

    public World.ChestVisits Chests { get; } = new();

    public World.SpringVisits Springs { get; } = new();

    public World.PropVisits Props { get; } = new();

    public RecoveryItems Recovery { get; } = new();

    /// <summary>
    /// 战斗逻辑帧时钟。所有战斗角色在一帧里读同一个帧号；
    /// 技能冷却、动作帧、状态时长都挂在它上面，所以它归会话所有、随存档往返，
    /// 而不是场景自己的计数。
    /// </summary>
    public Combat.CombatClock CombatClock { get; } = new();

    public long CombatTick => CombatClock.Frame;

    private Combat.CombatCooldownStore? _cooldowns;

    /// <summary>
    /// 技能冷却账本：按角色记「可用帧」。
    /// 它读会话时钟，所以两者必须是同一份实例——换一份时钟，冷却就对不上了。
    /// </summary>
    public Combat.CombatCooldownStore Cooldowns =>
        _cooldowns ??= new Combat.CombatCooldownStore(() => CombatClock.Frame);

    /// <summary>
    /// 资源恢复时钟：每 31 个逻辑帧回一次血魔。
    /// 它归会话所有，切图不该让回血相位重来。
    /// </summary>
    public Combat.CombatResourceRecovery ResourceRecovery { get; } = new();

    /// <summary>
    /// 狂气动画记忆：每个角色上次用到的动画帧数。
    /// 换装只更新已记过的角色，所以它必须跨战斗留存。
    /// </summary>
    public Combat.FrenzyAnimationMemory FrenzyAnimations { get; } = new();

    public void AdvanceCombatFrames(long deltaMs, Func<long, bool> step)
    {
        CombatClock.Advance(deltaMs, frame =>
        {
            QuestItems.Advance(frame,
                quest => Journal is not null && Journal.StatusOf(quest) == Story.QuestStatus.Active,
                GroundLoot);
            return step(frame);
        });
    }

    public World.QuestItemGenerators QuestItems { get; } = new();

    public Story.StoryWorldState StoryWorld { get; } = new();

    public Story.StoryCursorState? StoryCursor { get; set; }

    public Story.StoryCursorState? PendingStoryCursor { get; private set; }

    /// <summary>取走读档游标（取一次就清空）：续播只该发生一次，换图不该重演。</summary>
    public Story.StoryCursorState? ConsumePendingStoryCursor()
    {
        var cursor = PendingStoryCursor;
        PendingStoryCursor = null;
        return cursor;
    }

    public IReadOnlyList<Story.ActorPlacement> MapEntryPlacements { get; set; } = [];

    public const long DefaultEnemyRespawnMs = 60_000;

    private readonly Dictionary<string, EnemyState> _enemyStates = [];

    public EnemyState EnemyVitals(string id) =>
        _enemyStates.TryGetValue(id, out var state) ? state : new EnemyState();

    public void SetEnemyVitals(string id, int health, int mana) =>
        _enemyStates[id] = EnemyVitals(id) with { Health = health, Mana = mana };

    public void MarkEnemyDefeated(string id, bool respawn, long respawnMs = DefaultEnemyRespawnMs)
    {
        var state = EnemyVitals(id);
        _enemyStates[id] = respawn
            ? state with { DeadPermanently = false, DeadUntilMs = GameTimeMs + respawnMs }
            : state with { DeadPermanently = true, DeadUntilMs = null };
    }

    /// <summary>
    /// 这个生成点现在能不能上台：永久死亡永远不行，
    /// 计时未到的也不行；计时到了就把记录清掉、重新满血出场。
    /// </summary>
    public bool CanSpawnEnemy(Content.EnemySpawn spawn)
    {
        // 剧情可以让某个生成点先不可见（<c>remove</c> 命令），这类怪不上场，
        // 与「打死了在等复活」是两件事：先看剧情可见性，再查死亡计时。
        if (!StoryWorld.Visible(spawn.Id)) return false;

        var state = EnemyVitals(spawn.Id);
        if (state.DeadUntilMs is null && !state.DeadPermanently) return true;
        if (state.DeadPermanently || GameTimeMs < state.DeadUntilMs) return false;
        _enemyStates.Remove(spawn.Id);
        return true;
    }

    /// <summary>
    /// 剧情按名移除一个敌人：世界状态记「撤下」，
    /// 同时把它记成永久死亡——它不是玩家击杀，所以不发经验、不掉落。
    /// </summary>
    public void RemoveStoryEnemy(string id)
    {
        StoryWorld.Apply(new Story.ToggleEntityCommand("remove", id));
        _enemyStates[id] = EnemyVitals(id) with
        {
            Health = 0,
            DeadPermanently = true,
            DeadUntilMs = null,
        };
    }

    public readonly record struct EnemySource(string Id, string Map, float X, float Y);

    public bool WeakenQuestTarget(string enemyId, EnemySource source)
    {
        var journal = Journal;
        var campaign = journal?.Campaign;
        if (journal is null || campaign is null) return false;

        Story.QuestDefinition? matchedQuest = null;
        Story.QuestTargetChange? matchedChange = null;
        foreach (var quest in campaign.Quests)
        {
            if (journal.StatusOf(quest.Id) != Story.QuestStatus.Active) continue;
            foreach (var change in quest.TargetChanges ?? [])
            {
                if (change.EnemyId != enemyId) continue;
                var goal = quest.Goals.FirstOrDefault(candidate => candidate.Id == change.Goal);
                if (journal.GoalCount(quest.Id, change.Goal) >= (goal?.Quantity ?? 0)) continue;
                matchedQuest = quest;
                matchedChange = change;
                break;
            }

            if (matchedQuest is not null) break;
        }

        if (matchedQuest is null || matchedChange is null) return false;

        // 已经被剧情撤下、或正等着复活的生成点不再换人：先问能不能上台，再动世界状态。
        if (!CanSpawnEnemy(new Content.EnemySpawn { Id = source.Id, EnemyId = enemyId })) return false;

        var targetGoal = matchedQuest.Goals.FirstOrDefault(candidate => candidate.Id == matchedChange.Goal);
        StoryWorld.Apply(new Story.PlaceCommand(new Story.StoryEntityState
        {
            Id = "quest-target:" + source.Id,
            Map = source.Map,
            Actor = matchedChange.Actor,
            CharacterId = targetGoal?.CharacterId,
            Authority = Story.PlacementAuthority.Story,
            X = source.X,
            Y = source.Y,
            Visible = true,
            Hint = !string.IsNullOrEmpty(matchedChange.InteractText)
                ? matchedChange.InteractText
                : matchedChange.ChangedText,
        }, VisitScope: true));

        _enemyStates[source.Id] = new EnemyState
        {
            Health = 1,
            DeadPermanently = true,
            Capture = new EnemyCapture(source.Map, StoryWorld.Visit),
        };
        return true;
    }

    /// <summary>
    /// 玩家跟一个目标交互一次。「去跟某人说话」「去对某个东西
    /// 用一次」本身就是任务目标：进行中任务里指到这个目标的按类型各记一次事实。
    ///
    /// <c>quest-target:</c> 是捕捉换出来的角色，必须先过三道门（本次进图的 capture、
    /// 剧情世界里的实体还在、仍然可见）；任一不成立就不算交互，也不会误记目标。
    /// </summary>
    public bool Interact(string target, string instance, string? characterId = null)
    {
        var journal = Journal;
        if (journal is null) return false;

        var captured = instance.StartsWith("quest-target:", StringComparison.Ordinal);
        if (captured)
        {
            var enemyId = instance["quest-target:".Length..];
            var capture = EnemyVitals(enemyId).Capture;
            var entity = StoryWorld.Entity(instance);
            if (capture is null || capture.Visit != StoryWorld.Visit || entity is null ||
                !StoryWorld.Visible(instance)) return false;
        }

        var goals = journal.WaitingGoals(target, characterId);
        if (goals.Count == 0) return false;

        var remove = captured && (journal.Campaign?.Quests.Any(quest =>
            journal.StatusOf(quest.Id) == Story.QuestStatus.Active &&
            (quest.TargetChanges ?? []).Any(change =>
                change.Actor == target && change.RemoveAfterInteraction)) ?? false);

        foreach (var kind in goals.Select(goal => goal.Kind).Distinct())
            journal.EmitFact(kind, target, kind == "interact" ? "interaction" : "talk", characterId);

        if (remove) StoryWorld.Apply(new Story.ToggleEntityCommand("remove", instance));
        return true;
    }

    public bool MovePlayer(Story.StoryLocation destination)
    {
        var current = StoryWorld.CurrentLocation;
        if (current is not null && destination.Map != current.Map)
            throw new InvalidOperationException("换图要走剧情转场命令");
        if (current is not null && current.X == destination.X && current.Y == destination.Y &&
            current.Facing == destination.Facing)
            return false;

        StoryWorld.Apply(new Story.TravelCommand(destination));
        return true;
    }

    public IReadOnlyList<IReadOnlyList<Drops.QuestDropRequirement>> QuestDropRequirements()
    {
        if (Journal is not { } journal || journal.Bag is not { } bag) return [];
        var result = new List<IReadOnlyList<Drops.QuestDropRequirement>>();
        foreach (var id in journal.Book.RunningQuestIds)
        {
            var quest = journal.Campaign?.Quests.FirstOrDefault(quest => quest.Id == id);
            if (quest is null || journal.Book.Status(id) != Story.QuestStatus.Active ||
                quest.Completion != "all")
                continue;
            var goals = quest.Goals.Where(goal => goal.Kind == "item")
                .Select(goal => new Drops.QuestDropRequirement(goal.Target, goal.Quantity, bag.Count(goal.Target)))
                .ToList();
            if (goals.Count > 0) result.Add(goals);
        }

        return result;
    }

    public string StartMap { get; set; } = string.Empty;

    private static readonly IReadOnlyDictionary<string, int> NoModifiers =
        new Dictionary<string, int>(0);

    public Func<IReadOnlyDictionary<string, int>>? PartyModifiers { get; set; }

    public IReadOnlyDictionary<string, int> GroupModifiers() =>
        Party?.GroupModifiers() ?? PartyModifiers?.Invoke() ?? NoModifiers;

    /// <summary>
    /// 发放奖励。回执里的数字是展示用的，真正入账在这里。
    /// 有角色卡时经验进角色卡，否则记在会话上。
    /// </summary>
    public void GrantReward(int experience, int money)
    {
        if (experience > 0)
        {
            if (Hero is not null) Hero.AddExperience(experience);
            else _experience += experience;
        }

        Money = int.Max(0, Money + money);   // money 允许是负数（花钱），但不许透支到负
    }

    public void GainMoney(int amount)
    {
        if (amount < 0 || Money + (long)amount > int.MaxValue)
            throw new InvalidOperationException("金币数量无效");
        Money += amount;
    }

    public void SpendMoney(int amount)
    {
        if (amount < 0 || amount > Money) throw new InvalidOperationException("金币不足");
        Money -= amount;
    }

    public void Transaction(Action operation)
    {
        var before = CaptureState();
        try
        {
            operation();
        }
        catch
        {
            RestoreState(before);
            throw;
        }
    }

    public T Transaction<T>(Func<T> operation)
    {
        var before = CaptureState();
        try
        {
            return operation();
        }
        catch
        {
            RestoreState(before);
            throw;
        }
    }

    public void EnterMap(string mapId, Vec2 spawn, string facing,
        IReadOnlyList<Content.EnemySpawn>? mapEnemySpawns = null,
        IReadOnlyList<Content.QuestItemSpawn>? questItemSpawns = null)
    {
        var entering = mapId != MapId;

        if (entering && mapEnemySpawns is not null) ResetMapEnemies(mapEnemySpawns);

        foreach (var id in _enemyStates.Keys.ToArray())
        {
            var capture = _enemyStates[id].Capture;
            if (capture is not null && (capture.Map != mapId || capture.Visit != StoryWorld.Visit))
                _enemyStates.Remove(id);
        }

        if (entering)
        {
            foreach (var chestSpawn in Chests.Spawns) _enemyStates.Remove(chestSpawn.Id);
            if (MapId.Length > 0)
                foreach (var id in _enemyStates.Keys.ToArray())
                    if (!_enemyStates[id].DeadPermanently) _enemyStates.Remove(id);
        }

        if (entering) Visit++;
        MapId = mapId;
        Position = spawn;
        Facing = facing;

        if (entering && MapEntryPlacements.Count > 0)
            foreach (var entry in MapEntryPlacements)
            {
                if (entry.Map != mapId) continue;
                if (Journal is not null && !Story.StoryCondition.Evaluate(entry.Condition, Journal)) continue;

                StoryWorld.Apply(new Story.PlaceCommand(new Story.StoryEntityState
                {
                    Id = entry.Id,
                    Map = entry.Map,
                    Actor = entry.Actor,
                    CharacterId = entry.CharacterId,
                    Authority = Story.PlacementAuthority.Of(entry.Id, null),
                    X = entry.X,
                    Y = entry.Y,
                    Facing = entry.Facing,
                    Visible = true,
                }, VisitScope: true));
            }

        Chests.EnterMap(mapId);
        Springs.EnterMap(mapId);
        if (questItemSpawns is not null)
            QuestItems.Enter(mapId, StoryWorld.Visit, questItemSpawns, GroundLoot);
        Shops.EnterMap(mapId);
    }

    private void ResetMapEnemies(IReadOnlyList<Content.EnemySpawn> spawns)
    {
        foreach (var spawn in spawns)
        {
            if (spawn.Respawn == false) continue;
            if (_enemyStates.TryGetValue(spawn.Id, out var state) && state.DeadPermanently) continue;
            _enemyStates.Remove(spawn.Id);
        }
    }

    public void Moved(Vec2 position, string facing)
    {
        Position = position;
        Facing = facing;
    }

    public void Advance(long deltaMs)
    {
        if (deltaMs <= 0) return;
        GameTimeMs += deltaMs;
        Recovery.Advance(deltaMs);
    }

    /// <summary>
    /// 会话当前的职业：有角色卡取角色卡的，否则没有。
    /// 剧情会话里还能另外指定职业，这一档会话层没有，按「没有角色」处理。
    /// </summary>
    public string? ClassId => Hero?.ClassId;

    public SessionState CaptureState()
    {
        var journal = Journal ?? throw new InvalidOperationException("会话没有任务簿，不能快照");
        var bag = Bag ?? throw new InvalidOperationException("会话没有背包，不能快照");
        return new SessionState
        {
            ClassId = ClassId,
            QuestBook = journal.Book.CaptureState(),
            Money = Money,
            GameTimeMs = GameTimeMs,
            Bag = bag.CaptureState(),
            Hero = Hero?.CaptureState(),
            Skills = Skills?.CaptureState(),
            Party = Party?.CaptureState(),
            Shops = Shops.CaptureState(),
            GroundLoot = GroundLoot.CaptureState(),
            LootPity = LootPity.CaptureState(),
            Chests = Chests.CaptureState(),
            Springs = Springs.CaptureState(),
            Props = Props.CaptureState(),
            Recovery = Recovery.CaptureState(),
            CombatClock = this.CombatClock.CaptureState(),
            Cooldowns = Cooldowns.CaptureState(),
            FrenzyAnimations = new Combat.FrenzyAnimationState
            {
                Actors = [.. FrenzyAnimations.CaptureState()
                    .Select(pair => new Combat.FrenzyAnimationEntry { Id = pair.Key, Frames = pair.Value })],
            },
            ResourceRecovery = ResourceRecovery.CaptureState(),
            QuestItemGeneration = QuestItems.CaptureState(),
            StoryWorld = StoryWorld.CaptureState(),
            StoryEvents = journal.CaptureEvents(),
            StoryCursor = StoryCursor,
            Enemies = _enemyStates.ToDictionary(pair => pair.Key, pair => pair.Value),
            Receipts = journal.CaptureReceipts(),
            Runtime = CaptureStoryRuns?.Invoke(),
        };
    }

    public SessionState CapturePersistentState()
    {
        var state = CaptureState();
        var questBook = state.QuestBook;
        if (questBook.DebugPins.Count > 0)
        {
            state.QuestBook = new Story.QuestBookState
            {
                Version = questBook.Version,
                Quests = questBook.Quests,
                RunningQuestIds = questBook.RunningQuestIds,
                FactSequences = questBook.FactSequences,
            };
        }

        return state;
    }

    /// <summary>
    /// 把存档灌回当前会话。校验全部交给各子系统自己的 <c>RestoreState</c>：
    /// 它们对背包、任务、队伍逐项验；这里只补会话判不了的一条——存档必须出自同一个角色。
    /// 恢复次序有讲究：剧情世界、运行表、回执、
    /// 敌人状态与战斗时钟各有各的前提，下面按依赖顺序逐个落地（见注释）。
    /// </summary>
    public void RestoreState(SessionState state)
    {
        if (state.ClassId != ClassId || (state.Hero is not null) != (Hero is not null) ||
            (state.Party is not null) != (Party is not null))
            throw new InvalidOperationException("检查点与当前游戏角色不一致");
        var bag = Bag ?? throw new InvalidOperationException("会话没有背包，不能恢复");
        var journal = Journal ?? throw new InvalidOperationException("会话没有任务簿，不能恢复");

        bag.RestoreState(state.Bag);
        if (state.Hero is not null) Hero!.RestoreState(state.Hero);
        if (state.Skills is not null) Skills?.RestoreState(state.Skills);
        if (state.Party is not null) Party!.RestoreState(state.Party);
        journal.Book.RestoreState(state.QuestBook);
        Shops.RestoreState(state.Shops);
        if (state.GroundLoot is not null) GroundLoot.RestoreState(state.GroundLoot);
        if (state.LootPity is not null) LootPity.RestoreState(state.LootPity);
        if (state.Chests is not null) Chests.RestoreState(state.Chests);
        if (state.Springs is not null) Springs.RestoreState(state.Springs);
        if (state.Props is not null) Props.RestoreState(state.Props);
        if (state.Recovery is not null) Recovery.RestoreState(state.Recovery);

        var elapsed = state.ResourceRecovery?.ElapsedMs ?? 0;
        CombatClock.RestoreState(state.CombatClock ?? new Combat.CombatClockState
        {
            Frame = elapsed / Combat.CombatClock.LogicFrameMs,
            RemainderMs = elapsed % Combat.CombatClock.LogicFrameMs,
        });
        Cooldowns.RestoreState(state.Cooldowns ?? new Combat.CombatCooldownState());
        FrenzyAnimations.RestoreState(state.FrenzyAnimations is { } frenzy
            ? [.. frenzy.Actors.Select(entry => new KeyValuePair<string, int>(entry.Id, entry.Frames))]
            : []);
        ResourceRecovery.RestoreState(new Combat.CombatResourceRecoveryState
        {
            ElapsedMs = CombatClock.Frame * Combat.CombatClock.LogicFrameMs,
            DeadlineTick = state.ResourceRecovery?.DeadlineTick
                           ?? CombatClock.Frame + Combat.CombatResources.RecoveryIntervalFrames - 1,
        });

        if (state.QuestItemGeneration is not null)
            QuestItems.RestoreState(state.QuestItemGeneration, GroundLoot);
        PendingStoryCursor = state.StoryCursor;
        if (state.StoryWorld is not null) StoryWorld.RestoreState(state.StoryWorld);
        // 事件进度必须显式覆盖：读档前这份进程内标记可能是别的存档留下的。
        journal.RestoreEvents(state.StoryEvents);
        journal.RestoreReceipts(state.Receipts);
        if (ApplyStoryRuns is { } applyRuns)
        {
            applyRuns(state.Runtime);
            PendingStoryRuns = null;
        }
        else
        {
            PendingStoryRuns = state.Runtime;
        }

        _enemyStates.Clear();
        foreach (var (id, enemy) in state.Enemies) _enemyStates[id] = enemy;

        Money = state.Money;
        GameTimeMs = state.GameTimeMs;
    }
}

public sealed record EnemyCapture(string Map, int Visit);

public sealed record EnemyState
{
    public int? Health { get; init; }

    public int? Mana { get; init; }

    public long? DeadUntilMs { get; init; }

    public bool DeadPermanently { get; init; }

    public EnemyCapture? Capture { get; init; }
}
