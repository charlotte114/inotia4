using System;
using System.Collections.Generic;
using System.Linq;

namespace Inotia4.Domain.Character;

public sealed record ShortcutEntry(string Kind, string Id)
{
    public const string SkillKind = "skill";
    public const string ItemKind = "item";
}

/// <summary>
/// 原版快捷栏每组六格、共两组，<c>PLAYER_PutShortcut</c> 按 <c>page*6 + slot</c> 寻址，
/// 而且不校验槽位与类型的搭配——技能和物品都能放进任意一格，所以这十二格是通用的。
/// </summary>
public static class ShortcutLayout
{
    public const int SlotCount = 6;

    public const int PageCount = 2;

    public const int EntryCount = SlotCount * PageCount;

    public const int SkillSlots = SlotCount - 1;

    public static ShortcutEntry?[] EmptyRow() => new ShortcutEntry?[EntryCount];

    public static ShortcutEntry?[] DedupeSkills(IReadOnlyList<ShortcutEntry?> row)
    {
        var result = row.Select(entry => entry is null ? null : new ShortcutEntry(entry.Kind, entry.Id))
            .ToArray();
        var seen = new HashSet<string>();
        for (var index = result.Length - 1; index >= 0; index -= 1)
        {
            var entry = result[index];
            if (entry is null || entry.Kind != ShortcutEntry.SkillKind) continue;
            if (!seen.Add(entry.Id)) result[index] = null;
        }

        return result;
    }

    public static ShortcutEntry?[] DefaultRow(IReadOnlyList<string> learnedActive, string? startingItem)
    {
        var row = EmptyRow();
        for (var index = 0; index < learnedActive.Count; index += 1)
        {
            var page = index / SkillSlots;
            if (page >= PageCount) break;
            row[page * SlotCount + index % SkillSlots] = new ShortcutEntry(ShortcutEntry.SkillKind, learnedActive[index]);
        }

        if (!string.IsNullOrEmpty(startingItem))
            row[SkillSlots] = new ShortcutEntry(ShortcutEntry.ItemKind, startingItem);
        return row;
    }
}

/// <summary>
/// 一位成员的快捷栏。角色还没登记过时按默认内容铺开一次，之后这一行就是权威数据，
/// 所以后来学会的技能不会自己跳上快捷栏（原版学习技能也不动快捷栏），得在这一屏里登记。
/// </summary>
public sealed class ShortcutBar
{
    private readonly Bag _bag;
    private readonly string _owner;
    private readonly ShortcutEntry?[] _defaults;

    private ShortcutBar(Bag bag, string owner, ShortcutEntry?[] defaults)
    {
        _bag = bag;
        _owner = owner;
        _defaults = defaults;
    }

    public static ShortcutBar For(Bag bag, string owner, IReadOnlyList<string> learnedActive, string? startingItem = null) =>
        new(bag, owner, ShortcutLayout.DefaultRow(learnedActive, startingItem));

    public Bag Bag => _bag;

    private static void Check(int page)
    {
        if (page < 0 || page >= ShortcutLayout.PageCount) throw new ArgumentException("技能组无效");
    }

    public ShortcutEntry?[] Row() => _bag.EnsureShortcuts(_owner, _defaults);

    public ShortcutEntry?[] Page(int page)
    {
        Check(page);
        return Row().Skip(page * ShortcutLayout.SlotCount).Take(ShortcutLayout.SlotCount).ToArray();
    }

    /// <summary>落一格。已占用即覆盖，没有确认步骤，对应原版的 <c>PLAYER_PutShortcut</c>。</summary>
    public void Assign(int page, int slot, ShortcutEntry? entry)
    {
        Check(page);
        if (slot < 0 || slot >= ShortcutLayout.SlotCount) throw new ArgumentException("快捷位无效");

        var row = Row();
        if (entry is { Kind: ShortcutEntry.SkillKind })
            for (var index = 0; index < row.Length; index += 1)
                if (row[index] is { Kind: ShortcutEntry.SkillKind } existing && existing.Id == entry.Id)
                    row[index] = null;

        row[page * ShortcutLayout.SlotCount + slot] = entry;
        _bag.SetShortcuts(_owner, row);
    }

    public bool Assigned(string kind, string id) =>
        Row().Any(entry => entry is not null && entry.Kind == kind && entry.Id == id);

    /// <summary>把该对象从两组里一次清掉，对应原版的 <c>PLAYER_RemoveShortcutAll</c>。</summary>
    public void ClearObject(string kind, string id)
    {
        var row = Row();
        for (var index = 0; index < row.Length; index += 1)
            if (row[index] is { } entry && entry.Kind == kind && entry.Id == id) row[index] = null;
        _bag.SetShortcuts(_owner, row);
    }
}
