namespace Inotia4.Domain.Combat;

/// <summary>
/// 一条数值运算。所有加成都是「对某个数做一次确定运算」，
/// 顺序和取整方式都是规则的一部分——所以这里不做归一化，见到什么执行什么。
/// </summary>
public sealed class CombatOperation
{
    [System.Text.Json.Serialization.JsonPropertyName("kind")]
    public string Kind { get; init; } = "keep";

    [System.Text.Json.Serialization.JsonPropertyName("amount")]
    public int Amount { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("factor")]
    public float Factor { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("divisor")]
    public int Divisor { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("ratePerThousand")]
    public int RatePerThousand { get; init; }

    public static CombatOperation Keep() => new() { Kind = "keep" };
}

public static class CombatOperations
{
    public static int Apply(int current, CombatOperation operation) => operation.Kind switch
    {
        "keep" => current,
        "set" => operation.Amount,
        "add" => current + operation.Amount,
        "subtract" => current - operation.Amount,
        "multiply" => (int)Math.Truncate(current * (double)operation.Factor),
        "divide" => operation.Divisor != 0 ? (int)Math.Truncate(current / (double)operation.Divisor) : 0,
        "multiply-thousandth" => (int)Math.Truncate(current * (double)operation.RatePerThousand / 1000),
        "increase-thousandth" => (int)Math.Truncate(current * (1000.0 + operation.RatePerThousand) / 1000),
        "decrease-thousandth" => (int)Math.Truncate(current * (1000.0 - operation.RatePerThousand) / 1000),
        _ => throw new ArgumentOutOfRangeException(nameof(operation), $"未知的运算类型：{operation.Kind}"),
    };

    public static int ApplyModifiers(int baseValue, IEnumerable<(string Attribute, CombatOperation Operation)> modifiers,
        string attribute)
    {
        var value = baseValue;
        foreach (var (name, operation) in modifiers)
        {
            if (name == attribute) value = Apply(value, operation);
        }

        return value;
    }
}

public sealed class AttributeModifier
{
    [System.Text.Json.Serialization.JsonPropertyName("attribute")]
    public required string Attribute { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("operation")]
    public required CombatOperation Operation { get; init; }

    public static int Apply(string attribute, int baseValue, IReadOnlyList<AttributeModifier> modifiers)
    {
        var value = baseValue;
        foreach (var modifier in modifiers)
            if (modifier.Attribute == attribute) value = CombatOperations.Apply(value, modifier.Operation);

        return value;
    }
}
