using System.Text.Json.Serialization;

namespace Inotia4.Domain.Content;

public sealed class WorldIndex
{
    [JsonPropertyName("version")] public int Version { get; set; }

    [JsonPropertyName("maps")] public MapEntry[] Maps { get; set; } = Array.Empty<MapEntry>();

    [JsonPropertyName("actors")] public Dictionary<string, ActorDefinition> Actors { get; set; } = new();

    [JsonPropertyName("props")] public PropEntry[] Props { get; set; } = Array.Empty<PropEntry>();

    [JsonPropertyName("portraits")] public Dictionary<string, WorldPortrait> Portraits { get; set; } = new();

    [JsonPropertyName("storyEffects")]
    public Dictionary<string, StoryEffectEntry> StoryEffects { get; set; } = new();

    public StoryEffectEntry? StoryEffect(string id) =>
        StoryEffects.TryGetValue(id, out var entry) ? entry : null;
}

public sealed class WorldPortrait
{
    [JsonPropertyName("file")] public string File { get; set; } = string.Empty;

    [JsonPropertyName("width")] public int Width { get; set; }

    [JsonPropertyName("height")] public int Height { get; set; }
}

public sealed class StoryEffectEntry
{
    [JsonPropertyName("clips")] public Dictionary<string, EffectClip> Clips { get; set; } = new();

    [JsonPropertyName("placement")] public string? Placement { get; set; }

    public bool IsGround => Placement == "ground";

    /// <summary>
    /// 取一个片段。<c>default</c> 是剧情 <c>location-effect</c> 用的那一个；
    /// 缺 <c>default</c> 时退回第一个片段，避免整条演出因为命名差异停掉。
    /// </summary>
    public EffectClip? Clip(string? name = "default")
    {
        if (name is not null && Clips.TryGetValue(name, out var exact)) return exact;
        return Clips.TryGetValue("default", out var fallback) ? fallback : Clips.Values.FirstOrDefault();
    }
}

public static class EffectBlends
{
    public const string Normal = "normal";
    public const string Multiply = "multiply";
    public const string Screen = "screen";
    public const string Add = "add";

    public const string Backdrop = "backdrop";
}

public sealed class EffectLayer
{
    [JsonPropertyName("key")] public string Key { get; set; } = string.Empty;

    [JsonPropertyName("file")] public string File { get; set; } = string.Empty;

    [JsonPropertyName("blend")] public string Blend { get; set; } = EffectBlends.Normal;

    [JsonPropertyName("operations")] public string[] Operations { get; set; } = [];
}

public sealed class EffectClip
{
    [JsonPropertyName("version")] public int Version { get; set; }

    [JsonPropertyName("frameWidth")] public int FrameWidth { get; set; }

    [JsonPropertyName("frameHeight")] public int FrameHeight { get; set; }

    [JsonPropertyName("frames")] public int Frames { get; set; }

    [JsonPropertyName("originX")] public float OriginX { get; set; }

    [JsonPropertyName("originY")] public float OriginY { get; set; }

    [JsonPropertyName("frameRate")] public float FrameRate { get; set; }

    [JsonPropertyName("mirrorX")] public bool MirrorX { get; set; }

    [JsonPropertyName("layers")] public EffectLayer[] Layers { get; set; } = [];
}

public sealed class MapEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;

    [JsonPropertyName("file")] public string File { get; set; } = string.Empty;
}

public sealed class PropEntry
{
    [JsonPropertyName("key")] public string Key { get; set; } = string.Empty;

    [JsonPropertyName("file")] public string File { get; set; } = string.Empty;
}
