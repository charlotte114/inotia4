using System.Globalization;
using System.Text.RegularExpressions;
using Inotia4.Domain.Content;

namespace Inotia4.Domain.Character;

public sealed record SkillDisplayRow(
    string Label,
    string? Kind,
    int? EffectIndex,
    bool? ValueSpacing,
    string Template,
    int Decimals,
    bool Highlight,
    string ZeroText);

public sealed record SkillDetailEntry(
    string Label,
    string? Kind,
    int? EffectIndex,
    bool? ValueSpacing,
    string Text,
    List<DescriptionTextPart> Parts,
    string ZeroText);

public static class SkillDisplay
{
    public const string ValueColor = "#00b4ff";

    private static readonly Regex ValuePattern = new(
        "^(?<prefix>[^0-9+\\-]*)(?<sign>[+-]?)(?<number>\\d+(?:\\.\\d+)?)(?<suffix>[\\s\\S]*)$",
        RegexOptions.Compiled);

    private sealed record ValueParts(string Prefix, string Sign, string Number, string Suffix);

    private static ValueParts ParseValue(string value)
    {
        var match = ValuePattern.Match(value);
        if (!match.Success) throw new InvalidOperationException($"技能详情无法解析数值：{value}");
        return new ValueParts(
            match.Groups["prefix"].Value,
            match.Groups["sign"].Value,
            match.Groups["number"].Value,
            match.Groups["suffix"].Value);
    }

    private static SkillDisplayRow CompileRow(SkillDetailRow detail, string skillId)
    {
        var parsed = ParseValue(detail.Value);
        var template = $"{parsed.Prefix}{parsed.Sign}{{v}}{parsed.Suffix}";
        if (template.Split("{v}").Length != 2)
            throw new InvalidOperationException($"技能 {skillId} 的详情模板必须恰好包含一个 {{v}}：{detail.Value}");

        var parts = detail.ValueParts;
        if (parts is { Count: > 1 })
            throw new InvalidOperationException($"技能 {skillId} 的详情 valueParts 只支持单段：{detail.Value}");
        if (parts is { Count: 1 } && parts[0].Text != detail.Value)
            throw new InvalidOperationException($"技能 {skillId} 的详情 valueParts 与 value 不一致：{detail.Value}");
        if (parts is { Count: 1 } && parts[0].Color != ValueColor)
            throw new InvalidOperationException($"技能 {skillId} 的详情 valueParts 颜色不是 {ValueColor}：{detail.Value}");

        var dot = parsed.Number.IndexOf('.');
        return new SkillDisplayRow(
            detail.Label,
            detail.Kind,
            detail.EffectIndex,
            detail.ValueSpacing,
            template,
            dot < 0 ? 0 : parsed.Number.Length - dot - 1,
            parts is { Count: > 0 },
            detail.ZeroValue ?? "0");
    }

    /// <summary>
    /// 编译一门技能的全部等级。首级给出模板，其余各级只留数值；任何一行的格式与首级
    /// 不一致（行数、标签、模板、小数位）都拒绝，因为那样逐字还原就不再成立。
    /// </summary>
    public static (List<SkillDisplayRow> Display, List<double[]> Values) Compile(string skillId, IReadOnlyList<SkillRank> ranks)
    {
        if (ranks.Count == 0) throw new InvalidOperationException($"技能 {skillId} 缺少等级数据");
        var first = ranks[0].Details;
        if (first.Count == 0) throw new InvalidOperationException($"技能 {skillId} 缺少 details，无法编译展示文本");

        var display = first.Select(row => CompileRow(row, skillId)).ToList();
        var values = new List<double[]>();

        foreach (var rank in ranks)
        {
            var details = rank.Details;
            if (details.Count != display.Count)
                throw new InvalidOperationException($"技能 {skillId} 的 {rank.Level} 级详情行数与首级不一致");

            var row = new double[details.Count];
            for (var index = 0; index < details.Count; index += 1)
            {
                var detail = details[index];
                var compiled = CompileRow(detail, skillId);
                if (compiled != display[index])
                    throw new InvalidOperationException($"技能 {skillId} 的 {rank.Level} 级详情行 {index} 的展示格式与首级不一致");

                var parsed = ParseValue(detail.Value);
                row[index] = Math.Abs(double.Parse(parsed.Sign + parsed.Number, CultureInfo.InvariantCulture));

                if (detail.ZeroValue is { } zero && Format(display[index], 0).Text != zero)
                    throw new InvalidOperationException($"技能 {skillId} 的详情零值无法还原：{detail.Value} / {zero}");
            }

            values.Add(row);
        }

        return (display, values);
    }

    public static (string Text, List<DescriptionTextPart> Parts) Format(SkillDisplayRow row, double value)
    {
        var text = row.Template.Replace("{v}", value.ToString("F" + row.Decimals, CultureInfo.InvariantCulture));
        return (text, row.Highlight
            ? [DescriptionTextPart.Of(text, ValueColor)]
            : [DescriptionTextPart.Of(text)]);
    }

    public static List<SkillDetailEntry> Entries(SkillDefinition skill, SkillRank rank) => Entries(skill.Display, rank);

    public static List<SkillDetailEntry> Entries(IReadOnlyList<SkillDisplayRow> display, SkillRank rank)
    {
        if (display.Count > 0 && rank.DisplayValues.Length == display.Count)
        {
            var rows = new List<SkillDetailEntry>(display.Count);
            for (var index = 0; index < display.Count; index += 1)
            {
                var row = display[index];
                var formatted = Format(row, rank.DisplayValues[index]);
                rows.Add(new SkillDetailEntry(row.Label, row.Kind, row.EffectIndex, row.ValueSpacing,
                    formatted.Text, formatted.Parts, row.ZeroText));
            }

            return rows;
        }

        return rank.Details.Select(detail => new SkillDetailEntry(
            detail.Label,
            detail.Kind,
            detail.EffectIndex,
            detail.ValueSpacing,
            detail.Value,
            detail.ValueParts is { Count: > 0 } parts ? parts : [DescriptionTextPart.Of(detail.Value)],
            detail.ZeroValue ?? "0")).ToList();
    }
}
