using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inotia4.Domain.Character;

public sealed class ResourceLimitsConverter : JsonConverter<(int MaxHealth, int MaxMana)?>
{
    public override (int MaxHealth, int MaxMana)? Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("资源上限必须是对象");
        // 格式 37 及更早的 Godot 存档把 ValueTuple 写成 {}，数字已经丢失，不能恢复为零上限。
        if (!value.EnumerateObject().Any()) return null;
        if (!value.TryGetProperty("maxHealth", out var health) || health.ValueKind != JsonValueKind.Number ||
            !health.TryGetInt32(out var maxHealth) ||
            !value.TryGetProperty("maxMana", out var mana) || mana.ValueKind != JsonValueKind.Number ||
            !mana.TryGetInt32(out var maxMana))
            throw new JsonException("资源上限缺少生命或魔法数值");
        return (maxHealth, maxMana);
    }

    public override void Write(Utf8JsonWriter writer, (int MaxHealth, int MaxMana)? value,
        JsonSerializerOptions options)
    {
        if (value is not { } limits) { writer.WriteNullValue(); return; }
        writer.WriteStartObject();
        writer.WriteNumber("maxHealth", limits.MaxHealth);
        writer.WriteNumber("maxMana", limits.MaxMana);
        writer.WriteEndObject();
    }
}
