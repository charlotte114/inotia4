namespace Inotia4.Domain.Combat;

public sealed class Fighter
{
    public int Level { get; init; } = 1;

    public int PhysicalAttack { get; init; }

    public int MagicAttack { get; init; }

    public int? Defense { get; init; }

    public int DefenseRate { get; init; }

    public int PhysicalResistanceBonus { get; init; }

    public int PhysicalResistance { get; init; }

    public int MagicResistance { get; init; }

    public int MaxHealth { get; init; }

    public int HitBase { get; init; }

    public int HitBonus { get; init; }

    public int EvasionBase { get; init; }

    public int EvasionBonus { get; init; }

    public int CriticalRate { get; init; }

    public int CriticalResistance { get; init; }

    public int CriticalDamage { get; init; }

    public int CriticalDamageResistance { get; init; }

    public int WeaponBlock { get; init; }

    public int ShieldBlock { get; init; }

    public int PhysicalDamageRate { get; init; }

    public int MagicDamageRate { get; init; }

    public int ConditionalMagicDamageRate { get; init; }

    public bool StealthAttackEnabled { get; init; }

    public int StealthDamageRate { get; init; }

    public Fighter Resolved()
    {
        var defense = Math.Max(0, Defense ?? 0);
        var defenseRate = Math.Max(0, DefenseRate);
        var effective = EffectiveDefense(defense, defenseRate);

        var resistance = Defense is null
            ? PhysicalResistanceBonus
            : (int)Math.Truncate(1000.0 * effective / (effective + 20 * (Level + 3))) + PhysicalResistanceBonus;

        return new Fighter
        {
            Level = Level,
            PhysicalAttack = (int)Math.Truncate(
                PhysicalAttack * (1000.0 + PhysicalDamageRate + (StealthAttackEnabled ? StealthDamageRate : 0)) / 1000),
            MagicAttack = MagicAttack,
            Defense = Defense,
            DefenseRate = defenseRate,
            PhysicalResistanceBonus = PhysicalResistanceBonus,
            PhysicalResistance = resistance,
            MagicResistance = MagicResistanceLimit(MagicResistance),
            MaxHealth = MaxHealth,
            HitBase = HitBase,
            HitBonus = HitBonus,
            EvasionBase = EvasionBase,
            EvasionBonus = EvasionBonus,
            CriticalRate = CriticalRate,
            CriticalResistance = CriticalResistance,
            CriticalDamage = CriticalDamage,
            CriticalDamageResistance = CriticalDamageResistance,
            WeaponBlock = WeaponBlock,
            ShieldBlock = ShieldBlock,
            PhysicalDamageRate = PhysicalDamageRate,
            MagicDamageRate = MagicDamageRate,
            ConditionalMagicDamageRate = ConditionalMagicDamageRate,
            StealthAttackEnabled = StealthAttackEnabled,
            StealthDamageRate = StealthDamageRate,
        };
    }

    public static int EffectiveDefense(int defense, int defenseRate) =>
        (int)Math.Truncate(Math.Max(0, defense) * (1000.0 + Math.Max(0, defenseRate)) / 1000);

    public static int MagicResistanceLimit(int resistance) => Math.Min(750, resistance);
}
