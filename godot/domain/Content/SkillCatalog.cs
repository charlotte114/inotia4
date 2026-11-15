using System.Text.Json.Serialization;
using Inotia4.Domain.Character;

namespace Inotia4.Domain.Content;

/// <summary>
/// <c>battle.json</c> 里的技能表与职业表：技能按 id 索引，职业各自带一份技能清单。
///
/// 每门技能的 <c>ranks[].details</c> 是作者态的「标签 + 值」，展示文本要在读取时按
/// <see cref="SkillDisplay"/> 编译成「模板 + 每级数值」（建目录时跑一次，不是每次开界面重算）。
/// 所以解析完必须调一次 <see cref="Prepare"/>，否则技能页拿不到编译好的展示行与每级数值。
/// </summary>
public sealed class SkillCatalog
{
    public static SkillCatalog FromBattle(BattleContent battle) =>
        new() { Skills = battle.Skills, Classes = battle.Classes };

    [JsonPropertyName("skills")] public Dictionary<string, SkillDefinition> Skills { get; init; } = new();

    [JsonPropertyName("classes")] public BattleClassDefinition[] Classes { get; init; } = [];

    private Dictionary<string, BattleClassDefinition>? _byId;

    public BattleClassDefinition? Class(string? classId) =>
        classId is null ? null : Index().GetValueOrDefault(classId);

    private Dictionary<string, BattleClassDefinition> Index()
    {
        if (_byId is { } cached) return cached;
        _byId = new Dictionary<string, BattleClassDefinition>();
        foreach (var entry in Classes) _byId[entry.Id] = entry;
        return _byId;
    }

    public void Prepare()
    {
        foreach (var (id, skill) in Skills)
        {
            var (display, values) = SkillDisplay.Compile(id, skill.Ranks);
            skill.Display = display;
            for (var index = 0; index < skill.Ranks.Count && index < values.Count; index += 1)
                skill.Ranks[index].DisplayValues = values[index];

            if (skill.Companion is { } companion)
            {
                var (companionDisplay, companionValues) = SkillDisplay.Compile(id, companion.Ranks);
                companion.Display = companionDisplay;
                for (var index = 0; index < companion.Ranks.Count; index += 1)
                    companion.Ranks[index].DisplayValues = companionValues[index];
            }

            if (skill.Action is { } action) Combat.SkillActionCompiler.Compile(action);
            if (skill.FollowUpAction is { } followUpAction) Combat.SkillActionCompiler.Compile(followUpAction);
            if (skill.Companion?.Action is { } companionAction) Combat.SkillActionCompiler.Compile(companionAction);
            if (skill.Companion?.FollowUpAction is { } companionFollowUpAction) Combat.SkillActionCompiler.Compile(companionFollowUpAction);
        }
    }

    public IReadOnlyList<string> SkillsOf(string? classId) => Class(classId)?.SkillIds ?? [];

    /// <summary>
    /// 取一份用于成长计算的技能。主角取原样；伙伴按 <c>skill.companion</c> 换一份：
    /// 等级、动作、后续动作、被动单元与逐级反应表都取伙伴档，主角档的对应值不沿用。
    /// 内容校验要求伙伴块必须写 <c>action</c>（可为 <c>null</c>），所以缺伙伴块时明确拒绝。
    /// </summary>
    public SkillDefinition SkillForGrowth(string id, bool companion)
    {
        if (!Skills.TryGetValue(id, out var skill)) throw new KeyNotFoundException($"技能不存在：{id}");
        if (!companion) return skill;

        if (skill.Companion is null) throw new InvalidOperationException($"技能缺少伙伴成长配置：{id}");
        return skill.WithGrowth(skill.Companion);
    }
}

public sealed class SkillDefinition
{
    public const string ActiveKind = "active";

    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;

    [JsonPropertyName("kind")] public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("icon")] public string Icon { get; init; } = string.Empty;

    [JsonPropertyName("description")] public string Description { get; init; } = string.Empty;

    [JsonPropertyName("normalMaxLevel")] public int NormalMaxLevel { get; init; }

    [JsonPropertyName("requiredInvestedSkillPoints")] public int RequiredInvestedSkillPoints { get; init; }

    [JsonPropertyName("descriptionParts")] public List<DescriptionTextPart>? DescriptionParts { get; init; }

    [JsonPropertyName("ranks")] public List<SkillRank> Ranks { get; init; } = [];

    [JsonPropertyName("action")] public Combat.SkillAction? Action { get; init; }

    [JsonPropertyName("followUpAction")] public Combat.SkillAction? FollowUpAction { get; init; }

    [JsonPropertyName("reactionsByRank")]
    public List<List<Combat.SkillReaction>?>? ReactionsByRank { get; init; }

    [JsonPropertyName("passiveUnits")] public List<PassiveUnit>? PassiveUnits { get; init; }

    [JsonPropertyName("companion")] public SkillGrowthDefinition? Companion { get; init; }

    [JsonIgnore] public List<SkillDisplayRow> Display { get; set; } = [];

    public bool IsActive => Kind == ActiveKind;

    public SkillDefinition WithGrowth(SkillGrowthDefinition growth) => new()
    {
        Id = Id,
        Name = Name,
        Kind = Kind,
        Icon = Icon,
        Description = Description,
        DescriptionParts = DescriptionParts,
        NormalMaxLevel = NormalMaxLevel,
        RequiredInvestedSkillPoints = growth.RequiredInvestedSkillPoints ?? RequiredInvestedSkillPoints,
        Ranks = growth.Ranks,
        Action = growth.Action,
        FollowUpAction = growth.FollowUpAction,
        PassiveUnits = growth.PassiveUnits,
        ReactionsByRank = growth.ReactionsByRank,
        Companion = Companion,
        Display = growth.Display,
    };

    public IReadOnlyList<DescriptionTextPart> Parts() =>
        DescriptionParts is { Count: > 0 } parts ? parts : [DescriptionTextPart.Of(Description)];
}

public sealed class SkillGrowthDefinition
{
    [JsonPropertyName("requiredInvestedSkillPoints")] public int? RequiredInvestedSkillPoints { get; init; }

    [JsonPropertyName("ranks")] public List<SkillRank> Ranks { get; init; } = [];

    [JsonPropertyName("action")] public Combat.SkillAction? Action { get; init; }

    [JsonPropertyName("followUpAction")] public Combat.SkillAction? FollowUpAction { get; init; }

    [JsonPropertyName("reactionsByRank")] public List<List<Combat.SkillReaction>?>? ReactionsByRank { get; init; }

    [JsonPropertyName("passiveUnits")] public List<PassiveUnit>? PassiveUnits { get; init; }

    [JsonIgnore] public List<SkillDisplayRow> Display { get; set; } = [];
}

public sealed class PassiveUnit
{
    [JsonPropertyName("attribute")] public required string Attribute { get; init; }

    [JsonPropertyName("condition")] public required string Condition { get; init; }

    [JsonPropertyName("ranks")] public List<Combat.CombatOperation> Ranks { get; init; } = [];
}

public sealed class SkillRank
{
    [JsonPropertyName("level")] public int Level { get; init; }

    [JsonPropertyName("manaCost")] public int ManaCost { get; init; }

    [JsonPropertyName("details")] public List<SkillDetailRow> Details { get; init; } = [];

    [JsonPropertyName("requiredCharacterLevel")] public int RequiredCharacterLevel { get; init; }

    [JsonPropertyName("pointCost")] public int PointCost { get; init; }

    [JsonPropertyName("investedSkillPoints")] public int? InvestedSkillPoints { get; init; }

    [JsonIgnore] public double[] DisplayValues { get; set; } = [];
}

public sealed class SkillDetailRow
{
    [JsonPropertyName("label")] public string Label { get; init; } = string.Empty;

    [JsonPropertyName("value")] public string Value { get; init; } = string.Empty;

    [JsonPropertyName("kind")] public string? Kind { get; init; }

    [JsonPropertyName("effectIndex")] public int? EffectIndex { get; init; }

    [JsonPropertyName("valueSpacing")] public bool? ValueSpacing { get; init; }

    [JsonPropertyName("zeroValue")] public string? ZeroValue { get; init; }

    [JsonPropertyName("valueParts")] public List<DescriptionTextPart>? ValueParts { get; init; }
}

public sealed class BattleClassDefinition
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;

    [JsonPropertyName("description")] public string Description { get; init; } = string.Empty;

    [JsonPropertyName("preview")] public string Preview { get; init; } = string.Empty;

    [JsonPropertyName("skills")] public string[] SkillIds { get; init; } = [];

    [JsonPropertyName("actor")] public ActorDefinition Actor { get; init; } = new();

    [JsonPropertyName("basicAttack")] public Combat.BasicAttackBinding? BasicAttack { get; init; }
}
