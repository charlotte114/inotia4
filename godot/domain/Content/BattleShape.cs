using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Inotia4.Domain.Combat;
using static Inotia4.Domain.Content.ContentShape;

namespace Inotia4.Domain.Content;

public static class BattleShape
{
    public const int MaxEnemyLevel = 107;

    private static readonly string[] OutcomeEffects =
        ["energy-column", "enemy-vanish", "level-up-aura", "level-up-light", "level-up-burst"];

    private static readonly string[] Facings = ["north", "south", "east", "west"];

    private static readonly ShapeCheck FacingClips = Object(Fields(
        ("north", Text), ("south", Text), ("east", Text), ("west", Text)));

    private static readonly ShapeCheck RgbColor = BuildRgbColor();

    private static readonly ShapeCheck DescriptionParts = ArrayOf(Object(Fields(
        ("text", Text), ("color", Optional(RgbColor)))));

    private static readonly ShapeCheck Binding = Object(Fields(
        ("profileId", Text), ("clips", FacingClips)));

    private static readonly ShapeCheck Feedback = Object(Fields(
        ("digits", Object(Fields(
            ("enemy", ArrayOf(Text)),
            ("enemyCritical", ArrayOf(Text)),
            ("party", ArrayOf(Text)),
            ("partyCritical", ArrayOf(Text)),
            ("miss", ArrayOf(Text))))),
        ("normalImpacts", ArrayOf(Text)),
        ("skillImpact", Text),
        ("criticalImpact", Text),
        ("criticalRing", Text),
        ("criticalBursts", ArrayOf(Text)),
        ("missEffect", Text),
        ("shakeOffsets", ArrayOf(Object(Fields(("x", Number), ("y", Number)))))));

    private static readonly ShapeCheck EnemyAttributes = Object(Fields(
        ("maxMana", Integer),
        ("physicalAttack", Integer),
        ("magicAttack", Integer),
        ("defense", Integer),
        ("defenseRate", Integer),
        ("physicalResistanceBonus", Integer),
        ("magicResistance", Integer),
        ("hitBonus", Integer),
        ("evasionBonus", Integer),
        ("criticalRate", Integer),
        ("criticalResistance", Integer),
        ("criticalDamageResistance", Integer),
        ("criticalDamage", Integer),
        ("weaponBlock", Integer),
        ("shieldBlock", Integer)));

    private static readonly ShapeCheck Behavior = Object(Fields(
        ("aggression", OneOf("active", "passive", "none")),
        ("engagement", Optional(OneOf("map-wide"))),
        ("awarenessRangeUnits", Integer),
        ("moveSpeedPxPerSecond", Number),
        ("canMove", Flag),
        ("canTurn", Flag),
        ("wander", Optional(Object(Fields(("idleDurationMs", Positive)))))));

    private static readonly Dictionary<string, ShapeCheck> BasicAttackFields = BuildBasicAttackFields();

    private static readonly ShapeCheck BasicAttackObject = Object(BasicAttackFields);

    internal static readonly ShapeCheck BasicAttackShape = (value, path) =>
    {
        BasicAttackObject(value, path);
        CheckBasicAttack((JsonObject)value.Node!, path);
    };

    internal static readonly ShapeCheck BoundBasicAttackShape = (value, path) =>
    {
        Object(Merge(BasicAttackFields, Fields(("clips", FacingClips))))(value, path);
        CheckBasicAttack((JsonObject)value.Node!, path);
    };

    private static readonly ShapeCheck EnemySkill = BuildEnemySkill();

    private static readonly ShapeCheck Enemy = Object(Fields(
        ("stateRules", Optional(BattleActionShape.StateRules)),
        ("acceptsBuffs", Optional(Flag)),
        ("waterImmune", Optional(Flag)),
        ("skills", Optional(ArrayOf(EnemySkill))),
        ("commandAction", Optional(EnemySkill)),
        ("id", Text),
        ("name", Text),
        ("level", Nullable(Count)),
        ("maxHealth", Count),
        ("attributes", EnemyAttributes),
        ("variant", Text),
        ("actor", WorldShape.ActorShape),
        ("behavior", Behavior),
        ("basicAttack", Nullable(Binding)),
        ("displayOnlyReason", Optional(Text)),
        ("defeatPresentation", Optional(OneOf("vanish")))));

    private static readonly ShapeCheck SkillRank = Object(Fields(
        ("level", Positive),
        ("manaCost", Integer),
        ("requiredCharacterLevel", Positive),
        ("pointCost", Positive),
        ("investedSkillPoints", Optional(Integer)),
        ("details", Optional(ArrayOf(Object(Fields(
            ("label", Text),
            ("value", Text),
            ("kind", Optional(OneOf("mana", "cooldown", "effect"))),
            ("effectIndex", Optional(Integer)),
            ("valueSpacing", Optional(Flag)),
            ("zeroValue", Optional(Text)),
            ("valueParts", Optional(DescriptionParts))))))),
        ("displayValues", Optional(ArrayOf(Number)))));

    private static readonly ShapeCheck SkillDisplayRow = Object(Fields(
        ("label", Text),
        ("kind", Optional(OneOf("mana", "cooldown", "effect"))),
        ("effectIndex", Optional(Integer)),
        ("valueSpacing", Optional(Flag)),
        ("template", Text),
        ("decimals", Integer),
        ("highlight", Flag),
        ("zeroText", Text)));

    private static readonly Dictionary<string, ShapeCheck> GrowthFields = Fields(
        ("requiredInvestedSkillPoints", Optional(Integer)),
        ("ranks", ArrayOf(SkillRank)),
        ("display", Optional(ArrayOf(SkillDisplayRow))),
        ("action", Nullable(BattleActionShape.SkillAction)),
        ("followUpAction", Optional(BattleActionShape.SkillAction)),
        ("passiveUnits", Optional(ArrayOf(BattleActionShape.PassiveUnit))),
        ("reactionsByRank", Optional(ArrayOf(ArrayOf(BattleActionShape.SkillReaction)))));

    private static readonly ShapeCheck Skill = Object(Merge(
        Fields(
            ("id", Text),
            ("name", Text),
            ("kind", OneOf("active", "passive")),
            ("icon", AssetPath),
            ("description", Text),
            ("descriptionParts", Optional(DescriptionParts)),
            ("normalMaxLevel", Positive),
            ("companion", Object(GrowthFields))),
        GrowthFields));

    private static readonly ShapeCheck BattleClass = Object(Fields(
        ("id", Text),
        ("name", Text),
        ("description", Text),
        ("preview", AssetPath),
        ("actor", WorldShape.ActorShape),
        ("attributes", ArrayOf(Object(Fields(("name", Text), ("value", Number))))),
        ("skills", ArrayOf(Text)),
        ("basicAttack", Binding)));

    private static readonly ShapeCheck AttackEffect = Object(Fields(
        ("clips", Record(WorldShape.EffectClipShape)),
        ("attachmentPoint", Optional(OneOf("feet", "effect-anchor"))),
        ("attachedLifetime", Optional(OneOf("action", "character")))));

    private static readonly Dictionary<string, ShapeCheck> StatusAttackBuffFields = BuildStatusAttackBuffFields();

    private static readonly ShapeCheck Battle = Object(Fields(
        ("statusAttackBuffs", Optional(Object(StatusAttackBuffFields))),
        ("feedback", Feedback),
        ("classes", ArrayOf(BattleClass)),
        ("basicAttacks", Record(BasicAttackShape)),
        ("skills", Record(Skill)),
        ("enemies", ArrayOf(Enemy)),
        ("attackEffects", Record(AttackEffect))));

    public static void ValidateBattle(JsonNode? root)
    {
        Battle(new ShapeValue(root), "battle");
        if (root is not JsonObject battle) return;

        CheckSkillDisplays(battle);

        if (battle["enemies"] is JsonArray enemies)
        {
            foreach (var enemy in enemies)
            {
                if (enemy is not JsonObject definition || definition["level"] is not { } level || level is null) continue;
                TryDouble(level, out var value);
                Require(value <= MaxEnemyLevel, $"{Str(definition["id"])}.level", $"怪物等级上限为 {MaxEnemyLevel}");
            }

            Unique(enemies.Select(enemy => Str(enemy?["id"]) ?? string.Empty), "enemies");
        }

        if (battle["classes"] is JsonArray classes)
            Unique(classes.Select(character => Str(character?["id"]) ?? string.Empty), "classes");

        CheckFeedback(battle);
    }

    /// <summary>
    /// 技能展示：作者态写 <c>details</c>，构建产物写 <c>display</c> + 每个等级的
    /// <c>displayValues</c>，两者必须二选一。模板少了 <c>{v}</c> 就什么都不显示，
    /// 数值行数与模板行数对不上则高等级技能右边一片空白。
    /// </summary>
    private static void CheckSkillDisplays(JsonObject battle)
    {
        if (battle["skills"] is not JsonObject skills) return;
        foreach (var (id, node) in skills)
        {
            if (node is not JsonObject skill) continue;
            CheckDisplay(id, skill);
            if (skill["companion"] is JsonObject companion) CheckDisplay($"{id}.companion", companion);
        }
    }

    private static void CheckDisplay(string id, JsonObject profile)
    {
        if (profile["display"] is JsonArray display)
        {
            Require(display.Count > 0, id, "技能展示模板不能为空");
            foreach (var row in display)
                Require(Str(row?["template"])?.Contains("{v}", StringComparison.Ordinal) == true,
                    id, "技能展示模板缺少 {v}");
            if (profile["ranks"] is not JsonArray ranks) return;
            foreach (var rank in ranks)
                Require(rank?["displayValues"] is JsonArray values && values.Count == display.Count,
                    id, "技能展示数值必须覆盖全部模板行");
            return;
        }

        if (profile["ranks"] is not JsonArray authorRanks) return;
        foreach (var rank in authorRanks)
            Require(rank?["details"] is JsonArray { Count: > 0 }, id, "技能缺少 details 或 display");
    }

    private static void CheckFeedback(JsonObject battle)
    {
        if (battle["attackEffects"] is not JsonObject effects) return;
        if (battle["feedback"] is not JsonObject feedback) return;

        foreach (var name in OutcomeEffects) RequireEffect(effects, name, "default");

        var digitStyles = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["enemy"] = [.. Enumerable.Range(0, 10).Select(index => index.ToString())],
            ["enemyCritical"] = [.. Enumerable.Range(0, 10).Select(index => index.ToString())],
            ["party"] = [.. Enumerable.Range(0, 10).Select(index => index.ToString())],
            ["partyCritical"] = [.. Enumerable.Range(0, 10).Select(index => index.ToString())],
            ["miss"] = ["10"],
        };

        if (feedback["digits"] is JsonObject digits)
            foreach (var (style, variants) in digitStyles)
            {
                Require(digits[style] is JsonArray { Count: > 0 }, $"feedback.digits.{style}", "数字动画不能为空");
                if (digits[style] is not JsonArray names) continue;
                foreach (var node in names)
                {
                    var name = Str(node);
                    if (string.IsNullOrEmpty(name)) continue;
                    Require(effects[name] is not null, $"feedback.{name}", "反馈素材缺失");
                    foreach (var variant in variants) RequireEffect(effects, name, variant);
                }
            }

        var scalars = new List<string?>
        {
            Str(feedback["skillImpact"]), Str(feedback["criticalImpact"]),
            Str(feedback["criticalRing"]), Str(feedback["missEffect"]),
        };
        scalars.AddRange(StringsOf(feedback["normalImpacts"]));
        scalars.AddRange(StringsOf(feedback["criticalBursts"]));
        foreach (var name in scalars)
        {
            if (string.IsNullOrEmpty(name)) continue;
            RequireEffect(effects, name, "default");
            Require(effects[name] is not null, $"feedback.{name}", "反馈素材缺失");
        }

        Require(feedback["normalImpacts"] is JsonArray { Count: > 0 } &&
                feedback["criticalBursts"] is JsonArray { Count: > 0 } &&
                feedback["shakeOffsets"] is JsonArray { Count: 6 },
            "feedback", "命中特效或六步震屏缺失");
    }

    private static IEnumerable<string?> StringsOf(JsonNode? node) =>
        node is JsonArray array ? array.Select(Str) : [];

    private static void RequireEffect(JsonObject effects, string name, string variant)
    {
        Require(effects[name]?["clips"] is JsonObject clips && clips.ContainsKey(variant),
            $"effects.{name}.{variant}", "特效变体缺失");
    }

    private static ShapeCheck BuildRgbColor() => (value, path) =>
    {
        Text(value, path);
        var text = Str(value.Node)!;
        Require(text.Length == 7 && text[0] == '#' &&
                text.AsSpan(1).ToString().All(character =>
                    character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F'),
            path, "需要 RGB 颜色");
    };

    private static Dictionary<string, ShapeCheck> BuildBasicAttackFields()
    {
        ShapeCheck damage = (value, path) =>
        {
            BattleActionShape.CombatUnit(value, path);
            var kind = Str((value.Node as JsonObject)?["effect"]);
            Require(kind is "physical" or "magic", $"{path}.effect", "普攻需要物理或魔法伤害");
            Require((value.Node as JsonObject)?["ranks"] is JsonArray { Count: 1 }, $"{path}.ranks", "普攻需要一份等级公式");
        };

        return Fields(
            ("damage", damage),
            ("dispatch", Optional(BattleActionShape.ActionDispatch)),
            ("hitEffectStyle", Optional(OneOf("weapon", "skill"))),
            ("manaGemDrop", Optional(OneOf("skill", "weapon"))),
            ("timing", Optional(OneOf("fixed-animation"))),
            ("targeting", Optional(Object(Fields(
                ("target", OneOf("area")),
                ("targetRangePx", Positive),
                ("targetRangeMetric", OneOf("octagonal-grid")))))),
            ("frameDurationMs", Positive),
            ("cycleFrames", Count),
            ("approachDistanceUnits", Number),
            ("searchRadiusPx", Positive),
            ("strikeFrame", Integer),
            ("strikes", Optional(ArrayOf(Object(Fields(
                ("frame", Integer), ("hand", OneOf("main-hand", "offhand"))))))),
            ("swingFrame", Nullable(Integer)),
            ("swingOrder", Optional(OneOf("after-strike"))),
            ("projectilePlacement", Optional(OneOf("world", "above-characters"))),
            ("swingPlacement", Optional(OneOf("world", "above-characters"))),
            ("impactPlacement", Optional(OneOf("world", "attached"))),
            ("swings", ArrayOf(Text)),
            ("impacts", ArrayOf(Text)),
            ("targetEffects", Optional(ArrayOf(BattleActionShape.VisualEffect))),
            ("transition", Optional(BattleActionShape.Transition)),
            ("projectile", Nullable(Object(Fields(
                ("effect", Text),
                ("stepDistancePx", Positive),
                ("stepDurationMs", Positive),
                ("offsetX", Number),
                ("offsetY", Number))))));
    }

    private static void CheckBasicAttack(JsonObject attack, string path)
    {
        if (Str(attack["timing"]) == "fixed-animation")
        {
            TryDouble(attack["strikeFrame"], out var strikeFrame);
            TryDouble(attack["cycleFrames"], out var cycleFrames);
            Require(strikeFrame < cycleFrames, $"{path}.strikeFrame", "固定动画需要包含出手帧");

            if (attack["swingFrame"] is not null)
            {
                TryDouble(attack["swingFrame"], out var swingFrame);
                Require(swingFrame < cycleFrames, $"{path}.swingFrame", "固定动画需要包含挥击帧");
            }
        }

        if (attack["targeting"] is not null)
            Require(attack["strikes"] is null && attack["projectile"] is null && attack["transition"] is null,
                $"{path}.targeting", "范围普攻需要一次直接出手");
    }

    private static ShapeCheck BuildEnemySkill()
    {
        var unavailable = Object(Fields(("id", Text), ("name", Text), ("unavailableReason", Text)));
        var available = Object(Fields(
            ("id", Text),
            ("name", Text),
            ("previewNote", Optional(Text)),
            ("releaseCondition", Optional(OneOf("hp-below-30", "hp-below-70", "hp-below-50", "hp-below-95", "always"))),
            ("once", Optional(Flag)),
            ("action", BattleActionShape.SkillAction),
            ("companionAction", Optional(BattleActionShape.SkillAction)),
            ("clips", FacingClips)));

        return (value, path) =>
        {
            Require(value.Present && value.Node is JsonObject, path, "需要怪物技能");
            var check = ((JsonObject)value.Node!).ContainsKey("unavailableReason") ? unavailable : available;
            check(value, path);
        };
    }

    private static Dictionary<string, ShapeCheck> BuildStatusAttackBuffFields()
    {
        var fields = new Dictionary<string, ShapeCheck>(StringComparer.Ordinal);
        foreach (var key in BattleSemantics.BuffKeys) fields[key] = Optional(BattleActionShape.BuffEffect);
        return fields;
    }
}
