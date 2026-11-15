using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Inotia4.Domain.Content;

public sealed class StoryClassCatalog
{
    [JsonPropertyName("defaultClass")] public string DefaultClass { get; init; } = string.Empty;

    [JsonPropertyName("background")] public string Background { get; init; } = string.Empty;

    [JsonPropertyName("assets")] public StoryClassAssets Assets { get; init; } = new();

    [JsonPropertyName("classes")] public StoryClassInfo[] Classes { get; init; } = [];

    public Dictionary<string, string> Names()
    {
        var names = new Dictionary<string, string>();
        foreach (var info in Classes) names[info.Id] = info.Name;
        return names;
    }

    public string NameOf(string classId) => Names().GetValueOrDefault(classId, classId);

    public StoryClassInfo? Class(string classId) =>
        Classes.FirstOrDefault(info => info.Id == classId);

    public StoryClassInfo? Default => Class(DefaultClass) ?? Classes.FirstOrDefault();
}

public sealed class StoryClassAssets
{
    [JsonPropertyName("selected")] public string Selected { get; init; } = string.Empty;

    [JsonPropertyName("title")] public string Title { get; init; } = string.Empty;

    [JsonPropertyName("back")] public string Back { get; init; } = string.Empty;

    [JsonPropertyName("start")] public string Start { get; init; } = string.Empty;

    [JsonPropertyName("startPressed")] public string StartPressed { get; init; } = string.Empty;

    [JsonPropertyName("descriptionTop")] public string DescriptionTop { get; init; } = string.Empty;

    [JsonPropertyName("attributesTop")] public string AttributesTop { get; init; } = string.Empty;

    [JsonPropertyName("panelLeft")] public string PanelLeft { get; init; } = string.Empty;

    [JsonPropertyName("panelRight")] public string PanelRight { get; init; } = string.Empty;
}

public sealed class StoryClassInfo
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;

    [JsonPropertyName("description")] public string Description { get; init; } = string.Empty;

    [JsonPropertyName("icon")] public string Icon { get; init; } = string.Empty;

    [JsonPropertyName("attributes")] public StoryClassAttribute[] Attributes { get; init; } = [];

    [JsonPropertyName("previewActor")] public ActorDefinition PreviewActor { get; init; } = new();

    [JsonPropertyName("playerActor")] public ActorDefinition PlayerActor { get; init; } = new();

    [JsonPropertyName("player")] public StoryClassPlayerState Player { get; init; } = new();
}

public sealed class StoryClassAttribute
{
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;

    [JsonPropertyName("value")] public int Value { get; init; }
}

public sealed class StoryClassPlayerState
{
    [JsonPropertyName("level")] public int Level { get; init; } = 1;

    [JsonPropertyName("health")] public int Health { get; init; }

    [JsonPropertyName("maxHealth")] public int MaxHealth { get; init; }

    [JsonPropertyName("mana")] public int Mana { get; init; }

    [JsonPropertyName("maxMana")] public int MaxMana { get; init; }

    [JsonPropertyName("experience")] public int Experience { get; init; }

    [JsonPropertyName("nextLevelExperience")] public int NextLevelExperience { get; init; }
}
