using System;
using System.Collections.Generic;
using System.Linq;
using Inotia4.Domain.Combat;
using Inotia4.Domain.Content;

namespace Inotia4.Domain.Character;

/// <summary>
/// 主角的能力点账本：职业基础五维 + 玩家分配，以及 <c>(等级−1)×每级能力点 − 已分配</c> 的余额。
///
/// 整块角色卡（血量、经验、技能点的自持计数）还没搬，所以等级仍由会话给（<see cref="Func{TResult}"/>），
/// 面板由 <see cref="CharacterPanel.Calculate"/> 现算——同一个算式，只是不缓存。
/// </summary>
public sealed class PlayerAttributes : IAllocationOwner
{
    private readonly CharacterStatsCatalog _catalog;
    private readonly Func<int> _level;

    private Dictionary<string, int> _allocation =
        PrimaryStats.All.ToDictionary(stat => stat, _ => 0);

    public PlayerAttributes(CharacterStatsCatalog catalog, string classId, Func<int> level)
    {
        if (!catalog.Classes.ContainsKey(classId)) throw new ArgumentException("职业数值不存在");
        _catalog = catalog;
        _level = level;
        ClassId = classId;
    }

    public string ClassId { get; }

    public PrimaryAttributes Allocated => Build(_allocation);

    public int StatPoints => (_level() - 1) * _catalog.StatPointsPerLevel - PrimaryStats.All.Sum(stat => _allocation[stat]);

    public PrimaryAttributes Attributes
    {
        get
        {
            var baseAttributes = _catalog.Classes[ClassId].ToPrimary();
            return PrimaryAttributeMath.Add(baseAttributes, Allocated);
        }
    }

    public void CommitAllocation(PrimaryAttributes delta)
    {
        var values = new[]
        {
            delta.Strength, delta.Dexterity, delta.Vitality, delta.Intelligence, delta.Mentality,
        };
        if (values.Any(value => value < 0)) throw new ArgumentException("属性操作无效");

        var total = values.Sum();
        if (total > StatPoints) throw new InvalidOperationException("没有足够的能力点");
        if (total == 0) return;

        for (var index = 0; index < PrimaryStats.All.Length; index += 1)
            _allocation[PrimaryStats.All[index]] += values[index];
    }

    public void ResetAttributes() =>
        _allocation = PrimaryStats.All.ToDictionary(stat => stat, _ => 0);

    public Dictionary<string, int> CaptureAllocation() => new(_allocation);

    public void RestoreAllocation(IReadOnlyDictionary<string, int> values)
    {
        var restored = PrimaryStats.All.ToDictionary(stat => stat, stat => values.GetValueOrDefault(stat));
        if (restored.Values.Any(value => value < 0)) throw new ArgumentException("属性分配无效");
        _allocation = restored;
    }

    private static PrimaryAttributes Build(IReadOnlyDictionary<string, int> values) => new()
    {
        Strength = values.GetValueOrDefault(PrimaryStats.Strength),
        Dexterity = values.GetValueOrDefault(PrimaryStats.Dexterity),
        Vitality = values.GetValueOrDefault(PrimaryStats.Vitality),
        Intelligence = values.GetValueOrDefault(PrimaryStats.Intelligence),
        Mentality = values.GetValueOrDefault(PrimaryStats.Mentality),
    };
}
