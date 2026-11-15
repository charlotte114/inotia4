using System;
using System.Collections.Generic;

namespace Inotia4.Domain.Character;

/// <summary>
/// 背包翻页（用户要求，原版没有这一套）。
///
/// 原版背包是一块可滚动的四列网格：格位一多就要靠拖动／滚轮去找，找特定一格代价高，
/// 也看不出「包里一共有几页」。这里把它切成固定页——每页 <see cref="PageSize"/> 格，
/// 也就是原版网格本来的四列 × 四行，一页正好铺满网格区、不需要滚动。
///
/// 四行放得下：格子 74 见方、行距 83，第四行在 y=249，底边 249+74=323，
/// 正好落在网格区 323 的下沿（行距本身含 9 像素格间距，不能拿「行距 × 4」当占用高度）。
///
/// 分页只影响「显示哪一段格位」，不改袋位与格号：拖动落格、整理、堆叠都还落在原来的格上，
/// 所以翻页不会让界面与背包状态出现第二套坐标。
/// </summary>
public static class InventoryPaging
{
    public const int Columns = 4;

    public const int Rows = 4;

    public const int PageSize = Columns * Rows;

    public static int PageCount(int slotCount) =>
        Math.Max(1, (slotCount + PageSize - 1) / PageSize);

    public static int ClampPage(int page, int slotCount) =>
        Math.Clamp(page, 0, PageCount(slotCount) - 1);

    public static (int Start, int End) Range(int page, int slotCount)
    {
        var start = ClampPage(page, slotCount) * PageSize;
        return (start, Math.Min(slotCount, start + PageSize));
    }

    public static List<T> OnPage<T>(IReadOnlyList<T> slots, int page)
    {
        var (start, end) = Range(page, slots.Count);
        var page_ = new List<T>(end - start);
        for (var index = start; index < end; index += 1) page_.Add(slots[index]);
        return page_;
    }

    public static int PageOf(int cell) => Math.Max(0, cell) / PageSize;

    public static string Label(int page, int slotCount) =>
        $"第 {ClampPage(page, slotCount) + 1} / {PageCount(slotCount)} 页";

    /// <summary>
    /// 一页占用的高度：前 <c>Rows-1</c> 行各占一个行距，最后一行只占格子本身
    /// （行距里那 9 像素是格间距，不该算进高度）。四行 74 见方、行距 83 → 323。
    /// </summary>
    public static float GridHeight(float stride, float slotSize) => (Rows - 1) * stride + slotSize;
}
