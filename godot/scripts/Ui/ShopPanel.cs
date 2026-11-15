using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Inotia4.Domain;
using Inotia4.Domain.Character;
using Inotia4.Domain.Content;
using Inotia4.Domain.Ui;

namespace Inotia4.Ui;

public sealed record ShopPanelContext(
    Storefront Store,
    ShopCatalog Catalog,
    ItemCatalog Items,
    CharacterStatsCatalog Stats,
    MenuPanelsCatalog Panels,
    Session Session,
    string ClassId,
    int Level,
    string ClassName,
    IReadOnlyDictionary<string, string> ClassNames,
    Func<string, Texture2D?> Texture,
    Action? Changed = null)
{
    public string? Asset(string key) => Catalog.Assets.GetValueOrDefault(key);

    public string? Frame(string key) => Panels.Asset(key);
}

public partial class ShopPanel : Control
{
    private const float DesignWidth = 1024f;

    private const float DesignHeight = 640f;

    private static readonly Vector2 ContentOrigin = new(32f, 172f);

    private static readonly Vector2 ContentSize = new(960f, 431f);

    private static readonly Color Gold = new("#f3deb0");

    private static readonly Color White = new("#ffffff");

    private static readonly Color AreaColor = new(ShopLayouts.AreaColor);

    private static readonly Color Notice = new("#e9a08a");

    private ShopPanelContext? _context;
    private ShopTrade? _trade;

    private Control? _modal;

    private Control _canvas = null!;
    private Control _content = null!;
    private Control _overlay = null!;
    private InventoryViewKind _filter = InventoryViewKind.All;
    private int _bagOffset;
    private int _categoryOffset;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        Visible = false;

        _canvas = new Control { Name = "Canvas", MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_canvas);
        DesignStage.Center(_canvas);

        _content = new Control
        {
            Name = "Content",
            Position = ContentOrigin,
            Size = ContentSize,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _canvas.AddChild(_content);

        _overlay = new Control
        {
            Name = "Overlay",
            Position = ContentOrigin,
            Size = ContentSize,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _canvas.AddChild(_overlay);

        Resized += LayoutCanvas;
        LayoutCanvas();
    }

    private void LayoutCanvas()
    {
        var viewport = GetViewportRect().Size;
        if (viewport.X <= 0f || viewport.Y <= 0f) return;

        var scale = Math.Min(viewport.X / DesignWidth, viewport.Y / DesignHeight);
        _canvas.Scale = new Vector2(scale, scale);
        _canvas.Position = new Vector2(
            (viewport.X - DesignWidth * scale) / 2f,
            (viewport.Y - DesignHeight * scale) / 2f);
    }

    public bool IsOpen => Visible;

    public string? DebugShopName => _context?.Store.Shop.Name ?? null;

    public TradeStep Step => _trade?.Step ?? TradeStep.Description;

    public void Open(ShopPanelContext context)
    {
        _context = context;
        _trade = new ShopTrade(context.Store, context.Session);
        _filter = InventoryViewKind.All;
        _bagOffset = 0;
        _categoryOffset = 0;
        Visible = true;
        LayoutCanvas();
        Render();
    }

    public void Close()
    {
        Visible = false;
        _trade?.Cancel();
    }

    public void Escape()
    {
        if (_trade is { Active: true })
        {
            _trade.Cancel();
            Render();
            return;
        }

        Close();
    }

    public bool DebugTrade(TradeStep step, TradeKind kind = TradeKind.Buy)
    {
        if (_trade is null) return false;
        var ok = _trade.DebugTrade(step, kind);
        if (ok) Render();
        return ok;
    }

    public void Render()
    {
        if (_context is not { } context || _trade is not { } trade) return;

        PanelGfx.Clear(_content);
        PanelGfx.Clear(_overlay);
        // 弹层挂在画布上而不是内容块上，所以单独清。
        if (_modal is not null)
        {
            _canvas.RemoveChild(_modal);
            _modal.QueueFree();
            _modal = null;
        }

        var classified = context.Store.Shop.Categories.Length > 0;
        var layout = ShopLayouts.Create(classified);

        DrawFrames(context, layout, classified);
        DrawShelf(context, layout, trade);
        DrawBag(context, layout, trade);
        DrawMoney(context, layout);
        DrawSideButtons(context, layout);

        if (!trade.Active) return;
        if (trade.Step == TradeStep.Description) DrawDescription(context, layout, trade);
        else DrawModal(context, trade);
    }

    private void DrawFrames(ShopPanelContext context, ShopLayout layout, bool classified)
    {
        PanelGfx.Border(context.Texture, context.Frame,
            layout.LeftFrame.X, layout.LeftFrame.Y, layout.LeftFrame.Width, layout.LeftFrame.Height, _content);
        PanelGfx.Border(context.Texture, context.Frame,
            layout.BagFrame.X, layout.BagFrame.Y, layout.BagFrame.Width, layout.BagFrame.Height, _content);

        var title = layout.Title;
        PanelGfx.Image(context.Texture, context.Asset("merchant-title"), title.X, title.Y, title.Width,
            title.Height, _content);
        PanelGfx.CenteredAt(context.Store.Shop.Name, layout.TitleName.X, layout.TitleName.Y, 230f, 26,
            AreaColor, _content);

        if (!classified) return;

        var column = layout.CategoryColumn;
        PanelGfx.Image(context.Texture, context.Asset("category-cap-top"),
            column.Cap.X, column.Cap.Y, column.Cap.Width, column.Cap.Height, _content);
        PanelGfx.Image(context.Texture, context.Asset("category-cap-bottom"),
            column.Cap.X, column.Cap.Bottom, column.Cap.Width, column.Cap.Height, _content);
        PanelGfx.Image(context.Texture, context.Asset("category-divider"),
            column.Divider.X, column.Divider.Y, column.Divider.Width, column.Divider.Height, _content);

        var category = layout.Category;
        var categories = context.Store.Shop.Categories;
        var (scroll, list) = NewScroll(category.Scroll, categories.Length * category.Button.Stride, _content);

        for (var index = 0; index < categories.Length; index += 1)
        {
            var name = categories[index];
            var selected = context.Store.Category == name;
            var button = PanelGfx.Hit(0f, index * category.Button.Stride, category.Button.Width,
                category.Button.Height, () =>
                {
                    context.Store.SelectCategory(name);
                    Render();
                }, list);
            PanelGfx.Image(context.Texture, context.Asset(selected ? $"{name}-selected" : name),
                0f, 0f, category.Button.Width, category.Button.Height, button);
        }

        scroll.ScrollVertical = _categoryOffset;
        scroll.GetVScrollBar().ValueChanged += value => _categoryOffset = (int)value;

        PanelGfx.TextPlate(context.Texture, context.Asset, category.Plate.X, category.Plate.Y,
            category.Plate.Width, _content);
        var current = context.Store.Category;
        PanelGfx.CenteredAt(current is null ? string.Empty : context.Catalog.Categories.GetValueOrDefault(current)?.Name ?? current,
            category.Name.X, category.Name.Y, 290f, 22, AreaColor, _content);
    }

    private void DrawShelf(ShopPanelContext context, ShopLayout layout, ShopTrade trade)
    {
        var slot = layout.Slot;
        var offers = context.Store.Offers;

        for (var index = 0; index < ShopLayouts.ShelfSlots; index += 1)
        {
            var x = layout.Shelf.X + index % 4 * slot.Stride;
            var y = layout.Shelf.Y + index / 4 * slot.Stride;
            PanelGfx.Image(context.Texture, context.Asset("slot"), x, y, slot.Size, slot.Size, _content);

            var offer = index < offers.Count ? offers[index] : null;
            if (offer is null || offer.Sold) continue;

            var item = context.Items.Items.GetValueOrDefault(offer.Instance.ItemId);
            var button = PanelGfx.Hit(x, y, slot.Size, slot.Size, () =>
            {
                if (trade.Select(TradeKind.Buy, offer.Id)) Render();
            }, _content);

            PanelGfx.Image(context.Texture, item?.Icon, slot.Icon.X, slot.Icon.Y, slot.Icon.Size,
                slot.Icon.Size, button);
            if (trade.Kind == TradeKind.Buy && trade.Id == offer.Id)
                PanelGfx.Image(context.Texture, context.Asset("selection"), slot.Selection.X,
                    slot.Selection.Y, slot.Selection.Size, slot.Selection.Size, button);
        }
    }

    private void DrawBag(ShopPanelContext context, ShopLayout layout, ShopTrade trade)
    {
        var bag = context.Session.Bag;
        if (bag is null) return;

        var grid = layout.Bag;
        var entries = bag.Entries
            .Where(entry => InventoryViews.Matches(context.Items.Items.GetValueOrDefault(entry.ItemId), _filter))
            .ToList();
        var slots = Math.Max(ShopLayouts.ShelfSlots, (entries.Count + grid.Columns - 1) / grid.Columns * grid.Columns);
        var rows = (slots + grid.Columns - 1) / grid.Columns;

        var rect = new UiRect(grid.X, grid.Y, grid.Width, grid.Height);
        var (scroll, list) = NewScroll(rect, rows * grid.Stride, _content);
        var slot = layout.Slot;

        for (var index = 0; index < slots; index += 1)
        {
            var x = index % grid.Columns * grid.Stride;
            var y = index / grid.Columns * grid.Stride;
            PanelGfx.Image(context.Texture, context.Asset("slot"), x, y, slot.Size, slot.Size, list);

            if (index >= entries.Count) continue;

            var entry = entries[index];
            var item = context.Items.Items.GetValueOrDefault(entry.ItemId);
            var button = PanelGfx.Hit(x, y, slot.Size, slot.Size, () =>
            {
                if (trade.Select(TradeKind.Sell, entry.Id)) Render();
            }, list);

            PanelGfx.Image(context.Texture, item?.Icon, slot.Icon.X, slot.Icon.Y, slot.Icon.Size,
                slot.Icon.Size, button);
            if (entry.Quantity > 1)
            {
                var badge = new Label
                {
                    Text = entry.Quantity.ToString(),
                    Position = new Vector2(layout.Badge.X - 40f, layout.Badge.Y),
                    Size = new Vector2(40f, 22f),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    MouseFilter = MouseFilterEnum.Ignore,
                };
                badge.AddThemeFontSizeOverride("font_size", 20);
                badge.AddThemeColorOverride("font_color", new Color(layout.Badge.Color));
                button.AddChild(badge);
            }

            if (trade.Kind == TradeKind.Sell && trade.Id == entry.Id)
                PanelGfx.Image(context.Texture, context.Asset("selection"), slot.Selection.X,
                    slot.Selection.Y, slot.Selection.Size, slot.Selection.Size, button);
        }

        scroll.ScrollVertical = _bagOffset;
        scroll.GetVScrollBar().ValueChanged += value => _bagOffset = (int)value;
    }

    private void DrawMoney(ShopPanelContext context, ShopLayout layout)
    {
        var money = layout.Money;
        PanelGfx.TextPlate(context.Texture, context.Asset, money.Plate.X, money.Plate.Y, money.Plate.Width,
            _content);
        MoneyRow(context, _content, context.Session.Money, money.X, money.Y);
    }

    /// <summary>
    /// 背包栏右侧那 6 个按钮，与背包页是同一列、同一套画法。它们切换背包分类，
    /// 所以卖东西时看到的就是筛出来的那一类。
    /// </summary>
    private void DrawSideButtons(ShopPanelContext context, ShopLayout layout)
    {
        var side = layout.SideButtons;
        var filters = InventoryViews.SideFilters;

        for (var index = 0; index < filters.Length; index += 1)
        {
            var filter = filters[index];
            var button = PanelGfx.Hit(side.X, side.Y + side.Offsets[index], side.Width, side.Height, () =>
            {
                _filter = filter;
                _bagOffset = 0;
                Render();
            }, _content);

            PanelGfx.Image(context.Texture, context.Frame("page-button-frame"), -5f, -12f, 0f, 0f, button);
            if (_filter == filter)
                PanelGfx.Image(context.Texture, context.Frame("page-button-selected"), -5f, -12f, 0f, 0f, button);
            PanelGfx.CenteredAt(InventoryViews.LabelOf(filter), side.Width / 2f, side.Height / 2f,
                side.Width - 6f, BagLayouts.SideFilterTextSize, Gold, button);
        }

        if (_filter == filters[0])
            PanelGfx.Image(context.Texture, context.Frame("column-cap-top"), side.X - 19f, side.Y - 12f, 21f,
                34f, _content);
        if (_filter == filters[^1])
            PanelGfx.Image(context.Texture, context.Frame("column-cap-bottom"), side.X - 19f,
                side.Y + side.Offsets[^1] + 38f, 21f, 34f, _content);
    }

    private void DrawDescription(ShopPanelContext context, ShopLayout layout, ShopTrade trade)
    {
        var instance = trade.Instance;
        if (instance is null)
        {
            trade.Cancel();
            return;
        }

        var buying = trade.Kind == TradeKind.Buy;
        var covered = buying ? layout.BagFrame : layout.LeftFrame;
        var panel = BagLayouts.DescriptionPanel(buying ? BagLayouts.BagDescriptionWidth : covered.Width);
        var left = covered.X;
        var top = panel.Y;

        PanelGfx.Fill(left + panel.Inset, top + panel.Inset, panel.Width - panel.Inset * 2,
            panel.Height - panel.Inset * 2, new Color(0f, 0f, 0f, panel.BackgroundAlpha), _overlay);
        PanelGfx.Border(context.Texture, context.Frame, left, top, panel.Width, panel.Height, _overlay);

        var item = context.Items.Get(instance.ItemId);
        var body = ItemDetailLines.Build(item, instance, context.Stats.Equipment.GetValueOrDefault(instance.ItemId),
            context.Stats, context.Catalog.Grades,
            new DescriptionMember(context.ClassId, context.Level), context.ClassNames,
            instance.Skill is { } skill && context.Items.SkillDescriptions.TryGetValue(skill, out var info)
                ? new DescriptionSkill(info.Name, info.ClassName)
                : null,
            trade.Concealed);

        PanelGfx.Text(body.Title, left + panel.Title.X, top + panel.Title.Y, (int)panel.Title.Size,
            body.Color is null ? White : new Color(body.Color), _overlay);

        BodyText(context, string.Join("\n", body.Rows.Select(row => BuildBbcode(row).TrimEnd('\n'))
            .Where(text => text.Length > 0)), panel, left, top);

        var close = panel.Close;
        var closeButton = PanelGfx.Hit(left + close.X, top + close.Y, close.Width, close.Height, () =>
        {
            trade.Cancel();
            Render();
        }, _overlay);
        PanelGfx.Image(context.Texture, context.Frame("close"), 0f, 0f, close.Width, close.Height, closeButton);

        var offset = panel.Menu.Offsets[1];
        var action = PanelGfx.Hit(left + panel.Menu.X + offset, top + panel.Menu.Y, panel.Menu.ButtonWidth,
            panel.Menu.ButtonHeight, () =>
            {
                trade.BeginTrade();
                Render();
            }, _overlay);
        TextButton(context, action, buying ? "购买" : "出售", panel.Menu.ButtonWidth, panel.Menu.ButtonHeight);
    }

    private void BodyText(ShopPanelContext context, string bbcode, DescriptionPanelLayout panel,
        float left, float top)
    {
        if (bbcode.Length == 0) return;

        var body = new RichTextLabel
        {
            BbcodeEnabled = true,
            ScrollActive = true,
            FitContent = false,
            Position = new Vector2(left + panel.Scroll.X, top + panel.Scroll.Y),
            Size = new Vector2(panel.Scroll.Width, panel.Scroll.Height),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = MouseFilterEnum.Stop,
            Text = bbcode,
        };
        body.AddThemeFontSizeOverride("normal_font_size", 22);
        body.AddThemeColorOverride("default_color", White);
        body.AddThemeConstantOverride("line_separation", 5);
        var bar = body.GetVScrollBar();
        foreach (var state in new[] { "scroll", "grabber", "grabber_highlight", "grabber_pressed" })
            bar.AddThemeStyleboxOverride(state, PanelGfx.EmptyStyle());
        _overlay.AddChild(body);
    }

    private void DrawModal(ShopPanelContext context, ShopTrade trade)
    {
        var instance = trade.Instance;
        if (instance is null)
        {
            trade.Cancel();
            return;
        }

        var buying = trade.Kind == TradeKind.Buy;
        var verb = buying ? "购买" : "出售";
        var item = context.Items.Get(instance.ItemId);

        if (trade.Step == TradeStep.Notice)
        {
            // 不可出售：只有一行提示，确定居中。
            DrawBox(context, ["此物品无法出售"], () =>
            {
                trade.Cancel();
                Render();
            }, cancel: false);
            return;
        }

        var total = trade.Total();

        if (trade.Step == TradeStep.Quantity)
        {
            DrawQuantity(context, trade, total, item?.Icon);
            return;
        }

        var insufficient = trade.Insufficient();
        var lines = new List<string?>
        {
            $"确定{verb} {item?.Name ?? instance.ItemId}{(trade.Quantity > 1 ? $" × {trade.Quantity}" : string.Empty)}？",
            trade.Error ?? (insufficient ? "金币不足" : null),
        };

        DrawBox(context, lines, () =>
        {
            if (trade.Confirm() is not null) Render();
            else
            {
                context.Changed?.Invoke();
                Render();
            }
        }, total: total, disabled: insufficient);
    }

    private void DrawQuantity(ShopPanelContext context, ShopTrade trade, int total, string? icon)
    {
        var box = TradeLayouts.Quantity;
        var modal = NewModal(box.Width, box.Height);
        PanelGfx.PopupPanel(context.Texture, context.Frame, box.Width, box.Height, modal);

        PanelGfx.Image(context.Texture, icon, box.Icon.X, box.Icon.Y, box.Icon.Size, box.Icon.Size, modal);

        var maximum = trade.Maximum();
        var minus = PanelGfx.Hit(box.Field.X + box.Step.Left, box.Field.Y, box.Step.Width, box.Step.Height,
            () =>
            {
                trade.Adjust(-1);
                Render();
            }, modal, trade.Quantity <= 1);
        PanelGfx.Image(context.Texture, context.Asset("count-left"), 0f, 0f, box.Step.Width,
            box.Step.Height, minus);

        var plus = PanelGfx.Hit(box.Field.X + box.Step.Right, box.Field.Y, box.Step.Width, box.Step.Height,
            () =>
            {
                trade.Adjust(1);
                Render();
            }, modal, trade.Quantity >= maximum);
        PanelGfx.Image(context.Texture, context.Asset("count-right"), 0f, 0f, box.Step.Width, box.Step.Height,
            plus);

        PanelGfx.Text(trade.Quantity.ToString(), box.Count.X, box.Count.Y, 30, new Color(box.CountColor), modal);

        PanelGfx.TextPlate(context.Texture, context.Asset, box.TextPlate.X, box.TextPlate.Y, box.TextPlate.Width,
            modal);
        MoneyRow(context, modal, total, box.Money.X, box.Money.Y);

        var confirm = PanelGfx.Hit(box.Button.Left, box.Button.Y, box.Button.Width, box.Button.Height, () =>
        {
            trade.ToConfirm();
            Render();
        }, modal);
        TextButton(context, confirm, "确定", box.Button.Width, box.Button.Height);

        var cancel = PanelGfx.Hit(box.Button.Right, box.Button.Y, box.Button.Width, box.Button.Height, () =>
        {
            trade.Cancel();
            Render();
        }, modal);
        TextButton(context, cancel, "取消", box.Button.Width, box.Button.Height);
    }

    private void DrawBox(ShopPanelContext context, IEnumerable<string?> lines, Action confirm,
        int total = 0, bool disabled = false, bool cancel = true)
    {
        var shown = lines.Where(line => !string.IsNullOrEmpty(line)).Select(line => line!).ToList();
        var box = TradeLayouts.Message(shown.Count, total > 0);
        var modal = NewModal(box.Width, box.Height);
        PanelGfx.PopupPanel(context.Texture, context.Frame, box.Width, box.Height, modal);

        for (var index = 0; index < shown.Count; index += 1)
        {
            var value = shown[index];
            var question = value.Contains('？');
            var label = new Label
            {
                Text = value,
                Position = new Vector2(box.Text.X - 205f, box.Text.Y + index * box.TextStep),
                Size = new Vector2(410f, (question ? 26 : 22) + 6f),
                HorizontalAlignment = HorizontalAlignment.Center,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            label.AddThemeFontSizeOverride("font_size", question ? 26 : 22);
            label.AddThemeColorOverride("font_color", question ? Gold : Notice);
            modal.AddChild(label);
        }

        if (total > 0)
        {
            PanelGfx.TextPlate(context.Texture, context.Asset, box.TextPlate.X, box.TextPlate.Y,
                box.TextPlate.Width, modal);
            MoneyRow(context, modal, total, box.Money.X, box.Money.Y);
        }

        if (cancel)
        {
            var no = PanelGfx.Hit(box.Button.Right, box.Button.Y, box.Button.Width, box.Button.Height, () =>
            {
                _trade?.Cancel();
                Render();
            }, modal);
            TextButton(context, no, "取消", box.Button.Width, box.Button.Height);
        }

        var x = cancel ? box.Button.Left : (box.Width - box.Button.Width) / 2f;
        var yes = PanelGfx.Hit(x, box.Button.Y, box.Button.Width, box.Button.Height, confirm, modal, disabled);
        TextButton(context, yes, "确定", box.Button.Width, box.Button.Height);
    }

    private Control NewModal(float width, float height)
    {
        var modal = new Control
        {
            MouseFilter = MouseFilterEnum.Ignore,
            Position = new Vector2((DesignWidth - width) / 2f, (DesignHeight - height) / 2f),
        };
        _canvas.AddChild(modal);
        _modal = modal;
        return modal;
    }

    private (ScrollContainer Scroll, Control Content) NewScroll(UiRect rect, float contentHeight, Control parent)
    {
        var scroll = new ScrollContainer
        {
            Position = new Vector2(rect.X, rect.Y),
            Size = new Vector2(rect.Width, rect.Height),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.ShowNever,
            ClipContents = true,
        };
        scroll.AddThemeStyleboxOverride("panel", PanelGfx.EmptyStyle());
        parent.AddChild(scroll);

        var list = new Control
        {
            CustomMinimumSize = new Vector2(rect.Width, contentHeight),
            Size = new Vector2(rect.Width, contentHeight),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        scroll.AddChild(list);
        return (scroll, list);
    }

    private static void MoneyRow(ShopPanelContext context, Control parent, int amount, float x, float y)
    {
        var money = ItemLayout.Money;
        var value = MoneyDisplay.SplitMoney(amount);
        var units = new (string Key, int Amount)[]
            { ("gold", value.Gold), ("silver", value.Silver), ("copper", value.Copper) };

        for (var index = 0; index < units.Length; index += 1)
        {
            var left = x + index * money.Stride;
            PanelGfx.Image(context.Texture, context.Asset(units[index].Key), left, y, money.CoinWidth,
                money.CoinHeight, parent);
            PanelGfx.Text(units[index].Amount.ToString(), left + money.DigitX, y + money.DigitY, 21, White,
                parent);
        }
    }

    private void TextButton(ShopPanelContext context, Button button, string label, float width, float height)
    {
        PanelGfx.ButtonSkin(context.Texture, width, height, button);
        PanelGfx.CenteredAt(label, width / 2f, height / 2f, width - 12f,
            (int)Math.Min(22f, height * .42f), Gold, button);
    }

    private static string BuildBbcode(ItemDescriptionRow row)
    {
        if (row.Parts is null) return Escape(row.Text ?? string.Empty);
        return string.Concat(row.Parts.Select(part =>
            part.Color is null
                ? Escape(part.Text)
                : $"[color={part.Color}]{Escape(part.Text)}[/color]"));
    }

    private static string Escape(string text) => text.Replace("[", "[lb]");
}
