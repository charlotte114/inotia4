using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Inotia4.Domain.Content;

/// <summary>
/// 一条待校验的值：<see cref="Present"/> 是「键在不在」——JSON 里「缺键」与「显式 null」
/// 必须分开，「缺键就跳过」与「null 就跳过」两档检查就靠这个区分，
/// 而 <c>JsonObject[key]</c> 两种都返回 null，拿不到这个区别。
/// </summary>
public readonly struct ShapeValue
{
    public ShapeValue(JsonNode? node)
    {
        Node = node;
        Present = true;
    }

    public static ShapeValue Missing => default;

    public JsonNode? Node { get; }

    public bool Present { get; }
}

public delegate void ShapeCheck(ShapeValue value, string path);

public static class ContentShape
{
    public static void Require(bool condition, string path, string message)
    {
        if (!condition) throw new InvalidDataException($"内容无效 {path}: {message}");
    }

    public static readonly ShapeCheck Text = (value, path) =>
        Require(IsKind(value, JsonValueKind.String), path, "需要文字");

    public static readonly ShapeCheck Number = (value, path) =>
        Require(IsKind(value, JsonValueKind.Number) && TryDouble(value.Node, out var number) && double.IsFinite(number),
            path, "需要有限数字");

    public static readonly ShapeCheck Positive = (value, path) =>
    {
        Number(value, path);
        TryDouble(value.Node, out var number);
        Require(number > 0, path, "必须大于零");
    };

    public static readonly ShapeCheck Integer = (value, path) =>
    {
        Number(value, path);
        TryDouble(value.Node, out var number);
        Require(IsInteger(number) && number >= 0, path, "需要非负整数");
    };

    public static readonly ShapeCheck Count = (value, path) =>
    {
        Integer(value, path);
        TryDouble(value.Node, out var number);
        Require(number > 0, path, "必须是正整数");
    };

    public static readonly ShapeCheck DiceFace = (value, path) =>
    {
        Count(value, path);
        TryDouble(value.Node, out var number);
        Require(number <= 99, path, "骰面不能超过 99");
    };

    /// <summary>可以是负数、但必须是整数（尺寸偏移这类）。</summary>
    public static readonly ShapeCheck OffsetInteger = (value, path) =>
    {
        Number(value, path);
        TryDouble(value.Node, out var number);
        Require(IsInteger(number), path, "需要整数");
    };

    public static readonly ShapeCheck Flag = (value, path) =>
        Require(IsKind(value, JsonValueKind.True) || IsKind(value, JsonValueKind.False), path, "需要布尔值");

    public static readonly ShapeCheck AssetPath = (value, path) =>
    {
        Text(value, path);
        var text = Str(value.Node)!;
        Require((text.StartsWith("assets/", StringComparison.Ordinal) ||
                 text.StartsWith("content/", StringComparison.Ordinal)) &&
                text.All(character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9'
                    or '_' or '.' or '/' or '-') &&
                !text.Split('/').Contains(".."), path, "需要游戏内部资源路径");
    };

    public static ShapeCheck Optional(ShapeCheck check) => (value, path) =>
    {
        if (value.Present) check(value, path);
    };

    public static ShapeCheck Nullable(ShapeCheck check) => (value, path) =>
    {
        if (value.Present && value.Node is null) return;
        check(value, path);
    };

    public static ShapeCheck OneOf(params string[] values) => (value, path) =>
    {
        Require(IsKind(value, JsonValueKind.String) && Array.IndexOf(values, Str(value.Node)!) >= 0,
            path, $"需要 {string.Join('/', values)}");
    };

    public static ShapeCheck Variant(string key, IReadOnlyDictionary<string, ShapeCheck> variants) => (value, path) =>
    {
        Require(IsObject(value), path, "需要对象");
        var kind = Str(((JsonObject)value.Node!)[key]);
        Require(kind is not null && variants.ContainsKey(kind), path, $"未知 {key}: {kind ?? "无"}");
        variants[kind!](value, path);
    };

    public static ShapeCheck ArrayOf(ShapeCheck check) => (value, path) =>
    {
        Require(value.Present && value.Node is JsonArray, path, "需要数组");
        var items = (JsonArray)value.Node!;
        for (var index = 0; index < items.Count; index++)
            check(new ShapeValue(items[index]), $"{path}[{index}]");
    };

    public static ShapeCheck Record(ShapeCheck check) => (value, path) =>
    {
        Require(IsObject(value), path, "需要对象");
        foreach (var (key, item) in (JsonObject)value.Node!)
            check(new ShapeValue(item), $"{path}.{key}");
    };

    public static ShapeCheck Object(IReadOnlyDictionary<string, ShapeCheck> fields) => (value, path) =>
    {
        Require(IsObject(value), path, "需要对象");
        var data = (JsonObject)value.Node!;
        foreach (var (key, _) in data)
            Require(fields.ContainsKey(key), $"{path}.{key}", "未定义字段");
        foreach (var (key, check) in fields)
        {
            // 缺键与显式 null 必须分开传给检查器：optional 只跳过后者里的前者，
            // nullable 正相反（见 ShapeValue 的注释）。
            var present = data.TryGetPropertyValue(key, out var node);
            check(present ? new ShapeValue(node) : ShapeValue.Missing, $"{path}.{key}");
        }
    };

    public static Dictionary<string, ShapeCheck> Fields(params (string Name, ShapeCheck Check)[] fields)
    {
        var table = new Dictionary<string, ShapeCheck>(fields.Length, StringComparer.Ordinal);
        foreach (var (name, check) in fields) table[name] = check;
        return table;
    }

    public static Dictionary<string, ShapeCheck> Merge(params IReadOnlyDictionary<string, ShapeCheck>[] tables)
    {
        var merged = new Dictionary<string, ShapeCheck>(StringComparer.Ordinal);
        foreach (var table in tables)
        foreach (var (key, check) in table)
            merged[key] = check;
        return merged;
    }

    public static void Unique(IEnumerable<string> ids, string path)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids) Require(seen.Add(id), path, "标识重复");
    }

    public static string? Str(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.TryGetValue<string>(out var text)
            ? text
            : null;

    public static bool TryDouble(JsonNode? node, out double number)
    {
        number = double.NaN;
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.Number) return false;
        if (value.TryGetValue<double>(out var direct))
        {
            if (!double.IsFinite(direct)) return false;
            number = direct;
            return true;
        }

        if (!double.TryParse(node.ToJsonString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed)) return false;
        if (!double.IsFinite(parsed)) return false;
        number = parsed;
        return true;
    }

    private static bool IsObject(ShapeValue value) => value.Present && value.Node is JsonObject;

    private static bool IsKind(ShapeValue value, JsonValueKind kind) =>
        value.Present && value.Node is JsonValue json && json.GetValueKind() == kind;

    private static bool IsInteger(double number) => number == Math.Truncate(number);
}
