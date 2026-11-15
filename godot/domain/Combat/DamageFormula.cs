using System.Text.Json.Serialization;

namespace Inotia4.Domain.Combat;

public sealed class PrimaryAttributes
{
    public int Strength { get; init; }

    public int Dexterity { get; init; }

    public int Vitality { get; init; }

    public int Intelligence { get; init; }

    public int Mentality { get; init; }

    public int Get(string name) => name switch
    {
        "strength" => Strength,
        "dexterity" => Dexterity,
        "vitality" => Vitality,
        "intelligence" => Intelligence,
        "mentality" => Mentality,
        _ => 0,
    };
}

public sealed class DamageRank
{
    [JsonPropertyName("fixedAddition")] public int FixedAddition { get; init; }

    [JsonPropertyName("operation")] public CombatOperationJson Operation { get; init; } = new();

    public CombatOperation ToOperation() => Operation.ToOperation();
}

public sealed class CombatOperationJson
{
    [JsonPropertyName("kind")] public string Kind { get; init; } = "keep";

    [JsonPropertyName("amount")] public int Amount { get; init; }

    [JsonPropertyName("factor")] public float Factor { get; init; }

    [JsonPropertyName("divisor")] public int Divisor { get; init; }

    [JsonPropertyName("ratePerThousand")] public int RatePerThousand { get; init; }

    public CombatOperation ToOperation() => new()
    {
        Kind = Kind,
        Amount = Amount,
        Factor = Factor,
        Divisor = Divisor,
        RatePerThousand = RatePerThousand,
    };
}

public sealed class NumericUnit : ICombatUnit
{
    [JsonPropertyName("effect")] public string Effect { get; init; } = "";

    [JsonPropertyName("source")] public string Source { get; init; } = "none";

    [JsonPropertyName("attributeWeights")] public Dictionary<string, int> AttributeWeights { get; init; } = new();

    [JsonPropertyName("ranks")] public DamageRank[] Ranks { get; init; } = Array.Empty<DamageRank>();

    [JsonPropertyName("ownerValues")] public bool? OwnerValues { get; init; }

    [JsonPropertyName("alsoAppliesLearnedBuff")] public string? AlsoAppliesLearnedBuff { get; init; }
}

public sealed record PreparedDamage(string Source, int WeightedAddition, int FixedAddition, CombatOperation Operation);

/// <summary>
/// 伤害公式：把内容里的伤害定义算成一个数值。
///
/// 分两步是刻意的：技能可能在多个目标上复用同一条公式，但基础值来自不同的施法者属性，
/// 所以先「准备」出与施法者无关的部分，再逐目标「结算」。
/// </summary>
public static class DamageFormula
{
    private static readonly string[] PrimaryNames = ["strength", "dexterity", "vitality", "intelligence", "mentality"];

    public static int PhysicalNormalDamage(int physicalAttack, int physicalDamageRate, int stealthDamageRate) =>
        (int)Math.Truncate(physicalAttack * (1000.0 + physicalDamageRate + stealthDamageRate) / 1000);

    public static int MagicNormalDamage(int magicAttack, int magicDamageRate) =>
        (int)Math.Truncate(magicAttack * (1000.0 + magicDamageRate) / 1000);

    public static int BestNormalDamage(int physicalAttack, int physicalDamageRate, int stealthDamageRate,
        int magicAttack, int magicDamageRate) =>
        Math.Max(
            PhysicalNormalDamage(physicalAttack, physicalDamageRate, stealthDamageRate),
            MagicNormalDamage(magicAttack, magicDamageRate));

    public static int NormalDamageForClass(int physicalAttack, int physicalDamageRate, int stealthDamageRate,
        int magicAttack, int magicDamageRate, string? classId) =>
        classId is "warlock" or "priest"
            ? MagicNormalDamage(magicAttack, magicDamageRate)
            : PhysicalNormalDamage(physicalAttack, physicalDamageRate, stealthDamageRate);

    public static PreparedDamage? Prepare(NumericUnit unit, int rank, PrimaryAttributes attributes)
    {
        if (unit.Ranks.Length == 0) return null;

        var index = Math.Clamp(rank - 1, 0, unit.Ranks.Length - 1);
        var formula = unit.Ranks[index];

        var weightedSum = 0;
        foreach (var name in PrimaryNames)
            weightedSum += unit.AttributeWeights.GetValueOrDefault(name, 0) * attributes.Get(name);

        return new PreparedDamage(
            unit.Source,
            (int)Math.Truncate(weightedSum / 10.0),
            unit.Effect == "heal" ? 0 : formula.FixedAddition,
            formula.ToOperation());
    }

    public static int Resolve(PreparedDamage prepared, int sourceValue)
    {
        var baseValue = prepared.Source == "none" ? 0 : sourceValue;
        return CombatOperations.Apply(baseValue + prepared.WeightedAddition + prepared.FixedAddition, prepared.Operation);
    }
}
