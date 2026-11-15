using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Inotia4.Domain.Content;

namespace Inotia4.Domain.Character;

/// <summary>
/// 确定性随机：种子由身份串派生，之后按线性同余取值。
///
/// 同一件装备（同职业、同物品、同实例编号）永远掷出同一组词条。
/// 这不是为了「可预测」，而是因为面板每次重算都会重读实例——如果随机源不稳定，
/// 同一件装备的属性会在打开背包前后变来变去。
/// </summary>
public sealed class SeededRandom
{
    private uint _state = 2166136261u;

    public SeededRandom(string identity)
    {
        foreach (var character in identity)
            _state = unchecked((_state ^ (uint)character) * 16777619u);
    }

    public int Next(int min, int max)
    {
        if (max < min) throw new ArgumentException($"取值区间无效：{min}..{max}");

        _state = unchecked(_state * 1664525u + 1013904223u);
        var unit = _state / 4294967296.0;
        return min + (int)Math.Floor(unit * (max - min + 1));
    }
}

public sealed class Modifier
{
    public required string Stat { get; init; }

    public required int Value { get; init; }
}

public sealed class EnhancementRule
{
    [System.Text.Json.Serialization.JsonPropertyName("step")] public int Step { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("threshold")] public int Threshold { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("extra")] public int Extra { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("bonusChance")] public int? BonusChance { get; init; }

    public bool SameNumbers(EnhancementRule other) =>
        Step == other.Step && Threshold == other.Threshold && Extra == other.Extra;

    public EnhancementRule Clone() =>
        new() { Step = Step, Threshold = Threshold, Extra = Extra, BonusChance = BonusChance };
}

public sealed class ItemNumbers
{
    public int? BaseAttack { get; set; }

    public int? BaseDefense { get; set; }

    public int? Grade { get; set; }

    public string? GradeName { get; set; }

    public int MagicPercent { get; set; }

    public int Sockets { get; set; }

    public int EnhancementUses { get; set; }

    public EnhancementRule? EnhancementRule { get; set; }

    /// <summary>
    /// 第一张贴上去的强化卷轴。用它判「后面贴的必须是同一张（或同级的另一张）」，
    /// 混沌卷轴不写这个字段，也不消耗次数。
    /// </summary>
    public string? EnhancementScroll { get; set; }

    public EquipmentChaos? Chaos { get; set; }

    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public List<Modifier> Modifiers { get; } = [];

    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public List<Modifier> GemModifiers { get; } = [];

    /// <summary>
    /// 完整复制：数值也是独立的一份，
    /// 所以界面拿到的副本改词条不会影响背包里的那一件。
    /// </summary>
    public ItemNumbers Clone()
    {
        var copy = new ItemNumbers
        {
            BaseAttack = BaseAttack,
            BaseDefense = BaseDefense,
            Grade = Grade,
            GradeName = GradeName,
            MagicPercent = MagicPercent,
            Sockets = Sockets,
            EnhancementUses = EnhancementUses,
            EnhancementRule = EnhancementRule?.Clone(),
            EnhancementScroll = EnhancementScroll,
            Chaos = Chaos is null ? null : new EquipmentChaos(Chaos.Tier, Chaos.ValuePercent, Chaos.LevelOffset),
        };
        foreach (var modifier in Modifiers) copy.Modifiers.Add(new Modifier { Stat = modifier.Stat, Value = modifier.Value });
        foreach (var modifier in GemModifiers) copy.GemModifiers.Add(new Modifier { Stat = modifier.Stat, Value = modifier.Value });
        return copy;
    }
}

public sealed class ItemInstance
{
    public required string Id { get; init; }

    public required string ItemId { get; init; }

    /// <summary>这一格里的数量。不可堆叠物品恒为 1，一件一个实例。</summary>
    public int Quantity { get; set; } = 1;

    public int Enhancement { get; set; }

    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public List<string> Gems { get; } = [];

    public string? Skill { get; set; }

    public ItemNumbers? Numbers { get; set; }

    public ItemInstance Clone() => new()
    {
        Id = Id,
        ItemId = ItemId,
        Quantity = Quantity,
        Enhancement = Enhancement,
        Skill = Skill,
        Numbers = Numbers?.Clone(),
    };

    public ItemInstance DeepClone()
    {
        var copy = Clone();
        copy.Gems.AddRange(Gems);
        return copy;
    }
}

public static class EquipmentRoller
{
    public const int DefaultGrade = 2;

    private const int SocketChancePercent = 80;

    private static readonly int[] SocketWeights = [35, 30, 20, 15];

    public static ItemNumbers Roll(EquipmentNumbers definition, SeededRandom random, bool withSockets = true,
        EquipmentGradeSeed? grade = null) =>
        Roll(definition, random.Next, withSockets, grade);

    public static ItemNumbers Roll(EquipmentNumbers definition, Func<int, int, int> random,
        bool withSockets = true, EquipmentGradeSeed? grade = null)
    {
        var numbers = new ItemNumbers
        {
            Grade = grade?.Grade ?? definition.DefaultGrade ?? DefaultGrade,
            GradeName = grade?.GradeName ?? definition.GradeName,
        };

        if (grade is not null)
        {
            numbers.BaseAttack = grade.Attack;
            numbers.BaseDefense = grade.Defense;
        }

        numbers.MagicPercent = TruncAverage(definition.MagicPercent, random);
        numbers.EnhancementUses = definition.FixedEnhancementUses
                                  ?? random(definition.EnhancementUses[0], definition.EnhancementUses[1])
                                  + random(definition.EnhancementUses[0], definition.EnhancementUses[1]) + 1;

        foreach (var range in definition.FixedOptions) numbers.Modifiers.Add(RollModifier(range, random));

        var pool = new List<ModifierRange>(definition.RandomOptions);
        var optionCount = grade?.OptionCount ?? definition.OptionCount;
        for (var index = 0; index < optionCount && pool.Count > 0; index++)
        {
            var picked = pool[random(0, pool.Count - 1)];
            pool.Remove(picked);
            numbers.Modifiers.Add(RollModifier(picked, random));
        }

        if (!withSockets || random(0, 99) >= SocketChancePercent) return numbers;

        var roll = random(0, 99);
        var threshold = 0;
        var socketLimit = grade?.SocketLimit ?? definition.SocketLimit;
        for (var sockets = 0; sockets < socketLimit; sockets++)
        {
            if (sockets >= SocketWeights.Length) break;
            threshold += SocketWeights[sockets];
            if (roll >= threshold) continue;
            numbers.Sockets = sockets + 1;
            break;
        }

        return numbers;
    }

    public static ItemInstance Create(
        CharacterStatsCatalog stats, string classId, string itemId, int index, bool withSockets = true)
    {
        var instance = new ItemInstance
        {
            Id = $"{classId}#{itemId}#{index}",
            ItemId = itemId,
        };

        instance.Numbers = Roll(
            stats.GetEquipment(itemId),
            new SeededRandom($"battle:{classId}:{itemId}:{instance.Id}"),
            withSockets);

        return instance;
    }

    private static Modifier RollModifier(ModifierRange range, Func<int, int, int> random) =>
        new() { Stat = range.Stat, Value = random(range.Min, range.Max) };

    private static int TruncAverage(int[] range, Func<int, int, int> random)
    {
        if (range.Length < 2) return range.Length == 1 ? range[0] : 0;
        return (random(range[0], range[1]) + random(range[0], range[1])) / 2;
    }
}

public record EquipmentGradeSeed(
    int Grade, string GradeName, int Attack, int Defense, int OptionCount, int SocketLimit);

public readonly record struct EquipmentPower(int Attack, int MagicAttack, int Defense);

public static class EquipmentPowerCalculator
{
    public static EquipmentPower Power(EquipmentNumbers definition, ItemInstance entry)
    {
        var rule = entry.Numbers?.EnhancementRule;
        var bonus = rule is null
            ? 0
            : entry.Enhancement * rule.Step + Math.Max(0, entry.Enhancement - rule.Threshold + 1) * rule.Extra;

        var baseAttack = entry.Numbers?.BaseAttack ?? definition.Attack;
        var baseDefense = entry.Numbers?.BaseDefense ?? definition.Defense;

        var chaos = entry.Numbers?.Chaos;
        var rate = chaos?.ValuePercent ?? 100;

        var attack = baseAttack != 0
            ? chaos is not null
                ? Math.Max(1, (int)Math.Truncate((baseAttack + bonus) * (double)rate / 100))
                : baseAttack + bonus
            : 0;
        var magicPercent = entry.Numbers?.MagicPercent ?? 0;
        var defense = baseDefense != 0 ? (int)Math.Truncate((baseDefense + bonus) * (double)rate / 100) : 0;

        return new EquipmentPower(attack, (int)Math.Truncate(attack * (double)magicPercent / 100), defense);
    }
}
