using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Inotia4.Domain.Content;

namespace Inotia4.Domain.Character;

public enum InventoryPouch
{
    Weapon,
    Armor,
    Consumable,
    Material,
    Quest,
}

public enum InventoryViewKind
{
    All,
    Weapon,
    Armor,
    Consumable,
    Material,
    Quest,
}

public static class InventoryViews
{
    public static readonly InventoryPouch[] Pouches =
        [InventoryPouch.Weapon, InventoryPouch.Armor, InventoryPouch.Consumable, InventoryPouch.Material, InventoryPouch.Quest];

    public static readonly InventoryViewKind[] SideFilters =
    [
        InventoryViewKind.All, InventoryViewKind.Weapon, InventoryViewKind.Armor,
        InventoryViewKind.Consumable, InventoryViewKind.Material, InventoryViewKind.Quest,
    ];

    public const int Columns = 4;

    public static readonly string[] Categories = ["equipment", "consumable", "material", "quest"];

    private static readonly Dictionary<InventoryViewKind, string> Labels = new()
    {
        [InventoryViewKind.All] = "全部",
        [InventoryViewKind.Weapon] = "武器",
        [InventoryViewKind.Armor] = "防具",
        [InventoryViewKind.Consumable] = "消耗",
        [InventoryViewKind.Material] = "材料",
        [InventoryViewKind.Quest] = "任务",
    };

    private static readonly CompareInfo NameComparer =
        CultureInfo.GetCultureInfo("zh-CN").CompareInfo;

    /// <summary>
    /// 面向玩家的中文分组用自然序，所以名字里的数字要按数值比而不是按字符比
    /// （「封印石2」排在「封印石10」前面），文字段按 zh-CN 大小写不敏感比。
    /// </summary>
    private static int CompareNames(string left, string right)
    {
        var leftIndex = 0;
        var rightIndex = 0;
        while (leftIndex < left.Length && rightIndex < right.Length)
        {
            var leftDigit = char.IsDigit(left[leftIndex]);
            var rightDigit = char.IsDigit(right[rightIndex]);
            if (leftDigit != rightDigit)
            {
                var byChar = NameComparer.Compare(left[leftIndex].ToString(), right[rightIndex].ToString(),
                    CompareOptions.IgnoreCase);
                return byChar != 0 ? byChar : leftDigit ? -1 : 1;
            }

            if (!leftDigit)
            {
                var leftEnd = leftIndex;
                while (leftEnd < left.Length && !char.IsDigit(left[leftEnd])) leftEnd++;
                var rightEnd = rightIndex;
                while (rightEnd < right.Length && !char.IsDigit(right[rightEnd])) rightEnd++;
                var byText = NameComparer.Compare(left[leftIndex..leftEnd], right[rightIndex..rightEnd],
                    CompareOptions.IgnoreCase);
                if (byText != 0) return byText;
                leftIndex = leftEnd;
                rightIndex = rightEnd;
            }
            else
            {
                long leftNumber = 0;
                while (leftIndex < left.Length && char.IsDigit(left[leftIndex]))
                    leftNumber = leftNumber * 10 + (left[leftIndex++] - '0');
                long rightNumber = 0;
                while (rightIndex < right.Length && char.IsDigit(right[rightIndex]))
                    rightNumber = rightNumber * 10 + (right[rightIndex++] - '0');
                if (leftNumber != rightNumber) return leftNumber < rightNumber ? -1 : 1;
            }
        }

        return leftIndex < left.Length ? 1 : rightIndex < right.Length ? -1 : 0;
    }

    public static string LabelOf(InventoryViewKind view) => Labels[view];

    private static bool IsHandSlot(ItemDefinition item) =>
        item.Equipment?.Slot is "main-hand" or "off-hand";

    public static bool Matches(ItemDefinition? item, InventoryViewKind view)
    {
        if (view == InventoryViewKind.All) return true;
        if (item is null) return false;

        if (view is InventoryViewKind.Weapon or InventoryViewKind.Armor)
        {
            if (item.Category != "equipment") return false;
            return view == InventoryViewKind.Weapon ? IsHandSlot(item) : !IsHandSlot(item);
        }

        return item.Category == view switch
        {
            InventoryViewKind.Consumable => "consumable",
            InventoryViewKind.Material => "material",
            _ => "quest",
        };
    }

    public static InventoryPouch PouchFor(ItemDefinition? item)
    {
        if (item is null) return InventoryPouch.Quest;
        return item.Category switch
        {
            "equipment" => IsHandSlot(item) ? InventoryPouch.Weapon : InventoryPouch.Armor,
            "consumable" => InventoryPouch.Consumable,
            "material" => InventoryPouch.Material,
            _ => InventoryPouch.Quest,
        };
    }

    public static InventoryPouch PouchFor(ItemCatalog catalog, string itemId) =>
        PouchFor(catalog.Items.GetValueOrDefault(itemId));

    public static InventoryViewKind ViewOf(InventoryPouch pouch) => pouch switch
    {
        InventoryPouch.Weapon => InventoryViewKind.Weapon,
        InventoryPouch.Armor => InventoryViewKind.Armor,
        InventoryPouch.Consumable => InventoryViewKind.Consumable,
        InventoryPouch.Material => InventoryViewKind.Material,
        _ => InventoryViewKind.Quest,
    };

    /// <summary>这个袋用到第几格：袋尾的空格不算，否则一次远距离落格会把滚动区永久撑长。</summary>
    public static int UsedCells<T>(IReadOnlyList<T?> cells)
    {
        for (var index = cells.Count - 1; index >= 0; index -= 1)
            if (cells[index] is not null) return index + 1;
        return 0;
    }

    public static (int SlotCount, List<InventorySlot<T>> Slots) Layout<T>(
        IReadOnlyList<T?> cells, InventoryViewKind view) where T : class
    {
        var slotCount = Math.Max(16, ((UsedCells(cells) + Columns - 1) / Columns + 1) * Columns);
        var slots = new List<InventorySlot<T>>(slotCount);
        for (var cell = 0; cell < slotCount; cell += 1)
        {
            var entry = cell < cells.Count ? cells[cell] : null;
            slots.Add(entry is null
                ? InventorySlot<T>.Empty(view, cell)
                : InventorySlot<T>.Item(entry, view, cell));
        }

        return (slotCount, slots);
    }

    public static List<string> Organized(
        IReadOnlyDictionary<string, int> counts, IReadOnlyDictionary<string, ItemDefinition> items)
    {
        var result = counts.Keys.ToList();
        result.Sort((left, right) =>
        {
            var a = items[left];
            var b = items[right];
            var category = Array.IndexOf(Categories, a.Category) - Array.IndexOf(Categories, b.Category);
            if (category != 0) return category;
            var byName = CompareNames(a.Name, b.Name);
            if (byName != 0) return byName;
            return string.CompareOrdinal(left, right);
        });
        return result;
    }
}

public sealed class InventorySlot<T> where T : class
{
    private InventorySlot(T? entry, InventoryViewKind view, int cell)
    {
        Entry = entry;
        View = view;
        Cell = cell;
    }

    public T? Entry { get; }

    public InventoryViewKind View { get; }

    public int Cell { get; }

    public bool IsItem => Entry is not null;

    public static InventorySlot<T> Item(T entry, InventoryViewKind view, int cell) => new(entry, view, cell);

    public static InventorySlot<T> Empty(InventoryViewKind view, int cell) => new(null, view, cell);
}
