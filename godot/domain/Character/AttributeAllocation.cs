using System;
using System.Collections.Generic;
using System.Linq;
using Inotia4.Domain.Combat;

namespace Inotia4.Domain.Character;

public static class PrimaryStats
{
    public const string Strength = "strength";

    public const string Dexterity = "dexterity";

    public const string Vitality = "vitality";

    public const string Intelligence = "intelligence";

    public const string Mentality = "mentality";

    public static readonly string[] All = [Strength, Dexterity, Vitality, Intelligence, Mentality];

    public static bool IsPrimary(string stat) => Array.IndexOf(All, stat) >= 0;
}

/// <summary>五维的加减与按名字读写。五维在本仓库是只读记录，改一项只能生成新的一份，
/// 所以这里统一给几个小工具收口。</summary>
public static class PrimaryAttributeMath
{
    public static PrimaryAttributes Zero() => new();

    public static int Get(PrimaryAttributes values, string stat) => values.Get(stat);

    public static PrimaryAttributes With(PrimaryAttributes values, string stat, int value) => stat switch
    {
        PrimaryStats.Strength => Copy(values, Strength: value),
        PrimaryStats.Dexterity => Copy(values, Dexterity: value),
        PrimaryStats.Vitality => Copy(values, Vitality: value),
        PrimaryStats.Intelligence => Copy(values, Intelligence: value),
        PrimaryStats.Mentality => Copy(values, Mentality: value),
        _ => throw new ArgumentException("属性操作无效"),
    };

    public static PrimaryAttributes Add(PrimaryAttributes left, PrimaryAttributes right) => new()
    {
        Strength = left.Strength + right.Strength,
        Dexterity = left.Dexterity + right.Dexterity,
        Vitality = left.Vitality + right.Vitality,
        Intelligence = left.Intelligence + right.Intelligence,
        Mentality = left.Mentality + right.Mentality,
    };

    public static int Total(PrimaryAttributes values) =>
        values.Strength + values.Dexterity + values.Vitality + values.Intelligence + values.Mentality;

    private static PrimaryAttributes Copy(PrimaryAttributes values, int? Strength = null, int? Dexterity = null,
        int? Vitality = null, int? Intelligence = null, int? Mentality = null) => new()
    {
        Strength = Strength ?? values.Strength,
        Dexterity = Dexterity ?? values.Dexterity,
        Vitality = Vitality ?? values.Vitality,
        Intelligence = Intelligence ?? values.Intelligence,
        Mentality = Mentality ?? values.Mentality,
    };
}

public interface IAllocationOwner
{
    int StatPoints { get; }

    PrimaryAttributes Attributes { get; }

    PrimaryAttributes Allocated { get; }

    void CommitAllocation(PrimaryAttributes delta);

    void ResetAttributes();
}

public sealed class AttributeAllocation
{
    private readonly IAllocationOwner _hero;

    private Dictionary<string, int> _additions = Zero();

    public AttributeAllocation(IAllocationOwner hero) => _hero = hero;

    public PrimaryAttributes Pending => Build(_additions);

    public bool HasChanges => PrimaryStats.All.Any(stat => _additions[stat] > 0);

    public int RemainingPoints => _hero.StatPoints - PrimaryStats.All.Sum(stat => _additions[stat]);

    /// <summary>预览用的五维：面板是派生数据，只能改副本，不能把本轮加点写回角色。</summary>
    public PrimaryAttributes Attributes => PrimaryAttributeMath.Add(_hero.Attributes, Pending);

    public int PendingOf(string stat) => _additions.GetValueOrDefault(stat);

    /// <summary>加减一点。<paramref name="delta"/> 只能是 +1／−1：一次只能点一下，
    /// 不能点成负的，也不能超过剩余点数。</summary>
    public void Change(string stat, int delta)
    {
        if (!PrimaryStats.IsPrimary(stat) || delta is not (1 or -1))
            throw new ArgumentException("属性操作无效");
        if (delta == 1 && RemainingPoints < 1) throw new InvalidOperationException("没有可分配的能力点");
        if (delta == -1 && _additions[stat] < 1) throw new InvalidOperationException("该属性没有待确认的点数");

        _additions[stat] += delta;
    }

    public void Commit()
    {
        var delta = _additions;
        _additions = Zero();
        try
        {
            _hero.CommitAllocation(Build(delta));
        }
        catch
        {
            _additions = delta;
            throw;
        }
    }

    public void Cancel() => _additions = Zero();

    private static Dictionary<string, int> Zero() =>
        PrimaryStats.All.ToDictionary(stat => stat, _ => 0);

    private static PrimaryAttributes Build(IReadOnlyDictionary<string, int> values) => new()
    {
        Strength = values.GetValueOrDefault(PrimaryStats.Strength),
        Dexterity = values.GetValueOrDefault(PrimaryStats.Dexterity),
        Vitality = values.GetValueOrDefault(PrimaryStats.Vitality),
        Intelligence = values.GetValueOrDefault(PrimaryStats.Intelligence),
        Mentality = values.GetValueOrDefault(PrimaryStats.Mentality),
    };
}
