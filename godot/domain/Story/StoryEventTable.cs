using System.Collections.Generic;
using System.Text.Json;
using Inotia4.Domain.World;

namespace Inotia4.Domain.Story;

public sealed class StoryEventTable
{
    private readonly List<StoryEventTrigger> _triggers = [];

    public StoryEventTable(JsonElement[] events)
    {
        foreach (var element in events)
        {
            var trigger = Parse(element);
            if (trigger is not null) _triggers.Add(trigger);
        }
    }

    public IReadOnlyList<StoryEventTrigger> Triggers => _triggers;

    private static StoryEventTrigger? Parse(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        var trigger = new StoryEventTrigger
        {
            Id = StoryType.Text(element, "id"),
            Source = StoryType.Text(element, "source"),
            Key = StoryType.Text(element, "key"),
            Repeat = StoryType.Bool(element, "repeat"),
            Script = StoryType.Text(element, "script"),
            Map = StoryType.Text(element, "map"),
            Near = StoryType.Text(element, "near"),
            Entity = StoryType.Text(element, "entity"),
            NearCharacterId = element.TryGetProperty("nearCharacterId", out var nearCharacter) &&
                              nearCharacter.ValueKind == JsonValueKind.String
                ? nearCharacter.GetString()
                : null,
            Condition = element.TryGetProperty("condition", out var condition) ? condition : default,
        };

        if (element.TryGetProperty("region", out var region) && region.ValueKind == JsonValueKind.Object)
        {
            trigger.Region = new Rect(
                StoryType.Float(region, "x"),
                StoryType.Float(region, "y"),
                StoryType.Float(region, "width"),
                StoryType.Float(region, "height"));
        }

        trigger.Spots = ParseSpots(element);
        return trigger.Id.Length > 0 && trigger.Script.Length > 0 ? trigger : null;
    }

    private static List<EventSpot> ParseSpots(JsonElement element)
    {
        var spots = new List<EventSpot>();

        if (!element.TryGetProperty("place", out var place) || place.ValueKind != JsonValueKind.Object)
            return spots;

        if (StoryType.Text(place, "type") == "fixed")
        {
            spots.Add(new EventSpot(StoryType.Text(place, "map"),
                new Vec2(StoryType.Float(place, "x"), StoryType.Float(place, "y"))));
            return spots;
        }

        if (place.TryGetProperty("spots", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var spot in list.EnumerateArray())
                spots.Add(new EventSpot(StoryType.Text(spot, "map"),
                    new Vec2(StoryType.Float(spot, "x"), StoryType.Float(spot, "y"))));
        }

        return spots;
    }
}

public sealed class StoryEventTrigger
{
    public required string Id { get; init; }

    public required string Source { get; init; }

    public string Key { get; init; } = string.Empty;

    public bool Repeat { get; init; }

    public required string Script { get; init; }

    public string Map { get; init; } = string.Empty;

    public Rect? Region { get; set; }

    public string Near { get; init; } = string.Empty;

    public string? NearCharacterId { get; init; }

    /// <summary>
    /// 事件绑定的世界实体。内容里目前一条也没写，
    /// 但引用校验要对它做存在性检查，所以照样解析，不丢字段。
    /// </summary>
    public string Entity { get; init; } = string.Empty;

    public List<EventSpot> Spots { get; set; } = [];

    public JsonElement Condition { get; init; }
}

public sealed record EventSpot(string Map, Vec2 Point);

public sealed class ActorPlacement
{
    public required string Id { get; init; }

    public required string Actor { get; init; }

    public string? CharacterId { get; init; }

    public required string Map { get; init; }

    public float X { get; init; }

    public float Y { get; init; }

    public string? Facing { get; init; }

    public string? AfterInteractScript { get; init; }

    public JsonElement Condition { get; init; }
}
