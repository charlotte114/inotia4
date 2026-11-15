using System.Text.Json;
using System.Text.Json.Serialization;
using Inotia4.Domain.Combat;

namespace Inotia4.Domain.Content;

public sealed class BattleContent
{
    [JsonPropertyName("enemies")] public EnemyDefinition[] Enemies { get; init; } = Array.Empty<EnemyDefinition>();

    [JsonPropertyName("basicAttacks")]
    public Dictionary<string, BasicAttackDefinition> BasicAttacks { get; init; } = new();

    [JsonPropertyName("classes")] public BattleClassDefinition[] Classes { get; init; } = Array.Empty<BattleClassDefinition>();

    [JsonPropertyName("attackEffects")]
    public Dictionary<string, EffectDirections> AttackEffects { get; init; } = new();

    [JsonPropertyName("feedback")] public CombatFeedbackDefinition? Feedback { get; init; }

    [JsonPropertyName("skills")] public Dictionary<string, SkillDefinition> Skills { get; init; } = new();

    [JsonPropertyName("statusAttackBuffs")]
    public Dictionary<string, BattleBuffEffect>? StatusAttackBuffs { get; init; }

    public EffectClip? EffectClip(string effect, string facing)
    {
        if (!AttackEffects.TryGetValue(effect, out var directions)) return null;
        if (directions.Clips.TryGetValue(facing, out var clip)) return clip;
        return directions.Clips.GetValueOrDefault("south");
    }
}

public sealed class EffectDirections
{
    [JsonPropertyName("clips")] public Dictionary<string, EffectClip> Clips { get; init; } = new();

    [JsonPropertyName("attachmentPoint")] public string? AttachmentPoint { get; init; }
}

public class BasicAttackDefinition
{
    [JsonPropertyName("damage")] public NumericUnit Damage { get; init; } = new();

    [JsonPropertyName("dispatch")] public ActionDispatchDefinition? Dispatch { get; init; }

    [JsonPropertyName("hitEffectStyle")] public string? HitEffectStyle { get; init; }

    [JsonPropertyName("manaGemDrop")] public string? ManaGemDrop { get; init; }

    [JsonPropertyName("timing")] public string? Timing { get; init; }

    [JsonPropertyName("frameDurationMs")] public int FrameDurationMs { get; init; } = CombatClock.LogicFrameMs;

    [JsonPropertyName("cycleFrames")] public int CycleFrames { get; init; }

    [JsonPropertyName("approachDistanceUnits")] public int ApproachDistanceUnits { get; init; }

    [JsonPropertyName("searchRadiusPx")] public float SearchRadiusPx { get; init; }

    [JsonPropertyName("strikeFrame")] public int StrikeFrame { get; init; }

    [JsonPropertyName("strikes")] public BasicAttackStrike[]? Strikes { get; init; }

    [JsonPropertyName("swingFrame")] public int? SwingFrame { get; init; }

    [JsonPropertyName("swingOrder")] public string? SwingOrder { get; init; }

    [JsonPropertyName("projectilePlacement")] public string? ProjectilePlacement { get; init; }

    [JsonPropertyName("swingPlacement")] public string? SwingPlacement { get; init; }

    [JsonPropertyName("impactPlacement")] public string? ImpactPlacement { get; init; }

    [JsonPropertyName("targeting")] public BasicAttackTargeting? Targeting { get; init; }

    [JsonPropertyName("swings")] public string[] Swings { get; init; } = [];

    [JsonPropertyName("impacts")] public string[] Impacts { get; init; } = [];

    [JsonPropertyName("targetEffects")] public List<SkillVisualEffect>? TargetEffects { get; init; }

    [JsonPropertyName("transition")] public SkillTransition? Transition { get; init; }

    [JsonPropertyName("projectile")] public BasicAttackProjectile? Projectile { get; init; }

    public bool SwingAfterStrike => SwingOrder == "after-strike";
}

public sealed class BasicAttackProjectile
{
    [JsonPropertyName("effect")] public string Effect { get; init; } = string.Empty;

    [JsonPropertyName("stepDistancePx")] public float StepDistancePx { get; init; }

    [JsonPropertyName("stepDurationMs")] public int StepDurationMs { get; init; }

    [JsonPropertyName("offsetX")] public float OffsetX { get; init; }

    [JsonPropertyName("offsetY")] public float OffsetY { get; init; }
}

public sealed class BasicAttackTargeting
{
    [JsonPropertyName("target")] public string Target { get; init; } = "area";

    [JsonPropertyName("targetRangePx")] public float TargetRangePx { get; init; }

    [JsonPropertyName("targetRangeMetric")] public string TargetRangeMetric { get; init; } = string.Empty;
}

public sealed class EnemyDefinition
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;

    [JsonPropertyName("level")] public int? Level { get; init; }

    [JsonPropertyName("maxHealth")] public int MaxHealth { get; init; }

    [JsonPropertyName("attributes")] public EnemyAttributes Attributes { get; init; } = new();

    [JsonPropertyName("actor")] public ActorDefinition Actor { get; init; } = new();

    [JsonPropertyName("behavior")] public EnemyBehavior Behavior { get; init; } = new();

    [JsonPropertyName("basicAttack")] public BasicAttackBinding? BasicAttack { get; init; }

    [JsonPropertyName("acceptsBuffs")] public bool? AcceptsBuffs { get; init; }

    [JsonPropertyName("waterImmune")] public bool? WaterImmune { get; init; }

    [JsonPropertyName("defeatPresentation")] public string? DefeatPresentation { get; init; }

    [JsonPropertyName("stateRules")] public StateApplicationRules? StateRules { get; init; }

    [JsonPropertyName("skills")] public List<EnemyPlayableSkill>? Skills { get; init; }

    [JsonPropertyName("commandAction")] public EnemyPlayableSkill? CommandAction { get; init; }

    public Fighter ToFighter(int playerLevel = 1, int partySize = 1, bool elite = false, int? level = null)
    {
        var stats = EnemyStatsCalculator.Calculate(this, new EnemyStatsOptions(
            Level: level, PartySize: partySize, PlayerLevel: playerLevel, Elite: elite));

        // 防御率由 Fighter.Resolved() 统一放大，所以这里给原始防御与防御率，
        // 不能把已经乘过防御率的 EnemyStats.Defense 再填一遍。
        return new Fighter
        {
            Level = stats.Level,
            PhysicalAttack = stats.PhysicalAttack,
            MagicAttack = stats.MagicAttack,
            Defense = Attributes.Defense,
            DefenseRate = Attributes.DefenseRate,
            PhysicalResistanceBonus = Attributes.PhysicalResistanceBonus,
            MagicResistance = stats.MagicResistance,
            MaxHealth = stats.MaxHealth,
            HitBase = stats.HitBase,
            HitBonus = Attributes.HitBonus,
            EvasionBase = stats.EvasionBase,
            EvasionBonus = stats.EvasionBonus,
            CriticalRate = Attributes.CriticalRate,
            CriticalResistance = Attributes.CriticalResistance,
            CriticalDamage = Attributes.CriticalDamage,
            CriticalDamageResistance = Attributes.CriticalDamageResistance,
            WeaponBlock = Attributes.WeaponBlock,
            ShieldBlock = Attributes.ShieldBlock,
        }.Resolved();
    }
}

public sealed class EnemyPlayableSkill
{
    [JsonPropertyName("previewNote")] public string? PreviewNote { get; init; }

    [JsonPropertyName("level")] public int? Level { get; init; }

    [JsonPropertyName("manaCost")] public int? ManaCost { get; init; }

    [JsonPropertyName("frequency")] public int? Frequency { get; init; }

    [JsonPropertyName("releaseCondition")] public string? ReleaseCondition { get; init; }

    [JsonPropertyName("once")] public bool? Once { get; init; }

    [JsonPropertyName("id")] public required string Id { get; init; }

    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;

    [JsonPropertyName("action")] public Combat.SkillAction? Action { get; init; }

    [JsonPropertyName("companionAction")] public Combat.SkillAction? CompanionAction { get; init; }

    [JsonPropertyName("clips")] public Dictionary<string, string>? Clips { get; init; }
}

public sealed class EnemyAttributes
{
    [JsonPropertyName("maxMana")] public int MaxMana { get; init; }

    [JsonPropertyName("physicalAttack")] public int PhysicalAttack { get; init; }

    [JsonPropertyName("magicAttack")] public int MagicAttack { get; init; }

    [JsonPropertyName("defense")] public int Defense { get; init; }

    [JsonPropertyName("defenseRate")] public int DefenseRate { get; init; }

    [JsonPropertyName("physicalResistanceBonus")] public int PhysicalResistanceBonus { get; init; }

    [JsonPropertyName("magicResistance")] public int MagicResistance { get; init; }

    [JsonPropertyName("hitBonus")] public int HitBonus { get; init; }

    [JsonPropertyName("evasionBonus")] public int EvasionBonus { get; init; }

    [JsonPropertyName("criticalRate")] public int CriticalRate { get; init; }

    [JsonPropertyName("criticalResistance")] public int CriticalResistance { get; init; }

    [JsonPropertyName("criticalDamage")] public int CriticalDamage { get; init; }

    [JsonPropertyName("criticalDamageResistance")] public int CriticalDamageResistance { get; init; }

    [JsonPropertyName("weaponBlock")] public int WeaponBlock { get; init; }

    [JsonPropertyName("shieldBlock")] public int ShieldBlock { get; init; }
}

public sealed class EnemyBehavior
{
    [JsonPropertyName("aggression")] public string Aggression { get; init; } = "passive";

    [JsonPropertyName("engagement")] public string? Engagement { get; init; }

    [JsonPropertyName("awarenessRangeUnits")] public int AwarenessRangeUnits { get; init; }

    [JsonPropertyName("moveSpeedPxPerSecond")] public float MoveSpeedPxPerSecond { get; init; }

    [JsonPropertyName("canMove")] public bool CanMove { get; init; } = true;

    /// <summary>能不能转身面向目标；为假时出手保持原朝向。</summary>
    [JsonPropertyName("canTurn")] public bool CanTurn { get; init; } = true;

    [JsonPropertyName("wander")] public EnemyWander? Wander { get; init; }
}

public sealed class EnemyWander
{
    [JsonPropertyName("idleDurationMs")] public int IdleDurationMs { get; init; }
}
