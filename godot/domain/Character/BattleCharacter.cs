using System;
using System.Collections.Generic;
using Inotia4.Domain.Combat;
using Inotia4.Domain.Content;

namespace Inotia4.Domain.Character;

public sealed class BattleCharacterState
{
    public int Level { get; set; } = 1;

    public Dictionary<string, int> Allocation { get; set; } = [];

    public int Health { get; set; }

    public int Mana { get; set; }

    [System.Text.Json.Serialization.JsonConverter(typeof(ResourceLimitsConverter))]
    public (int MaxHealth, int MaxMana)? ResourceLimits { get; set; }

    public int Experience { get; set; }

    public int BonusSkillPoints { get; set; }

    public int SpentSkillPoints { get; set; }

    public BattleCharacterState Clone() => new()
    {
        Level = Level,
        Allocation = new Dictionary<string, int>(Allocation),
        Health = Health,
        Mana = Mana,
        ResourceLimits = ResourceLimits,
        Experience = Experience,
        BonusSkillPoints = BonusSkillPoints,
        SpentSkillPoints = SpentSkillPoints,
    };
}

public readonly record struct PlayerVitals(
    int Level, int Health, int MaxHealth, int Mana, int MaxMana, int Experience, int NextLevelExperience);

public sealed class BattleCharacter : IAllocationOwner
{
    /// <summary>强化判定掷 1..998（原版 MATH_GetRandom）：小于阈值即成功。</summary>
    private const int EnhancementRollMax = 998;

    private const int MaxEnhancementLevel = 30;

    private const int ChaosChanceNumerator = 50000;

    private const int ChaosChanceBase = 10;

    private const int SecondChanceNumerator = 1500;

    private const int SecondChanceOffset = 5;

    private readonly CharacterStatsCatalog _catalog;

    private readonly ItemCatalog _items;

    private int _currentLevel = 1;

    private Dictionary<string, int> _allocation = Zero();

    private int _health;

    private int _mana;

    private (int MaxHealth, int MaxMana)? _combatResourceLimits;

    private int _experience;

    private int _bonusSkillPoints;

    private int _spentSkillPoints;

    private readonly List<Action> _listeners = [];

    private PanelCache? _panelCache;

    private int _panelRevision;

    private sealed record PanelCache(int BagRevision, int Level, int PanelRevision, CharacterPanel Panel);

    public BattleCharacter(string classId, CharacterStatsCatalog catalog, ItemCatalog items)
    {
        if (!catalog.Classes.ContainsKey(classId)) throw new ArgumentException("职业数值不存在");

        ClassId = classId;
        _catalog = catalog;
        _items = items;
        Bag = new Bag(Notify, items, classId, seed: true, new CharacterBagRules(this));
        Refill();
    }

    public string ClassId { get; }

    public Bag Bag { get; }

    public CharacterStatsCatalog Catalog => _catalog;

    public int Level => _currentLevel;

    public PrimaryAttributes Allocated => Build(_allocation);

    public PrimaryAttributes Attributes
    {
        get
        {
            var baseAttributes = _catalog.Classes[ClassId].ToPrimary();
            return PrimaryAttributeMath.Add(baseAttributes, Allocated);
        }
    }

    PrimaryAttributes IAllocationOwner.Attributes => Panel.Attributes;

    public int StatPoints =>
        (_currentLevel - 1) * _catalog.StatPointsPerLevel - PrimaryAttributeMath.Total(Build(_allocation));

    public int SkillPoints =>
        (_currentLevel - 1) * _catalog.SkillPointsPerLevel + _bonusSkillPoints - _spentSkillPoints;

    public CharacterPanel Panel
    {
        get
        {
            var cached = _panelCache;
            if (cached is not null && cached.BagRevision == Bag.Revision && cached.Level == _currentLevel &&
                cached.PanelRevision == _panelRevision)
                return cached.Panel;

            var equipped = new Dictionary<string, ItemInstance>();
            foreach (var (slot, id) in Bag.WornIds)
            {
                var entry = Bag.Get(id);
                if (entry is not null) equipped[slot] = entry;
            }

            var panel = CharacterPanel.Calculate(_catalog, _items, ClassId, _currentLevel, Allocated, equipped);
            _panelCache = new PanelCache(Bag.Revision, _currentLevel, _panelRevision, panel);
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
            return new PlayerVitals(_currentLevel, _health, maxHealth, _mana, maxMana, _experience,
                _catalog.ExperienceToNext(_currentLevel) ?? 0);
        }
    }

    public bool Alive => _health > 0;

    public IDisposable Subscribe(Action listener)
    {
        _listeners.Add(listener);
        return new Subscription(() => _listeners.Remove(listener));
    }

    private sealed class Subscription(Action cancel) : IDisposable
    {
        public void Dispose() => cancel();
    }

    public BattleCharacterState CaptureState() => new()
    {
        Level = _currentLevel,
        Allocation = new Dictionary<string, int>(_allocation),
        Health = _health,
        Mana = _mana,
        ResourceLimits = _combatResourceLimits,
        Experience = _experience,
        BonusSkillPoints = _bonusSkillPoints,
        SpentSkillPoints = _spentSkillPoints,
    };

    public void RestoreState(BattleCharacterState state)
    {
        var restored = state.Clone();
        _currentLevel = restored.Level;
        _allocation = new Dictionary<string, int>(restored.Allocation);
        _health = restored.Health;
        _mana = restored.Mana;
        _combatResourceLimits = restored.ResourceLimits;
        _experience = restored.Experience;
        _bonusSkillPoints = restored.BonusSkillPoints;
        _spentSkillPoints = restored.SpentSkillPoints;
        _panelRevision++;
    }

    public void SetHealth(int value)
    {
        _health = BoundedHealth(value, Player.MaxHealth);
        NotifyListeners();
    }

    public void SetMana(int value)
    {
        _mana = BoundedHealth(value, Player.MaxMana);
        NotifyListeners();
    }

    public void SetCombatResourceLimits(int maxHealth, int maxMana)
    {
        var next = (Math.Max(0, maxHealth), Math.Max(0, maxMana));
        if (_combatResourceLimits is { } current && current.MaxHealth == next.Item1 && current.MaxMana == next.Item2)
            return;

        _combatResourceLimits = next;
        NotifyListeners();
    }

    public void SetCombatResourceCapacity(string key, int value)
    {
        var before = Player;
        var next = Math.Max(0, value);
        var current = key == "maxHealth" ? _health : _mana;
        var clamped = BoundedHealth(current, next);
        if ((key == "maxHealth" ? before.MaxHealth : before.MaxMana) == next && current == clamped) return;

        _combatResourceLimits = key == "maxHealth"
            ? (next, before.MaxMana)
            : (before.MaxHealth, next);
        if (key == "maxHealth") _health = clamped;
        else _mana = clamped;
        NotifyListeners();
    }

    public void ClearCombatResourceLimits()
    {
        if (_combatResourceLimits is null) return;
        _combatResourceLimits = null;
        Changed();
    }

    public bool SpendMana(int cost)
    {
        if (_mana < cost) return false;
        _mana -= cost;
        NotifyListeners();
        return true;
    }

    public void Recover()
    {
        Refill();
        Changed();
    }

    public int AddExperience(int amount)
    {
        if (amount < 0) throw new ArgumentException("经验奖励无效");
        if (_currentLevel >= _catalog.MaxLevel || amount == 0) return 0;

        // 经验值本身没有上限，加多少都不该溢出；这里用 long 累加再收回 int，
        // 否则一次给到 int.MaxValue 会绕成负数、停在中途而不是直接满级。
        var pending = _experience + (long)amount;
        var gained = 0;
        while (_currentLevel < _catalog.MaxLevel)
        {
            var needed = _catalog.ExperienceToNext(_currentLevel) ?? 0;
            if (needed <= 0 || pending < needed) break;
            pending -= needed;
            _currentLevel++;
            gained++;
        }

        _experience = _currentLevel == _catalog.MaxLevel ? 0 : (int)Math.Min(pending, int.MaxValue);
        if (gained > 0) Refill();
        Changed();
        return gained;
    }

    public void SpendSkillPoints(int amount)
    {
        if (amount <= 0 || amount > SkillPoints) throw new InvalidOperationException("技能点不足");
        _spentSkillPoints += amount;
        Changed();
    }

    public void GrantSkillPoint()
    {
        _bonusSkillPoints++;
        Changed();
    }

    public void ResetSkillPoints()
    {
        _bonusSkillPoints = 0;
        _spentSkillPoints = 0;
        Changed();
    }

    /// <summary>确认一次加点。增量必须五项齐全且非负，总数不能超过余额。</summary>
    public void CommitAllocation(PrimaryAttributes delta)
    {
        var values = new[]
        {
            delta.Strength, delta.Dexterity, delta.Vitality, delta.Intelligence, delta.Mentality,
        };
        foreach (var value in values)
            if (value < 0) throw new ArgumentException("属性操作无效");

        var total = 0;
        foreach (var value in values) total += value;
        if (total > StatPoints) throw new InvalidOperationException("没有足够的能力点");
        if (total == 0) return;

        for (var index = 0; index < PrimaryStats.All.Length; index += 1)
            _allocation[PrimaryStats.All[index]] += values[index];
        Changed();
    }

    public void Allocate(string stat, int delta)
    {
        if (!PrimaryStats.IsPrimary(stat) || delta is not (1 or -1)) throw new ArgumentException("属性操作无效");
        if (delta == 1 && StatPoints < 1) throw new InvalidOperationException("没有可分配的属性点");
        if (delta == -1 && _allocation.GetValueOrDefault(stat) < 1)
            throw new InvalidOperationException("该属性没有已分配的点数");

        _allocation[stat] += delta;
        Changed();
    }

    public void ResetAttributes()
    {
        _allocation = Zero();
        Changed();
    }

    private void Changed()
    {
        _panelRevision++;
        var player = Player;
        _health = Math.Min(_health, player.MaxHealth);
        _mana = Math.Min(_mana, player.MaxMana);
        NotifyListeners();
    }

    private void Refill()
    {
        var player = Player;
        _health = player.MaxHealth;
        _mana = player.MaxMana;
    }

    private void Notify() => Changed();

    private void NotifyListeners()
    {
        foreach (var listener in _listeners.ToArray()) listener();
    }

    internal static int BoundedHealth(int value, int maxHealth) => Math.Max(0, Math.Min(value, maxHealth));

    private static Dictionary<string, int> Zero() =>
        new()
        {
            [PrimaryStats.Strength] = 0,
            [PrimaryStats.Dexterity] = 0,
            [PrimaryStats.Vitality] = 0,
            [PrimaryStats.Intelligence] = 0,
            [PrimaryStats.Mentality] = 0,
        };

    private static PrimaryAttributes Build(IReadOnlyDictionary<string, int> values) => new()
    {
        Strength = values.GetValueOrDefault(PrimaryStats.Strength),
        Dexterity = values.GetValueOrDefault(PrimaryStats.Dexterity),
        Vitality = values.GetValueOrDefault(PrimaryStats.Vitality),
        Intelligence = values.GetValueOrDefault(PrimaryStats.Intelligence),
        Mentality = values.GetValueOrDefault(PrimaryStats.Mentality),
    };

    private sealed class CharacterBagRules(BattleCharacter hero) : BagRules
    {
        private CharacterStatsCatalog Stats => hero._catalog;

        private ItemCatalog Items => hero._items;

        /// <summary>
        /// 掷数值。身份串是 <c>battle:职业:物品:实例编号</c>，
        /// 所以同一件装备（同职业、同物品、同编号）永远同一组词条。
        /// </summary>
        public override void Initialize(ItemInstance entry, DropRoll? drop = null)
        {
            var random = new SeededRandom($"battle:{hero.ClassId}:{entry.ItemId}:{entry.Id}");
            Modifier Roll(ModifierRange range) => new() { Stat = range.Stat, Value = random.Next(range.Min, range.Max) };

            Stats.Equipment.TryGetValue(entry.ItemId, out var definition);
            Stats.Gems.TryGetValue(entry.ItemId, out var gem);
            if (definition is null && gem is null) return;

            var numbers = new ItemNumbers { MagicPercent = 0, Sockets = 0, EnhancementUses = 0 };
            entry.Numbers = numbers;

            if (gem is not null)
            {
                var choices = gem[random.Next(0, gem.Length - 1)];
                numbers.Modifiers.Add(Roll(choices[random.Next(0, choices.Length - 1)]));
                return;
            }

            entry.Numbers = EquipmentRoller.Roll(definition!, random, drop?.Sockets ?? true, drop?.Grade);
        }

        public override string? EquipProblem(ItemInstance entry)
        {
            if (!Items.Items.TryGetValue(entry.ItemId, out var definition)) return null;

            var itemLevel = ChaosEquipment.EquipmentLevel(definition.Level, entry);
            var level = hero.Level;
            return itemLevel > level ? $"需要等级 {itemLevel}，当前等级 {level}" : null;
        }

        public override ItemNotice? TargetProblem(ItemInstance source, ItemInstance target)
        {
            var use = Items.Get(source.ItemId).Use!;
            var numbers = target.Numbers;
            if (numbers is null) return ItemNotice.SocketIncompatible;

            if (use.Kind == ItemUseRules.Socket && target.Gems.Count >= numbers.Sockets)
                return ItemNotice.SocketFull;

            if (use.Kind != ItemUseRules.Enchant) return null;

            if (use.RequiresEnhanced && numbers.EnhancementUses != 0) return ItemNotice.EnchantChaosDurability;
            if (!use.RequiresEnhanced && numbers.EnhancementUses < 1) return ItemNotice.EnchantDurability;
            if (target.Enhancement >= MaxEnhancementLevel) return ItemNotice.EnchantMax;

            var targetKind = Items.Get(target.ItemId).Kind;
            var next = Stats.EnhancementRuleFor(source.ItemId, targetKind);
            if (next is null) return ItemNotice.EnchantRule;

            if (!use.RequiresEnhanced && numbers.EnhancementScroll is { } scroll && scroll != source.ItemId)
            {
                // 只有顶级与特级配成一对可互换的同类卷轴（19↔946 武器、24↔947 防具），
                // 其余必须沿用同一张。
                var original = Items.Items.TryGetValue(scroll, out var originalItem) ? originalItem.Use : null;
                var sameTierPair = original is not null && original.MaxLevel is null &&
                                   !original.RequiresEnhanced && original.Target == use.Target &&
                                   use.MaxLevel is null;
                if (!sameTierPair) return ItemNotice.EnchantRule;
            }

            if (!use.RequiresEnhanced && numbers.EnhancementRule is { } rule && !rule.SameNumbers(next))
                return ItemNotice.EnchantRule;

            return null;
        }

        public override string? ApplyUse(ItemInstance source, ItemInstance? target, string? skill)
        {
            var use = Items.Get(source.ItemId).Use!;
            if (use.Kind == ItemUseRules.Enchant && target?.Numbers is { } numbers)
            {
                var rule = Stats.EnhancementRuleFor(source.ItemId, Items.Get(target.ItemId).Kind);
                numbers.EnhancementRule ??= rule;
                if (!use.RequiresEnhanced)
                {
                    numbers.EnhancementScroll ??= source.ItemId;
                    numbers.EnhancementUses--;
                }
            }

            if (use.Kind == ItemUseRules.Socket && target?.Numbers is { } socketNumbers && source.Numbers is { } gemNumbers)
                foreach (var modifier in gemNumbers.Modifiers)
                    socketNumbers.GemModifiers.Add(new Modifier { Stat = modifier.Stat, Value = modifier.Value });

            return hero.SkillUseHandler?.Invoke(source.ItemId, skill);
        }

        /// <summary>技能书进背包就掷一条随机技能：全部职业的可训练技能一起等概率抽（造物品时走 ITEMSYSTEM_MakeSkillBook）。</summary>
        public override string? RollLearnSkill() => RollLearnSkill(Items, Random.Shared.NextDouble);

        public override EnchantOutcome? Enchant(ItemInstance source, ItemInstance target, Func<double> random)
        {
            var name = Items.Get(target.ItemId).Name;
            var level = target.Enhancement;
            var sourceUse = Items.Get(source.ItemId).Use!;

            if (sourceUse.RequiresEnhanced &&
                !EnhancementSucceeds(random, ChaosChanceNumerator / (ChaosChanceBase + (double)(level * level))))
            {
                var lowered = Math.Max(0, level - 1);
                return new EnchantOutcome(lowered, $"{name} 强化失败，降为 +{lowered}");
            }

            var raised = level + 1;
            var rule = Stats.EnhancementRuleFor(source.ItemId, Items.Get(target.ItemId).Kind);
            var threshold = SecondChanceNumerator / (raised + (double)SecondChanceOffset) + (rule?.BonusChance ?? 0);
            return EnhancementSucceeds(random, threshold)
                ? new EnchantOutcome(raised + 1, $"{name} 强化大成功 +{raised + 1}", ItemNotice.EnchantDouble)
                : new EnchantOutcome(raised, $"{name} 强化 +{raised}");
        }
    }

    /// <summary>
    /// 一次强化判定：掷 1..998（原版 MATH_GetRandom），小于阈值即成功。
    /// </summary>
    internal static bool EnhancementSucceeds(Func<double> random, double threshold)
    {
        var value = random();
        if (double.IsNaN(value) || value < 0 || value >= 1) throw new InvalidOperationException("随机结果无效");
        return 1 + Math.Floor(value * EnhancementRollMax) < Math.Truncate(threshold);
    }

    public Func<string, string?, string?>? SkillUseHandler { get; set; }
}
