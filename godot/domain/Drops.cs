using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Inotia4.Domain.Character;
using Inotia4.Domain.Content;
using Inotia4.Domain.Story;

namespace Inotia4.Domain;

/// <summary>
/// 掉落目录与结算：槽位判定的公式、
/// 装备等级窗口、固定掉落权重表、容器（宝箱/景物）等级封顶与章节带稀有度。
/// 槽位的判定次序就是 <see cref="ChanceKinds"/> 的次序——原表随机抽取次序不能动。
/// </summary>
public static class Drops
{

    public static readonly string[] ChanceKinds =
    [
        "money", "equipment", "consumable", "upgrade", "crafting", "misc", "unique", "fixed",
    ];

    public static readonly string[] PoolKinds = ["consumable", "upgrade", "crafting", "misc"];

    public static readonly Dictionary<string, string> BreakableEnemyIds = new()
    {
        ["bush"] = "enemy-bush-1",
        ["jar"] = "enemy-jar-1",
        ["box"] = "enemy-box-1",
    };

    private static readonly Dictionary<string, string> BreakableKindByEnemy =
        BreakableEnemyIds.ToDictionary(pair => pair.Value, pair => pair.Key);

    public static string? BreakableKindOf(string enemyId) =>
        BreakableKindByEnemy.TryGetValue(enemyId, out var kind) ? kind : null;

    public const string ChestOrdinary = "ordinary";
    public const string ChestImproved = "improved";
    public const string ChestBoss = "boss";

    /// <summary>
    /// 池子里的一次抽选：要么有 <c>item</c>（可能是显式 <c>null</c>＝这一格什么都不掉），
    /// 要么有 <c>tiers</c> 分层表。「字段存在但为 null」与「字段缺省」必须区分开，
    /// 所以用手动转换器读。
    /// </summary>
    [JsonConverter(typeof(DropChoiceConverter))]
    public sealed class DropChoice
    {
        [JsonPropertyName("weight")] public int Weight { get; init; }

        [JsonPropertyName("item")] public JsonElement? ItemRaw { get; init; }

        [JsonPropertyName("tiers")] public DropTier[]? Tiers { get; init; }

        [JsonIgnore] public bool HasItemKey => ItemRaw is not null;

        [JsonIgnore] public string? Item =>
            ItemRaw is { } raw && raw.ValueKind == JsonValueKind.String ? raw.GetString() : null;
    }

    public sealed class DropChoiceConverter : JsonConverter<DropChoice>
    {
        public override DropChoice Read(ref Utf8JsonReader reader, Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("掉落池选项必须是对象");
            int weight = 0;
            JsonElement? item = null;
            DropTier[]? tiers = null;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                    return new DropChoice { Weight = weight, ItemRaw = item, Tiers = tiers };
                if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("掉落池选项结构无效");
                switch (reader.GetString())
                {
                    case "weight":
                        reader.Read();
                        weight = reader.GetInt32();
                        break;
                    case "item":
                        reader.Read();
                        item = JsonElement.ParseValue(ref reader);
                        break;
                    case "tiers":
                        reader.Read();
                        tiers = reader.TokenType == JsonTokenType.Null
                            ? null
                            : JsonSerializer.Deserialize<DropTier[]>(ref reader, options);
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }

            throw new JsonException("掉落池选项结构无效");
        }

        public override void Write(Utf8JsonWriter writer, DropChoice value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("weight", value.Weight);
            if (value.ItemRaw is { } item)
            {
                writer.WritePropertyName("item");
                item.WriteTo(writer);
            }

            if (value.Tiers is not null)
            {
                writer.WritePropertyName("tiers");
                JsonSerializer.Serialize(writer, value.Tiers, options);
            }

            writer.WriteEndObject();
        }
    }

    public sealed class DropTier
    {
        [JsonPropertyName("item")] public string? Item { get; init; }

        [JsonPropertyName("level")] public int Level { get; init; }
    }

    public sealed class EnemyDropRule
    {
        [JsonPropertyName("level")] public int Level { get; init; }

        [JsonPropertyName("pool")] public string Pool { get; init; } = string.Empty;

        [JsonPropertyName("rolls")] public int Rolls { get; init; }

        [JsonPropertyName("threat")] public string? Threat { get; init; }

        /// <summary>原版 MONDATABASE.u16@0x1b bit4：死亡时不走 CHARSYSTEM_DropItem，任务掉落仍保留。</summary>
        [JsonPropertyName("noNormalDrops")] public bool? NoNormalDrops { get; init; }

        [JsonPropertyName("chances")] public Dictionary<string, int> Chances { get; init; } = new();

        [JsonPropertyName("fixed")] public FixedDrop[] Fixed { get; init; } = [];
    }

    public sealed class FixedDrop
    {
        [JsonPropertyName("item")] public string? Item { get; init; }

        [JsonPropertyName("weight")] public int Weight { get; init; }
    }

    public sealed class ChapterBand
    {
        [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;

        [JsonPropertyName("minLevel")] public int MinLevel { get; init; }

        [JsonPropertyName("maxLevel")] public int MaxLevel { get; init; }

        [JsonPropertyName("maxItemLevel")] public int MaxItemLevel { get; init; }

        [JsonPropertyName("rarityScale")] public int[] RarityScale { get; init; } = [];

        [JsonPropertyName("luck")] public int Luck { get; init; }

        [JsonPropertyName("profiles")] public Dictionary<string, string> Profiles { get; init; } = new();
    }

    public sealed class PityConfig
    {
        [JsonPropertyName("equipment")] public RatePair Equipment { get; init; } = new();

        [JsonPropertyName("rare")] public RatePair Rare { get; init; } = new();

        [JsonPropertyName("levelWindow")] public int LevelWindow { get; init; }

        [JsonPropertyName("bossGuarantee")] public BossGuaranteeConfig? BossGuarantee { get; init; }
    }

    public sealed class RatePair
    {
        [JsonPropertyName("stepPermille")] public int StepPermille { get; init; }

        [JsonPropertyName("capPermille")] public int CapPermille { get; init; }
    }

    public sealed class BossGuaranteeConfig
    {
        [JsonPropertyName("equipment")] public bool Equipment { get; init; }

        [JsonPropertyName("minRarity")] public int MinRarity { get; init; }
    }

    public sealed class QuestItemDropRule
    {
        [JsonPropertyName("item")] public string Item { get; init; } = string.Empty;

        [JsonPropertyName("threshold")] public int Threshold { get; init; }
    }

    public sealed record QuestDropRequirement(string Item, int Quantity, int Owned);

    public sealed class ChestDropRule
    {
        [JsonPropertyName("pool")] public string Pool { get; init; } = string.Empty;

        [JsonPropertyName("rolls")] public int Rolls { get; init; }

        [JsonPropertyName("chances")] public Dictionary<string, int> Chances { get; init; } = new();

        [JsonPropertyName("fixed")] public FixedDrop[] Fixed { get; init; } = [];

        [JsonPropertyName("uniqueChance")] public UniqueChance? UniqueChance { get; init; }

        [JsonPropertyName("spawn")] public SpawnRule? Spawn { get; init; }

        [JsonPropertyName("rarityProfile")] public string? RarityProfile { get; init; }

        [JsonPropertyName("minRarity")] public int? MinRarity { get; init; }
    }

    public sealed class UniqueChance
    {
        [JsonPropertyName("numerator")] public int Numerator { get; init; }

        [JsonPropertyName("levelOffset")] public int LevelOffset { get; init; }
    }

    public sealed class SpawnRule
    {
        [JsonPropertyName("enemyId")] public string EnemyId { get; init; } = string.Empty;

        [JsonPropertyName("chance")] public int Chance { get; init; }
    }

    public sealed class DropCatalog
    {
        [JsonPropertyName("version")] public int Version { get; init; }

        [JsonPropertyName("questItems")] public Dictionary<string, QuestItemDropRule[]>? QuestItems { get; init; }

        [JsonPropertyName("chests")]
        public Dictionary<string, ChestDropRule>? Chests { get; init; }

        [JsonPropertyName("breakables")]
        public Dictionary<string, ChestDropRule>? Breakables { get; init; }

        [JsonPropertyName("enemies")] public Dictionary<string, EnemyDropRule>? Enemies { get; init; }

        [JsonPropertyName("pools")]
        public Dictionary<string, Dictionary<string, DropChoice[]>>? Pools { get; init; }

        [JsonPropertyName("equipment")]
        public Dictionary<string, EquipmentPoolEntry[]>? Equipment { get; init; }

        [JsonPropertyName("rarityProfiles")]
        public Dictionary<string, int[]>? RarityProfiles { get; init; }

        [JsonPropertyName("chapterBands")] public ChapterBand[]? ChapterBands { get; init; }

        [JsonPropertyName("pity")] public PityConfig? Pity { get; init; }
    }

    public sealed class EquipmentPoolEntry
    {
        [JsonPropertyName("item")] public string Item { get; init; } = string.Empty;

        [JsonPropertyName("dropLevel")] public int DropLevel { get; init; }
    }

    public sealed record ChestOpenResult(IReadOnlyList<DropReward> Rewards, IReadOnlyList<string> Enemies);

    public sealed record DropRoll(
        int Grade, string GradeName, int Attack, int Defense, int OptionCount, int SocketLimit, int Rarity,
        bool Sockets) : EquipmentGradeSeed(Grade, GradeName, Attack, Defense, OptionCount, SocketLimit);

    public abstract record DropReward
    {
        public sealed record ItemDrop(string Item, ItemInstance? Instance = null, DropRoll? Roll = null)
            : DropReward;

        public sealed record MoneyDrop(int Amount) : DropReward;
    }

    public sealed record DropModifiers(
        int? EquipmentDrop = null, int? ScrollDrop = null, int? MaterialDrop = null, int? ChestQuality = null)
    {
        public int? this[string key] => key switch
        {
            "equipment-drop" => EquipmentDrop,
            "scroll-drop" => ScrollDrop,
            "material-drop" => MaterialDrop,
            "chest-quality" => ChestQuality,
            _ => null,
        };
    }

    public sealed record EnemyLootOptions(
        int? EquipmentBonus = null, int? UniqueBonus = null, int? MinRarity = null, int? ItemChance = null);

    public static DropCatalog ParseDrops(JsonElement value, ItemCatalog items, IReadOnlyList<string> enemyIds)
    {
        var catalog = value.Deserialize<DropCatalog>(SaveGameStore.JsonOptions)
                      ?? throw new InvalidOperationException("掉落目录无效");
        var questItems = catalog.QuestItems ?? throw new InvalidOperationException("掉落目录版本或结构无效");
        var chests = catalog.Chests ?? throw new InvalidOperationException("掉落目录版本或结构无效");
        var enemies = catalog.Enemies ?? throw new InvalidOperationException("掉落目录版本或结构无效");
        var pools = catalog.Pools ?? throw new InvalidOperationException("掉落目录版本或结构无效");
        var equipment = catalog.Equipment ?? throw new InvalidOperationException("掉落目录版本或结构无效");
        if (catalog.Version != 2 && catalog.Version != 3)
            throw new InvalidOperationException("掉落目录版本或结构无效");

        var idSet = enemyIds.ToHashSet();
        bool ItemExists(string? item) => item is null || items.Items.ContainsKey(item);

        foreach (var (enemyId, rules) in questItems)
        {
            if (!idSet.Contains(enemyId) || rules.Any(rule => rule is null || !items.Items.ContainsKey(rule.Item) ||
                    rule.Threshold is < 1 or > 100) ||
                rules.Select(rule => rule.Item).Distinct().Count() != rules.Length)
                throw new InvalidOperationException($"任务掉落无效：{enemyId}");
        }

        foreach (var kind in new[] { "weapon", "armor", "special", "unique", "epic" })
        {
            if (kind == "epic" && catalog.Version == 2) continue;
            if (!equipment.TryGetValue(kind, out var list) || list.Any(choice =>
                    choice is null || !items.Items.ContainsKey(choice.Item) ||
                    (kind != "special" && items.Items[choice.Item].Category != "equipment") ||
                    choice.DropLevel < 1))
                throw new InvalidOperationException($"装备掉落池无效：{kind}");
        }

        foreach (var enemyId in enemyIds.Where(id => !BreakableEnemyIds.ContainsValue(id)))
        {
            if (!enemies.TryGetValue(enemyId, out var rule) || rule is null ||
                rule.Level < 1 ||
                !pools.ContainsKey(rule.Pool) || false ||
                rule.Rolls is < 1 or > 3)
                throw new InvalidOperationException($"敌人掉落规则缺失：{enemyId}");
            foreach (var kind in ChanceKinds)
                if (!rule.Chances.TryGetValue(kind, out var chance) || chance < 0)
                    throw new InvalidOperationException($"敌人掉率无效：{enemyId}.{kind}");
            if (rule.Fixed.Any(choice => !ItemExists(choice.Item) || false ||
                    choice.Weight < 1))
                throw new InvalidOperationException($"固定掉落无效：{enemyId}");
            if (rule.Fixed.Length > 0 && rule.Fixed.Sum(entry => entry.Weight) != 1000)
                throw new InvalidOperationException($"固定掉落权重无效：{enemyId}");
        }

        foreach (var kind in new[] { ChestOrdinary, ChestImproved, ChestBoss })
        {
            if (kind == ChestBoss && !chests.ContainsKey(kind)) continue;
            var rule = chests[kind];
            if (rule is null || !pools.ContainsKey(rule.Pool) ||
                rule.Rolls != (kind == ChestOrdinary ? 1 : 3) ||
                ChanceKinds.Any(slot => !rule.Chances.TryGetValue(slot, out var chance) ||
                    chance < 0) ||
                rule.Fixed.Length != 0 || rule.UniqueChance is null ||
                rule.UniqueChance.Numerator < 0 ||
                rule.UniqueChance.LevelOffset < 1 ||
                (rule.Spawn is not null && (!idSet.Contains(rule.Spawn.EnemyId) ||
                    rule.Spawn.Chance < 0)))
                throw new InvalidOperationException($"宝箱掉落规则无效：{kind}");
        }

        foreach (var (kind, rule) in catalog.Breakables ?? [])
        {
            if (!BreakableEnemyIds.ContainsKey(kind) && kind != "fortunate-box" || rule is null ||
                !pools.ContainsKey(rule.Pool) || rule.Rolls != 1 ||
                ChanceKinds.Any(slot => !rule.Chances.TryGetValue(slot, out var chance) ||
                    chance < 0) ||
                rule.UniqueChance is null || false ||
                rule.UniqueChance.Numerator < 0 || false ||
                rule.UniqueChance.LevelOffset < 1 ||
                (rule.Spawn is not null && (!idSet.Contains(rule.Spawn.EnemyId) ||
                    rule.Spawn.Chance < 0)))
                throw new InvalidOperationException($"景物掉落规则无效：{kind}");
        }

        foreach (var (name, pool) in pools)
        foreach (var kind in PoolKinds)
        foreach (var choice in pool.GetValueOrDefault(kind) ?? [])
        {
            var hasTiers = choice.Tiers is not null;
            if (choice.Weight is < 1 or > 10000 ||
                hasTiers == choice.HasItemKey ||
                (choice.Tiers is not null
                    ? choice.Tiers.Length == 0 || choice.Tiers.Any(tier =>
                        !ItemExists(tier.Item) || tier.Level < 0)
                    : !ItemExists(choice.Item)))
                throw new InvalidOperationException($"随机掉落池无效：{name}.{kind}");
        }

        foreach (var (name, pool) in pools)
        foreach (var kind in PoolKinds)
            if ((pool.GetValueOrDefault(kind) ?? []).Sum(choice => choice.Weight) > 10000)
                throw new InvalidOperationException($"随机掉落权重无效：{name}.{kind}");

        if (catalog.Version == 3)
        {
            if (catalog.RarityProfiles is null || catalog.ChapterBands is null or [] || catalog.Pity is null)
                throw new InvalidOperationException("掉落目录缺少 v3 配置");

            bool Rate(RatePair pair) => pair.StepPermille is >= 0 and <= 10000 && pair.CapPermille is >= 0 and <= 10000 && pair.StepPermille <= pair.CapPermille;

            var pity = catalog.Pity;
            if (!Rate(pity.Equipment) || !Rate(pity.Rare) || false ||
                pity.LevelWindow < 0 || pity.BossGuarantee is null ||
                pity.BossGuarantee.MinRarity is < 0 or > 4)
                throw new InvalidOperationException("保底配置无效");
            foreach (var (name, weights) in catalog.RarityProfiles)
                if (weights.Length != 5 || weights.Any(value => value < 0))
                    throw new InvalidOperationException($"稀有度权重无效：{name}");

            var nextLevel = 1;
            foreach (var band in catalog.ChapterBands ?? [])
            {
                if (band.MinLevel != nextLevel || band.MaxLevel < band.MinLevel || band.MaxItemLevel < 1 ||
                    band.RarityScale.Length != 5 ||
                    band.RarityScale.Any(value => value < 0) ||
                    band.Luck < 0)
                    throw new InvalidOperationException($"章节带无效：{band.Id}");
                foreach (var profile in band.Profiles.Values)
                    if (!catalog.RarityProfiles.ContainsKey(profile))
                        throw new InvalidOperationException($"章节带引用未知稀有度：{profile}");
                nextLevel = band.MaxLevel + 1;
            }

            foreach (var (owner, rule) in catalog.Chests)
                if ((rule.RarityProfile is not null && !catalog.RarityProfiles.ContainsKey(rule.RarityProfile)) ||
                    (rule.MinRarity is { } minRarity && minRarity is < 0 or > 4))
                    throw new InvalidOperationException($"宝箱稀有度无效：{owner}");
        }

        return catalog;
    }

    private static int Draw(Func<double> random, int max)
    {
        var value = random();
        if (!double.IsFinite(value) || value < 0 || value >= 1) throw new InvalidOperationException("掉落随机值无效");
        return (int)Math.Floor(value * max);
    }

    private static bool Success(int basis, int monsterLevel, int playerLevel, Func<double> random,
        int bonus = 0, int itemChance = 0)
    {
        // 普通加成不能开启基础为 0 的槽位；保底/首杀的 10000 加值是明确的必中。
        if (basis == 0 && bonus < 10000) return false;
        var rate = (int)Math.Floor(Math.Floor(2.0 * basis * monsterLevel / (monsterLevel + playerLevel)) *
                                   (1000 + itemChance) / 1000.0);
        if (rate + bonus >= 10000) return true;
        return rate > 0 && Draw(random, 9999) + 1 < rate + bonus;
    }

    private static string? PickPool(DropChoice[] choices, int level, Func<double> random)
    {
        var roll = Draw(random, 10000);
        var weight = 0;
        foreach (var choice in choices)
        {
            weight += choice.Weight;
            if (roll >= weight) continue;
            if (choice.Tiers is null) return choice.Item;
            return choice.Tiers.FirstOrDefault(tier => tier.Level <= level)?.Item ??
                   choice.Tiers[^1].Item;
        }

        return null;
    }

    private static string? PickEquipment(DropCatalog catalog, int level, Func<double> random, int? maxItemLevel)
    {
        var equipment = catalog.Equipment ?? throw new InvalidOperationException("掉落目录版本或结构无效");
        var lowest = level < 10 ? 1 : Math.Max(1, (int)Math.Floor(level * 3.0 / 10));
        var first = lowest + Draw(random, level - lowest + 1);
        var second = lowest + Draw(random, level - lowest + 1);
        var cap = maxItemLevel ?? level;
        var top = Math.Min((first + second) / 2 + 2, cap);
        var bottom = Math.Max(1, Math.Max(top, 6) - 5);
        var classRoll = Draw(random, 100);
        var kind = classRoll < 20 ? "weapon" : classRoll < 70 ? "armor" : "special";

        bool InWindow(EquipmentPoolEntry choice) => choice.DropLevel >= bottom && choice.DropLevel <= top;
        var options = equipment[kind].Where(InWindow).ToList();
        // 某个类别在这个等级窗口没有候选（武器池有断档）时，换到任意装备类别；
        // 仍然没有就取最接近窗口上限的一件，不能因为类别掷点让成功的槽位掉空。
        if (options.Count == 0)
            options = new[] { "weapon", "armor", "special" }
                .SelectMany(name => equipment[name].Where(InWindow)).ToList();
        if (options.Count == 0)
        {
            var nearest = new[] { "weapon", "armor", "special" }.SelectMany(name => equipment[name])
                .Where(choice => choice.DropLevel <= cap)
                .OrderBy(choice => Math.Abs(choice.DropLevel - top))
                .ToList();
            if (nearest.Count == 0) return null;
            return nearest[0].Item;
        }

        return options[Draw(random, options.Count)].Item;
    }

    private static string? PickUnique(DropCatalog catalog, int level, Func<double> random, int? maxItemLevel)
    {
        var equipment = catalog.Equipment ?? throw new InvalidOperationException("掉落目录版本或结构无效");
        var options = equipment["unique"]
            .Where(choice => choice.DropLevel <= Math.Min(level, maxItemLevel ?? level)).ToList();
        if (options.Count == 0) return null;
        return options[Draw(random, options.Count)].Item;
    }

    public static IReadOnlyList<DropReward> RollEnemyDrops(DropCatalog catalog, string enemyId, int playerLevel,
        Func<double>? random = null, DropModifiers? modifiers = null)
    {
        if (catalog.Enemies?.GetValueOrDefault(enemyId) is not { } rule || rule.NoNormalDrops == true)
            return [];
        return RollDropRule(catalog, rule, playerLevel, random, null, modifiers).Rewards;
    }

    public static IReadOnlyList<DropReward> RollQuestDrops(DropCatalog catalog, string enemyId,
        IReadOnlyList<IReadOnlyList<QuestDropRequirement>> quests, Func<double>? random = null)
    {
        random ??= System.Random.Shared.NextDouble;
        var rules = catalog.QuestItems?.GetValueOrDefault(enemyId) ?? [];
        foreach (var goals in quests)
        foreach (var goal in goals)
        {
            var rule = rules.FirstOrDefault(rule => rule.Item == goal.Item);
            if (rule is null || rule.Threshold < 1) continue;
            if (goal.Owned < goal.Quantity && Draw(random, 99) + 1 < rule.Threshold)
                return [new DropReward.ItemDrop(goal.Item)];
            break;
        }

        return [];
    }

    private static int ContainerScaleLevel(int level, int playerLevel) =>
        level > 0 ? Math.Min(playerLevel, level) : playerLevel;

    public static ChestOpenResult RollBreakableLoot(DropCatalog catalog, CharacterStatsCatalog stats, string kind,
        int level, int playerLevel, Func<double>? random = null, DropModifiers? modifiers = null,
        int? equipmentBonus = null)
    {
        if (level is < 0 or > 255) throw new InvalidOperationException("景物掉落等级无效");
        random ??= System.Random.Shared.NextDouble;
        var selected = kind == "box" && (modifiers?["chest-quality"] ?? 0) != 0 &&
                       Draw(random, 999) + 1 <= 49
            ? "fortunate-box"
            : kind;
        if (catalog.Breakables is null || !catalog.Breakables.TryGetValue(selected, out var baseRule) ||
            baseRule is null)
            throw new InvalidOperationException("景物掉落规则缺失");
        var band = ChapterBandOf(catalog, level);
        var rule = new EnemyDropRule
        {
            Level = level,
            Pool = baseRule.Pool,
            Rolls = baseRule.Rolls,
            Chances = new Dictionary<string, int>(baseRule.Chances)
            {
                ["unique"] = (int)Math.Floor((double)baseRule.UniqueChance!.Numerator /
                                             (level + baseRule.UniqueChance.LevelOffset)),
            },
            Fixed = baseRule.Fixed,
        };
        return RollDropRule(catalog, rule, ContainerScaleLevel(level, playerLevel), random, baseRule.Spawn,
            modifiers,
            new LootContext(stats, ProfileFor(catalog, band, "normal", baseRule.MinRarity, baseRule.RarityProfile),
                maxItemLevel: band?.MaxItemLevel, equipmentBonus: equipmentBonus));
    }

    public static (int EquipmentBonus, int? MinRarity) FirstBossGuarantee(PityConfig? pity, bool firstDefeat)
    {
        if (!firstDefeat || pity?.BossGuarantee is not { } guarantee || !guarantee.Equipment) return (0, null);
        return (10000, guarantee.MinRarity);
    }

    private sealed class LootContext(
        CharacterStatsCatalog stats, GradeProfile profile, int? maxItemLevel = null,
        int? equipmentBonus = null, int? uniqueBonus = null, int? itemChance = null)
    {
        public CharacterStatsCatalog Stats = stats;
        public GradeProfile Profile = profile;
        public int? MaxItemLevel = maxItemLevel;
        public int? EquipmentBonus = equipmentBonus;
        public int? UniqueBonus = uniqueBonus;
        public int? ItemChance = itemChance;
    }

    private static DropRoll? GradeFor(LootContext? context, string? item, Func<double> random,
        bool sockets, bool staticOnly = false)
    {
        if (item is null || context is not { } loot) return null;
        if (!loot.Stats.Equipment.TryGetValue(item, out var equipment)) return null;
        if (equipment.GradeVariants.Length == 0) return null;
        var rolled = staticOnly
            ? EquipmentGrade.StaticGrade(loot.Stats, equipment)
            : EquipmentGrade.RollGrade(loot.Stats, equipment, loot.Profile, random);
        return rolled is null ? null : new DropRoll(rolled.Grade, rolled.GradeName, rolled.Attack, rolled.Defense,
            rolled.OptionCount, rolled.SocketLimit, rolled.Rarity, sockets);
    }

    private static ChestOpenResult RollDropRule(DropCatalog catalog, EnemyDropRule rule, int playerLevel,
        Func<double>? random, SpawnRule? spawn = null, DropModifiers? modifiers = null,
        LootContext? loot = null)
    {
        random ??= System.Random.Shared.NextDouble;
        var result = new List<DropReward>();
        var enemies = new List<string>();

        void Add(string? item, bool sockets = true, bool staticOnly = false)
        {
            if (item is null) return;
            var roll = GradeFor(loot, item, random, sockets, staticOnly);
            result.Add(new DropReward.ItemDrop(item, Roll: roll));
        }

        var level = rule.Level;
        var pools = catalog.Pools ?? throw new InvalidOperationException("掉落目录版本或结构无效");
        var pool = pools[rule.Pool];
        for (var repeat = 0; repeat < rule.Rolls; repeat++)
        foreach (var kind in ChanceKinds)
        {
            if (kind == "unique" && spawn is not null &&
                Success(spawn.Chance, level, playerLevel, random))
                enemies.Add(spawn.EnemyId);
            var baseBonus = kind == "equipment" ? modifiers?["equipment-drop"]
                : kind == "upgrade" ? modifiers?["scroll-drop"]
                : kind == "crafting" ? modifiers?["material-drop"]
                : 0;
            var bonus = (baseBonus ?? 0) +
                        (kind == "equipment" ? loot?.EquipmentBonus ?? 0 : kind == "unique" ? loot?.UniqueBonus ?? 0 : 0);
            if (!Success(rule.Chances.GetValueOrDefault(kind), level, playerLevel, random, bonus,
                    loot?.ItemChance ?? 0))
                continue;
            if (kind == "money")
            {
                var percent = 50 + Draw(random, 51);
                result.Add(new DropReward.MoneyDrop(
                    Economy.MoneyGain(Math.Max(1, 3 + (int)Math.Floor(4.0 * level * percent / 500)))));
            }
            else if (kind == "equipment")
            {
                Add(PickEquipment(catalog, level, random, loot?.MaxItemLevel), true, false);
            }
            else if (kind == "unique")
            {
                Add(PickUnique(catalog, level, random, loot?.MaxItemLevel), false, true);
            }
            else if (kind == "fixed")
            {
                var roll = Draw(random, 1000);
                var sum = 0;
                foreach (var choice in rule.Fixed)
                {
                    sum += choice.Weight;
                    if (roll < sum)
                    {
                        Add(choice.Item, false, true);
                        break;
                    }
                }
            }
            else
            {
                Add(PickPool(pool.GetValueOrDefault(kind) ?? [], level, random));
            }
        }

        return new ChestOpenResult(result, enemies);
    }

    public static ChapterBand? ChapterBandOf(DropCatalog catalog, int level) =>
        catalog.ChapterBands?.FirstOrDefault(band => level >= band.MinLevel && level <= band.MaxLevel);

    private static GradeProfile ProfileFor(DropCatalog catalog, ChapterBand? band, string threat,
        int? minRarity = null, string? explicitName = null)
    {
        var name = explicitName ?? band?.Profiles.GetValueOrDefault(threat) ?? "original";
        var weights = catalog.RarityProfiles?.GetValueOrDefault(name)
                      ?? catalog.RarityProfiles?.GetValueOrDefault("original")
                      ?? [1000, 0, 0, 0, 0];
        return new GradeProfile
        {
            Weights = weights,
            RarityScale = band?.RarityScale,
            MinRarity = minRarity,
            MaxGrade = 9,
            Luck = band?.Luck,
        };
    }

    public static ChestOpenResult RollEnemyLoot(DropCatalog catalog, CharacterStatsCatalog stats, string enemyId,
        int playerLevel, int chapterLevel, Func<double>? random = null, DropModifiers? modifiers = null,
        EnemyLootOptions? options = null)
    {
        if (catalog.Enemies?.GetValueOrDefault(enemyId) is not { } rule || rule.NoNormalDrops == true)
            return new ChestOpenResult([], []);
        var band = ChapterBandOf(catalog, chapterLevel);
        return RollDropRule(catalog, rule, playerLevel, random, null, modifiers,
            new LootContext(stats, ProfileFor(catalog, band, rule.Threat ?? "normal", options?.MinRarity),
                maxItemLevel: band?.MaxItemLevel, equipmentBonus: options?.EquipmentBonus,
                uniqueBonus: options?.UniqueBonus, itemChance: options?.ItemChance));
    }

    public static ChestOpenResult RollChestLoot(DropCatalog catalog, CharacterStatsCatalog stats, string kind,
        int level, int playerLevel, Func<double>? random = null, DropModifiers? modifiers = null,
        int? equipmentBonus = null)
    {
        if (level is < 1 or > 255) throw new InvalidOperationException("宝箱掉落等级无效");
        if (catalog.Chests is not { } chests || !chests.TryGetValue(kind, out var baseRule) || baseRule is null)
            throw new InvalidOperationException($"宝箱掉落规则缺失：{kind}");
        random ??= System.Random.Shared.NextDouble;
        var band = ChapterBandOf(catalog, level);
        var rule = new EnemyDropRule
        {
            Level = level,
            Pool = baseRule.Pool,
            Rolls = baseRule.Rolls,
            Chances = new Dictionary<string, int>(baseRule.Chances)
            {
                ["unique"] = (int)Math.Floor((double)baseRule.UniqueChance!.Numerator /
                                             (level + baseRule.UniqueChance.LevelOffset)),
            },
            Fixed = baseRule.Fixed,
        };
        return RollDropRule(catalog, rule, ContainerScaleLevel(level, playerLevel), random, baseRule.Spawn,
            modifiers,
            new LootContext(stats, ProfileFor(catalog, band, "normal", baseRule.MinRarity, baseRule.RarityProfile),
                maxItemLevel: band?.MaxItemLevel, equipmentBonus: equipmentBonus));
    }
}
