using System.Text.Json.Serialization;

namespace Inotia4.Domain.Content;

public sealed class StatusHudSkin
{
    [JsonPropertyName("assets")] public Dictionary<string, string> Assets { get; init; } = new();

    [JsonPropertyName("player")] public StatusPanelSkin Player { get; init; } = new();

    [JsonPropertyName("member")] public StatusMemberSkin Member { get; init; } = new();

    [JsonPropertyName("target")] public StatusTargetSkin Target { get; init; } = new();

    [JsonPropertyName("overhead")] public StatusOverheadSkin Overhead { get; init; } = new();

    [JsonPropertyName("buffIcons")] public StatusBuffIconSkin BuffIcons { get; init; } = new();

    [JsonPropertyName("levelColors")] public StatusLevelColorSkin[] LevelColors { get; init; } = [];

    [JsonPropertyName("lowestLevelColor")] public string LowestLevelColor { get; init; } = "#ffffff";
}

public sealed class StatusBuffIconSkin
{
    [JsonPropertyName("size")] public float Size { get; init; }

    [JsonPropertyName("step")] public float Step { get; init; }

    [JsonPropertyName("catalog")] public Dictionary<string, string> Catalog { get; init; } = new();
}

public sealed class StatusTargetSkin
{
    [JsonPropertyName("width")] public float Width { get; init; }

    [JsonPropertyName("height")] public float Height { get; init; }

    [JsonPropertyName("name")] public StatusRectSkin Name { get; init; } = new();

    [JsonPropertyName("gauge")] public StatusGaugeSkin Gauge { get; init; } = new();
}

public sealed class StatusLevelColorSkin
{
    [JsonPropertyName("minDifference")] public int MinDifference { get; init; }

    [JsonPropertyName("color")] public string Color { get; init; } = "#ffffff";
}

public class StatusPanelSkin
{
    [JsonPropertyName("width")] public float Width { get; init; }

    [JsonPropertyName("height")] public float Height { get; init; }

    [JsonPropertyName("inset")] public StatusInsetSkin Inset { get; init; } = new();

    [JsonPropertyName("portrait")] public StatusRectSkin Portrait { get; init; } = new();

    [JsonPropertyName("level")] public StatusRectSkin Level { get; init; } = new();

    [JsonPropertyName("gauges")] public Dictionary<string, StatusGaugeSkin> Gauges { get; init; } = new();
}

public sealed class StatusInsetSkin
{
    [JsonPropertyName("left")] public float Left { get; init; }

    [JsonPropertyName("top")] public float Top { get; init; }

    [JsonPropertyName("right")] public float Right { get; init; }

    [JsonPropertyName("bottom")] public float Bottom { get; init; }
}

public sealed class StatusMemberSkin : StatusPanelSkin
{
    [JsonPropertyName("slots")] public float[] Slots { get; init; } = [];
}

public class StatusRectSkin
{
    [JsonPropertyName("x")] public float X { get; init; }

    [JsonPropertyName("y")] public float Y { get; init; }

    [JsonPropertyName("width")] public float Width { get; init; }

    [JsonPropertyName("height")] public float Height { get; init; }
}

/// <summary>
/// 头顶双条（血槽与蓝槽）：<c>frame</c> 是底框、<c>leader</c> 是队长标记，
/// <c>health</c>/<c>mana</c> 是两条纯色进度条。坐标都相对人物头顶锚点，
/// 所以整块可以直接挂成角色节点的子节点，跟着人物走、跟着镜头缩放。
/// </summary>
public sealed class StatusOverheadSkin
{
    [JsonPropertyName("frame")] public StatusRectSkin Frame { get; init; } = new();

    [JsonPropertyName("leader")] public StatusRectSkin Leader { get; init; } = new();

    [JsonPropertyName("health")] public StatusBarSkin Health { get; init; } = new();

    [JsonPropertyName("mana")] public StatusBarSkin Mana { get; init; } = new();
}

public sealed class StatusBarSkin : StatusRectSkin
{
    [JsonPropertyName("color")] public string Color { get; init; } = "#ffffff";
}

public sealed class StatusGaugeSkin
{
    [JsonPropertyName("x")] public float X { get; init; }

    [JsonPropertyName("y")] public float Y { get; init; }

    [JsonPropertyName("fillOffset")] public float FillOffset { get; init; }

    [JsonPropertyName("fillWidth")] public float FillWidth { get; init; }

    [JsonPropertyName("startWidth")] public float StartWidth { get; init; }

    [JsonPropertyName("endWidth")] public float EndWidth { get; init; }

    [JsonPropertyName("height")] public float Height { get; init; }
}
