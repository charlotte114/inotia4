using System.Text.Json.Serialization;

namespace Inotia4.Domain.Content;

public sealed class StoryInterfaceAssets
{
    [JsonPropertyName("assets")] public Dictionary<string, string> Assets { get; init; } = new();

    /// <summary>
    /// 立绘键 → 图片路径。对白行自己给了 <c>portrait</c> 就用它，
    /// 否则拿人物定义里的 <c>portrait</c> 当键来这里查；查不到这一句就不画立绘。
    /// </summary>
    [JsonPropertyName("portraits")] public Dictionary<string, string> Portraits { get; init; } = new();
}
