using System.Text.Json.Serialization;

namespace Inotia4.Domain.Content;

public sealed record EquipmentNumbers
{
    [JsonPropertyName("attack")] public int Attack { get; init; }

    [JsonPropertyName("defense")] public int Defense { get; init; }

    [JsonPropertyName("gradeName")] public string GradeName { get; init; } = string.Empty;

    [JsonPropertyName("defaultGrade")] public int? DefaultGrade { get; init; }

    [JsonPropertyName("defaultRarity")] public int? DefaultRarity { get; init; }

    [JsonPropertyName("gradeVariants")] public EquipmentGradeVariant[] GradeVariants { get; init; } = [];

    [JsonPropertyName("magicPercent")] public int[] MagicPercent { get; init; } = [];

    [JsonPropertyName("criticalRate")] public int CriticalRate { get; init; }

    [JsonPropertyName("hitBonus")] public int HitBonus { get; init; }

    [JsonPropertyName("magicAttackRate")] public int MagicAttackRate { get; init; }

    [JsonPropertyName("shieldBlock")] public int ShieldBlock { get; init; }

    [JsonPropertyName("fixedOptions")] public ModifierRange[] FixedOptions { get; init; } = [];

    [JsonPropertyName("randomOptions")] public ModifierRange[] RandomOptions { get; init; } = [];

    [JsonPropertyName("optionCount")] public int OptionCount { get; init; }

    [JsonPropertyName("socketLimit")] public int SocketLimit { get; init; }

    [JsonPropertyName("fixedEnhancementUses")] public int? FixedEnhancementUses { get; init; }

    [JsonPropertyName("enhancementUses")] public int[] EnhancementUses { get; init; } = [];
}

public sealed class ModifierRange
{
    [JsonPropertyName("stat")] public string Stat { get; init; } = string.Empty;

    [JsonPropertyName("min")] public int Min { get; init; }

    [JsonPropertyName("max")] public int Max { get; init; }
}

public sealed class EquipmentGradeVariant
{
    [JsonPropertyName("attack")] public int Attack { get; init; }

    [JsonPropertyName("defense")] public int Defense { get; init; }

    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;

    [JsonPropertyName("rarity")] public int Rarity { get; init; }

    [JsonPropertyName("optionCount")] public int OptionCount { get; init; }

    [JsonPropertyName("socketLimit")] public int SocketLimit { get; init; }
}
