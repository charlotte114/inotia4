namespace Inotia4.Domain.Combat;

/// <summary>
/// 战斗规则与内容共用的那几组概念键：属性、状态、增益。
///
/// 这些键是内容文件与规则代码之间的约定名，写错一个就成了不起作用的空条目，
/// 所以另给一份名单，让内容加载与调试断言能查出来。次序有讲究——它出现在报错信息和面板行里。
/// </summary>
public static class BattleSemantics
{
    public static readonly string[] StatKeys =
    [
        "criticalRate", "criticalResistance", "criticalDamageResistance", "criticalDamage",
        "physicalAttack", "physicalDamageRate", "physicalResistanceBonus", "magicAttack",
        "magicDamageRate", "conditionalMagicDamageRate", "magicResistance", "evasionBonus",
        "evasionBase", "hitBonus", "hitBase", "defense", "defenseRate", "weaponBlock",
        "shieldBlock", "attackSpeedRate", "fireSplashChance", "windStrikeChance", "frostStrikeChance",
        "divineStrikeChance", "darknessStrikeChance", "poisonStrikeChance", "maxHealth",
        "maxMana", "skillDamageBonus", "healingPowerRate", "assassinSwordHitAdjustment",
        "stealthAttackEnabled", "stealthDamageRate",
        "summonDurationBonus", "moveSpeed", "rearPenetrationRate", "bestNormalDamage",
        "lifeFeastRate", "attackRangeBonusUnits", "damageTransferPerThousand", "godOfJusticeChance",
        "healthRecoveryPerPulse", "manaRecoveryPerPulse", "manaCostRate", "manaCostReductionRate",
        "moltenReflectionPerThousand", "weaponBlockCounterStunDuration", "sleepImmunity", "stunImmunity",
        "stateResistance", "threatGenerationRate", "threatReductionRate", "sightBonusUnits",
        "armorBreakChance", "bloodThirstChance", "bloodThirstLifeDrainPerThousand",
        "bloodThirstManaRecovery", "offhandAttackChance", "revengeChance", "cooldownResetChance",
        "frenzyAttackChance", "defenseCooldownResetChance", "deepBreathDurationBonus",
    ];

    public static readonly string[] StateKeys =
        ["sleep", "stun", "immobilized", "stealthed", "ambush", "damage-immune", "taunted"];

    public static readonly string[] BuffKeys =
    [
        "dark-strike", "phantom-clash", "curse-of-screams", "demon-s-shield",
        "guardian-of-darkness", "molten-armor", "blood-curse", "pain-overture",
        "spell-redemption", "summon-bard", "spiritual-blade", "poison-touch",
        "whist", "blade-deflection", "blade-deflection-weaken", "energy-bodhi", "apostles-of-light",
        "light-command", "celestial-armor", "holy-sacrifice", "aimed-shot",
        "ambush", "ambush-recovery", "net-shackles", "spirit-of-nature", "survival-instinct", "sniping",
        "shouts-of-threat", "armor-penetration", "armor-break", "weapon-hitback", "blood-thirst",
        "storm-of-frenzy", "storm-of-frenzy-power", "enrage", "rampage", "attack-stun", "attack-slow",
        "divine-defense-break", "poisoned", "darkness-strike-impact", "monster-landshake-stun",
        "monster-strong-stun", "monster-hammer-stun", "monster-web-fire",
        "monster-haste", "monster-sleep", "monster-enrage",
        "monster-emergency-recovery", "monster-recovery", "monster-companion-attack",
        "monster-companion-blessing", "monster-blazing-armor", "freeze-extreme",
        "fire-offering", "critical-hit-stun", "blunt-hit-stun",
        "shield-of-rage", "shield-of-trust", "blood-revenge", "bard-of-destruction", "deep-breath",
        "ordination-prayer", "magic-insight", "disturbanceThreshold", "flares-of-pain",
        "wrath-of-darkness", "mana-recovery", "dark-sense", "revenge-incarnate",
        "story-trap", "spring-mana-recovery", "spring-health-recovery", "spring-mana-cost",
        "spring-power-up", "spring-frenzy",
    ];

    public static readonly string[] LearnedReactionSkillIds =
        ["bard-of-destruction", "deep-breath", "sniping", "blood-revenge"];

    public static readonly string[] AcceptedLearnedBuffSkillIds =
        ["bard-of-destruction", "deep-breath", "sniping"];

    private static readonly HashSet<string> StatKeySet = [.. StatKeys];

    private static readonly HashSet<string> StateKeySet = [.. StateKeys];

    private static readonly HashSet<string> BuffKeySet = [.. BuffKeys];

    public static bool IsStatKey(string key) => StatKeySet.Contains(key);

    public static bool IsStateKey(string key) => StateKeySet.Contains(key);

    public static bool IsBuffKey(string key) => BuffKeySet.Contains(key);
}
