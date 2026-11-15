using System.Collections.Generic;
using System.Text.Json;

namespace Inotia4.Domain.Story;

public interface IWorldCommandSink
{
    void Apply(StoryWorldCommand command);

    void DropItem(string map, float x, float y, string item, int quantity);
}

public readonly record struct WorldPlace(
    string EntityId,
    string Map,
    string Actor,
    string? CharacterId,
    float X,
    float Y,
    string? Facing,
    bool Visible,
    string? Scope);

public static class WorldCommands
{
    public static void Apply(
        JsonElement command, IWorldCommandSink? sink, ICollection<string> skipped)
    {
        var body = command.TryGetProperty("command", out var nested) ? nested : command;
        var kind = StoryType.Text(body, "kind");

        if (sink is null)
        {
            skipped.Add($"world:{kind}");
            return;
        }

        switch (kind)
        {
            case "place":
            {
                if (!body.TryGetProperty("entity", out var entity) || entity.ValueKind != JsonValueKind.Object)
                {
                    skipped.Add("world:place（缺 entity）");
                    return;
                }

                sink.Apply(new PlaceCommand(
                    new StoryEntityState
                    {
                        Id = StoryType.Text(entity, "id"),
                        Map = StoryType.Text(entity, "map"),
                        Actor = StoryType.Text(entity, "actor"),
                        CharacterId = OptionalText(entity, "characterId"),
                        Authority = OptionalText(entity, "authority"),
                        X = StoryType.Float(entity, "x"),
                        Y = StoryType.Float(entity, "y"),
                        Facing = OptionalText(entity, "facing"),
                        Visible = entity.TryGetProperty("visible", out _)
                            ? StoryType.Bool(entity, "visible")
                            : true,
                        Hint = OptionalText(entity, "hint"),
                        Objective = OptionalText(entity, "objective"),
                    },
                    VisitScope: StoryType.Text(body, "scope") == "visit",
                    MapFromStage: StoryType.Bool(body, "mapFromStage")));
                return;
            }

            case "remove-character":
                sink.Apply(new RemoveCharacterCommand(
                    StoryType.Text(body, "characterId"),
                    StoryType.Text(body, "map"),
                    StoryType.Bool(body, "all")));
                return;

            case "actor-visibility":
                sink.Apply(new ActorVisibilityCommand(
                    StoryType.Text(body, "map"),
                    StoryType.Text(body, "actor"),
                    OptionalText(body, "characterId"),
                    StoryType.Bool(body, "visible")));
                return;

            case "travel":
                var destination = body.GetProperty("destination");
                sink.Apply(new TravelCommand(new StoryLocation
                {
                    Map = StoryType.Text(destination, "map"),
                    X = StoryType.Float(destination, "x"),
                    Y = StoryType.Float(destination, "y"),
                    Facing = OptionalText(destination, "facing") ?? "south",
                }));
                return;

            case "remove-enemy":
                sink.Apply(new RemoveEnemyCommand(
                    StoryType.Text(body, "enemyId"),
                    OptionalText(body, "map"),
                    StoryType.Bool(body, "all")));
                return;

            case "enemy-location":
            {
                StoryLocation? location = null;
                if (body.TryGetProperty("location", out var point) && point.ValueKind == JsonValueKind.Object)
                    location = new StoryLocation
                    {
                        Map = StoryType.Text(point, "map"),
                        X = StoryType.Float(point, "x"),
                        Y = StoryType.Float(point, "y"),
                    };

                sink.Apply(new EnemyLocationCommand(
                    StoryType.Text(body, "enemyId"),
                    location,
                    VisitScope: StoryType.Text(body, "scope") == "visit",
                    NoRespawn: body.TryGetProperty("respawn", out var respawn) &&
                               respawn.ValueKind == JsonValueKind.False));
                return;
            }

            case "enemy-availability":
                sink.Apply(new EnemyAvailabilityCommand(
                    StoryType.Text(body, "map"),
                    StoryType.Text(body, "enemyId"),
                    StoryType.Bool(body, "available")));
                return;

            case "actor-appearance":
                sink.Apply(new ActorAppearanceCommand(
                    StoryType.Text(body, "actor"),
                    StoryType.Text(body, "appearance")));
                return;

            case "party-buff":
                sink.Apply(new PartyBuffCommand(
                    StoryType.Text(body, "buff"),
                    StoryType.Int(body, "strength")));
                return;

            case "exit":
                sink.Apply(new ExitCommand(StoryType.Text(body, "id"), StoryType.Bool(body, "enabled")));
                return;

            case "flag":
                sink.Apply(new FlagCommand(
                    StoryType.Text(body, "id"),
                    StoryType.Bool(body, "value")));
                return;

            case "choice":
                sink.Apply(new ChoiceCommand(
                    StoryType.Text(body, "id"),
                    ScalarText(body, "value")));
                return;

            case "show":
            case "hide":
            case "remove":
                sink.Apply(new ToggleEntityCommand(kind, StoryType.Text(body, "id")));
                return;

            case "encounter":
                sink.Apply(new EncounterCommand(body.GetProperty("encounter").Deserialize<StoryEncounter>(SaveGameStore.JsonOptions)
                    ?? throw new JsonException("剧情遭遇缺少内容")));
                return;

            case "encounter-result":
                sink.Apply(new EncounterResultCommand(StoryType.Text(body, "id"), StoryType.Text(body, "result")));
                return;

            default:
                skipped.Add($"world:{kind}");
                return;
        }
    }

    private static string? OptionalText(JsonElement body, string name) =>
        body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string ScalarText(JsonElement body, string name)
    {
        if (!body.TryGetProperty(name, out var value)) return string.Empty;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => string.Empty,
        };
    }
}
