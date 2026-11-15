using System.Text.Json.Serialization;
using Inotia4.Domain.Character;
using Inotia4.Domain.Combat;
using Inotia4.Domain.Content;

namespace Inotia4.Domain.Party;

public sealed class CompanionDefinition
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;

    [JsonPropertyName("actor")] public string Actor { get; init; } = string.Empty;

    [JsonPropertyName("classId")] public string ClassId { get; init; } = string.Empty;

    [JsonPropertyName("elite")] public bool Elite { get; init; }

    [JsonPropertyName("source")] public string? Source { get; init; }

    [JsonPropertyName("quality")] public int? Quality { get; init; }

    [JsonPropertyName("recruitable")] public bool? Recruitable { get; init; }

    [JsonPropertyName("fixedSkills")] public string[] FixedSkills { get; init; } = [];

    [JsonPropertyName("defaultShield")] public bool DefaultShield { get; init; }

    public string SourceOrDefault => Source ?? "mercenary";
}

public sealed class CompanionCatalog
{
    [JsonPropertyName("definitions")] public Dictionary<string, CompanionDefinition> Definitions { get; init; } = new();

    [JsonPropertyName("growth")] public Dictionary<string, PrimaryAttributes[]> Growth { get; init; } = new();

    [JsonPropertyName("generation")] public CompanionGenerationRules Generation { get; init; } = new();

    [JsonPropertyName("groupSkills")] public Dictionary<string, CompanionGroupSkill> GroupSkills { get; init; } = new();

    [JsonPropertyName("autoSkills")] public Dictionary<string, CompanionAutoSkill> AutoSkills { get; init; } = new();

    public string? AutoSkillKind(string skillId) =>
        AutoSkills.TryGetValue(skillId, out var entry) ? entry.Kind : null;
}

public sealed class CompanionAutoSkill
{
    [JsonPropertyName("kind")] public string Kind { get; init; } = string.Empty;
}

public sealed class CompanionSnapshot
{
    public required string Id { get; init; }

    public required string DefinitionId { get; init; }

    public int? RosterSlot { get; init; }

    public Dictionary<string, int> SkillLevels { get; init; } = new();

    public Dictionary<string, int> SkillCaps { get; init; } = new();

    public List<string> GroupSkills { get; init; } = [];

    public int Quality { get; init; }

    public string Name { get; init; } = string.Empty;

    public string ClassId { get; init; } = string.Empty;

    public string Source { get; init; } = "mercenary";

    public bool Elite { get; init; }

    public int Level { get; init; }

    public int Health { get; init; }

    public int MaxHealth { get; init; }

    public int Mana { get; init; }

    public int MaxMana { get; init; }

    public int Experience { get; init; }

    public int NextLevelExperience { get; init; }
}

public sealed class CompanionState
{
    public required string Id { get; set; }

    public required string DefinitionId { get; set; }

    public required string Source { get; set; }

    public GeneratedCompanion Generated { get; set; } = new();

    public int Level { get; set; }

    public int Health { get; set; }

    public int Mana { get; set; }

    public int Experience { get; set; }

    public SkillConfigurationState Skills { get; set; } = new();

    [System.Text.Json.Serialization.JsonConverter(typeof(ResourceLimitsConverter))]
    public (int MaxHealth, int MaxMana)? CombatResourceLimits { get; set; }

    public List<KeyValuePair<string, int>> AiFrequency { get; set; } = [];

    public int Counterattack { get; set; } = 2;

    public CompanionState Clone() => new()
    {
        Id = Id,
        DefinitionId = DefinitionId,
        Source = Source,
        Generated = Generated.Clone(),
        Level = Level,
        Health = Health,
        Mana = Mana,
        Experience = Experience,
        Skills = Skills.Clone(),
        CombatResourceLimits = CombatResourceLimits,
        AiFrequency = [.. AiFrequency],
        Counterattack = Counterattack,
    };
}

public sealed class PartyState
{
    public List<CompanionState> Members { get; set; } = [];

    public List<string?> RosterSlots { get; set; } = [];

    public List<string?> Slots { get; set; } = [];

    public string Selected { get; set; } = "player";

    public int Serial { get; set; }

    public List<KeyValuePair<string, int>> HeroAiFrequency { get; set; } = [];

    public int HeroCounterattack { get; set; } = 2;

    public PartyState Clone()
    {
        var members = new List<CompanionState>(Members.Count);
        foreach (var member in Members) members.Add(member.Clone());
        return new PartyState
        {
            Members = members,
            RosterSlots = [.. RosterSlots],
            Slots = [.. Slots],
            Selected = Selected,
            Serial = Serial,
            HeroAiFrequency = [.. HeroAiFrequency],
            HeroCounterattack = HeroCounterattack,
        };
    }
}

public sealed class CompanionMember
{
    private readonly CharacterStatsCatalog _stats;

    private readonly ItemCatalog _items;

    private readonly PrimaryAttributes[] _growth;

    private readonly Action _changed;

    private readonly Dictionary<string, int> _aiFrequency = [];

    private int _currentLevel;

    private int _currentHealth;

    private int _currentMana;

    private int _currentExperience;

    private (int MaxHealth, int MaxMana)? _combatResourceLimits;

    private PanelCache? _panelCache;

    private sealed record PanelCache(int Revision, int Level, CharacterPanel Panel);

    public CompanionMember(string id, CompanionDefinition definition, string source, int level,
        CharacterStatsCatalog stats, ItemCatalog items, Bag bag, PrimaryAttributes[] growth,
        Action changed, GeneratedCompanion generated, Dictionary<string, CompanionTraining> training)
    {
        if (!stats.Classes.ContainsKey(definition.ClassId) || growth.Length < stats.MaxLevel)
            throw new ArgumentException("伙伴职业成长数据缺失");

        Id = id;
        Definition = definition;
        Source = source;
        _stats = stats;
        _items = items;
        Bag = bag;
        _growth = growth;
        _changed = changed;
        _currentLevel = Math.Min(stats.MaxLevel, Math.Max(1, level));
        Generated = generated.Clone();
        Skills = new CompanionSkillProgression(Generated.SkillCaps, training);
        Skills.UpdateLevel(_currentLevel);

        _currentHealth = Panel.MaxHealth;
        _currentMana = Panel.MaxMana;
    }

    public string Id { get; }

    public CompanionDefinition Definition { get; }

    public string Source { get; }

    public CompanionSkillProgression Skills { get; }

    public GeneratedCompanion Generated { get; }

    public Bag Bag { get; }

    public Dictionary<string, int> AiFrequency => _aiFrequency;

    public int Counterattack { get; set; } = 2;

    public string ClassId => Definition.ClassId;

    public int Level => _currentLevel;

    public CharacterPanel Panel
    {
        get
        {
            var cached = _panelCache;
            if (cached is not null && cached.Revision == Bag.Revision && cached.Level == _currentLevel)
                return cached.Panel;

            var baseAttributes = _stats.Classes[Definition.ClassId].ToPrimary();
            var total = _growth[_currentLevel - 1];
            var values = new int[PrimaryStats.All.Length];
            for (var index = 0; index < PrimaryStats.All.Length; index++)
            {
                var stat = PrimaryStats.All[index];
                values[index] = total.Get(stat) - 2 * baseAttributes.Get(stat) + Generated.BaseAttributes.Get(stat);
            }

            var allocated = new PrimaryAttributes
            {
                Strength = values[0],
                Dexterity = values[1],
                Vitality = values[2],
                Intelligence = values[3],
                Mentality = values[4],
            };

            var equipped = new Dictionary<string, ItemInstance>();
            foreach (var (slot, id) in Bag.EquipmentFor(Id))
            {
                var entry = Bag.Get(id);
                if (entry is not null) equipped[slot] = entry;
            }

            var panel = CharacterPanel.Calculate(_stats, _items, Definition.ClassId, _currentLevel, allocated, equipped);
            _panelCache = new PanelCache(Bag.Revision, _currentLevel, panel);
            return panel;
        }
    }

    public PlayerVitals Player
    {
        get
        {
            var panel = Panel;
            var maxHealth = _combatResourceLimits?.MaxHealth ?? panel.MaxHealth;
            var maxMana = _combatResourceLimits?.MaxMana ?? panel.MaxMana;
            return new PlayerVitals(_currentLevel, _currentHealth, maxHealth, _currentMana, maxMana, _currentExperience,
                _stats.ExperienceToNext(_currentLevel) ?? 0);
        }
    }

    public bool Alive => _currentHealth > 0;

    public void SetHealth(int value)
    {
        _currentHealth = BattleCharacter.BoundedHealth(value, Player.MaxHealth);
        _changed();
    }

    public void SetMana(int value)
    {
        _currentMana = BattleCharacter.BoundedHealth(value, Player.MaxMana);
        _changed();
    }

    public bool SpendMana(int cost)
    {
        if (_currentMana < cost) return false;
        _currentMana -= cost;
        _changed();
        return true;
    }

    public void SetCombatResourceLimits(int maxHealth, int maxMana)
    {
        var next = (Math.Max(0, maxHealth), Math.Max(0, maxMana));
        if (_combatResourceLimits is { } current && current.MaxHealth == next.Item1 && current.MaxMana == next.Item2)
            return;

        _combatResourceLimits = next;
        _changed();
    }

    public void SetCombatResourceCapacity(string key, int value)
    {
        var before = Player;
        var next = Math.Max(0, value);
        var current = key == "maxHealth" ? _currentHealth : _currentMana;
        var clamped = BattleCharacter.BoundedHealth(current, next);
        if ((key == "maxHealth" ? before.MaxHealth : before.MaxMana) == next && current == clamped) return;

        _combatResourceLimits = key == "maxHealth" ? (next, before.MaxMana) : (before.MaxHealth, next);
        if (key == "maxHealth") _currentHealth = clamped;
        else _currentMana = clamped;
        _changed();
    }

    public void ClearCombatResourceLimits()
    {
        _combatResourceLimits = null;
        _currentHealth = BattleCharacter.BoundedHealth(_currentHealth, Panel.MaxHealth);
        _currentMana = BattleCharacter.BoundedHealth(_currentMana, Panel.MaxMana);
        _changed();
    }

    public void Recover()
    {
        var player = Player;
        _currentHealth = player.MaxHealth;
        _currentMana = player.MaxMana;
        _changed();
    }

    public int AddExperience(int amount)
    {
        if (amount < 0) throw new ArgumentException("经验奖励无效");
        if (amount == 0 || _currentLevel >= _stats.MaxLevel) return 0;

        _currentExperience += amount;
        var levels = 0;
        while (_currentLevel < _stats.MaxLevel)
        {
            var needed = _stats.ExperienceToNext(_currentLevel);
            if (needed is null) break;
            if (_currentExperience < needed) break;
            _currentExperience -= needed.Value;
            _currentLevel++;
            levels++;
        }

        if (_currentLevel == _stats.MaxLevel) _currentExperience = 0;
        if (levels > 0)
        {
            Skills.UpdateLevel(_currentLevel);
            if (Alive) Recover();
            else _changed();
        }
        else
        {
            _changed();
        }

        return levels;
    }

    public void SetAiFrequency(string skillId, int frequency)
    {
        if (frequency < 0 || frequency > 2) throw new ArgumentException("技能频率需要是 0–2");
        _aiFrequency[skillId] = frequency;
        _changed();
    }

    public CompanionSnapshot Snapshot(int? rosterSlot = null)
    {
        var player = Player;
        return new CompanionSnapshot
        {
            Id = Id,
            DefinitionId = Definition.Id,
            RosterSlot = rosterSlot,
            SkillLevels = Skills.SkillLevels(),
            SkillCaps = new Dictionary<string, int>(Generated.SkillCaps),
            GroupSkills = [.. Generated.GroupSkills],
            Quality = Definition.Quality ?? 0,
            Name = Definition.Name,
            ClassId = Definition.ClassId,
            Source = Source,
            Elite = Definition.Elite,
            Level = player.Level,
            Health = player.Health,
            MaxHealth = player.MaxHealth,
            Mana = player.Mana,
            MaxMana = player.MaxMana,
            Experience = player.Experience,
            NextLevelExperience = player.NextLevelExperience,
        };
    }

    public CompanionState CaptureState() => new()
    {
        Id = Id,
        DefinitionId = Definition.Id,
        Source = Source,
        Generated = Generated.Clone(),
        Level = _currentLevel,
        Health = _currentHealth,
        Mana = _currentMana,
        Experience = _currentExperience,
        CombatResourceLimits = _combatResourceLimits,
        Skills = Skills.CaptureState(),
        AiFrequency = [.. _aiFrequency],
        Counterattack = Counterattack,
    };

    public void RestoreState(CompanionState state)
    {
        var restored = state.Clone();
        _currentLevel = restored.Level;
        _currentHealth = restored.Health;
        _currentMana = restored.Mana;
        _currentExperience = restored.Experience;
        _combatResourceLimits = restored.CombatResourceLimits;
        Skills.UpdateLevel(_currentLevel);
        Skills.RestoreState(restored.Skills);
        _aiFrequency.Clear();
        foreach (var (id, value) in restored.AiFrequency) _aiFrequency[id] = value;
        Counterattack = restored.Counterattack;
    }
}

public sealed class PartyRoster
{
    public const int MercenarySlotCount = 18;

    private readonly CharacterStatsCatalog _stats;

    private readonly ItemCatalog _items;

    private readonly Action _changed;

    private readonly Action<string>? _onMemberRemoved;

    private readonly Action<string, string>? _onRecruited;

    private readonly Action<Action>? _transaction;

    private readonly Dictionary<string, CompanionMember> _owned = [];

    private string?[] _slots = [null, null];

    private string _selected = "player";

    private int _serial;

    private string?[] _rosterSlots = new string?[MercenarySlotCount];

    public PartyRoster(BattleCharacter hero, CharacterStatsCatalog stats, ItemCatalog items, CompanionCatalog catalog,
        Action changed, Action<string>? onMemberRemoved = null, Action<string, string>? onRecruited = null,
        Action<Action>? transaction = null)
    {
        Hero = hero;
        Bag = hero.Bag;
        _stats = stats;
        _items = items;
        Catalog = catalog;
        _changed = changed;
        _onMemberRemoved = onMemberRemoved;
        _onRecruited = onRecruited;
        _transaction = transaction;
    }

    public BattleCharacter Hero { get; }

    public Bag Bag { get; }

    public CompanionCatalog Catalog { get; }

    public Dictionary<string, int> HeroAiFrequency { get; } = [];

    public int HeroCounterattack { get; set; } = 2;

    public string ControlledId => _selected;

    public List<string> ActiveIds
    {
        get
        {
            var ids = new List<string> { "player" };
            foreach (var id in _slots)
                if (id is not null) ids.Add(id);
            return ids;
        }
    }

    public List<string> LivingIds => ActiveIds.Where(Alive).ToList();

    public int ActiveSize => ActiveIds.Count;

    public CompanionMember? Get(string id) => _owned.GetValueOrDefault(id);

    public Dictionary<string, int> AiFrequency(string id) =>
        id == "player" ? HeroAiFrequency : _owned.TryGetValue(id, out var member) ? member.AiFrequency : [];

    public int Counterattack(string id) =>
        id == "player" ? HeroCounterattack : _owned.TryGetValue(id, out var member) ? member.Counterattack : 0;

    public void SetCounterattack(string id, int mode)
    {
        if (mode is not (0 or 1 or 2)) throw new ArgumentException("反击档位需要是 0–2");
        if (id == "player")
        {
            HeroCounterattack = mode;
        }
        else
        {
            var member = _owned.GetValueOrDefault(id) ?? throw new InvalidOperationException("伙伴不存在");
            member.Counterattack = mode;
        }

        _changed();
    }

    public void SetAiFrequency(string id, string skill, int frequency)
    {
        if (frequency < 0 || frequency > 2) throw new ArgumentException("技能频率需要是 0–2");
        if (id != "player" && !_owned.ContainsKey(id)) throw new InvalidOperationException("伙伴不存在");
        AiFrequency(id)[skill] = frequency;
        _changed();
    }

    public bool Alive(string id) =>
        id == "player" ? Hero.Player.Health > 0 : _owned.GetValueOrDefault(id)?.Alive ?? false;

    public PartySnapshot Snapshot()
    {
        var active = ActiveIds;
        var members = new List<CompanionSnapshot>();
        foreach (var member in _owned.Values)
        {
            var slot = Array.IndexOf(_rosterSlots, member.Id);
            members.Add(member.Snapshot(slot < 0 ? null : slot));
        }

        return new PartySnapshot
        {
            Active = active,
            Living = LivingIds,
            RosterSlots = [.. _rosterSlots],
            Reserves = _owned.Keys.Where(id => !active.Contains(id)).ToList(),
            Controlled = _selected,
            Members = members,
        };
    }

    private CompanionMember Create(CompanionDefinition definition, string source, string id, GeneratedCompanion generated)
    {
        if (!Catalog.Growth.ContainsKey(definition.ClassId)) throw new InvalidOperationException("伙伴成长数据缺失");

        var member = new CompanionMember(id, definition, source, generated.Level, _stats, _items, Bag,
            Catalog.Growth[definition.ClassId], _changed, generated, Catalog.Generation.Training);

        var gear = _items.StartingEquipmentOf(definition.ClassId);
        try
        {
            foreach (var (slot, item) in gear) Bag.AddEquipped(item, slot, id);
        }
        catch
        {
            Bag.RemoveOwner(id, false);
            throw;
        }

        member.Recover();
        _owned[id] = member;

        if (source == "mercenary")
        {
            var slot = Array.IndexOf(_rosterSlots, null);
            if (slot >= 0) _rosterSlots[slot] = id;
        }
        else
        {
            var slot = Array.IndexOf(_slots, null);
            if (slot >= 0) _slots[slot] = id;
        }

        _changed();
        return member;
    }

    public (CompanionMember Member, ItemResult Result) Recruit(string itemInstanceId, Func<double>? random = null)
    {
        var roll = random ?? Random.Shared.NextDouble;
        if (_transaction is null) return RecruitMember(itemInstanceId, roll);

        (CompanionMember, ItemResult) outcome = default;
        _transaction(() => { outcome = RecruitMember(itemInstanceId, roll); });
        return outcome;
    }

    private (CompanionMember Member, ItemResult Result) RecruitMember(string itemInstanceId, Func<double> random)
    {
        var entry = Bag.Get(itemInstanceId);
        if (entry is null || !Bag.Entries.Any(value => value.Id == itemInstanceId))
            throw new InvalidOperationException("招募徽章已不在背包中");

        var use = _items.Items.GetValueOrDefault(entry.ItemId)?.Use;
        if (use?.Kind != ItemUseRules.Recruit) throw new InvalidOperationException("该物品不能招募伙伴");
        if (Array.IndexOf(_rosterSlots, null) < 0) throw new InvalidOperationException("佣兵名册已满");

        var rule = Catalog.Generation.Emblems.GetValueOrDefault(entry.ItemId)
                   ?? throw new InvalidOperationException("招募徽章规则缺失");

        var classId = rule.ClassId ?? CompanionProgression.MercenaryClassOrder[
            CompanionProgression.RandomInteger(random, 0, CompanionProgression.MercenaryClassOrder.Length - 1)];

        var weights = (int[])rule.QualityWeights.Clone();
        if (!rule.GuaranteedRare && GroupModifiers().GetValueOrDefault("rare-recruit-chance") != 0)
        {
            weights[0] -= 2;
            weights[2] += 2;
        }

        var quality = CompanionProgression.WeightedIndex(random, weights);
        var pool = Catalog.Definitions.Values
            .Where(definition => definition.SourceOrDefault == "mercenary" &&
                                 definition.Quality == quality &&
                                 definition.ClassId == classId &&
                                 definition.Recruitable != false)
            .ToList();
        if (pool.Count == 0) throw new InvalidOperationException("招募角色数据缺失");

        var definition = pool[CompanionProgression.RandomInteger(random, 0, pool.Count - 1)];
        var generated = CompanionProgression.Generate(Catalog.Generation, definition.ClassId,
            definition.Quality, definition.FixedSkills, Hero.Level, random);

        var member = Create(definition, "mercenary", $"companion-{_serial + 1}", generated);
        _serial++;
        Bag.Take(itemInstanceId, 1);
        _onRecruited?.Invoke(entry.ItemId, member.Id);

        var result = new ItemResult
        {
            Kind = "recruit",
            Title = "招募成功",
            Message = $"{member.Definition.Name}加入队伍",
            ClassId = definition.ClassId,
        };
        _changed();
        return (member, result);
    }

    public int RestAll()
    {
        var touched = 0;
        if (Hero.Player.Health < Hero.Player.MaxHealth || Hero.Player.Mana < Hero.Player.MaxMana) touched++;
        Hero.ClearCombatResourceLimits();
        Hero.SetHealth(Hero.Player.MaxHealth);
        Hero.SetMana(Hero.Player.MaxMana);

        foreach (var member in _owned.Values)
        {
            if (member.Player.Health < member.Player.MaxHealth || member.Player.Mana < member.Player.MaxMana) touched++;
            member.ClearCombatResourceLimits();
            member.SetHealth(member.Player.MaxHealth);
            member.SetMana(member.Player.MaxMana);
        }

        _changed();
        return touched;
    }

    public int ReviveFallen(int percent)
    {
        var ratio = Math.Clamp(percent, 1, 100) / 100.0;
        var revived = 0;
        foreach (var id in ActiveIds)
        {
            if (Alive(id)) continue;
            if (id == "player")
            {
                Hero.ClearCombatResourceLimits();
                Hero.SetHealth((int)Math.Floor(Hero.Player.MaxHealth * ratio));
                Hero.SetMana((int)Math.Floor(Hero.Player.MaxMana * ratio));
            }
            else
            {
                var member = _owned[id];
                member.ClearCombatResourceLimits();
                member.SetHealth((int)Math.Floor(member.Player.MaxHealth * ratio));
                member.SetMana((int)Math.Floor(member.Player.MaxMana * ratio));
            }

            revived++;
        }

        if (revived > 0) _changed();
        return revived;
    }

    public ItemResult Revive(string id, string itemInstanceId)
    {
        var entry = Bag.Get(itemInstanceId);
        var use = entry is null ? null : _items.Items.GetValueOrDefault(entry.ItemId)?.Use;
        if (entry is null || !Bag.Entries.Any(value => value.Id == itemInstanceId) || use?.Kind != ItemUseRules.Revive)
            throw new InvalidOperationException("复活道具不可用");

        if (entry.ItemId == "resurrection-prayer")
        {
            var fallen = ActiveIds.Where(member => !Alive(member)).ToList();
            if (LivingIds.Count > 0 || fallen.Count == 0)
                throw new InvalidOperationException("复活圣文只能在全队阵亡时使用");

            foreach (var memberId in fallen)
            {
                var member = memberId == "player" ? null : _owned[memberId];
                if (member is null)
                {
                    Hero.ClearCombatResourceLimits();
                    Hero.SetHealth(Hero.Player.MaxHealth);
                    Hero.SetMana(Hero.Player.MaxMana);
                }
                else
                {
                    member.ClearCombatResourceLimits();
                    member.SetHealth(member.Player.MaxHealth);
                    member.SetMana(member.Player.MaxMana);
                }
            }

            Bag.Take(itemInstanceId, 1);
            _changed();
            return new ItemResult { Kind = "revive", Title = "队伍复活", Message = "队伍成员已复活" };
        }

        if (LivingIds.Count == 0) throw new InvalidOperationException("全队阵亡时需要复活圣文");
        if (!ActiveIds.Contains(id) || Alive(id)) throw new InvalidOperationException("请选择已倒下的上阵队员");

        var full = entry.ItemId == "improved-resurrection-scroll";
        if (id == "player")
        {
            Hero.ClearCombatResourceLimits();
            Hero.SetHealth((int)Math.Floor(Hero.Player.MaxHealth * (full ? 1 : .1)));
            Hero.SetMana((int)Math.Floor(Hero.Player.MaxMana * (full ? 1 : .1)));
        }
        else
        {
            var member = _owned[id];
            member.ClearCombatResourceLimits();
            member.SetHealth((int)Math.Floor(member.Player.MaxHealth * (full ? 1 : .1)));
            member.SetMana((int)Math.Floor(member.Player.MaxMana * (full ? 1 : .1)));
        }

        Bag.Take(itemInstanceId, 1);
        _changed();
        return new ItemResult
        {
            Kind = "revive",
            Title = "复活成功",
            Message = $"{(id == "player" ? "主角" : _owned[id].Definition.Name)}已复活",
        };
    }

    /// <summary>
    /// 剧情伙伴入队。只要进名册就够：上阵位满了就留在候补，由玩家自己换人，
    /// 不因为队伍已满中断剧情。重复加入返回同一个人。
    /// </summary>
    public CompanionMember JoinStory(string definitionId)
    {
        var definition = Catalog.Definitions.GetValueOrDefault(definitionId);
        if (definition is null || definition.SourceOrDefault != "story")
            throw new InvalidOperationException($"剧情伙伴不存在：{definitionId}");

        var id = $"story-{definitionId}";
        var existing = _owned.GetValueOrDefault(id);
        if (existing is not null) return existing;

        var training = Catalog.Generation.Training;
        var caps = new Dictionary<string, int>();
        foreach (var (key, skill) in training)
            if (skill.ClassId == definition.ClassId) caps[key] = skill.MaxLevel;

        var baseAttributes = _stats.Classes[definition.ClassId];
        var generated = new GeneratedCompanion
        {
            Level = Hero.Level,
            BaseAttributes = new PrimaryAttributes
            {
                Strength = baseAttributes.Strength,
                Dexterity = baseAttributes.Dexterity,
                Vitality = baseAttributes.Vitality,
                Intelligence = baseAttributes.Intelligence,
                Mentality = baseAttributes.Mentality,
            },
            SkillCaps = caps,
            GroupSkills = [],
        };

        return Create(definition, "story", id, generated);
    }

    public bool LeaveStory(string definitionId)
    {
        var id = $"story-{definitionId}";
        var member = _owned.GetValueOrDefault(id);
        if (member is null || member.Source != "story") return false;
        Remove(id, returnEquipment: true);
        return true;
    }

    public void Dismiss(string id)
    {
        var member = _owned.GetValueOrDefault(id);
        if (member is null || member.Source != "mercenary") throw new InvalidOperationException("只能放逐佣兵");
        if (ActiveIds.Contains(id)) throw new InvalidOperationException("请先将佣兵移出队伍");
        Remove(id, returnEquipment: false);
    }

    private void Remove(string id, bool returnEquipment)
    {
        _owned.Remove(id);
        _onMemberRemoved?.Invoke(id);
        _rosterSlots = _rosterSlots.Select(value => value == id ? null : value).ToArray();
        _slots = _slots.Select(value => value == id ? null : value).ToArray();
        if (_selected == id) _selected = LivingIds.FirstOrDefault() ?? "player";
        Bag.RemoveOwner(id, returnEquipment);
        _changed();
    }

    public void Deploy(string id, int slot)
    {
        if (!_owned.ContainsKey(id)) throw new InvalidOperationException("伙伴不存在");

        var index = slot - 1;
        var previous = _slots[index];
        var current = Array.IndexOf(_slots, id);
        if (current == index) return;
        if (!Alive(id)) throw new InvalidOperationException("阵亡佣兵需要先复活");
        if (previous is not null && current < 0) throw new InvalidOperationException("上阵位置已占用，请先移出队员");

        if (current >= 0) _slots[current] = previous;
        _slots[index] = id;
        if (previous is not null && current < 0 && _selected == previous)
            _selected = LivingIds.FirstOrDefault() ?? "player";
        _changed();
    }

    public void DeployFirstAvailable(string id)
    {
        if (ActiveIds.Contains(id)) return;
        var slot = Array.IndexOf(_slots, null);
        if (slot < 0) throw new InvalidOperationException("上阵位置已满，请先将一名队员退至候补");
        Deploy(id, slot + 1);
    }

    /// <summary>退回候补。阵亡的队员不能退——先复活再换人。</summary>
    public void Reserve(string id)
    {
        if (!_owned.ContainsKey(id) || !_slots.Contains(id)) throw new InvalidOperationException("伙伴未上阵");
        if (!Alive(id)) throw new InvalidOperationException("阵亡队员无法移出队伍");
        _slots = _slots.Select(value => value == id ? null : value).ToArray();
        if (_selected == id) _selected = LivingIds.FirstOrDefault() ?? "player";
        _changed();
    }

    public bool SwitchControl(string id)
    {
        if (!ActiveIds.Contains(id) || !Alive(id)) return false;
        if (_selected == id) return true;
        _selected = id;
        _changed();
        return true;
    }

    public bool OnDefeat(string id)
    {
        if (_selected == id)
        {
            var order = new List<string> { "player" };
            order.AddRange(_slots.Where(value => value is not null)!);
            var start = order.IndexOf(id);
            for (var offset = 1; offset <= order.Count; offset++)
            {
                var next = order[(start + offset) % order.Count];
                if (next is not null && Alive(next))
                {
                    _selected = next;
                    break;
                }
            }

            _changed();
        }

        return LivingIds.Count == 0;
    }

    public void Recover()
    {
        Hero.Recover();
        foreach (var id in ActiveIds.Skip(1))
            _owned.GetValueOrDefault(id)?.Recover();
        _selected = "player";
        _changed();
    }

    public void AddExperience(int amount)
    {
        if (amount < 0) throw new ArgumentException("经验奖励无效");

        var heroLevel = Hero.Level;
        var reward = (int)Math.Floor(amount * (1000 + GroupModifiers().GetValueOrDefault("experience")) / 1000.0);
        if (Alive("player")) Hero.AddExperience(reward);

        foreach (var (id, member) in _owned)
            if (!ActiveIds.Contains(id) || (member.Alive && member.Level <= heroLevel))
                member.AddExperience(reward);
    }

    /// <summary>
    /// 佣兵团技能的合并结果：同一效果后写覆盖前写（直接赋值，不累加），
    /// 候补成员也计入——所以放逐最后一名带某项技能的佣兵后该项就消失。
    /// </summary>
    public Dictionary<string, int> GroupModifiers()
    {
        var result = new Dictionary<string, int>();
        foreach (var member in _owned.Values)
        foreach (var id in member.Generated.GroupSkills)
            if (Catalog.GroupSkills.TryGetValue(id, out var skill)) result[skill.Effect] = skill.Value;
        return result;
    }

    public PartyState CaptureState()
    {
        var state = new PartyState
        {
            RosterSlots = [.. _rosterSlots],
            Slots = [.. _slots],
            Selected = _selected,
            Serial = _serial,
            HeroAiFrequency = [.. HeroAiFrequency],
            HeroCounterattack = HeroCounterattack,
        };
        foreach (var member in _owned.Values) state.Members.Add(member.CaptureState());
        return state;
    }

    public void RestoreState(PartyState state)
    {
        var saved = state.Clone();
        _owned.Clear();
        foreach (var entry in saved.Members)
        {
            var definition = Catalog.Definitions.GetValueOrDefault(entry.DefinitionId)
                             ?? throw new InvalidOperationException("检查点中的伙伴不存在");
            var member = new CompanionMember(entry.Id, definition, entry.Source, entry.Level, _stats, _items, Bag,
                Catalog.Growth[definition.ClassId], _changed, entry.Generated, Catalog.Generation.Training);
            member.RestoreState(entry);
            _owned[entry.Id] = member;
        }

        _rosterSlots = [.. saved.RosterSlots];
        _slots = [.. saved.Slots];
        _selected = saved.Selected;
        _serial = saved.Serial;
        HeroAiFrequency.Clear();
        foreach (var (id, value) in saved.HeroAiFrequency) HeroAiFrequency[id] = value;
        HeroCounterattack = saved.HeroCounterattack;
    }
}

public sealed class PartySnapshot
{
    public required List<string> Active { get; init; }

    public required List<string> Living { get; init; }

    public required List<string?> RosterSlots { get; init; }

    public required List<string> Reserves { get; init; }

    public required string Controlled { get; init; }

    public required List<CompanionSnapshot> Members { get; init; }
}
