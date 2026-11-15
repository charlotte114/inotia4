using System.Text.Json.Serialization;

namespace Inotia4.Domain.Content;

public sealed class ActorDefinition
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;

    [JsonPropertyName("portrait")] public string? Portrait { get; set; }

    /// <summary>没有任务可谈时的闲话。空串表示一路小心。</summary>
    [JsonPropertyName("description")] public string Description { get; set; } = string.Empty;

    [JsonPropertyName("service")] public string Service { get; set; } = string.Empty;

    [JsonPropertyName("chest")] public string? Chest { get; set; }

    [JsonPropertyName("clips")] public Dictionary<string, ActorClip> Clips { get; set; } = new();

    [JsonPropertyName("collision")] public CollisionBox Collision { get; set; } = new();

    [JsonPropertyName("effectAnchor")] public AnchorPoint? EffectAnchor { get; set; }

    [JsonPropertyName("headAnchor")] public AnchorPoint? HeadAnchor { get; set; }

    public bool HasClip(string name) => Clips.ContainsKey(name);

    /// <summary>
    /// 是不是「四向素材」：有东西向片段，朝向已经由片段本身表达。
    /// 这类角色不能再叠加地图摆放点的 flip，否则东西镜像正负抵消，人会背对行进方向走。
    /// </summary>
    public bool IsDirectional =>
        Clips.ContainsKey("idle-east") || Clips.ContainsKey("idle-west") ||
        Clips.ContainsKey("walk-east") || Clips.ContainsKey("walk-west");

    /// <summary>
    /// 当前片段要不要水平镜像。
    ///
    /// 朝东的素材通常是朝西图的原样副本再翻转（<see cref="ActorClip.MirrorX"/>），
    /// 所以四向角色直接听片段的；只有单向素材（灯、火把这类静态物件）才用摆放点的 flip 决定左右。
    /// </summary>
    public bool IsMirrored(ActorClip clip, bool placementFlip) =>
        clip.MirrorX != (IsDirectional ? false : placementFlip);
}

public sealed class ActorClip
{
    [JsonPropertyName("frameWidth")] public int FrameWidth { get; set; }

    [JsonPropertyName("frameHeight")] public int FrameHeight { get; set; }

    [JsonPropertyName("frames")] public int Frames { get; set; }

    [JsonPropertyName("originX")] public float OriginX { get; set; }

    [JsonPropertyName("originY")] public float OriginY { get; set; }

    [JsonPropertyName("frameRate")] public float FrameRate { get; set; } = 16.67f;

    [JsonPropertyName("mirrorX")] public bool MirrorX { get; set; }

    [JsonPropertyName("layers")] public ActorClipLayer[] Layers { get; set; } = Array.Empty<ActorClipLayer>();
}

public sealed class ActorClipLayer
{
    [JsonPropertyName("key")] public string Key { get; set; } = string.Empty;

    [JsonPropertyName("file")] public string File { get; set; } = string.Empty;

    [JsonPropertyName("blend")] public string Blend { get; set; } = "normal";
}

public sealed class CollisionBox
{
    [JsonPropertyName("x")] public float X { get; set; } = -8f;

    [JsonPropertyName("y")] public float Y { get; set; } = -8f;

    [JsonPropertyName("width")] public float Width { get; set; } = 16f;

    [JsonPropertyName("height")] public float Height { get; set; } = 16f;
}

public sealed class AnchorPoint
{
    [JsonPropertyName("x")] public float X { get; set; }

    [JsonPropertyName("y")] public float Y { get; set; }
}
