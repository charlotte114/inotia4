using Inotia4.Domain.Character;
using Inotia4.Domain.Content;

namespace Inotia4.Domain.Ui;

public enum BagActionKind
{
    Unequip,

    Equip,

    Use,

    Shortcut,

    ClearShortcut,

    Discard,
}

public sealed record BagMenuAction(string Label, BagActionKind Kind, string? Slot = null, bool Disabled = false);

/// <summary>
/// 说明面板底部的动作菜单（使用／装备／丢弃那一排）：
/// 穿着的只给「卸下」；背包里的先想空槽，再想唯一可穿槽，多个可穿槽就每个给一个「替换X」；
/// 然后是使用、快捷键、丢弃。这些「菜单里出现什么」的规矩不该由画面层各写一份。
/// </summary>
public static class BagMenu
{
    private static readonly HashSet<string> NonMenuUseKinds = ["enchant", "socket", "purify"];

    public static List<BagMenuAction> Build(
        Bag bag,
        ItemInstance entry,
        string owner,
        string classId,
        int level,
        Func<string, string> slotName,
        bool shortcutAssigned = false,
        bool isPlayer = true)
    {
        var definition = bag.Catalog.Get(entry.ItemId);
        var actions = new List<BagMenuAction>();

        if (wornSlotOf(bag, entry, owner) is { } worn)
        {
            actions.Add(new BagMenuAction("卸下", BagActionKind.Unequip, worn));
            return actions;
        }

        var slots = bag.EquippableSlots(entry.Id, owner, classId, level);
        var empty = bag.EmptyEquipSlot(entry.Id, owner, classId, level);
        if (empty is not null)
            actions.Add(new BagMenuAction("装备", BagActionKind.Equip, empty));
        else if (slots.Count == 1)
            actions.Add(new BagMenuAction("装备", BagActionKind.Equip, slots[0]));
        else if (slots.Count > 1)
            foreach (var slot in slots)
                actions.Add(new BagMenuAction($"替换{slotName(slot)}", BagActionKind.Equip, slot));
        else
        {
            var fallback = bag.SlotsFor(entry.Id, classId).FirstOrDefault();
            if (fallback is not null)
                actions.Add(new BagMenuAction("装备", BagActionKind.Equip, fallback, Disabled: true));
        }

        if (definition.Use is { } use && !NonMenuUseKinds.Contains(use.Kind))
            actions.Add(new BagMenuAction(ItemUseRules.LabelOf(use.Kind), BagActionKind.Use,
                Disabled: UseProblem(bag, entry, isPlayer) is not null));

        if (definition.Shortcut)
            actions.Add(shortcutAssigned
                ? new BagMenuAction("取消快捷键", BagActionKind.ClearShortcut)
                : new BagMenuAction("快捷键", BagActionKind.Shortcut));

        if (definition.Discardable)
            actions.Add(new BagMenuAction("丢弃", BagActionKind.Discard));

        return actions;
    }

    public static string? WornSlot(Bag bag, ItemInstance entry, string owner) => wornSlotOf(bag, entry, owner);

    private static string? wornSlotOf(Bag bag, ItemInstance entry, string owner)
    {
        foreach (var pair in bag.EquipmentFor(owner))
            if (pair.Value == entry.Id) return pair.Key;
        return null;
    }

    public static string? UseProblem(Bag bag, ItemInstance entry, bool isPlayer = true)
    {
        var definition = bag.Catalog.Get(entry.ItemId);
        var unimplemented = ItemUseRules.UnimplementedItemUse(entry.ItemId, definition);
        if (unimplemented is not null) return unimplemented;

        var kind = definition.Use?.Kind;
        if (!isPlayer && (kind == "reset-skills" || kind == "learn" || entry.ItemId == ItemUseRules.SkillPointItem))
            return "该道具仅能用于主角";

        return null;
    }
}
