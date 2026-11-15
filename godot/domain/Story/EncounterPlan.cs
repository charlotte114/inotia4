namespace Inotia4.Domain.Story;

public sealed class EncounterPlan
{
    public required string Id { get; init; }

    public string DefinitionId { get; init; } = string.Empty;

    public string Map { get; init; } = string.Empty;

    public IReadOnlyList<EncounterSpawn> Spawns { get; init; } = [];
}

public sealed record EncounterSpawn(string Id, string EnemyId, float X, float Y);

public sealed class EncounterOutcomes
{
    [System.Text.Json.Serialization.JsonPropertyName("won")]
    public StoryTarget? Won { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("lost")]
    public StoryTarget? Lost { get; init; }
}
