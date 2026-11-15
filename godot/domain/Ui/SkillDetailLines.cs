using Inotia4.Domain.Character;
using Inotia4.Domain.Content;

namespace Inotia4.Domain.Ui;

public sealed record SkillLearningRequirements(
    int Level,
    int Cost,
    int Invested,
    int RequiredInvested,
    bool AtLimit,
    bool ConditionMet,
    bool PointsAvailable);

public sealed record SkillPreviewRow(string Label, string From, string To, string Kind)
{
    public const string ManaKind = "mana";

    public const string EffectKind = "effect";

    public bool IsMana => Kind == ManaKind;
}

public static class SkillDetailLines
{
    public const string White = "#ffffff";

    public const string Red = "#ff0000";

    public static string DetailLabel(SkillDetailEntry detail, int index, string skillKind) =>
        $"{detail.Label}{(skillKind == SkillDefinition.ActiveKind && index < 2 ? ": " : detail.ValueSpacing == true ? " " : "")}";

    public static string DetailLabel(SkillDisplayRow row, int index, string skillKind) =>
        $"{row.Label}{(skillKind == SkillDefinition.ActiveKind && index < 2 ? ": " : row.ValueSpacing == true ? " " : "")}";

    public static List<DescriptionTextPart> DescriptionParts(SkillDefinition skill, int level, int? companionMaximum = null)
    {
        var rankIndex = Math.Max(0, level - 1);
        var rank = rankIndex < skill.Ranks.Count ? skill.Ranks[rankIndex] : skill.Ranks[^1];
        var parts = new List<DescriptionTextPart>
        {
            DescriptionTextPart.Of($"{skill.Name} (LV {Math.Max(1, level)})\n"),
        };

        if (companionMaximum is { } maximum)
            parts.Add(DescriptionTextPart.Of($"最高等级： LV {maximum}\n"));

        parts.AddRange(skill.Parts());
        parts.Add(DescriptionTextPart.Of("\n"));

        var entries = SkillDisplay.Entries(skill, rank);
        for (var index = 0; index < entries.Count; index += 1)
        {
            var entry = entries[index];
            parts.Add(DescriptionTextPart.Of(DetailLabel(entry, index, skill.Kind)));
            parts.AddRange(entry.Parts);
            parts.Add(DescriptionTextPart.Of("\n"));
        }

        return parts;
    }

    public static List<DescriptionTextPart> LearningParts(string name, int level,
        SkillLearningRequirements? requirements, int? companionLearningLevel = null)
    {
        var parts = new List<DescriptionTextPart> { DescriptionTextPart.Of($"{name} (LV {level + 1})\n") };

        if (companionLearningLevel is { } companionLevel)
        {
            parts.Add(DescriptionTextPart.Of($"学习级别： LV {companionLevel}", Red));
        }
        else if (requirements is { } state)
        {
            parts.Add(DescriptionTextPart.Of($"条件：投入{state.RequiredInvested}技能点\n",
                state.ConditionMet ? White : Red));
            parts.Add(DescriptionTextPart.Of($"需要技能点数： {state.Cost}",
                state.PointsAvailable ? White : Red));
        }

        return parts;
    }

    public static List<SkillPreviewRow> NextLevelPreview(IReadOnlyList<SkillRank> ranks, int level, string skillKind,
        int maximumLearningLevel, IReadOnlyList<SkillDisplayRow> display)
    {
        if (level >= maximumLearningLevel || level >= ranks.Count || level < 0) return [];

        var before = new Dictionary<string, string>();
        if (level > 0)
        {
            var current = SkillDisplay.Entries(display, ranks[level - 1]);
            for (var index = 0; index < current.Count; index += 1) before[Key(current[index], index)] = current[index].Text;
        }

        var next = SkillDisplay.Entries(display, ranks[level]);
        var rows = new List<SkillPreviewRow>();
        for (var index = 0; index < next.Count; index += 1)
        {
            var entry = next[index];
            var kind = entry.Kind ?? (skillKind == SkillDefinition.ActiveKind
                ? index == 0 ? "mana" : index == 1 ? "cooldown" : SkillPreviewRow.EffectKind
                : SkillPreviewRow.EffectKind);
            if (kind == "cooldown") continue;
            rows.Add(new SkillPreviewRow(entry.Label, before.GetValueOrDefault(Key(entry, index), entry.ZeroText),
                entry.Text, kind));
        }

        return rows;
    }

    private static string Key(SkillDetailEntry entry, int index) =>
        entry.Kind == "effect" ? $"effect-{entry.EffectIndex ?? index}" : index.ToString();
}
