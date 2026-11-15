namespace Inotia4.Domain.Content;

using System.Text.Json.Serialization;

public sealed class DescriptionTextPart
{
    [JsonPropertyName("text")] public string Text { get; init; } = string.Empty;

    [JsonPropertyName("color")] public string? Color { get; init; }

    public static DescriptionTextPart Of(string text, string? color = null) => new() { Text = text, Color = color };

    public static string Plain(IEnumerable<DescriptionTextPart> parts) =>
        string.Concat(parts.Select(part => part.Text));
}
