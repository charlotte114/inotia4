using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inotia4.Domain.Content;

public sealed class NpcFunctionBehavior
{
    public const string Rest = "rest";

    public const string Revive = "revive";

    public const string PullSword = "pull-sword";

    public static readonly IReadOnlySet<string> KnownKinds =
        new HashSet<string>(StringComparer.Ordinal) { Rest, Revive, PullSword };

    [JsonPropertyName("kind")] public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("percent")] public int Percent { get; init; } = 100;

    [JsonPropertyName("rewardByClass")] public Dictionary<string, string> RewardByClass { get; init; } = [];
}

public sealed class NpcFunction
{
    [JsonPropertyName("index")] public int Index { get; init; }

    [JsonPropertyName("textId")] public int TextId { get; init; }

    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;

    [JsonPropertyName("params")] public int[] Params { get; init; } = [];

    [JsonIgnore] public NpcFunctionBehavior? Behavior { get; set; }
}

public sealed class NpcFunctionCatalog
{
    [JsonPropertyName("version")] public int Version { get; init; }

    [JsonPropertyName("note")] public string Note { get; init; } = string.Empty;

    [JsonPropertyName("functions")] public NpcFunction[] Functions { get; init; } = [];

    [JsonPropertyName("npcs")] public Dictionary<string, int[]> Npcs { get; init; } = [];

    [JsonPropertyName("behaviors")] public Dictionary<string, NpcFunctionBehavior> Behaviors { get; init; } = [];

    private NpcFunction[]? _byIndex;

    public NpcFunction? Of(int index)
    {
        _byIndex ??= BuildIndex();
        return index >= 0 && index < _byIndex.Length ? _byIndex[index] : null;
    }

    public IReadOnlyList<NpcFunction> FunctionsOf(string? characterId)
    {
        if (characterId is null || !Npcs.TryGetValue(characterId, out var indices)) return [];
        var result = new List<NpcFunction>(indices.Length);
        foreach (var index in indices)
        {
            if (Of(index) is { } function) result.Add(function);
        }

        return result;
    }

    public NpcFunction? ActionOf(string? characterId) =>
        FunctionsOf(characterId).FirstOrDefault(function => function.Behavior is not null);

    public static string? RewardFor(NpcFunction function, string? classId) =>
        classId is not null && function.Behavior is { } behavior &&
        behavior.RewardByClass.TryGetValue(classId, out var item)
            ? item
            : null;

    public static NpcFunctionCatalog Parse(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("NPC 功能表格式不正确");
        var catalog = value.Deserialize<NpcFunctionCatalog>()
                      ?? throw new InvalidOperationException("NPC 功能表格式不正确");
        if (catalog.Version != 1) throw new InvalidOperationException("NPC 功能表版本不支持");

        var byIndex = new Dictionary<int, NpcFunction>();
        if (catalog.Functions.Length == 0) throw new InvalidOperationException("NPC 功能表是空的");
        foreach (var function in catalog.Functions)
        {
            if (function.Index < 0 || !byIndex.TryAdd(function.Index, function))
                throw new InvalidOperationException($"NPC 功能索引重复：{function.Index}");
            if (function.Params.Length != 3)
                throw new InvalidOperationException($"NPC 功能 {function.Index} 的参数不是三个");
        }

        foreach (var (npc, indices) in catalog.Npcs)
        {
            if (indices.Length == 0) throw new InvalidOperationException($"NPC {npc} 的功能表是空的");
            foreach (var index in indices)
            {
                if (!byIndex.TryGetValue(index, out var bound))
                    throw new InvalidOperationException($"NPC {npc} 挂到了不存在的功能 {index}");
                // 表里有几条没有人用的功能原本就没有名字（第 38 条），挂到 NPC 身上的必须有名字。
                if (bound.Name.Length == 0)
                    throw new InvalidOperationException($"NPC {npc} 的功能 {index} 没有名字");
            }
        }

        foreach (var (key, behavior) in catalog.Behaviors)
        {
            if (!int.TryParse(key, out var index) || !byIndex.TryGetValue(index, out var function))
                throw new InvalidOperationException($"功能行为挂到了不存在的功能：{key}");
            if (!NpcFunctionBehavior.KnownKinds.Contains(behavior.Kind))
                throw new InvalidOperationException($"功能 {index} 的行为没有实现：{behavior.Kind}");
            if (behavior.Kind == NpcFunctionBehavior.PullSword && behavior.RewardByClass.Count == 0)
                throw new InvalidOperationException($"功能 {index} 没有配置奖励");
            if (behavior.Kind == NpcFunctionBehavior.Revive && behavior.Percent is <= 0 or > 100)
                throw new InvalidOperationException($"功能 {index} 的复活比例不正确：{behavior.Percent}");

            function.Behavior = behavior;
        }

        return catalog;
    }

    private NpcFunction[] BuildIndex()
    {
        var size = Functions.Max(function => function.Index) + 1;
        var array = new NpcFunction[size];
        foreach (var function in Functions) array[function.Index] = function;
        return array;
    }
}
