using System.Text.Json.Serialization;
using Inotia4.Domain.Character;
using Inotia4.Domain.Combat;

namespace Inotia4.Domain.Content;

public sealed class CharacterStatsCatalog
{
    [JsonPropertyName("maxLevel")] public int MaxLevel { get; init; } = 105;

    [JsonPropertyName("statPointsPerLevel")] public int StatPointsPerLevel { get; init; } = 3;

    [JsonPropertyName("skillPointsPerLevel")] public int SkillPointsPerLevel { get; init; } = 1;

    [JsonPropertyName("experience")] public int?[] Experience { get; init; } = [];

    [JsonPropertyName("classes")] public Dictionary<string, ClassAttributes> Classes { get; init; } = new();

    [JsonPropertyName("equipment")] public Dictionary<string, EquipmentNumbers> Equipment { get; init; } = new();

    [JsonPropertyName("grades")] public GradeDefinition[] Grades { get; init; } = [];

    [JsonPropertyName("modifiers")] public Dictionary<string, ModifierDescriptor> Modifiers { get; init; } = new();

    [JsonPropertyName("gems")]
    public Dictionary<string, ModifierRange[][]> Gems { get; init; } = new();

    [JsonPropertyName("enchantments")]
    public Dictionary<string, Dictionary<string, EnhancementRule>> Enchantments { get; init; } = new();

    public EquipmentNumbers GetEquipment(string itemId) =>
        Equipment.TryGetValue(itemId, out var numbers)
            ? numbers
            : throw new KeyNotFoundException($"装备数值不存在：{itemId}");

    public EnhancementRule? EnhancementRuleFor(string scrollId, string itemKind) =>
        Enchantments.TryGetValue(scrollId, out var kinds) && kinds.TryGetValue(itemKind, out var rule)
            ? rule
            : null;

    public int? ExperienceToNext(int level) =>
        level >= 1 && level <= Experience.Length ? Experience[level - 1] : 0;
}

public sealed class ClassAttributes
{
    [JsonPropertyName("strength")] public int Strength { get; init; }

    [JsonPropertyName("dexterity")] public int Dexterity { get; init; }

    [JsonPropertyName("vitality")] public int Vitality { get; init; }

    [JsonPropertyName("intelligence")] public int Intelligence { get; init; }

    [JsonPropertyName("mentality")] public int Mentality { get; init; }

    public PrimaryAttributes ToPrimary() => new()
    {
        Strength = Strength,
        Dexterity = Dexterity,
        Vitality = Vitality,
        Intelligence = Intelligence,
        Mentality = Mentality,
    };
}

public sealed class GradeDefinition
{
    [JsonPropertyName("index")] public int Index { get; init; }

    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;

    [JsonPropertyName("rarity")] public int Rarity { get; init; }

    [JsonPropertyName("weight")] public int Weight { get; init; }
}

public sealed class ModifierDescriptor
{
    [JsonPropertyName("label")] public string Label { get; init; } = string.Empty;

    [JsonPropertyName("percent")] public bool Percent { get; init; }
}
