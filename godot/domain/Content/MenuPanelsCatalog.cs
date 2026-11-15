using System.Text.Json.Serialization;

namespace Inotia4.Domain.Content;

public sealed class MenuPanelsCatalog
{
    [JsonPropertyName("assets")] public Dictionary<string, string> Assets { get; init; } = new();

    [JsonPropertyName("equipmentSlots")] public EquipmentSlotPlacement[] EquipmentSlots { get; init; } = [];

    [JsonPropertyName("classes")] public Dictionary<string, ClassPanelInfo> Classes { get; init; } = new();

    [JsonPropertyName("itemMessages")] public Dictionary<string, string> ItemMessages { get; init; } = new();

    [JsonPropertyName("skillPositions")] public SkillPositionInfo[] SkillPositions { get; init; } = [];

    [JsonPropertyName("skillColumns")] public SkillColumnInfo[] SkillColumns { get; init; } = [];

    [JsonPropertyName("skillPanel")] public SkillPanelInfo SkillPanel { get; init; } = new();

    [JsonPropertyName("character")] public CharacterPanelInfo Character { get; init; } = new();

    public string? Asset(string key) => Assets.GetValueOrDefault(key);

    public string Message(string key) => ItemMessages.GetValueOrDefault(key) ?? key;

    public string SlotName(string slotId)
    {
        foreach (var slot in EquipmentSlots)
            if (slot.Id == slotId) return slot.Name;
        return slotId;
    }
}

public sealed class EquipmentSlotPlacement
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;

    [JsonPropertyName("x")] public float X { get; init; }

    [JsonPropertyName("y")] public float Y { get; init; }

    [JsonPropertyName("emptyIcon")] public string? EmptyIcon { get; init; }
}

public sealed class ClassPanelInfo
{
    [JsonPropertyName("preview")] public string Preview { get; init; } = string.Empty;
}

public sealed class SkillPositionInfo
{
    [JsonPropertyName("column")] public int Column { get; init; }

    [JsonPropertyName("row")] public int Row { get; init; }
}

public sealed class SkillColumnInfo
{
    [JsonPropertyName("label")] public string Label { get; init; } = string.Empty;

    [JsonPropertyName("image")] public string Image { get; init; } = string.Empty;
}

public sealed class SkillPanelInfo
{
    [JsonPropertyName("labels")] public SkillPanelLabels Labels { get; init; } = new();
}

public sealed class SkillPanelLabels
{
    [JsonPropertyName("counterattackTitle")] public string CounterattackTitle { get; init; } = string.Empty;

    [JsonPropertyName("initialSkillPoints")] public string InitialSkillPoints { get; init; } = string.Empty;

    [JsonPropertyName("resetButton")] public string ResetButton { get; init; } = string.Empty;
}

public sealed class CharacterPanelInfo
{
    [JsonPropertyName("labels")] public CharacterPanelLabels Labels { get; init; } = new();

    [JsonPropertyName("portraits")] public Dictionary<string, CharacterPortraitInfo> Portraits { get; init; } = new();

    [JsonPropertyName("combatLabels")] public string[] CombatLabels { get; init; } = [];
}

public sealed class CharacterPanelLabels
{
    [JsonPropertyName("statPoints")] public string StatPoints { get; init; } = string.Empty;

    [JsonPropertyName("skillPoints")] public string SkillPoints { get; init; } = string.Empty;

    [JsonPropertyName("health")] public string Health { get; init; } = string.Empty;

    [JsonPropertyName("mana")] public string Mana { get; init; } = string.Empty;

    [JsonPropertyName("experience")] public string Experience { get; init; } = string.Empty;
}

public sealed class CharacterPortraitInfo
{
    [JsonPropertyName("image")] public string Image { get; init; } = string.Empty;

    [JsonPropertyName("partyIcon")] public string PartyIcon { get; init; } = string.Empty;
}
