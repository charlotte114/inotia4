using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Inotia4.Domain.Combat;

public sealed class SkillVisualEffect
{
    [JsonPropertyName("effect")] public required string Effect { get; init; }

    [JsonPropertyName("recipient")] public required string Recipient { get; init; }

    [JsonPropertyName("placement")] public required string Placement { get; init; }
}

public sealed class SkillEffectCue
{
    [JsonPropertyName("target")] public required string Target { get; init; }

    [JsonPropertyName("targetRangePx")] public required float TargetRangePx { get; init; }

    [JsonPropertyName("targetSelection")] public string? TargetSelection { get; init; }

    [JsonPropertyName("unitFeedbackPolicy")] public string? UnitFeedbackPolicy { get; init; }

    [JsonPropertyName("sound")] public string? Sound { get; init; }

    [JsonPropertyName("targetRangeMetric")] public string? TargetRangeMetric { get; init; }

    [JsonPropertyName("effects")] public List<SkillVisualEffect> Effects { get; init; } = [];
}

public sealed class ActionDispatchDefinition
{
    [JsonPropertyName("breaksStealth")] public required bool BreaksStealth { get; init; }

    [JsonPropertyName("casterEffects")] public List<SkillVisualEffect>? CasterEffects { get; init; }

    [JsonPropertyName("shake")] public bool? Shake { get; init; }

    [JsonPropertyName("vibrationMs")] public int? VibrationMs { get; init; }

    [JsonPropertyName("sound")] public string? Sound { get; init; }
}

public abstract class SkillEvent
{
    [JsonPropertyName("frame")] public required int Frame { get; init; }
}

public sealed class FeedbackEvent : SkillEvent
{
    [JsonPropertyName("shake")] public bool? Shake { get; init; }

    [JsonPropertyName("vibrationMs")] public int? VibrationMs { get; init; }

    [JsonPropertyName("sound")] public string? Sound { get; init; }
}

public sealed class CaptureTargetsEvent : SkillEvent
{
    [JsonPropertyName("target")] public required string Target { get; init; }

    [JsonPropertyName("targetRangePx")] public required float TargetRangePx { get; init; }

    [JsonPropertyName("limit")] public required int Limit { get; init; }
}

public sealed class CapturedEffectEvent : SkillEvent
{
    [JsonPropertyName("effect")] public required string Effect { get; init; }

    [JsonPropertyName("placement")] public required string Placement { get; init; }
}

public sealed class SummonEvent : SkillEvent
{
    [JsonPropertyName("creatures")] public List<SummonCreature> Creatures { get; init; } = [];

    [JsonPropertyName("placement")] public required SummonPlacement Placement { get; init; }

    [JsonPropertyName("effect")] public required string Effect { get; init; }

    [JsonPropertyName("dispatch")] public ActionDispatchDefinition? Dispatch { get; init; }
}

public sealed class CastEvent : SkillEvent
{
    [JsonPropertyName("effect")] public required SkillVisualEffect Effect { get; init; }
}

public sealed class UnitsEvent : SkillEvent
{
    [JsonPropertyName("cues")] public List<SkillEffectCue> Cues { get; init; } = [];

    [JsonPropertyName("dispatch")] public ActionDispatchDefinition? Dispatch { get; init; }
}

public sealed class CasterEvent : SkillEvent
{
    [JsonPropertyName("effects")] public List<SkillVisualEffect> Effects { get; init; } = [];
}

public sealed class SwingEvent : SkillEvent
{
    [JsonPropertyName("effects")] public List<SkillVisualEffect> Effects { get; init; } = [];
}

public sealed class ImpactEvent : SkillEvent
{
    [JsonPropertyName("target")] public required string Target { get; init; }

    [JsonPropertyName("effect")] public required SkillVisualEffect Effect { get; init; }
}

public sealed class SpawnEvent : SkillEvent
{
    [JsonPropertyName("until")] public required int Until { get; init; }

    [JsonPropertyName("effect")] public required string Effect { get; init; }

    [JsonPropertyName("placement")] public required string Placement { get; init; }

    [JsonPropertyName("marginPx")] public required float MarginPx { get; init; }
}

public sealed class ScreenEvent : SkillEvent
{
    [JsonPropertyName("fillTransparency")] public required int FillTransparency { get; init; }
}

public sealed class SummonCreature
{
    [JsonPropertyName("enemyId")] public required string EnemyId { get; init; }
}

public sealed class SummonPlacement
{
    [JsonPropertyName("searchGapPx")] public required float SearchGapPx { get; init; }

    [JsonPropertyName("searchExtentPx")] public required float SearchExtentPx { get; init; }

    [JsonPropertyName("offsetChoices")] public List<SummonOffset> OffsetChoices { get; init; } = [];

    [JsonPropertyName("jitterPx")] public required float JitterPx { get; init; }
}

public sealed class SummonOffset
{
    [JsonPropertyName("x")] public required float X { get; init; }

    [JsonPropertyName("y")] public required float Y { get; init; }
}

public sealed class SkillTransition
{
    [JsonPropertyName("directional")] public bool? Directional { get; init; }

    [JsonPropertyName("missEffects")] public List<SkillVisualEffect>? MissEffects { get; init; }

    [JsonPropertyName("casterStrikes")] public CasterStrikes? CasterStrikes { get; init; }

    [JsonPropertyName("anchor")] public string? Anchor { get; init; }

    [JsonPropertyName("kind")] public required string Kind { get; init; }

    [JsonPropertyName("effect")] public string? Effect { get; init; }

    [JsonPropertyName("actionEffect")] public string? ActionEffect { get; init; }

    [JsonPropertyName("switchFrame")] public required int SwitchFrame { get; init; }

    [JsonPropertyName("stepDistancePx")] public required float StepDistancePx { get; init; }

    [JsonPropertyName("offsetX")] public required float OffsetX { get; init; }

    [JsonPropertyName("offsetY")] public required float OffsetY { get; init; }
}

public sealed class CasterStrikes
{
    [JsonPropertyName("effect")] public required string Effect { get; init; }

    [JsonPropertyName("offsets")] public List<SummonOffset> Offsets { get; init; } = [];
}

public sealed class SkillRepeat
{
    [JsonPropertyName("restartAfterFrame")] public required int RestartAfterFrame { get; init; }

    [JsonPropertyName("countByLevel")] public List<int> CountByLevel { get; init; } = [];

    [JsonPropertyName("requiresLivingTarget")] public bool? RequiresLivingTarget { get; init; }

    [JsonPropertyName("endsWithoutTarget")] public bool? EndsWithoutTarget { get; init; }

    [JsonPropertyName("facesTargetOnRelease")] public bool? FacesTargetOnRelease { get; init; }
}

public sealed class SkillCharge
{
    [JsonPropertyName("arrivalFrame")] public required int ArrivalFrame { get; init; }

    [JsonPropertyName("closeDistanceUnits")] public required int CloseDistanceUnits { get; init; }

    [JsonPropertyName("lungeDistancePx")] public required float LungeDistancePx { get; init; }

    [JsonPropertyName("stoppingDistanceUnits")] public required int StoppingDistanceUnits { get; init; }

    [JsonPropertyName("gridSizePx")] public required int GridSizePx { get; init; }
}

public sealed class SkillAfterimages
{
    [JsonPropertyName("frame")] public required int Frame { get; init; }

    [JsonPropertyName("opacity")] public required float Opacity { get; init; }

    [JsonPropertyName("distancesPx")] public List<float> DistancesPx { get; init; } = [];
}

public sealed class RearTeleport
{
    [JsonPropertyName("frame")] public required int Frame { get; init; }
}

public sealed class SkillAction : IApproachProfile
{
    [JsonPropertyName("requiredOffhand")] public string? RequiredOffhand { get; init; }

    [JsonPropertyName("hitEffectStyle")] public string? HitEffectStyle { get; init; }

    [JsonPropertyName("manaGemDrop")] public string? ManaGemDrop { get; init; }

    [JsonPropertyName("combatUnits")] public List<ICombatUnit>? CombatUnits { get; init; }

    [JsonPropertyName("usesSkillDamageBuff")] public bool UsesSkillDamageBuff { get; init; }

    [JsonPropertyName("repeat")] public SkillRepeat? Repeat { get; init; }

    [JsonPropertyName("charge")] public SkillCharge? Charge { get; init; }

    [JsonPropertyName("requiresActiveSummon")] public bool RequiresActiveSummon { get; init; }

    [JsonPropertyName("rearTeleport")] public RearTeleport? RearTeleport { get; init; }

    [JsonPropertyName("afterimages")] public SkillAfterimages? Afterimages { get; init; }

    [JsonPropertyName("requiresTarget")] public bool? RequiresTarget { get; set; }

    /// <summary>起手门槛：当前生命必须严格高于最大生命的这一千分率。</summary>
    [JsonPropertyName("minHealthRatePerThousand")] public int? MinHealthRatePerThousand { get; init; }

    [JsonPropertyName("events")] public List<SkillEvent> Events { get; init; } = [];

    [JsonPropertyName("cooldownFrames")] public required int CooldownFrames { get; init; }

    [JsonPropertyName("approachDistanceUnits")] public required int ApproachDistanceUnits { get; init; }

    [JsonPropertyName("searchRadiusPx")] public required float SearchRadiusPx { get; init; }

    [JsonPropertyName("transition")] public SkillTransition? Transition { get; init; }

    [JsonIgnore] public string[]? EffectNames { get; set; }

    [JsonIgnore] public string[]? SoundNames { get; set; }

    [JsonIgnore] public string[]? SummonNames { get; set; }

    [JsonIgnore] public Dictionary<int, int[]>? EventFrames { get; set; }
}

public interface ICombatUnit
{
    string Effect { get; }
}

public sealed class SimpleCombatUnit : ICombatUnit
{
    [JsonPropertyName("effect")] public required string Effect { get; init; }
}

public sealed class StateCombatUnit : ICombatUnit
{
    [JsonPropertyName("effect")] public required string Effect { get; init; }

    [JsonPropertyName("state")] public required string State { get; init; }

    [JsonPropertyName("ranks")] public List<StateUnitRank> Ranks { get; init; } = [];
}

public sealed class StateUnitRank
{
    [JsonPropertyName("chanceThreshold")] public required int ChanceThreshold { get; init; }

    [JsonPropertyName("durationMs")] public required int DurationMs { get; init; }

    [JsonPropertyName("stateDurationProfile")] public StateDurationProfile? StateDurationProfile { get; init; }
}

public sealed class BuffCombatUnit : ICombatUnit
{
    [JsonPropertyName("effect")] public required string Effect { get; init; }

    [JsonPropertyName("buff")] public required string Buff { get; init; }

    [JsonPropertyName("ranks")] public List<BuffUnitRank> Ranks { get; init; } = [];
}

public sealed class BuffUnitRank
{
    [JsonPropertyName("strength")] public required int Strength { get; init; }

    [JsonPropertyName("chanceThreshold")] public required int ChanceThreshold { get; init; }

    [JsonPropertyName("effect")] public required BattleBuffEffect Effect { get; init; }
}

public sealed class SummonCombatUnit : ICombatUnit
{
    [JsonPropertyName("effect")] public required string Effect { get; init; }

    [JsonPropertyName("creature")] public required string Creature { get; init; }

    [JsonPropertyName("ranks")] public List<SummonUnitRank> Ranks { get; init; } = [];

    /// <summary>召唤物属性按主人等级派生，见 <c>CHAR_GetAttrFromSummonMonster</c>。</summary>
    [JsonPropertyName("ownerDerived")] public SummonOwnerDerivation? OwnerDerived { get; init; }

    /// <summary>寿命 = trunc((主人属性×每点＋基础)/1000)，再按 15/10 换算成帧（见 <c>CHAR_Act_Summon</c>）。</summary>
    [JsonPropertyName("lifetimeFromOwner")] public SummonLifetime? LifetimeFromOwner { get; init; }

    [JsonPropertyName("appearanceEffects")] public List<SkillVisualEffect>? AppearanceEffects { get; init; }
}

public sealed class SummonUnitRank
{
    [JsonPropertyName("durationMs")] public required int DurationMs { get; init; }

    [JsonPropertyName("simulationFrames")] public BuffSimulationFrames? SimulationFrames { get; init; }

    [JsonPropertyName("attackRatePerThousand")] public int? AttackRatePerThousand { get; init; }

    [JsonPropertyName("healthRatePerThousand")] public int? HealthRatePerThousand { get; init; }

    [JsonPropertyName("tauntChanceThreshold")] public int? TauntChanceThreshold { get; init; }
}

public sealed class SummonOwnerDerivation
{
    [JsonPropertyName("attack")] public required SummonOwnerAttack Attack { get; init; }

    [JsonPropertyName("health")] public required SummonOwnerHealth Health { get; init; }

    [JsonPropertyName("physicalResistanceBonus")] public required int PhysicalResistanceBonus { get; init; }

    [JsonPropertyName("magicResistance")] public required int MagicResistance { get; init; }
}

public sealed class SummonOwnerAttack
{
    [JsonPropertyName("attribute")] public required string Attribute { get; init; }

    [JsonPropertyName("perLevel")] public required int PerLevel { get; init; }

    [JsonPropertyName("flat")] public required int Flat { get; init; }
}

public sealed class SummonOwnerHealth
{
    [JsonPropertyName("perLevel")] public required int PerLevel { get; init; }

    [JsonPropertyName("flat")] public required int Flat { get; init; }
}

public sealed class SummonLifetime
{
    [JsonPropertyName("attribute")] public required string Attribute { get; init; }

    [JsonPropertyName("perPoint")] public required int PerPoint { get; init; }

    [JsonPropertyName("base")] public required int Base { get; init; }
}

public sealed class SetAttributeUnit : ICombatUnit
{
    [JsonPropertyName("effect")] public required string Effect { get; init; }

    [JsonPropertyName("attribute")] public required string Attribute { get; init; }

    [JsonPropertyName("ranks")] public List<CombatOperation> Ranks { get; init; } = [];
}

public sealed class ClearAttributeUnit : ICombatUnit
{
    [JsonPropertyName("effect")] public required string Effect { get; init; }

    [JsonPropertyName("attribute")] public required string Attribute { get; init; }
}

public sealed class BattleBuffEffect
{
    [JsonPropertyName("durationMs")] public required int DurationMs { get; init; }

    [JsonPropertyName("tickMs")] public required int TickMs { get; init; }

    [JsonPropertyName("appearanceColors")] public List<BuffColor>? AppearanceColors { get; init; }

    [JsonPropertyName("simulationFrames")] public BuffSimulationFrames? SimulationFrames { get; init; }

    [JsonPropertyName("applicationEffect")] public string? ApplicationEffect { get; init; }

    [JsonPropertyName("onApply")] public List<BuffOnApply>? OnApply { get; init; }

    [JsonPropertyName("onAccepted")] public List<BuffOnAccepted>? OnAccepted { get; init; }

    [JsonPropertyName("loopEffects")] public List<BuffLoopEffect>? LoopEffects { get; init; }

    [JsonPropertyName("reactions")] public List<BuffReaction>? Reactions { get; init; }

    [JsonPropertyName("maxStacks")] public int? MaxStacks { get; init; }

    [JsonPropertyName("modifiers")] public List<AttributeModifier> Modifiers { get; init; } = [];

    [JsonPropertyName("invalidatesAttributes")] public List<string>? InvalidatesAttributes { get; init; }

    [JsonPropertyName("attributeRamps")] public List<BuffAttributeRamp>? AttributeRamps { get; init; }

    [JsonPropertyName("durationRateFromOwner")] public BuffDurationRateFromOwner? DurationRateFromOwner { get; init; }

    [JsonPropertyName("durationFramesFromOwner")] public BuffDurationFramesFromOwner? DurationFramesFromOwner { get; init; }

    [JsonPropertyName("states")] public List<string> States { get; init; } = [];

    [JsonPropertyName("stateDurationProfiles")] public Dictionary<string, StateDurationProfile>? StateDurationProfiles { get; init; }

    [JsonPropertyName("periodic")] public List<BuffPeriodicEffect> Periodic { get; init; } = [];

    /// <summary>
    /// 浅拷贝：列表引用照抄（内容来自解析好的表，当作不可变），
    /// 时长与寿命帧可覆盖——同一份增益按不同等级施放时只换这两个数。
    /// </summary>
    public BattleBuffEffect Copy(int? durationMs = null, BuffSimulationFrames? simulationFrames = null) => new()
    {
        DurationMs = durationMs ?? DurationMs,
        TickMs = TickMs,
        AppearanceColors = AppearanceColors,
        SimulationFrames = simulationFrames ?? SimulationFrames,
        ApplicationEffect = ApplicationEffect,
        OnApply = OnApply,
        OnAccepted = OnAccepted,
        LoopEffects = LoopEffects,
        Reactions = Reactions,
        MaxStacks = MaxStacks,
        Modifiers = Modifiers,
        InvalidatesAttributes = InvalidatesAttributes,
        AttributeRamps = AttributeRamps,
        DurationRateFromOwner = DurationRateFromOwner,
        DurationFramesFromOwner = DurationFramesFromOwner,
        States = States,
        StateDurationProfiles = StateDurationProfiles,
        Periodic = Periodic,
    };
}

public sealed class BuffColor
{
    [JsonPropertyName("red")] public required int Red { get; init; }

    [JsonPropertyName("green")] public required int Green { get; init; }

    [JsonPropertyName("blue")] public required int Blue { get; init; }
}

public sealed class BuffSimulationFrames
{
    [JsonPropertyName("lastActiveFrame")] public required int LastActiveFrame { get; init; }

    [JsonPropertyName("pulseIntervalFrames")] public int? PulseIntervalFrames { get; init; }

    [JsonPropertyName("pulseAfterAgeIncrement")] public bool? PulseAfterAgeIncrement { get; init; }
}

public sealed class StateDurationProfile
{
    [JsonPropertyName("normal")] public required StateDurationBound Normal { get; init; }

    [JsonPropertyName("targetShortened")] public StateDurationBound? TargetShortened { get; init; }

    [JsonPropertyName("buffShortened")] public StateDurationBound? BuffShortened { get; init; }

    [JsonPropertyName("buffAndTargetShortened")] public StateDurationBound? BuffAndTargetShortened { get; init; }
}

public sealed class StateDurationBound
{
    [JsonPropertyName("lastActiveFrame")] public required int LastActiveFrame { get; init; }
}

public sealed class BuffLoopEffect
{
    [JsonPropertyName("effect")] public required string Effect { get; init; }

    [JsonPropertyName("firstFrameAge")] public required int FirstFrameAge { get; init; }
}

public abstract class BuffOnApply
{
    [JsonPropertyName("kind")] public required string Kind { get; init; }
}

public sealed class RestoreHealthFromMaxHealthOnApply : BuffOnApply
{
    [JsonPropertyName("operation")] public required CombatOperation Operation { get; init; }
}

public sealed class AddManaOnApply : BuffOnApply
{
    [JsonPropertyName("value")] public required int Value { get; init; }
}

public sealed class BuffOnAccepted
{
    [JsonPropertyName("kind")] public required string Kind { get; init; }

    [JsonPropertyName("skill")] public required string Skill { get; init; }

    [JsonPropertyName("target")] public required string Target { get; init; }
}

public sealed class BuffAttributeRamp
{
    [JsonPropertyName("attribute")] public required string Attribute { get; init; }

    [JsonPropertyName("operation")] public required CombatOperation Operation { get; init; }

    [JsonPropertyName("intervalFrames")] public required int IntervalFrames { get; init; }
}

public sealed class BuffDurationRateFromOwner
{
    [JsonPropertyName("attribute")] public required string Attribute { get; init; }

    [JsonPropertyName("baseFrames")] public required int BaseFrames { get; init; }
}

public sealed class BuffDurationFramesFromOwner
{
    [JsonPropertyName("attribute")] public required string Attribute { get; init; }
}

public abstract class BuffPeriodicEffect
{
    [JsonPropertyName("kind")] public required string Kind { get; init; }
}

public sealed class FixedDamageTick : BuffPeriodicEffect
{
    [JsonPropertyName("amount")] public required int Amount { get; init; }
}

public sealed class MagicCurseTick : BuffPeriodicEffect
{
    [JsonPropertyName("ratePerThousand")] public required int RatePerThousand { get; init; }

    [JsonPropertyName("healEffect")] public string? HealEffect { get; init; }
}

public sealed class NormalDamageTick : BuffPeriodicEffect
{
    [JsonPropertyName("operation")] public required CombatOperation Operation { get; init; }
}

public sealed class AttackDamageTick : BuffPeriodicEffect
{
    [JsonPropertyName("source")] public required string Source { get; init; }

    [JsonPropertyName("operation")] public required CombatOperation Operation { get; init; }
}

public sealed class HealingTick : BuffPeriodicEffect
{
    [JsonPropertyName("attribute")] public required string Attribute { get; init; }

    [JsonPropertyName("operation")] public required CombatOperation Operation { get; init; }
}

public sealed class AttributeDamageTick : BuffPeriodicEffect
{
    [JsonPropertyName("attribute")] public required string Attribute { get; init; }

    [JsonPropertyName("operation")] public required CombatOperation Operation { get; init; }
}

public sealed class VisualTick : BuffPeriodicEffect
{
    [JsonPropertyName("effect")] public required string Effect { get; init; }
}

public abstract class BuffReaction
{
    [JsonPropertyName("kind")] public required string Kind { get; init; }
}

public interface IBuffReactionWithBuff
{
    BuffReactionBuff Buff { get; }
}

public sealed class PhysicalAvoidanceReaction : BuffReaction, IBuffReactionWithBuff
{
    [JsonPropertyName("buff")] public required BuffReactionBuff Buff { get; init; }
}

public sealed class PhysicalSuccessPartyReaction : BuffReaction, IBuffReactionWithBuff
{
    [JsonPropertyName("buff")] public required BuffReactionBuff Buff { get; init; }
}

public sealed class WeaponBlockCounterReaction : BuffReaction, IBuffReactionWithBuff
{
    [JsonPropertyName("buff")] public required BuffReactionBuff Buff { get; init; }
}

public sealed class ManaOnPhysicalSuccessReaction : BuffReaction
{
    [JsonPropertyName("amount")] public required int Amount { get; init; }

    [JsonPropertyName("effect")] public required string Effect { get; init; }
}

public sealed class WeaponBlockStunReaction : BuffReaction
{
    [JsonPropertyName("state")] public required string State { get; init; }

    [JsonPropertyName("durationAttribute")] public required string DurationAttribute { get; init; }
}

public sealed class PhysicalSuccessThreatReaction : BuffReaction
{
}

public sealed class BuffReactionBuff
{
    [JsonPropertyName("key")] public required string Key { get; init; }

    [JsonPropertyName("rankPriority")] public required int RankPriority { get; init; }

    [JsonPropertyName("effect")] public required BattleBuffEffect Effect { get; init; }
}
