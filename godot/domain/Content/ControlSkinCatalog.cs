using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inotia4.Domain.Content;

/// <summary>
/// 操作区的按钮皮肤（<c>public/content/control-skin.json</c>）。文件是一张「键 → 图片路径」的平表，
/// 键名带连字符（<c>slot-shadow</c>、<c>swap-pressed</c>），所以按扩展数据整表收进来，
/// 用 <see cref="Asset"/> 按键取。
/// </summary>
public sealed class ControlSkinCatalog
{
    [JsonExtensionData] public Dictionary<string, JsonElement> Raw { get; init; } = new();

    public string? Asset(string key) =>
        Raw.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
