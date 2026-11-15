using System;
using System.Collections.Generic;
using Godot;
using Inotia4.Domain;

namespace Inotia4.Ui;

internal static class SaveRowView
{
    /// <summary>
    /// 一行存档的读数、动作与「能不能删除」。
    /// 行内原本还有「改名」「删除」两枚文字按钮并排在右下角；改名按用户要求去掉了，
    /// 删除改成右上角那枚叉（<see cref="DeleteArt"/>），所以这里只剩一个布尔。
    /// </summary>
    public sealed record Row(
        SlotSummaryReading Reading,
        Action? Action,
        string Title,
        bool Auto,
        Action? Delete = null);

    public static readonly Color SlotTextColor = new(SaveBoard.SlotTextHex);

    public static readonly Color SlotTimeTextColor = new(SaveBoard.SlotTimeTextHex);

    public const string DeleteArt = "assets/ui/title-menu/cancel.png";

    /// <summary>贴图缺失时返回 null 而不是抛异常（缺图不该让整页失败）。</summary>
    private static Texture2D? Safe(Func<string, Texture2D?> texture, string path)
    {
        try
        {
            return texture(path);
        }
        catch
        {
            return null;
        }
    }

    public static void Draw(Func<string, Texture2D?> texture, string? framePath, string? emptyPath,
        float originX, float top, Row row, Control parent)
    {
        PanelGfx.Image(texture, framePath, originX, top,
            SaveBoard.FrameWidth, SaveBoard.FrameHeight, parent);
        var left = originX + SaveBoard.RowInset;
        var bottom = top + SaveBoard.RowInset;

        if (row.Reading is SlotSummaryReading.Ok ok)
        {
            var summary = ok.Summary;
            // 立绘只给高度，宽度按原图比例：人物页立绘是竖幅的胸像。
            // 旧档引用的图没了也只是少一张画，不该让整页失败。
            var portraitPath = summary.Portrait;
            if (portraitPath is { Length: > 0 } && Safe(texture, portraitPath) is { } portrait)
            {
                var width = portrait.GetHeight() > 0
                    ? SaveBoard.PortraitHeight * portrait.GetWidth() / portrait.GetHeight()
                    : 0f;
                PanelGfx.Image(texture, portraitPath, left + 20f, bottom + 12f, width,
                    SaveBoard.PortraitHeight, parent);
            }

            PanelGfx.Text(row.Title, left + 170f, bottom + 14f, 22, SlotTextColor, parent);
            PanelGfx.Text(summary.MapName, left + 170f, bottom + 48f, 20, SlotTextColor, parent);
            PanelGfx.Text($"LV.{summary.Level} · {summary.ClassName}", left + 170f, bottom + 78f, 18,
                SlotTextColor, parent);
            var savedAt = SaveBoard.FormatSavedAt(summary.SavedAt);
            if (savedAt.Length > 0)
                PanelGfx.Text(savedAt, left + 170f, bottom + 106f, 18, SlotTimeTextColor, parent);
        }
        else
        {
            PanelGfx.Image(texture, emptyPath,
                originX + (SaveBoard.PanelWidth - SaveBoard.EmptyWidth) / 2f,
                top + (SaveBoard.FrameHeight - SaveBoard.EmptyHeight) / 2f,
                SaveBoard.EmptyWidth, SaveBoard.EmptyHeight, parent);
            if (row.Auto)
                PanelGfx.Text(row.Title, left + 170f, bottom + 58f, 22, SlotTextColor, parent);
        }

        if (row.Action is not null)
        {
            var width = row.Delete is not null
                ? SaveBoard.DeleteLeft - left
                : SaveBoard.RowWidth;
            PanelGfx.Hit(left, bottom, width, SaveBoard.RowHeight, row.Action, parent);
        }

        if (row.Delete is not { } delete) return;

        var art = Safe(texture, DeleteArt);
        var deleteX = originX + SaveBoard.DeleteLeft;
        var deleteTop = top + SaveBoard.DeleteInset;
        if (art is not null)
            PanelGfx.Image(texture, DeleteArt, deleteX, deleteTop, SaveBoard.DeleteSize,
                SaveBoard.DeleteSize, parent);
        var hit = PanelGfx.Hit(deleteX, deleteTop, SaveBoard.DeleteSize, SaveBoard.DeleteSize, delete,
            parent);
        hit.TooltipText = $"删除{row.Title}";
    }
}
