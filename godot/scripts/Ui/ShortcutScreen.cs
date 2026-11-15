using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Inotia4.Domain.Character;
using Inotia4.Domain.Content;
using Inotia4.Domain.Ui;

namespace Inotia4.Ui;

public readonly record struct ShortcutEdit(string Kind, string Id, int Page);

public sealed record ShortcutScreenContext(
    ShortcutBar Bar,
    SkillCatalog Skills,
    MenuPanelsCatalog Panels,
    ControlSkinCatalog ControlSkin,
    Func<string, Texture2D?> Texture)
{
    public string? Asset(string key) => Panels.Asset(key);

    public string? Skin(string key) => ControlSkin.Asset(key);
}

/// <summary>
/// 登记快捷键这一屏（原版的快捷键菜单 <c>UIShortcutMenu</c>）：
/// 菜单整块让位、露出背后压暗的画面；顶部一行白色提示，
/// 右侧浮出与游戏 HUD 同一排的六个圆槽与换组钮，待登记的对象画在屏幕中间偏右的圆框里，
/// 左上角一枚圆形关闭钮。这一层以屏幕左上角为原点，圆槽位置读设备键位配置。
///
/// 技能页与背包页两处入口共用这一份绘制，所以抽出来由两边一起调，不再各写一份。
/// </summary>
public static class ShortcutScreen
{
    public const string Prompt = "请选择要设置的快捷键。";

    public const float SlotIconSize = 75f;

    public const float ItemIconSize = 64f;

    public const float SlotHitSize = 80f;

    /// <summary>
    /// 画这一屏。<paramref name="screen"/> 是 1024×640 的设计坐标层，
    /// <paramref name="scale"/>／<paramref name="offset"/> 是它相对视口的缩放与居中偏移——
    /// 圆槽位置按视口像素算，所以要换算回设计坐标。
    /// </summary>
    public static void Draw(Control screen, float scale, Vector2 offset, Vector2 viewport,
        ShortcutScreenContext context, ShortcutEdit edit,
        Action<int> assign, Action swap, Action close)
    {
        var safeScale = scale > 0f ? scale : 1f;

        // 屏幕像素 → 画布设计坐标（画布可能因窗口比例被居中，所以要减掉偏移）。
        Vector2 ToDesign((float X, float Y) point) =>
            new((point.X - offset.X) / safeScale, (point.Y - offset.Y) / safeScale);

        PanelGfx.Centered(Prompt, 0f, 110f, viewport.X / safeScale, 26, Colors.White, screen);

        var slots = context.Bar.Page(edit.Page);
        for (var slot = 0; slot < ShortcutLayout.SlotCount; slot += 1)
        {
            var slotIndex = slot;
            var center = ToDesign(ControlLayouts.ShortcutPoint(viewport.X, viewport.Y, slot));
            var hit = PanelGfx.Hit(center.X - SlotHitSize / 2f, center.Y - SlotHitSize / 2f,
                SlotHitSize, SlotHitSize, () => assign(slotIndex), screen);
            SlotFace(context, hit, slots[slot], SlotHitSize);
        }

        var swapPoint = ToDesign(ControlLayouts.SwapPoint(viewport.X, viewport.Y));
        var swapHit = PanelGfx.Hit(swapPoint.X - 37.5f, swapPoint.Y - 37.5f, 75f, 75f, swap, screen);
        PanelGfx.Image(context.Texture, context.Skin("swap"), 0f, 0f, 75f, 75f, swapHit);
        AddPressedLayer(swapHit, context, "swap-pressed", 0f, 0f, 75f, 75f);

        var pending = ToDesign(ControlLayouts.PendingPoint(viewport.X, viewport.Y));
        PanelGfx.Image(context.Texture, context.Skin("slot-shadow"),
            pending.X - 47f, pending.Y - 47f, 94f, 94f, screen);
        PanelGfx.Image(context.Texture, context.Skin("slot-ring"),
            pending.X - 47f, pending.Y - 47f, 94f, 94f, screen);
        if (Icon(context, edit.Kind, edit.Id) is { } icon)
        {
            var size = IconSize(edit.Kind);
            PanelGfx.Image(context.Texture, icon, pending.X - size / 2f, pending.Y - size / 2f, size, size, screen);
        }

        // 左上角圆形关闭钮：全菜单统一用菜单页右上角那枚图，这里只是需要更大的命中区，所以 67×67。
        var back = PanelGfx.Hit(12f, 12f, 67f, 67f, close, screen);
        PanelGfx.Image(context.Texture, context.Asset("close"), 0f, 0f, 67f, 67f, back);
        AddPressedLayer(back, context, "close-pressed", 0f, 0f, 67f, 67f);
    }

    public static float IconSize(string kind) => kind == ShortcutEntry.ItemKind ? ItemIconSize : SlotIconSize;

    public static string? Icon(ShortcutScreenContext context, string kind, string id) =>
        kind == ShortcutEntry.SkillKind
            ? context.Skills.Skills.GetValueOrDefault(id)?.Icon
            : context.Bar.Bag.Catalog.Items.GetValueOrDefault(id)?.Icon;

    private static void SlotFace(ShortcutScreenContext context, Control parent, ShortcutEntry? entry, float hitSize)
    {
        PanelGfx.Image(context.Texture, context.Skin("slot-shadow"), -7f, -7f, 94f, 94f, parent);
        PanelGfx.Image(context.Texture, context.Skin("slot-ring"), -7f, -7f, 94f, 94f, parent);
        if (entry is null) return;
        if (Icon(context, entry.Kind, entry.Id) is not { } icon) return;

        var size = IconSize(entry.Kind);
        var inset = (hitSize - size) / 2f;
        PanelGfx.Image(context.Texture, icon, inset, inset, size, size, parent);
    }

    private static void AddPressedLayer(Button button, ShortcutScreenContext context, string key,
        float x, float y, float width, float height)
    {
        var pressed = new Control
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
            Material = new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add },
        };
        button.AddChild(pressed);
        PanelGfx.Image(context.Texture, context.Skin(key), x, y, width, height, pressed);
        button.ButtonDown += () => pressed.Visible = true;
        button.ButtonUp += () => pressed.Visible = false;
        button.MouseExited += () => pressed.Visible = false;
    }
}
