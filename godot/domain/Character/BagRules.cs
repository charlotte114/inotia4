using System;
using System.Collections.Generic;
using System.Linq;
using Inotia4.Domain.Content;

namespace Inotia4.Domain.Character;

public sealed record DropRoll(bool Sockets = true, EquipmentGradeSeed? Grade = null);

public sealed record EnchantOutcome(int Level, string Message, ItemNotice? Notice = null);

public abstract class BagRules
{
    public virtual void Initialize(ItemInstance entry, DropRoll? drop = null)
    {
    }

    public virtual string? EquipProblem(ItemInstance entry) => null;

    public virtual ItemNotice? TargetProblem(ItemInstance source, ItemInstance target) => null;

    public virtual string? ApplyUse(ItemInstance source, ItemInstance? target, string? skill) => null;

    /// <summary>
    /// 技能书进背包时随机附着一条可训练的技能。造物品时技能书一律走这一条路
    /// （原版 ITEMSYSTEM_CreateItem → ITEMSYSTEM_MakeSkillBook，见 SO_REVERSE.md）：
    /// 在全部可训练技能里等概率挑一条，**不分职业**，
    /// 所以开出的书可能是别的职业的（用不了，只能卖掉）。返回 <c>null</c> 表示没有池。
    /// </summary>
    public virtual string? RollLearnSkill() => null;

    public static string? RollLearnSkill(ItemCatalog items, Func<double> random)
    {
        var pool = items.ClassSkills.Values.SelectMany(skills => skills).Distinct().ToList();
        if (pool.Count == 0) return null;

        var value = random();
        if (!double.IsFinite(value) || value < 0 || value >= 1)
            throw new InvalidOperationException("随机结果无效");
        return pool[(int)Math.Floor(value * pool.Count)];
    }

    public virtual EnchantOutcome? Enchant(ItemInstance source, ItemInstance target, Func<double> random) => null;

    public static string LabelOf(string kind) => ItemUseRules.LabelOf(kind);
}

public sealed class EquipmentBagRules : BagRules
{
    private readonly CharacterStatsCatalog _stats;
    private readonly ItemCatalog _items;
    private readonly string _classId;
    private readonly Func<int> _level;
    private readonly Func<double> _bookRoll;

    public EquipmentBagRules(CharacterStatsCatalog stats, ItemCatalog items, string classId,
        Func<int>? level = null, Func<double>? bookRoll = null)
    {
        _stats = stats;
        _items = items;
        _classId = classId;
        _level = level ?? (() => 1);
        _bookRoll = bookRoll ?? Random.Shared.NextDouble;
    }

    public override string? RollLearnSkill() => RollLearnSkill(_items, _bookRoll);

    /// <summary>
    /// 掷数值。身份串是 <c>battle:职业:物品:实例编号</c>，
    /// 所以同一件装备（同职业、同物品、同编号）永远同一组词条。
    /// </summary>
    public override void Initialize(ItemInstance entry, DropRoll? drop = null)
    {
        if (!_stats.Equipment.ContainsKey(entry.ItemId)) return;

        entry.Numbers = EquipmentRoller.Roll(
            _stats.GetEquipment(entry.ItemId),
            new SeededRandom($"battle:{_classId}:{entry.ItemId}:{entry.Id}"),
            drop?.Sockets ?? true,
            drop?.Grade);
    }

    public override string? EquipProblem(ItemInstance entry)
    {
        if (!_items.Items.TryGetValue(entry.ItemId, out var definition)) return null;

        var itemLevel = ChaosEquipment.EquipmentLevel(definition.Level, entry);
        var level = _level();
        return itemLevel > level ? $"需要等级 {itemLevel}，当前等级 {level}" : null;
    }
}
