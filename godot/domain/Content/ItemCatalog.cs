using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inotia4.Domain.Content;

public sealed class ItemCatalog
{
    [JsonPropertyName("items")] public Dictionary<string, ItemDefinition> Items { get; init; } = new();

    [JsonPropertyName("startingEquipment")]
    public Dictionary<string, Dictionary<string, string>> StartingEquipment { get; init; } = new();

    [JsonPropertyName("startingInventory")] public List<ItemReward> StartingInventory { get; init; } = [];

    [JsonPropertyName("startingShortcut")] public string StartingShortcut { get; init; } = string.Empty;

    [JsonPropertyName("dualWieldClasses")] public string[] DualWieldClasses { get; init; } = [];

    [JsonPropertyName("merchantItems")] public string[] MerchantItems { get; init; } = [];

    [JsonPropertyName("classSkills")] public Dictionary<string, string[]> ClassSkills { get; init; } = new();

    [JsonPropertyName("recipes")] public Dictionary<string, RecipeDefinition> Recipes { get; init; } = new();

    [JsonPropertyName("skillDescriptions")]
    public Dictionary<string, SkillDescription> SkillDescriptions { get; init; } = new();

    public ItemDefinition Get(string itemId) =>
        Items.TryGetValue(itemId, out var definition)
            ? definition
            : throw new KeyNotFoundException($"物品不存在：{itemId}");

    public bool Has(string itemId) => Items.ContainsKey(itemId);

    public bool HasDefinitions => Items.Count > 0;

    public IReadOnlyDictionary<string, string> StartingEquipmentOf(string classId) =>
        StartingEquipment.TryGetValue(classId, out var slots)
            ? slots
            : (IReadOnlyDictionary<string, string>)new Dictionary<string, string>();

    public IReadOnlyList<string> ClassSkillsOf(string classId) =>
        ClassSkills.TryGetValue(classId, out var skills) ? skills : [];
}

public sealed class ItemDefinition
{
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;

    [JsonPropertyName("groundSprite")] public GroundItemSprite? GroundSprite { get; init; }

    /// <summary>物品种类，例如 <c>longsword</c> / <c>shield</c> / <c>consumable</c>。
    /// 武器类型直接决定五维换算系数，是数值口径的一部分，不能当纯展示字段看。</summary>
    [JsonPropertyName("kind")] public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("typeName")] public string TypeName { get; init; } = string.Empty;

    [JsonPropertyName("category")] public string Category { get; init; } = string.Empty;

    [JsonPropertyName("level")] public int Level { get; init; } = 1;

    [JsonPropertyName("stackable")] public bool Stackable { get; init; } = true;

    [JsonPropertyName("discardable")] public bool Discardable { get; init; }

    [JsonPropertyName("sellable")] public bool Sellable { get; init; }

    [JsonPropertyName("priceKind")] public string? PriceKind { get; init; }

    [JsonPropertyName("pricePremium")] public bool PricePremium { get; init; }

    [JsonPropertyName("shortcut")] public bool Shortcut { get; init; }

    [JsonPropertyName("basePrice")] public int BasePrice { get; init; }

    [JsonPropertyName("storeListed")] public bool StoreListed { get; init; }

    /// <summary>地面拾取即生效的物品（元气球之类），不能进背包。</summary>
    [JsonPropertyName("pickup")] public JsonElement? Pickup { get; init; }

    [JsonPropertyName("equipment")] public EquipmentSlotDefinition? Equipment { get; init; }

    [JsonPropertyName("use")] public ItemUse? Use { get; init; }

    [JsonPropertyName("description")] public string Description { get; init; } = string.Empty;

    [JsonPropertyName("descriptionParts")] public DescriptionTextPart[]? DescriptionParts { get; init; }

    [JsonPropertyName("icon")] public string Icon { get; init; } = string.Empty;

    public bool IsEquipment => Equipment is not null;

    public bool IsPickup => Pickup is not null;
}

public sealed class EquipmentSlotDefinition
{
    [JsonPropertyName("slot")] public string Slot { get; init; } = string.Empty;

    [JsonPropertyName("classes")] public string[] Classes { get; init; } = [];

    [JsonPropertyName("twoHanded")] public bool TwoHanded { get; init; }

    [JsonPropertyName("dualWield")] public bool DualWield { get; init; }

    /// <summary>不可穿脱（剧情固定装备），不进可装备槽位列表。</summary>
    [JsonPropertyName("locked")] public bool Locked { get; init; }

    [JsonPropertyName("socketCapacity")] public int SocketCapacity { get; init; }

    [JsonPropertyName("enchantable")] public bool Enchantable { get; init; }

    [JsonPropertyName("description")] public EquipmentDescription? Description { get; init; }

    /// <summary>
    /// 这件装备能否给该职业穿。<c>classes</c> 为空就是谁都不能穿，没有「空表全员」的
    /// 例外（正式内容里也不存在这种装备）。
    /// </summary>
    public bool Allows(string classId) => Classes.Length > 0 && System.Array.IndexOf(Classes, classId) >= 0;
}

public sealed class EquipmentDescription
{
    [JsonPropertyName("showEnhancementUses")] public bool? ShowEnhancementUses { get; init; }

    [JsonPropertyName("attackSpeed")] public string? AttackSpeed { get; init; }

    [JsonPropertyName("classAttributes")] public DescriptionTextPart[] ClassAttributes { get; init; } = [];
}

public sealed class ItemReward
{
    [JsonPropertyName("item")] public string Item { get; init; } = string.Empty;

    [JsonPropertyName("quantity")] public int Quantity { get; init; }
}

public sealed class ItemRecovery
{
    [JsonPropertyName("mode")] public string Mode { get; init; } = "amount";

    [JsonPropertyName("minHealth")] public int MinHealth { get; init; }

    [JsonPropertyName("maxHealth")] public int MaxHealth { get; init; }

    [JsonPropertyName("ratePercent")] public int RatePercent { get; init; }

    [JsonPropertyName("restoresMana")] public bool RestoresMana { get; init; }

    [JsonPropertyName("cooldownMs")] public int CooldownMs { get; init; }
}

public sealed class ItemRegeneration
{
    [JsonPropertyName("durationMs")] public int DurationMs { get; init; }

    [JsonPropertyName("healthRecoveryPerPulse")] public int HealthRecoveryPerPulse { get; init; }

    [JsonPropertyName("manaRecoveryPerPulse")] public int ManaRecoveryPerPulse { get; init; }
}

public sealed class ItemUse
{
    [JsonPropertyName("kind")] public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("recovery")] public ItemRecovery? Recovery { get; init; }

    [JsonPropertyName("regenerate")] public ItemRegeneration? Regenerate { get; init; }

    [JsonPropertyName("rewards")] public ItemReward[] Rewards { get; init; } = [];

    [JsonPropertyName("classId")] public string? ClassId { get; init; }

    [JsonPropertyName("elite")] public bool Elite { get; init; }

    [JsonPropertyName("recipes")] public string[] Recipes { get; init; } = [];

    [JsonPropertyName("target")] public string? Target { get; init; }

    [JsonPropertyName("maxLevel")] public int? MaxLevel { get; init; }

    [JsonPropertyName("requiresEnhanced")] public bool RequiresEnhanced { get; init; }
}

public sealed class SkillDescription
{
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;

    [JsonPropertyName("className")] public string ClassName { get; init; } = string.Empty;
}

public sealed class RecipeDefinition
{    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;

    [JsonPropertyName("result")] public string Result { get; init; } = string.Empty;

    [JsonPropertyName("materials")] public ItemReward[] Materials { get; init; } = [];
}

public sealed class GroundItemSprite
{
    [JsonPropertyName("file")] public string File { get; init; } = string.Empty;

    [JsonPropertyName("width")] public float Width { get; init; }

    [JsonPropertyName("height")] public float Height { get; init; }

    [JsonPropertyName("anchorX")] public float AnchorX { get; init; }

    [JsonPropertyName("anchorY")] public float AnchorY { get; init; }
}
