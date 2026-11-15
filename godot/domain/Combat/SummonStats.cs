using System;
using System.Text.Json.Serialization;

namespace Inotia4.Domain.Combat;

public sealed record SummonStats
{
    public int MaxHealth { get; init; }

    public int? PhysicalAttack { get; init; }

    public int? MagicAttack { get; init; }

    public int PhysicalResistanceBonus { get; init; }

    public int MagicResistance { get; init; }

    public PrimaryAttributes? PrimaryAttributes { get; init; }
}

/// <summary>
/// <c>CHAR_GetAttrFromSummonMonster</c>（原版符号，见反汇编符号表）：召唤物攻击取
/// 「每级×主人等级＋基础」再乘本等级倍率，生命上限取「每级×主人等级＋基础」，
/// 两项抗性直接取固定值。倍率本身不含 +1000。
/// </summary>
public static class SummonStatsRules
{
    public static SummonStats OwnerStats(SummonOwnerDerivation derivation,
        (int? AttackRatePerThousand, int? HealthRatePerThousand) rank, int ownerLevel,
        PrimaryAttributes? ownerAttributes)
    {
        var attack = (int)Math.Truncate(
            (derivation.Attack.PerLevel * ownerLevel + derivation.Attack.Flat) *
            (rank.AttackRatePerThousand ?? 1000) / 1000.0);
        var stats = new SummonStats
        {
            MaxHealth = (int)Math.Truncate(
                (derivation.Health.PerLevel * ownerLevel + derivation.Health.Flat) *
                (1000 + (rank.HealthRatePerThousand ?? 0)) / 1000.0),
            PhysicalResistanceBonus = derivation.PhysicalResistanceBonus,
            MagicResistance = derivation.MagicResistance,
            PrimaryAttributes = ownerAttributes,
        };
        return derivation.Attack.Attribute == "magicAttack"
            ? stats with { MagicAttack = attack }
            : stats with { PhysicalAttack = attack };
    }

    /// <summary>寿命输入（<c>CHAR_Act_Summon</c> 那一段）：按主人属性线性换算，再乘 15/10 截断成帧。</summary>
    public static int LifetimeFrames(SummonLifetime rule, int ownerValue) =>
        (int)Math.Truncate(Math.Truncate((ownerValue * rule.PerPoint + rule.Base) / 1000.0) * 15 / 10.0);

    /// <summary>
    /// 自然之精灵那一条链的换算：直接在上限帧上乘 (1000+主人比例)/1000，只取整一次。
    /// 与兽的两次取整顺序不同，所以两者不能共用一个函数。
    /// </summary>
    public static int OwnerRateFrames(int baseFrames, int ownerRate) =>
        (int)Math.Truncate(baseFrames * (1000.0 + ownerRate) / 1000);
}
