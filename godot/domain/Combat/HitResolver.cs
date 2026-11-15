namespace Inotia4.Domain.Combat;

public enum HitKind
{
    Miss,
    ShieldBlock,
    WeaponBlock,
    Hit,
    Critical,
}

public readonly record struct HitResult(HitKind Kind, int Damage)
{
    public bool Landed => Kind is HitKind.Hit or HitKind.Critical;
}

public static class HitResolver
{
    private static int Roll1000(Func<double> random) =>
        Math.Clamp((int)Math.Floor(random() * 1000), 0, 999);

    private static int Div(int a, int b) => (int)Math.Truncate(a / (double)b);

    public static HitResult RollPhysical(
        Fighter attacker, Fighter target, Func<double> random,
        int? baseDamage = null, int extraDamageRate = 0, int hitRateAdjustment = 0,
        Func<int>? skillPhysicalAttack = null)
    {
        var hitRate = Div(attacker.HitBase * 1000 + 20000, attacker.HitBase + target.EvasionBase + 25)
                      + Math.Max(0, attacker.HitBonus) + 100;
        var missChance = Math.Max(0, 1000 - hitRate + Math.Min(750, target.EvasionBonus) - hitRateAdjustment);

        var roll = Roll1000(random);
        if (roll < missChance) return new HitResult(HitKind.Miss, 0);

        var difference = attacker.Level - target.Level;
        var levelAdvantage = Math.Sign(difference) *
                             Div(Math.Abs(difference) * 1000, 100 + Math.Abs(difference));

        var kind = HitKind.Hit;
        var threshold = missChance;

        var shield = target.ShieldBlock > 0 ? Math.Max(0, target.ShieldBlock - levelAdvantage) : 0;
        var weapon = target.WeaponBlock > 0 ? Math.Max(0, target.WeaponBlock - levelAdvantage) : 0;

        threshold += shield;
        if (roll < threshold)
        {
            kind = HitKind.ShieldBlock;
        }
        else
        {
            threshold += weapon;
            if (roll < threshold) kind = HitKind.WeaponBlock;
            else if (attacker.CriticalRate + levelAdvantage - target.CriticalResistance > 0 &&
                     roll < threshold + attacker.CriticalRate + levelAdvantage - target.CriticalResistance)
                kind = HitKind.Critical;
        }

        var stealthRate = attacker.StealthAttackEnabled ? attacker.StealthDamageRate : 0;
        var damage = Div((baseDamage ?? attacker.PhysicalAttack) *
                         (1000 + attacker.PhysicalDamageRate + stealthRate + extraDamageRate), 1000);

        if (attacker.StealthAttackEnabled && skillPhysicalAttack is not null)
            damage += Div(skillPhysicalAttack() * (1000 + stealthRate), 1000);

        if (kind == HitKind.WeaponBlock) damage = Div(damage * 700, 1000);
        damage = Div(damage * (1000 - target.PhysicalResistance), 1000);
        if (kind == HitKind.ShieldBlock) damage = ShieldDamage(damage, target);
        if (kind == HitKind.Critical) damage = CriticalDamage(damage, attacker, target);

        return new HitResult(kind, Math.Max(0, damage));
    }

    public static HitResult RollMagic(
        Fighter attacker, Fighter target, Func<double> random, int baseDamage, int extraDamageRate = 0)
    {
        var hitRate = Div(attacker.HitBase * 1000 + 20000, attacker.HitBase + target.EvasionBase + 25)
                      + Math.Max(0, attacker.HitBonus) + 100;
        var missChance = Math.Max(0, 1000 - hitRate + Math.Min(750, target.EvasionBonus));

        var roll = Roll1000(random);
        if (roll < missChance) return new HitResult(HitKind.Miss, 0);

        var difference = attacker.Level - target.Level;
        var advantage = 10 * Div(Math.Abs(difference), 100 + Math.Abs(difference));

        var kind = HitKind.Hit;
        var shield = target.ShieldBlock > 0 ? Math.Max(0, target.ShieldBlock - advantage) : 0;

        if (roll < missChance + shield) kind = HitKind.ShieldBlock;
        else if (attacker.CriticalRate + advantage - target.CriticalResistance > 0 &&
                 roll < missChance + shield + attacker.CriticalRate + advantage - target.CriticalResistance)
            kind = HitKind.Critical;

        var damage = Div(baseDamage * (1000 + attacker.MagicDamageRate + extraDamageRate), 1000);
        damage = Div(damage * (1000 - target.MagicResistance), 1000);
        if (kind == HitKind.ShieldBlock) damage = ShieldDamage(damage, target);
        if (kind == HitKind.Critical) damage = CriticalDamage(damage, attacker, target);

        return new HitResult(kind, Math.Max(0, damage));
    }

    private static int CriticalDamage(int damage, Fighter attacker, Fighter target) =>
        Div(Div(damage * (1000 + attacker.CriticalDamage), 1000) * (1000 - target.CriticalDamageResistance), 1000);

    private static int ShieldDamage(int damage, Fighter target)
    {
        damage -= Div(target.ShieldBlock, target.ShieldBlock + target.Level * 20 + 60);

        var defense = Div(target.ShieldBlock * 2 * (1000 + target.DefenseRate), 1000);
        var reduction = Div(1000 * defense, defense + 20 * (target.Level + 3));
        var product = damage * (1000 - reduction);

        return product >= -999 ? Div(product, 1000) : 1;
    }
}
