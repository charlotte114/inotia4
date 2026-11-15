using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Inotia4.Domain.Combat;
using static Inotia4.Domain.Content.ContentShape;

namespace Inotia4.Domain.Content;

internal static class BattleActionShape
{
    private static readonly string[] PrimaryStats =
        ["strength", "dexterity", "vitality", "intelligence", "mentality"];

    private static readonly string[] DamageSources = ["physicalAttack", "magicAttack", "maxHealth", "bestNormalDamage", "none"];

    private static readonly string[] TargetKinds =
    [
        "self", "current", "area", "target-area", "ally", "ally-heal", "party", "party-ally", "enemy", "none",
    ];

    private static readonly string[] Placements = ["attached", "ground", "above-characters"];

    private static readonly ShapeCheck Strings = ArrayOf(Text);

    private static readonly ShapeCheck Stat = OneOf(BattleSemantics.StatKeys);

    private static readonly ShapeCheck State = OneOf(BattleSemantics.StateKeys);

    private static readonly ShapeCheck Buff = OneOf(BattleSemantics.BuffKeys);

    private static readonly ShapeCheck Target = OneOf(TargetKinds);

    private static readonly ShapeCheck AttributeWeights = BuildAttributeWeights();

    private static void BuffEffectRef(ShapeValue value, string path) => BuffEffect(value, path);

    private static void BuffReactionRef(ShapeValue value, string path) => BuffReaction(value, path);

    internal static readonly ShapeCheck CombatOperation = BuildCombatOperation();

    internal static readonly ShapeCheck VisualEffect = Object(Fields(
        ("effect", Text), ("recipient", OneOf("caster", "target")), ("placement", OneOf(Placements))));

    internal static readonly ShapeCheck Transition = Object(Fields(
        ("directional", Optional(Flag)),
        ("missEffects", Optional(ArrayOf(Object(Fields(
            ("effect", Text), ("recipient", OneOf("caster", "target")), ("placement", OneOf(Placements))))))),
        ("casterStrikes", Optional(Object(Fields(
            ("effect", Text),
            ("offsets", ArrayOf(Object(Fields(("x", OffsetInteger), ("y", OffsetInteger))))))))),
        ("anchor", Optional(OneOf("target", "caster"))),
        ("kind", OneOf("stationary", "trace", "position")),
        ("effect", Nullable(Text)),
        ("actionEffect", Nullable(Text)),
        ("switchFrame", Integer),
        ("stepDistancePx", Number),
        ("offsetX", Number),
        ("offsetY", Number)));

    internal static readonly ShapeCheck ActionDispatch = Object(Fields(
        ("breaksStealth", Flag),
        ("casterEffects", Optional(ArrayOf(Object(Fields(
            ("effect", Text), ("recipient", OneOf("caster")), ("placement", OneOf(Placements))))))),
        ("shake", Optional(Flag)),
        ("vibrationMs", Optional(Count)),
        ("sound", Optional(Text))));

    private static readonly ShapeCheck Charge = Object(Fields(
        ("arrivalFrame", Integer),
        ("closeDistanceUnits", Positive),
        ("lungeDistancePx", Positive),
        ("stoppingDistanceUnits", Positive),
        ("gridSizePx", Positive)));

    private static readonly ShapeCheck Afterimages = Object(Fields(
        ("frame", Integer), ("opacity", Number), ("distancesPx", ArrayOf(Positive))));

    private static readonly ShapeCheck StateFrameDuration = Object(Fields(("lastActiveFrame", Integer)));

    private static readonly ShapeCheck StateDurationProfile = Object(Fields(
        ("normal", StateFrameDuration),
        ("targetShortened", Optional(StateFrameDuration)),
        ("buffShortened", Optional(StateFrameDuration)),
        ("buffAndTargetShortened", Optional(StateFrameDuration))));

    private static readonly ShapeCheck DurationScale = (value, path) =>
    {
        Number(value, path);
        TryDouble(value.Node, out var number);
        Require(number is 1 or 0.5, path, "需要完整或减半的状态持续期");
    };

    private static readonly ShapeCheck Cue = Object(Fields(
        ("target", Target),
        ("targetRangePx", Number),
        ("targetSelection", Optional(OneOf("search", "reuse-previous"))),
        ("unitFeedbackPolicy", Optional(OneOf("always"))),
        ("sound", Optional(Text)),
        ("targetRangeMetric", Optional(OneOf("octagonal-grid"))),
        ("effects", ArrayOf(VisualEffect))));

    internal static readonly ShapeCheck SkillEvent = BuildSkillEvent();

    private static readonly ShapeCheck BuffPeriodic = BuildBuffPeriodic();

    internal static readonly ShapeCheck BuffEffect = BuildBuffEffect();

    internal static readonly ShapeCheck CombatUnit = BuildCombatUnit();

    internal static readonly ShapeCheck SkillAction = Object(Fields(
        ("requiredOffhand", Optional(OneOf("shield"))),
        ("requiresActiveSummon", Optional(Flag)),
        ("hitEffectStyle", Optional(OneOf("weapon", "skill"))),
        ("manaGemDrop", Optional(OneOf("skill", "weapon"))),
        ("combatUnits", Optional(ArrayOf(CombatUnit))),
        ("usesSkillDamageBuff", Optional(Flag)),
        ("repeat", Optional(Object(Fields(
            ("restartAfterFrame", Count),
            ("countByLevel", ArrayOf(Count)),
            ("requiresLivingTarget", Optional(Flag)),
            ("endsWithoutTarget", Optional(Flag)),
            ("facesTargetOnRelease", Optional(Flag)))))),
        ("charge", Optional(Charge)),
        ("rearTeleport", Optional(Object(Fields(("frame", Count))))),
        ("afterimages", Optional(Afterimages)),
        ("requiresTarget", Optional(Flag)),
        ("minHealthRatePerThousand", Optional(Count)),
        ("events", ArrayOf(SkillEvent)),
        ("cooldownFrames", Integer),
        ("approachDistanceUnits", Number),
        ("searchRadiusPx", Positive),
        ("transition", Nullable(Transition)),
        ("effectNames", Optional(Strings)),
        ("soundNames", Optional(Strings)),
        ("summonNames", Optional(Strings)),
        ("eventFrames", Optional(Record(ArrayOf(Integer))))));

    internal static readonly ShapeCheck PassiveUnit = Object(Fields(
        ("attribute", Stat),
        ("condition", OneOf("always", "weapon-two-handed", "weapon-short-or-long-sword", "weapon-mace", "stealthed")),
        ("ranks", ArrayOf(CombatOperation))));

    private static readonly ShapeCheck ReactionBuff = Object(Fields(
        ("key", Buff), ("rankPriority", Count), ("effect", BuffEffectRef)));

    internal static readonly ShapeCheck BuffReaction = BuildBuffReaction();

    internal static readonly ShapeCheck SkillReaction = BuildSkillReaction();

    internal static readonly ShapeCheck StateRules = BuildStateRules();

    private static ShapeCheck BuildAttributeWeights()
    {
        var fields = new Dictionary<string, ShapeCheck>(StringComparer.Ordinal);
        foreach (var stat in PrimaryStats) fields[stat] = OffsetInteger;
        return Object(fields);
    }

    private static ShapeCheck BuildCombatOperation()
    {
        var variants = new Dictionary<string, ShapeCheck>(StringComparer.Ordinal)
        {
            ["keep"] = Object(Fields(("kind", OneOf("keep")))),
            ["multiply"] = Object(Fields(("kind", OneOf("multiply")), ("factor", OffsetInteger))),
            ["divide"] = Object(Fields(("kind", OneOf("divide")), ("divisor", OffsetInteger))),
        };
        foreach (var kind in new[] { "set", "add", "subtract" })
            variants[kind] = Object(Fields(("kind", OneOf(kind)), ("amount", OffsetInteger)));
        foreach (var kind in new[] { "multiply-thousandth", "increase-thousandth", "decrease-thousandth" })
            variants[kind] = Object(Fields(("kind", OneOf(kind)), ("ratePerThousand", OffsetInteger)));
        return Variant("kind", variants);
    }

    private static ShapeCheck BuildSkillEvent()
    {
        return Variant("kind", new Dictionary<string, ShapeCheck>(StringComparer.Ordinal)
        {
            ["feedback"] = Object(Fields(
                ("kind", OneOf("feedback")), ("frame", Integer), ("shake", Optional(Flag)),
                ("vibrationMs", Optional(Count)), ("sound", Optional(Text)))),
            ["cast"] = Object(Fields(("kind", OneOf("cast")), ("frame", Integer), ("effect", VisualEffect))),
            ["units"] = Object(Fields(
                ("kind", OneOf("units")), ("frame", Integer), ("cues", ArrayOf(Cue)),
                ("dispatch", Optional(ActionDispatch)))),
            ["caster"] = Object(Fields(
                ("kind", OneOf("caster")), ("frame", Integer), ("effects", ArrayOf(VisualEffect)))),
            ["swing"] = Object(Fields(
                ("kind", OneOf("swing")), ("frame", Integer), ("effects", ArrayOf(VisualEffect)))),
            ["impact"] = Object(Fields(
                ("kind", OneOf("impact")), ("frame", Integer), ("target", Target), ("effect", VisualEffect))),
            ["spawn"] = Object(Fields(
                ("kind", OneOf("spawn")), ("frame", Integer), ("until", Integer), ("effect", Text),
                ("placement", OneOf(Placements)), ("marginPx", Integer))),
            ["capture-targets"] = Object(Fields(
                ("kind", OneOf("capture-targets")), ("frame", Integer), ("target", Target),
                ("targetRangePx", Number), ("limit", Count))),
            ["captured-effect"] = Object(Fields(
                ("kind", OneOf("captured-effect")), ("frame", Integer), ("effect", Text),
                ("placement", OneOf("ground", "above-characters")))),
            ["summon"] = Object(Fields(
                ("kind", OneOf("summon")), ("frame", Integer), ("dispatch", Optional(ActionDispatch)),
                ("creatures", ArrayOf(Object(Fields(("enemyId", Text))))),
                ("effect", Text),
                ("placement", Object(Fields(
                    ("searchGapPx", Positive),
                    ("searchExtentPx", Positive),
                    ("offsetChoices", ArrayOf(Object(Fields(("x", Number), ("y", Number))))),
                    ("jitterPx", Integer)))))),
            ["screen"] = Object(Fields(
                ("kind", OneOf("screen")), ("frame", Integer), ("fillTransparency", Integer))),
        });
    }

    private static ShapeCheck BuildBuffPeriodic()
    {
        return Variant("kind", new Dictionary<string, ShapeCheck>(StringComparer.Ordinal)
        {
            ["fixed-damage"] = Object(Fields(
                ("kind", OneOf("fixed-damage")), ("amount", OffsetInteger))),
            ["magic-curse"] = Object(Fields(
                ("kind", OneOf("magic-curse")), ("ratePerThousand", OffsetInteger),
                ("healEffect", Optional(Text)))),
            ["normal-damage"] = Object(Fields(
                ("kind", OneOf("normal-damage")), ("operation", CombatOperation))),
            ["attack-damage"] = Object(Fields(
                ("kind", OneOf("attack-damage")), ("source", OneOf("physical", "magic")),
                ("operation", CombatOperation))),
            ["healing"] = Object(Fields(
                ("kind", OneOf("healing")), ("attribute", Stat), ("operation", CombatOperation))),
            ["attribute-damage"] = Object(Fields(
                ("kind", OneOf("attribute-damage")), ("attribute", Stat), ("operation", CombatOperation))),
            ["visual"] = Object(Fields(("kind", OneOf("visual")), ("effect", Text))),
        });
    }

    private static ShapeCheck BuildBuffEffect()
    {
        var onApply = new Dictionary<string, ShapeCheck>(StringComparer.Ordinal)
        {
            ["restore-health-from-max-health"] = Object(Fields(
                ("kind", OneOf("restore-health-from-max-health")), ("operation", CombatOperation))),
            ["add-mana"] = Object(Fields(("kind", OneOf("add-mana")), ("value", Integer))),
        };

        var stateProfiles = new Dictionary<string, ShapeCheck>(StringComparer.Ordinal);
        foreach (var state in BattleSemantics.StateKeys) stateProfiles[state] = Optional(StateDurationProfile);

        var shape = Object(Fields(
            ("durationMs", Integer),
            ("tickMs", Integer),
            ("maxStacks", Optional(Count)),
            ("applicationEffect", Optional(Text)),
            ("invalidatesAttributes", Optional(ArrayOf(Stat))),
            ("onApply", Optional(ArrayOf(Variant("kind", onApply)))),
            ("onAccepted", Optional(ArrayOf(Object(Fields(
                ("kind", OneOf("apply-learned-buff")),
                ("skill", OneOf(BattleSemantics.AcceptedLearnedBuffSkillIds)),
                ("target", OneOf("source", "target"))))))),
            ("loopEffects", Optional(ArrayOf(Object(Fields(("effect", Text), ("firstFrameAge", Integer)))))),
            ("stateDurationProfiles", Optional(Object(stateProfiles))),
            ("reactions", Optional(ArrayOf(BuffReactionRef))),
            ("simulationFrames", Optional(Object(Fields(
                ("lastActiveFrame", Integer),
                ("pulseIntervalFrames", Optional(Count)),
                ("pulseAfterAgeIncrement", Optional(Flag)))))),
            ("appearanceColors", Optional(ArrayOf(Object(Fields(
                ("red", Integer), ("green", Integer), ("blue", Integer)))))),
            ("attributeRamps", Optional(ArrayOf(Object(Fields(
                ("attribute", Stat), ("operation", CombatOperation), ("intervalFrames", Count)))))),
            ("durationRateFromOwner", Optional(Object(Fields(("attribute", Stat), ("baseFrames", Integer))))),
            ("durationFramesFromOwner", Optional(Object(Fields(("attribute", Stat))))),
            ("modifiers", ArrayOf(Object(Fields(("attribute", Stat), ("operation", CombatOperation))))),
            ("states", ArrayOf(State)),
            ("periodic", ArrayOf(BuffPeriodic))));

        return (value, path) =>
        {
            shape(value, path);
            CheckBuffFrames((JsonObject)value.Node!, path);
        };
    }

    /// <summary>
    /// 帧计时的一致性：字段通过之后再查这几条——有逐段属性斜坡、
    /// 按主人比例放大的寿命或独立状态帧龄时，必须声明 <c>simulationFrames</c>；
    /// 声明了就必须与 <c>durationMs</c>／<c>tickMs</c> 对得上。
    /// 写错不会崩，只会「增益早一拍结束」这种查不出来源的现象。
    /// </summary>
    private static void CheckBuffFrames(JsonObject effect, string path)
    {
        var hasFrames = effect["simulationFrames"] is JsonObject;

        if (effect["attributeRamps"] is JsonArray { Count: > 0 })
            Require(hasFrames, $"{path}.simulationFrames", "逐段属性斜坡需要母Buff帧计时");

        if (effect["durationRateFromOwner"] is not null)
            Require(hasFrames, $"{path}.simulationFrames", "按主人比例放大的寿命需要母Buff帧计时");

        if (effect["stateDurationProfiles"] is JsonObject profiles)
        {
            Require(hasFrames, $"{path}.simulationFrames", "独立状态帧龄需要母Buff帧计时");
            var states = (effect["states"] as JsonArray)?.Select(node => Str(node)).OfType<string>().ToArray() ?? [];
            foreach (var state in states)
                Require(profiles.ContainsKey(state), $"{path}.stateDurationProfiles.{state}", "需要声明状态的寿命方案");
            foreach (var (state, _) in profiles)
                Require(states.Contains(state), $"{path}.stateDurationProfiles.{state}", "寿命方案需要对应声明状态");
        }

        if (effect["simulationFrames"] is not JsonObject frames) return;

        TryDouble(frames["lastActiveFrame"], out var lastActiveFrame);
        Require(TryDouble(effect["durationMs"], out var duration) &&
                duration == (lastActiveFrame + 1) * CombatClock.LogicFrameMs,
            $"{path}.durationMs", "需要首个逻辑处理到到期之间的时长");

        var interval = frames["pulseIntervalFrames"] is { } pulseNode && TryDouble(pulseNode, out var pulses)
            ? pulses
            : 0;
        Require(TryDouble(effect["tickMs"], out var tick) && tick == interval * CombatClock.LogicFrameMs,
            $"{path}.tickMs", "需要与逻辑脉冲间隔一致");

        var pulseAfterAge = frames["pulseAfterAgeIncrement"] is JsonValue flag &&
                            flag.GetValueKind() == JsonValueKind.True;
        if (pulseAfterAge)
            Require(frames["pulseIntervalFrames"] is not null,
                $"{path}.simulationFrames.pulseAfterAgeIncrement", "计龄后脉冲需要逻辑脉冲间隔");

        var periodic = effect["periodic"] is JsonArray array ? array.Count : 0;
        Require(periodic == 0 || frames["pulseIntervalFrames"] is not null,
            $"{path}.simulationFrames.pulseIntervalFrames", "周期效果需要逻辑脉冲间隔");
    }

    private static ShapeCheck BuildCombatUnit()
    {
        var damageRanks = ArrayOf(Object(Fields(
            ("fixedAddition", OffsetInteger), ("operation", CombatOperation))));

        var variants = new Dictionary<string, ShapeCheck>(StringComparer.Ordinal);
        foreach (var effect in new[] { "physical", "magic", "heal", "drain" })
            variants[effect] = Object(Fields(
                ("effect", OneOf(effect)),
                ("source", OneOf(DamageSources)),
                ("attributeWeights", AttributeWeights),
                ("ranks", damageRanks),
                ("ownerValues", Optional(Flag))));

        variants["self-damage"] = Object(Fields(
            ("effect", OneOf("self-damage")),
            ("source", OneOf(DamageSources)),
            ("attributeWeights", AttributeWeights),
            ("ranks", damageRanks),
            ("alsoAppliesLearnedBuff", Optional(OneOf(BattleSemantics.LearnedReactionSkillIds)))));

        variants["state"] = Object(Fields(
            ("effect", OneOf("state")),
            ("state", State),
            ("ranks", ArrayOf(Object(Fields(
                ("chanceThreshold", Integer),
                ("durationMs", Integer),
                ("stateDurationProfile", Optional(StateDurationProfile))))))));

        variants["buff"] = Object(Fields(
            ("effect", OneOf("buff")),
            ("buff", Buff),
            ("ranks", ArrayOf(Object(Fields(
                ("strength", OffsetInteger),
                ("chanceThreshold", Integer),
                ("effect", BuffEffect)))))));

        variants["summon"] = Object(Fields(
            ("effect", OneOf("summon")),
            ("creature", Text),
            ("ranks", ArrayOf(Object(Fields(
                ("durationMs", Count),
                ("simulationFrames", Optional(Object(Fields(("lastActiveFrame", Integer))))),
                ("attackRatePerThousand", Optional(Integer)),
                ("healthRatePerThousand", Optional(Integer)),
                ("tauntChanceThreshold", Optional(Integer)))))),
            ("lifetimeFromOwner", Optional(Object(Fields(
                ("attribute", Stat), ("perPoint", Integer), ("base", Integer))))),
            ("ownerDerived", Optional(Object(Fields(
                ("attack", Object(Fields(
                    ("attribute", OneOf("physicalAttack", "magicAttack")),
                    ("perLevel", Integer), ("flat", Integer)))),
                ("health", Object(Fields(("perLevel", Integer), ("flat", Integer)))),
                ("physicalResistanceBonus", Integer),
                ("magicResistance", Integer))))),
            ("appearanceEffects", Optional(ArrayOf(VisualEffect)))));

        variants["set-attribute"] = Object(Fields(
            ("effect", OneOf("set-attribute")),
            ("attribute", Stat),
            ("ranks", ArrayOf(CombatOperation))));

        variants["clear-attribute"] = Object(Fields(
            ("effect", OneOf("clear-attribute")), ("attribute", Stat)));

        foreach (var effect in new[] { "action", "taunt", "visual" })
            variants[effect] = Object(Fields(("effect", OneOf(effect))));

        return Variant("effect", variants);
    }

    private static ShapeCheck BuildBuffReaction()
    {
        var variants = new Dictionary<string, ShapeCheck>(StringComparer.Ordinal);
        foreach (var kind in new[] { "physical-avoidance", "physical-success-party", "weapon-block-counter" })
            variants[kind] = Object(Fields(("kind", OneOf(kind)), ("buff", ReactionBuff)));

        variants["mana-on-physical-success"] = Object(Fields(
            ("kind", OneOf("mana-on-physical-success")), ("amount", Positive), ("effect", OneOf("mana-recovery"))));
        variants["weapon-block-stun"] = Object(Fields(
            ("kind", OneOf("weapon-block-stun")), ("state", State), ("durationAttribute", Stat)));
        variants["physical-success-threat"] = Object(Fields(("kind", OneOf("physical-success-threat"))));
        return Variant("kind", variants);
    }

    private static ShapeCheck BuildSkillReaction()
    {
        var variants = new Dictionary<string, ShapeCheck>(StringComparer.Ordinal);

        variants["flare"] = Object(Fields(
            ("kind", OneOf("flare")),
            ("chanceThreshold", Integer),
            ("offhandChanceThreshold", Integer),
            ("stealthFromLearnedSkill", OneOf("whist")),
            ("effect", OneOf("smoke-puff")),
            ("unlearnedStealth", Object(Fields(
                ("key", OneOf("whist")), ("rankPriority", Integer), ("effect", BuffEffect))))));

        variants["soul-sword"] = Object(Fields(
            ("kind", OneOf("soul-sword")),
            ("chanceThreshold", Integer),
            ("buffFromLearnedSkill", OneOf("dark-strike")),
            ("physicalEffects", Strings),
            ("magicEffects", Strings)));

        variants["blood-sword"] = Object(Fields(
            ("kind", OneOf("blood-sword")), ("lifeDrainPerThousand", Integer)));

        variants["blood-thirst"] = Object(Fields(
            ("kind", OneOf("blood-thirst")), ("lifeEffect", OneOf("life-drain")),
            ("manaEffect", OneOf("mana-recovery"))));

        foreach (var (kind, key) in new[]
                 {
                     ("shield-of-rage", "shield-of-rage"), ("shield-of-trust", "shield-of-trust"),
                     ("blood-revenge", "blood-revenge"), ("bard-of-destruction", "bard-of-destruction"),
                     ("deep-breath", "deep-breath"), ("sniping", "sniping"),
                     ("magic-insight", "magic-insight"), ("dark-sense", "dark-sense"),
                     ("armor-penetration", "armor-break"), ("revenge-incarnate", "revenge-incarnate"),
                 })
            variants[kind] = Object(Fields(
                ("kind", OneOf(kind)),
                ("buff", Object(Fields(("key", OneOf(key)), ("rankPriority", Count), ("effect", BuffEffect))))));

        variants["ordination-prayer"] = Object(Fields(
            ("kind", OneOf("ordination-prayer")),
            ("chanceThreshold", Integer),
            ("buff", Object(Fields(
                ("key", OneOf("ordination-prayer")), ("rankPriority", Count), ("effect", BuffEffect))))));

        foreach (var kind in new[] { "disturbance", "lightning-of-hell", "god-of-justice", "clever-fellow" })
            variants[kind] = Object(Fields(("kind", OneOf(kind)), ("chanceThreshold", Integer)));

        variants["owner-mana-restore"] = Object(Fields(
            ("kind", OneOf("owner-mana-restore")), ("amount", Integer), ("effect", OneOf("mana-recovery"))));

        foreach (var kind in new[] { "flares-of-pain", "wrath-of-darkness" })
            variants[kind] = Object(Fields(
                ("kind", OneOf(kind)), ("chanceThreshold", Integer), ("buff", ReactionBuff)));

        return Variant("kind", variants);
    }

    private static ShapeCheck BuildStateRules()
    {
        var stateScales = new Dictionary<string, ShapeCheck>(StringComparer.Ordinal);
        foreach (var state in BattleSemantics.StateKeys) stateScales[state] = Optional(DurationScale);

        return Object(Fields(
            ("immuneToAllStates", Optional(Flag)),
            ("immuneStates", Optional(ArrayOf(State))),
            ("durationScale", Optional(DurationScale)),
            ("buffDurationScaleByState", Optional(Object(stateScales))),
            ("frameDurationScale", Optional(DurationScale))));
    }
}
