using System.Text.Json.Serialization;

namespace Inotia4.Domain.Audio;

public sealed class AudioCatalog
{
    [JsonPropertyName("version")] public int Version { get; init; } = 3;

    [JsonPropertyName("music")] public Dictionary<string, string> Music { get; init; } = new();

    [JsonPropertyName("effects")] public Dictionary<string, string> Effects { get; init; } = new();

    [JsonPropertyName("menuMusic")] public string MenuMusic { get; init; } = string.Empty;

    [JsonPropertyName("maps")] public Dictionary<string, AudioMapEntry> Maps { get; init; } = new();

    [JsonPropertyName("enemies")] public Dictionary<string, AudioEnemyEntry> Enemies { get; init; } = new();

    [JsonPropertyName("combat")] public AudioCombatEntry Combat { get; init; } = new();

    [JsonPropertyName("skills")] public Dictionary<string, string> Skills { get; init; } = new();

    [JsonPropertyName("ui")] public AudioUiEntry Ui { get; init; } = new();

    [JsonPropertyName("unbound")] public string[] Unbound { get; init; } = [];
}

public sealed class AudioMapEntry
{
    [JsonPropertyName("music")] public string? Music { get; init; }

    [JsonPropertyName("ambience")] public string? Ambience { get; init; }
}

public sealed class AudioEnemyEntry
{
    [JsonPropertyName("attack")] public string? Attack { get; init; }

    [JsonPropertyName("death")] public string? Death { get; init; }

    [JsonPropertyName("struck")] public string? Struck { get; init; }
}

public sealed class AudioCombatEntry
{
    [JsonPropertyName("weapons")] public Dictionary<string, AudioWeaponEntry> Weapons { get; init; } = new();

    [JsonPropertyName("shieldBlock")] public string ShieldBlock { get; init; } = string.Empty;

    [JsonPropertyName("periodicDamage")] public string PeriodicDamage { get; init; } = string.Empty;
}

public sealed class AudioWeaponEntry
{
    [JsonPropertyName("normal")] public AudioWeaponCue[] Normal { get; init; } = [];

    [JsonPropertyName("critical")] public string Critical { get; init; } = string.Empty;

    [JsonPropertyName("miss")] public string? Miss { get; init; }

    [JsonPropertyName("blocked")] public string Blocked { get; init; } = string.Empty;
}

public sealed class AudioWeaponCue
{
    [JsonPropertyName("sound")] public string Sound { get; init; } = string.Empty;

    [JsonPropertyName("weight")] public int Weight { get; init; }
}

public sealed class AudioUiEntry
{
    [JsonPropertyName("open")] public string Open { get; init; } = string.Empty;

    [JsonPropertyName("move")] public string Move { get; init; } = string.Empty;

    [JsonPropertyName("select")] public string Select { get; init; } = string.Empty;

    [JsonPropertyName("questStart")] public string QuestStart { get; init; } = string.Empty;

    [JsonPropertyName("questComplete")] public string QuestComplete { get; init; } = string.Empty;

    [JsonPropertyName("heal")] public string Heal { get; init; } = string.Empty;

    [JsonPropertyName("loot")] public string Loot { get; init; } = string.Empty;

    [JsonPropertyName("itemMove")] public string ItemMove { get; init; } = string.Empty;

    [JsonPropertyName("chestOpen")] public string ChestOpen { get; init; } = string.Empty;

    [JsonPropertyName("enchant")] public string Enchant { get; init; } = string.Empty;

    [JsonPropertyName("craft")] public string Craft { get; init; } = string.Empty;

    public string Of(string name) => name switch
    {
        "open" => Open,
        "move" => Move,
        "select" => Select,
        "questStart" => QuestStart,
        "questComplete" => QuestComplete,
        "heal" => Heal,
        "loot" => Loot,
        "itemMove" => ItemMove,
        "chestOpen" => ChestOpen,
        "enchant" => Enchant,
        "craft" => Craft,
        _ => string.Empty,
    };
}
