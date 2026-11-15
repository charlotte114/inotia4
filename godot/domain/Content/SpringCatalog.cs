using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inotia4.Domain.Content;

/// <summary>
/// 一档泉水。等级达到 <see cref="UnlockLevel"/> 才有机会随机到它，所以等级越高水越杂。
/// </summary>
public sealed class SpringDefinition
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;

    [JsonPropertyName("unlockLevel")] public int UnlockLevel { get; init; }
}

public sealed class SpringEffect
{
    [JsonPropertyName("spring")] public string Spring { get; init; } = string.Empty;

    [JsonPropertyName("kind")] public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("weight")] public int Weight { get; init; }

    [JsonPropertyName("text")] public string Text { get; init; } = string.Empty;

    [JsonPropertyName("percent")] public int? Percent { get; init; }

    [JsonPropertyName("buff")] public string? Buff { get; init; }

    [JsonPropertyName("buffRow")] public int? BuffRow { get; init; }

    [JsonPropertyName("enemy")] public string? Enemy { get; init; }

    [JsonPropertyName("points")] public int? Points { get; init; }
}

/// <summary>
/// 恢复泉水目录（<c>public/content/springs.json</c>，5 档 20 条效果）。
/// 规则来自原版 <c>CHAR_MakeSpring</c>（生成泉水）与 <c>CHAR_DrinkSpring</c>（喝下结算）。
/// </summary>
public sealed class SpringCatalog
{
    [JsonPropertyName("version")] public int Version { get; init; }

    [JsonPropertyName("empty")] public string Empty { get; init; } = string.Empty;

    [JsonPropertyName("springs")] public SpringDefinition[] Springs { get; init; } = [];

    [JsonPropertyName("effects")] public SpringEffect[] Effects { get; init; } = [];

    public static SpringCatalog Parse(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("泉水内容格式不正确");
        var catalog = value.Deserialize<SpringCatalog>()
                      ?? throw new InvalidOperationException("泉水内容格式不正确");
        if (catalog.Version != 1) throw new InvalidOperationException("泉水内容格式不正确");
        if (string.IsNullOrEmpty(catalog.Empty)) throw new InvalidOperationException("泉水内容缺少喝空后的外观");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var spring in catalog.Springs)
        {
            if (string.IsNullOrEmpty(spring.Id) || string.IsNullOrEmpty(spring.Name) || spring.UnlockLevel <= 0)
                throw new InvalidOperationException($"泉水档位不正确：{spring.Id}");
            ids.Add(spring.Id);
        }

        foreach (var effect in catalog.Effects)
        {
            if (!ids.Contains(effect.Spring))
                throw new InvalidOperationException($"泉水效果挂到了不存在的档位：{effect.Spring}");
            if (effect.Weight <= 0 || string.IsNullOrEmpty(effect.Text))
                throw new InvalidOperationException($"泉水效果不完整：{effect.Spring}");
        }

        return catalog;
    }
}

public static class SpringRoll
{
    /// <summary>
    /// 原版 <c>CHAR_MakeSpring</c> 的选档：按角色等级决定这次最高能到哪一档，
    /// 再在「能喝到的档位」里均匀随机一个。
    /// </summary>
    public static SpringDefinition Roll(SpringCatalog catalog, int level, Func<double> random)
    {
        var unlocked = catalog.Springs.Where(spring => level >= spring.UnlockLevel).ToList();
        if (unlocked.Count == 0) throw new InvalidOperationException($"没有等级 {level} 能喝到的泉水");
        var index = Math.Min(unlocked.Count - 1, (int)Math.Floor(random() * unlocked.Count));
        return unlocked[index];
    }

    /// <summary>
    /// 原版 <c>CHAR_DrinkSpring</c> 的结算：在这一档的候选效果里按权重随机一条。
    /// 原版掷的是定值骰，权重和不够时会落空、改走通用提示；
    /// 这里按权重归一化，不会落空。
    /// </summary>
    public static SpringEffect RollEffect(SpringCatalog catalog, string spring, Func<double> random)
    {
        var candidates = catalog.Effects.Where(effect => effect.Spring == spring).ToList();
        if (candidates.Count == 0) throw new InvalidOperationException($"泉水 {spring} 没有可用效果");

        var total = candidates.Sum(effect => effect.Weight);
        var roll = random() * total;
        foreach (var effect in candidates)
        {
            roll -= effect.Weight;
            if (roll < 0) return effect;
        }

        return candidates[^1];
    }
}
