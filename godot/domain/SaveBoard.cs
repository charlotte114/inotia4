using System;
using System.Collections.Generic;

namespace Inotia4.Domain;

/// <summary>
/// 存档槽版面与行数据的纯逻辑。
/// 几何量自原版存档槽界面（<c>Scene_Init_POPUP_SC_SAVESLOT</c>，反汇编在 0x1eae4c）：
/// 面板 668×530 居中、三个槽按钮 634×150、行距 166、面板内缩 17；行框是 668×184 的图
/// （图组 88 项 0），画在槽按钮左上角 −17，所以 184 高的图里可见的框正好落在 150 高的
/// 按钮上，相邻两行不重叠。面板本身没有底图——界面的样子全来自三张行框图。
/// 这个类放域层是因为它不碰引擎：排序、标题与时间格式化都要能脱离 Godot 测试。
/// </summary>
public static class SaveBoard
{
    public const int PanelWidth = 668;

    public const int RowWidth = 634;

    public const int RowHeight = 150;

    public const int RowStep = 166;

    public const int RowInset = 17;

    public const int FrameWidth = 668;

    public const int FrameHeight = 184;

    public const int AutoTop = 56;

    public const int ListTop = AutoTop + RowStep;

    public const int ListHeight = 386;

    public const int EmptyWidth = 173;

    public const int EmptyHeight = 68;

    /// <summary>槽位里立绘的高度：原版槽位立绘约 196×138（<c>UI_TITLESLOT_01</c> 图组 88 的 5/6/7 项）。</summary>
    public const int PortraitHeight = 126;

    public const string SlotTextHex = "#e6d9a0";

    public const string SlotTimeTextHex = "#b9ad86";

    public const int DeleteSize = 57;

    public const int DeleteInset = 10;

    public const int DeleteLeft = PanelWidth - DeleteSize - DeleteInset;

    /// <summary>
    /// 手动槽的展示顺序：有存档的按写入时间倒序，最新的排在最前；空槽和损坏档
    /// 没有可比较的时间，排在已有存档之后并按槽号稳定排列。槽号本身不变，操作仍落在原槽。
    /// 排序比较器会反复问同一个槽，所以每个槽只读一次；
    /// 否则一次排序要重复解析近二十次存档摘要。
    /// </summary>
    public static int[] NewestFirstSlots(Func<int, SlotSummaryReading> read)
    {
        var readings = new Dictionary<int, SlotSummaryReading>();
        long SavedAt(int slot)
        {
            if (!readings.TryGetValue(slot, out var reading))
            {
                reading = read(slot);
                readings[slot] = reading;
            }
            return reading is SlotSummaryReading.Ok ok && ok.Summary.SavedAt is { } at ? at : -1;
        }

        var slots = new int[SaveGameStore.ManualSaveSlotCount];
        for (var slot = 0; slot < slots.Length; slot++) slots[slot] = slot;
        Array.Sort(slots, (left, right) =>
        {
            var byTime = SavedAt(right).CompareTo(SavedAt(left));
            return byTime != 0 ? byTime : left - right;
        });
        return slots;
    }

    public static string RowTitle(SaveSlotRef reference, SlotSummaryReading reading)
    {
        var name = reading is SlotSummaryReading.Ok ok ? ok.Summary.Name : null;
        if (reference.IsAuto) return SaveGameStore.SaveSlotLabel(reference);
        return name ?? SaveGameStore.SaveSlotLabel(reference);
    }

    public static string FormatSavedAt(long? savedAt)
    {
        if (savedAt is not { } at) return string.Empty;
        var date = DateTimeOffset.FromUnixTimeMilliseconds(at).LocalDateTime;
        return $"{date.Year:0000}-{date.Month:00}-{date.Day:00} {date.Hour:00}:{date.Minute:00}";
    }

    public static (float X, float Y) Origin(float width) => ((float)MathF.Round((width - PanelWidth) / 2f), AutoTop);
}
