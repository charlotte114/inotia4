using Inotia4.Domain.Combat;
using Inotia4.Domain.Content;
using Inotia4.Domain.Story;

namespace Inotia4.Domain.World;

public sealed record WorldSpawn(
    string Id,
    string EnemyId,
    float X,
    float Y,
    int? TierLevel = null,
    bool BreakableEligible = false,
    bool Respawn = true,
    string? ActiveQuest = null,
    string? RewardItem = null)
{
    public static WorldSpawn From(EnemySpawn spawn) => new(
        spawn.Id, spawn.EnemyId, spawn.X, spawn.Y, spawn.TierLevel, spawn.BreakableEligible,
        spawn.Respawn != false);

    public static WorldSpawn From(ChestEnemySpawn spawn) => new(
        spawn.Id, spawn.EnemyId, spawn.X, spawn.Y, Respawn: spawn.Respawn);
}

public sealed record QuestEnemySnapshot(int Revision, IReadOnlySet<string> Ids);

public interface IQuestEnemyProvider
{
    QuestEnemySnapshot Snapshot();
}

public interface IStoryEnemyWorld
{
    int Revision { get; }

    bool EnemyAvailable(string mapId, string enemyId);

    bool Visible(string id);

    IReadOnlyList<WorldSpawn> EnemySpawns(string mapId);

    IReadOnlyList<StoryEncounter> ActiveEncounters { get; }
}

public sealed class EmptyStoryEnemyWorld : IStoryEnemyWorld
{
    public static readonly EmptyStoryEnemyWorld Instance = new();

    public int Revision => 0;

    public bool EnemyAvailable(string mapId, string enemyId) => true;

    public bool Visible(string id) => true;

    public IReadOnlyList<WorldSpawn> EnemySpawns(string mapId) => [];

    public IReadOnlyList<StoryEncounter> ActiveEncounters => [];
}

public sealed class StoryWorldEnemyWorld(StoryWorldState world) : IStoryEnemyWorld
{
    public int Revision => world.Revision;

    public bool EnemyAvailable(string mapId, string enemyId) => world.EnemyAvailable(mapId, enemyId);

    public bool Visible(string id) => world.Visible(id);

    public IReadOnlyList<WorldSpawn> EnemySpawns(string mapId) => world.EnemySpawns(mapId)
        .Select(location => new WorldSpawn(
            location.Id, location.EnemyId, location.X, location.Y, Respawn: location.Respawn != false))
        .ToArray();

    public IReadOnlyList<StoryEncounter> ActiveEncounters => world.ActiveEncounters;
}

/// <summary>一次档位选择可用的外部判断：哪些敌人算任务关键、某个刷怪点当前可不可生成。</summary>
public sealed record MapSpawnSelectionOptions(
    IReadOnlySet<string>? QuestEnemies = null,
    Func<WorldSpawn, bool>? Spawnable = null);

public static class WorldEnemyRules
{
    public static int[] TierLevels(IEnumerable<WorldSpawn> spawns) =>
        spawns.Where(spawn => spawn.TierLevel is not null).Select(spawn => spawn.TierLevel!.Value)
            .Distinct().OrderBy(value => value).ToArray();

    /// <summary>
    /// 按主角等级选择地图的敌人档位：取不超过主角等级的最高档，低于全部档位时用最低档；
    /// 进行中任务的关键敌人无视档位保留。整个静态选择在当前剧情下都不可生成时，
    /// 只向更低的一档回退一次，避免提前放出未来章节的敌人。
    /// </summary>
    public static IReadOnlyList<WorldSpawn> SelectMapSpawns(
        IReadOnlyList<WorldSpawn> raw,
        IReadOnlyList<WorldSpawn> questSpawns,
        int heroLevel,
        MapSpawnSelectionOptions? options = null)
    {
        options ??= new MapSpawnSelectionOptions();
        var tiers = TierLevels(raw);
        if (tiers.Length == 0) return [.. raw, .. questSpawns];

        var selected = tiers[0];
        foreach (var tier in tiers)
            if (tier <= heroLevel) selected = tier;

        var questEnemies = options.QuestEnemies;
        var spawnable = options.Spawnable ?? (_ => true);
        var chosen = new HashSet<int>();
        for (var index = 0; index < raw.Count; index += 1)
        {
            var spawn = raw[index];
            if (spawn.TierLevel is null || spawn.TierLevel == selected ||
                (questEnemies?.Contains(spawn.EnemyId) ?? false))
                chosen.Add(index);
        }

        var baseSpawns = chosen.OrderBy(index => index).Select(index => raw[index]).ToArray();
        if (!baseSpawns.Concat(questSpawns).Any(spawnable))
        {
            // 只补更低的一档，并用它替换不可用的选中档；没有低档可用就保持空图。
            foreach (var tier in tiers.Where(value => value < selected).Reverse())
            {
                var indices = raw.Select((spawn, index) => (spawn, index))
                    .Where(pair => pair.spawn.TierLevel == tier).Select(pair => pair.index).ToArray();
                if (!indices.Any(index => spawnable(raw[index]))) continue;

                foreach (var (spawn, index) in raw.Select((spawn, index) => (spawn, index)))
                {
                    if (spawn.TierLevel == selected && !(questEnemies?.Contains(spawn.EnemyId) ?? false))
                        chosen.Remove(index);
                }

                foreach (var index in indices) chosen.Add(index);
                break;
            }
        }

        return [.. raw.Where((_, index) => chosen.Contains(index)), .. questSpawns];
    }
}

public sealed class WorldEnemyRoster : EnemyRoster
{
    private readonly Session _session;
    private readonly string _mapId;
    private readonly IReadOnlyList<WorldSpawn> _rawMapSpawns;
    private readonly IReadOnlyList<WorldSpawn> _bandSpawns;
    private readonly HashSet<string> _mapSpawnIds;
    private readonly int _heroLevel;
    private readonly IQuestEnemyProvider? _questEnemies;
    private readonly string? _breakableSpawn;
    private readonly Func<double> _random;
    private readonly IStoryEnemyWorld _story;

    private readonly Dictionary<string, string> _breakableRolls = [];

    private readonly IReadOnlyDictionary<string, WorldSpawn> _breakableSlots;

    private readonly HashSet<string> _questSpawnIds = [];

    private readonly Dictionary<string, WorldSpawn> _sources = [];

    /// <summary>可破坏物替换结果的版本：缓存重建后必须重新掷骰。</summary>
    private int _breakableRevision;

    private (string Key, IReadOnlyList<WorldSpawn> Value)? _spawnsCache;

    public WorldEnemyRoster(Session session, string mapId, IReadOnlyList<EnemySpawn> mapSpawns,
        IReadOnlyList<WorldSpawn> questSpawns, int heroLevel, string? breakableSpawn,
        IQuestEnemyProvider? questEnemies = null, Func<double>? random = null,
        IStoryEnemyWorld? story = null)
    {
        _session = session;
        _mapId = mapId;
        _heroLevel = heroLevel;
        _questEnemies = questEnemies;
        _breakableSpawn = breakableSpawn;
        _random = random ?? (() => 1.0);
        _story = story ?? new StoryWorldEnemyWorld(session.StoryWorld);
        _rawMapSpawns = mapSpawns.Select(WorldSpawn.From).ToArray();

        _breakableSlots = _rawMapSpawns.Concat(questSpawns)
            .Where(spawn => spawn.BreakableEligible)
            .GroupBy(spawn => spawn.Id)
            .ToDictionary(group => group.Key, group => group.First());

        SetPartySize(_session.Party?.ActiveSize ?? 1, []);
        CollectQuestSpawns();
        _bandSpawns = WorldEnemyRules.SelectMapSpawns(_rawMapSpawns, questSpawns, heroLevel,
            new MapSpawnSelectionOptions(_questSpawnIds, StorySpawnable));
        _mapSpawnIds = _bandSpawns.Select(spawn => spawn.Id).ToHashSet();

        foreach (var spawn in Spawns) _sources[spawn.Id] = spawn;
        Refresh();
    }

    public IReadOnlyList<WorldSpawn> Spawns
    {
        get
        {
            var questRevision = CollectQuestSpawns();
            var key = $"{_story.Revision}|{_session.Chests.Revision}|{questRevision}|{_questSpawnIds.Count}|{_breakableRevision}";
            if (_spawnsCache is { } cached && cached.Key == key) return cached.Value;

            var extras = _questSpawnIds.Count > 0
                ? _rawMapSpawns.Where(spawn => _questSpawnIds.Contains(spawn.EnemyId) &&
                                               !_mapSpawnIds.Contains(spawn.Id))
                : [];

            var value = new List<WorldSpawn>();
            value.AddRange(_bandSpawns.Where(spawn => _story.EnemyAvailable(_mapId, spawn.EnemyId))
                .Select(BreakableSpawnFor));
            value.AddRange(extras.Where(spawn => _story.EnemyAvailable(_mapId, spawn.EnemyId))
                .Select(BreakableSpawnFor));
            value.AddRange(_story.EnemySpawns(_mapId));
            value.AddRange(_session.Chests.Spawns.Select(WorldSpawn.From));

            value.AddRange(_story.ActiveEncounters
                .Where(encounter => encounter.Map == _mapId)
                .SelectMany(encounter => encounter.Spawns.Select(spawn => new WorldSpawn(
                    EncounterSpawnId(encounter.Id, spawn.Id), spawn.EnemyId, spawn.X, spawn.Y,
                    Respawn: false))));

            _spawnsCache = (key, value);
            return value;
        }
    }

    public IReadOnlyList<StoryEncounter> CompletedEncounters =>
        _story.ActiveEncounters
            .Where(encounter => encounter.Map == _mapId && encounter.Spawns.Count > 0)
            .Where(encounter => encounter.Spawns.All(spawn =>
            {
                var state = _session.EnemyVitals(EncounterSpawnId(encounter.Id, spawn.Id));
                return (state.DeadPermanently || state.DeadUntilMs is not null) && state.Capture is null;
            }))
            .ToArray();

    public static string EncounterSpawnId(string encounter, string spawn) => $"{encounter}:{spawn}";

    public WorldSpawn? Source(string id) => _sources.GetValueOrDefault(id);

    public override void RestoreState(EnemyRosterState state)
    {
        base.RestoreState(state);

        // 可破坏物槽的替换结果必须跟着名册里的身份走，否则下一次 refresh 会把同 key
        // 当成「换了只怪」，先删再按新随机身份加回，与场上实例错开。
        foreach (var placed in Enemies)
        {
            if (!_breakableSlots.TryGetValue(placed.Key, out var slot)) continue;
            if (_questSpawnIds.Contains(slot.EnemyId)) _breakableRolls.Remove(placed.Key);
            else _breakableRolls[placed.Key] = placed.EnemyId;
        }

        _breakableRevision++;
        _sources.Clear();
        foreach (var spawn in Spawns) _sources[spawn.Id] = spawn;
    }

    public (IReadOnlyList<PlacedEnemy> Added, IReadOnlyList<string> Removed) Refresh()
    {
        var added = new List<PlacedEnemy>();
        var removed = new List<string>();

        var spawns = Spawns;
        var active = new HashSet<string>();
        var current = new Dictionary<string, WorldSpawn>();
        foreach (var spawn in spawns)
        {
            active.Add(spawn.Id);
            current[spawn.Id] = spawn;
        }

        var present = Enemies.Select(enemy => enemy.Key).ToHashSet();
        foreach (var placed in Enemies.ToArray())
        {
            var spawn = current.GetValueOrDefault(placed.Key) ?? Source(placed.Key);
            if (spawn is null) continue;

            var leaving = !active.Contains(spawn.Id) || !CanSpawn(spawn);
            // 同 key 换身份只是这一槽改了外观，缓存仍是本次访问的结果，下一帧不能再掷一次。
            if (!leaving && placed.EnemyId == spawn.EnemyId) continue;

            base.RemoveEnemy(placed.Key);
            present.Remove(placed.Key);
            removed.Add(placed.Key);
            if (!leaving) continue;
            _breakableRolls.Remove(placed.Key);
            _breakableRevision++;
        }

        foreach (var spawn in spawns)
        {
            _sources[spawn.Id] = spawn;
            if (!CanSpawn(spawn) || present.Contains(spawn.Id)) continue;

            var vitals = _session.EnemyVitals(spawn.Id);
            var placed = new PlacedEnemy
            {
                Key = spawn.Id, EnemyId = spawn.EnemyId, X = spawn.X, Y = spawn.Y,
                Health = vitals.Health, Mana = vitals.Mana,
            };
            RestoreEnemy(placed);
            present.Add(spawn.Id);
            added.Add(placed);
        }

        return (added, removed);
    }

    public void SetEnemyVitals(string key, int health, int mana) => _session.SetEnemyVitals(key, health, mana);

    public override void SetEnemyHealth(string key, int value, EnemyDefinition definition)
    {
        base.SetEnemyHealth(key, value, definition);
        PersistVitals(key, definition);
    }

    public override void SetEnemyMana(string key, int value, EnemyDefinition definition)
    {
        base.SetEnemyMana(key, value, definition);
        PersistVitals(key, definition);
    }

    public override void SetPartySize(int size, IReadOnlyList<EnemyDefinition> definitions)
    {
        if (size == PartySize) return;
        base.SetPartySize(size, definitions);
        foreach (var placed in Enemies)
        {
            var definition = definitions.FirstOrDefault(enemy => enemy.Id == placed.EnemyId);
            if (definition is not null) PersistVitals(placed.Key, definition);
        }
    }

    private void PersistVitals(string key, EnemyDefinition definition)
    {
        var snapshot = EnemySnapshot(key, definition, _session.Hero?.Level ?? 1);
        if (snapshot is not null) _session.SetEnemyVitals(key, snapshot.Health, snapshot.Mana);
    }

    public static EnemySpawn ToEnemySpawn(WorldSpawn spawn) => new()
    {
        Id = spawn.Id, EnemyId = spawn.EnemyId, X = spawn.X, Y = spawn.Y,
        TierLevel = spawn.TierLevel, BreakableEligible = spawn.BreakableEligible,
        Respawn = spawn.Respawn ? null : false,
    };

    private WorldSpawn BreakableSpawnFor(WorldSpawn spawn)
    {
        if (!spawn.BreakableEligible) return spawn;

        if (_questSpawnIds.Contains(spawn.EnemyId))
        {
            _breakableRolls.Remove(spawn.Id);
            _breakableRevision++;
            return spawn;
        }

        if (!_breakableRolls.TryGetValue(spawn.Id, out var enemyId))
        {
            enemyId = RollBreakableEnemy(spawn.EnemyId);
            _breakableRolls[spawn.Id] = enemyId;
            _breakableRevision++;
        }

        return enemyId == spawn.EnemyId ? spawn : spawn with { EnemyId = enemyId };
    }

    private string RollBreakableEnemy(string original)
    {
        var roll = (int)Math.Floor(_random() * 100);
        var ids = Drops.BreakableEnemyIds;
        switch (_breakableSpawn)
        {
            case "bush":
                if (roll < 5) return ids["bush"];
                if (roll < 7) return ids["box"];
                break;
            case "jar":
                if (roll < 5) return ids["jar"];
                if (roll < 7) return ids["box"];
                break;
            default:
                if (roll < 5) return ids["box"];
                break;
        }

        return original;
    }

    private int CollectQuestSpawns()
    {
        var snapshot = _questEnemies?.Snapshot();
        if (snapshot is null) return 0;
        foreach (var id in snapshot.Ids) _questSpawnIds.Add(id);
        return snapshot.Revision;
    }

    private bool StorySpawnable(WorldSpawn spawn)
    {
        if (spawn.ActiveQuest is { Length: > 0 } quest &&
            _session.Journal?.Book.Status(quest) != QuestStatus.Active)
            return false;
        return _story.Visible(spawn.Id) && _story.EnemyAvailable(_mapId, spawn.EnemyId);
    }

    private bool CanSpawn(WorldSpawn spawn) =>
        StorySpawnable(spawn) && _session.CanSpawnEnemy(ToEnemySpawn(spawn));
}
