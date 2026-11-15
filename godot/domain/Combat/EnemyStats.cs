using Inotia4.Domain.Content;

namespace Inotia4.Domain.Combat;

public sealed record EnemyStatsOptions(
    int? Level = null,
    int? SceneLevel = null,
    bool? Elite = null,
    int? PartySize = null,
    int? PlayerLevel = null);

public sealed record EnemyStats(
    int MaxMana,
    int PhysicalAttack,
    int MagicAttack,
    int Defense,
    int DefenseRate,
    int PhysicalResistanceBonus,
    int MagicResistance,
    int HitBonus,
    int EvasionBonus,
    int CriticalRate,
    int CriticalResistance,
    int CriticalDamageResistance,
    int CriticalDamage,
    int WeaponBlock,
    int ShieldBlock,
    int Level,
    bool SceneLevel,
    bool Elite,
    int PartySize,
    int MaxHealth,
    int HitBase,
    int EvasionBase,
    int HitRate,
    int EvasionRate,
    int PhysicalResistance,
    int BaseExperience,
    int Experience);

public sealed record EnemySnapshot(
    string Id,
    string Name,
    EnemyStats Stats,
    int Health,
    int Mana)
{
    public int Level => Stats.Level;
    public bool SceneLevel => Stats.SceneLevel;
    public int MaxHealth => Stats.MaxHealth;
    public int MaxMana => Stats.MaxMana;
}

public static class EnemyStatsCalculator
{
    public const int MaxEnemyLevel = 107;

    private const int MaxPlayerLevel = 105;

    public static int PartySize(int value)
    {
        if (value is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(value), "模拟队伍人数需要是 1–3 的整数");
        return value;
    }

    public static EnemyStats Calculate(EnemyDefinition definition, EnemyStatsOptions? options = null)
    {
        options ??= new EnemyStatsOptions();

        var level = EnemyLevel(options.Level ?? definition.Level ?? options.SceneLevel ?? 1);
        var partySize = PartySize(options.PartySize ?? 1);
        var elite = options.Elite ?? false;
        var playerLevel = options.PlayerLevel ?? 1;
        if (playerLevel is < 1 or > MaxPlayerLevel)
            throw new ArgumentOutOfRangeException(nameof(options), "主角等级需要是 1–105 的整数");

        var attributes = definition.Attributes;

        var eliteHealth = elite ? definition.MaxHealth * 360 / 100 : definition.MaxHealth;
        var elitePhysical = elite ? attributes.PhysicalAttack * 120 / 100 : attributes.PhysicalAttack;
        var magicAttack = elite ? attributes.MagicAttack * 120 / 100 : attributes.MagicAttack;

        var partyPercent = 100 + 20 * (partySize - 1);
        var maxHealth = eliteHealth * partyPercent / 100;
        var physicalAttack = elitePhysical * partyPercent / 100;

        var hitBase = level * 4 + 40;
        var evasionBase = level + 10;
        var hitRate = (hitBase * 1000 + 20000) / (hitBase + level + 35) + Math.Max(0, attributes.HitBonus) + 100;

        var evasionBonus = Math.Min(750, attributes.EvasionBonus);
        var evasionRate = Math.Min(750,
            1000 + evasionBonus - (5000 * level + 1000000) / (5 * level + evasionBase + 1200));

        var defense = attributes.Defense * (1000 + attributes.DefenseRate) / 1000;
        var physicalResistance = 1000 * defense / (defense + 20 * (level + 3)) + attributes.PhysicalResistanceBonus;

        var baseExperience = attributes.PhysicalAttack * 10 + definition.MaxHealth;
        var partyExperience = baseExperience * (100 - 20 * (partySize - 1)) / 100;
        var experience = partyExperience < 1
            ? 0
            : partyExperience * (10 + Math.Clamp(level - playerLevel, -8, 5)) / 10;

        return new EnemyStats(
            attributes.MaxMana, physicalAttack, magicAttack, defense, attributes.DefenseRate,
            attributes.PhysicalResistanceBonus, Math.Min(750, attributes.MagicResistance), attributes.HitBonus,
            evasionBonus, attributes.CriticalRate, attributes.CriticalResistance,
            attributes.CriticalDamageResistance, attributes.CriticalDamage, attributes.WeaponBlock,
            attributes.ShieldBlock,
            level,
            options.Level is null && definition.Level is null,
            elite, partySize,
            maxHealth, hitBase, evasionBase, hitRate, evasionRate, physicalResistance,
            baseExperience, experience);
    }

    private static int EnemyLevel(int value)
    {
        if (value is < 1 or > MaxEnemyLevel)
            throw new ArgumentOutOfRangeException(nameof(value), $"怪物等级需要是 1–{MaxEnemyLevel} 的整数");
        return value;
    }
}
