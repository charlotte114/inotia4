using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inotia4.Domain.Content;

public sealed class ShopCatalog
{
    [JsonPropertyName("shops")] public Dictionary<string, ShopDefinition> Shops { get; init; } = new();

    [JsonPropertyName("lampShop")] public string LampShop { get; init; } = string.Empty;

    [JsonPropertyName("categories")] public Dictionary<string, ShopCategory> Categories { get; init; } = new();

    [JsonPropertyName("assets")] public Dictionary<string, string> Assets { get; init; } = new();

    [JsonPropertyName("mapShopLevels")] public Dictionary<string, int> MapShopLevels { get; init; } = new();

    [JsonPropertyName("profiles")] public Dictionary<string, ShopProfile> Profiles { get; init; } = new();

    [JsonPropertyName("equipment")] public Dictionary<string, ShopEquipmentTemplate> Equipment { get; init; } = new();

    [JsonPropertyName("grades")] public ShopGrade[] Grades { get; init; } = [];

    [JsonPropertyName("rarityWeights")] public ShopRarityWeights RarityWeights { get; init; } = new();

    public static ShopCatalog Parse(JsonElement value, CharacterStatsCatalog? stats = null)
    {
        var catalog = value.Deserialize<ShopCatalog>() ?? throw new InvalidOperationException("商店表无效");

        if (catalog.Shops.Count != 18) throw new InvalidOperationException("需要 18 个商店 NPC");
        if (!catalog.Shops.ContainsKey(catalog.LampShop)) throw new InvalidOperationException("缺少神灯商人");
        if (catalog.Grades.Length != 15 ||
            catalog.RarityWeights.Ordinary.Length != 5 || catalog.RarityWeights.Rare.Length != 5)
            throw new InvalidOperationException("品质规则不完整");

        foreach (var (id, shop) in catalog.Shops)
        {
            if (shop.PriceRatePercent is <= 0 or > 300) throw new InvalidOperationException($"{id}：商人价格倍率无效");

            foreach (var category in shop.Categories)
            {
                if (!catalog.Categories.ContainsKey(category) ||
                    !catalog.Assets.ContainsKey(category) ||
                    !catalog.Assets.ContainsKey($"{category}-selected"))
                    throw new InvalidOperationException($"{id}：分类或分类图片不存在");
            }

            foreach (var rule in shop.StockRules)
            {
                if (rule.Kind == ShopStockRule.KindLevelRange && rule.MinimumLevel > rule.MaximumLevel)
                    throw new InvalidOperationException($"{id}：等级范围无效");
                if (rule.Kind == ShopStockRule.KindRandomStock &&
                    (rule.Profile is null || !catalog.Profiles.ContainsKey(rule.Profile)))
                    throw new InvalidOperationException($"{id}：随机商品规则不存在");
            }
        }

        foreach (var (id, equipment) in catalog.Equipment)
        {
            equipment.Variants ??= DeriveVariants(id, stats);
            if (equipment.Variants.Length != 15 || equipment.DefaultGrade >= 15 ||
                equipment.ConcealedPrices.Length != 128)
                throw new InvalidOperationException($"{id}：装备品质或报价不完整");
        }

        foreach (var profile in catalog.Profiles.Values)
        {
            if (profile.UniqueChanceByLevel.Length != 128)
                throw new InvalidOperationException("珍稀物品概率不完整");

            foreach (var category in ShopProfile.RequiredPools)
                if (!profile.Pools.ContainsKey(category))
                    throw new InvalidOperationException("随机物品池缺失");

            foreach (var pool in profile.Pools.Values)
                if (pool.Sum(entry => entry.Weight) > 10000)
                    throw new InvalidOperationException("权重超过概率基数");
        }

        foreach (var key in new[] { "slot", "selection", "gold", "silver", "copper", "merchant-title" })
            if (!catalog.Assets.ContainsKey(key)) throw new InvalidOperationException($"缺少素材 {key}");

        return catalog;
    }

    private static ShopGradeVariant[] DeriveVariants(string id, CharacterStatsCatalog? stats)
    {
        var variants = stats?.Equipment.GetValueOrDefault(id)?.GradeVariants;
        if (variants is null || variants.Length != 15)
            throw new InvalidOperationException($"{id}：装备品级需要 character-stats 派生");

        return variants.Select(variant => new ShopGradeVariant
        {
            Attack = variant.Attack,
            Defense = variant.Defense,
            GradeName = variant.Name,
            OptionCount = variant.OptionCount,
            SocketLimit = variant.SocketLimit,
        }).ToArray();
    }
}

/// <summary>一家店。<see cref="Categories"/> 非空表示这是「分类货架」，否则按固定商品与随机规则开。 </summary>
public sealed class ShopDefinition
{
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;

    [JsonPropertyName("priceRatePercent")] public int PriceRatePercent { get; init; } = 100;

    [JsonPropertyName("fixedStock")] public string[] FixedStock { get; init; } = [];

    [JsonPropertyName("categories")] public string[] Categories { get; init; } = [];

    [JsonPropertyName("stockRules")] public ShopStockRule[] StockRules { get; init; } = [];
}

public sealed class ShopStockRule
{
    public const string KindLevelRange = "level-range";
    public const string KindRandomStock = "random-stock";
    public const string KindRareEquipment = "rare-equipment";

    [JsonPropertyName("kind")] public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("minimumLevel")] public int MinimumLevel { get; init; }

    [JsonPropertyName("maximumLevel")] public int MaximumLevel { get; init; }

    [JsonPropertyName("category")] public string? Category { get; init; }

    [JsonPropertyName("attempts")] public int Attempts { get; init; }

    [JsonPropertyName("levelRadius")] public int LevelRadius { get; init; }

    [JsonPropertyName("profile")] public string? Profile { get; init; }

    public bool IsLevelRange => Kind == KindLevelRange;
    public bool IsRandomStock => Kind == KindRandomStock;
    public bool IsRareEquipment => Kind == KindRareEquipment;
}

public sealed class ShopCategory
{
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;

    [JsonPropertyName("group")] public string Group { get; init; } = "weapons";
}

public sealed class WeightedItem
{
    [JsonPropertyName("weight")] public int Weight { get; init; }

    [JsonPropertyName("item")] public string? Item { get; init; }

    [JsonPropertyName("tiers")] public ShopTier[]? Tiers { get; init; }
}

public sealed class ShopTier
{
    [JsonPropertyName("item")] public string? Item { get; init; }

    [JsonPropertyName("level")] public int Level { get; init; }
}

public sealed class ShopProfile
{
    /// <summary>随机货架的固定尝试顺序，顺序会改变掷骰次数，所以顺序是规则的一部分。</summary>
    public static readonly string[] PoolCategories =
        ["generic", "equipment", "consumable", "upgrade", "crafting", "misc"];

    /// <summary>除 <c>equipment</c> 外的五个池都必须存在；装备走模板表，不从池里抽。</summary>
    public static readonly string[] RequiredPools = ["generic", "consumable", "upgrade", "crafting", "misc"];

    [JsonPropertyName("pools")] public Dictionary<string, WeightedItem[]> Pools { get; init; } = new();

    [JsonPropertyName("chances")] public Dictionary<string, int> Chances { get; init; } = new();

    [JsonPropertyName("uniqueChanceByLevel")] public int[] UniqueChanceByLevel { get; init; } = [];
}

public sealed class ShopEquipmentTemplate
{
    [JsonPropertyName("defaultGrade")] public int DefaultGrade { get; init; }

    [JsonPropertyName("randomGrade")] public bool RandomGrade { get; init; }

    [JsonPropertyName("dropLevel")] public int DropLevel { get; init; }

    [JsonPropertyName("randomGroup")] public string RandomGroup { get; init; } = "none";

    [JsonPropertyName("randomListed")] public bool RandomListed { get; init; }

    [JsonPropertyName("uniqueListed")] public bool UniqueListed { get; init; }

    [JsonPropertyName("variants")] public ShopGradeVariant[]? Variants { get; set; }

    [JsonPropertyName("concealedPrices")] public int[] ConcealedPrices { get; init; } = [];
}

public sealed class ShopGradeVariant
{
    [JsonPropertyName("attack")] public int Attack { get; init; }

    [JsonPropertyName("defense")] public int Defense { get; init; }

    [JsonPropertyName("gradeName")] public string GradeName { get; init; } = string.Empty;

    [JsonPropertyName("optionCount")] public int OptionCount { get; init; }

    [JsonPropertyName("socketLimit")] public int SocketLimit { get; init; }
}

public sealed class ShopGrade
{
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;

    [JsonPropertyName("armorName")] public string? ArmorName { get; init; }

    [JsonPropertyName("rarity")] public int Rarity { get; init; }

    [JsonPropertyName("weight")] public int Weight { get; init; }
}

public sealed class ShopRarityWeights
{
    [JsonPropertyName("ordinary")] public int[] Ordinary { get; init; } = [];

    [JsonPropertyName("rare")] public int[] Rare { get; init; } = [];
}
