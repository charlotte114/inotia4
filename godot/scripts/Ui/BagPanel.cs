using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Inotia4.Domain;
using Inotia4.Domain.Character;
using Inotia4.Domain.Content;
using Inotia4.Domain.Party;
using Inotia4.Domain.Ui;

namespace Inotia4.Ui;

public sealed record BagPanelContext(
    Bag Bag,
    ItemCatalog Items,
    CharacterStatsCatalog Stats,
    ShopCatalog Shops,
    MenuPanelsCatalog Panels,
    IReadOnlyDictionary<string, string> StoryAssets,
    PartyMemberView Member,
    Func<int> Money,
    IReadOnlyDictionary<string, string> ClassNames,
    Func<string, Texture2D?> Texture,
    ShortcutBar Bar,
    SkillCatalog Skills,
    ControlSkinCatalog ControlSkin,
    IReadOnlyList<string> MemberIds,
    Func<string, string?> MemberIconOf,
    Action<string>? SelectMember = null,
    Action? Changed = null,
    Action<string>? ShowPage = null,
    Action<string>? UiSound = null,
    PartyRoster? Party = null,
    Func<string>? ReviveTarget = null,
    Action<string>? Recruited = null,
    Func<string, RecoveryResult>? UseRecovery = null,
    Action? MerchantSummoned = null)
{
    public string? Asset(string key) => Panels.Asset(key);

    public string Owner => Member.Id;

    public bool IsPlayer => Member.IsPlayer;

    public string ClassId => Member.ClassId;

    public string ClassName => Member.ClassName;

    public int Level => Member.Level;
}

/// <summary>
/// 背包页。左栏装十个装备槽与人物预览，
/// 右栏是四列网格 + 六格分类列 + 金额，点物品在对面那一栏盖出说明面板。
///
/// 与原版的差别只在「怎么画」：这里用 Godot 的 TextureRect／Label／Button
/// 按同一批 UI 素材（<c>menu-panels.json</c> 的 assets）逐块贴，没有图集，也没有原版那套容器树。
/// 布局数值、绘制次序、按钮落点都与原版一致，所以两种实现的画面应当是同一个版面。
/// </summary>
public partial class BagPanel : Control
{
    private const float DesignWidth = 1024f;

    private const float DesignHeight = 640f;

    private static readonly Vector2 ContentOrigin = new(32f, 172f);

    private static readonly Vector2 ContentSize = new(960f, 431f);

    private static readonly Color Gold = new("#f3deb0");

    private static readonly Color White = new("#ffffff");

    private static readonly Color LevelText = new("#e2cb9e");

    private BagPanelContext? _context;

    private Control _canvas = null!;
    private Control _content = null!;
    private Control _overlay = null!;

    /// <summary>登记屏画在整块 1024×640 画布上（原版是另一层界面，盖在菜单之上），所以单独一层。</summary>
    private Control _screen = null!;

    private Control _tabs = null!;

    private Control? _modal;

    private InventoryViewKind _filter = InventoryViewKind.All;
    private string? _selectedItem;
    private string _descSide = "left";
    private int _gridScroll;

    private int _page;

    private ItemResult? _itemResult;

    private (string Id, int Quantity)? _discard;

    private string? _learnConfirm;

    private ShortcutEdit? _shortcutEdit;

    private sealed class DropTarget
    {
        public Control Node = null!;
        public string? EntryId;
        public string? Slot;
        public InventoryViewKind? View;
        public int? Cell;
    }

    private sealed class DragState
    {
        public string EntryId = string.Empty;
        public Vector2 Start;
        public float Held;
        public bool Active;
        public TextureRect? Ghost;
    }

    private readonly List<DropTarget> _drops = [];
    private DragState? _drag;

    private const float DragHoldSeconds = .35f;

    private const float DragSlop = 8f;

    private const float DragGhostSize = 72f;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        Visible = false;

        _canvas = new Control { Name = "Canvas", MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_canvas);
        DesignStage.Center(_canvas);

        _tabs = new Control
        {
            Name = "Tabs",
            Size = new Vector2(DesignWidth, DesignHeight),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _canvas.AddChild(_tabs);

        _content = new Control
        {
            Name = "Content",
            Position = ContentOrigin,
            Size = ContentSize,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _canvas.AddChild(_content);

        // 说明面板画在内容块之上，而且得单独一层：否则会被面板自己的黑底压住。
        _overlay = new Control
        {
            Name = "Overlay",
            Position = ContentOrigin,
            Size = ContentSize,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _canvas.AddChild(_overlay);

        _screen = new Control
        {
            Name = "Screen",
            Size = new Vector2(DesignWidth, DesignHeight),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _canvas.AddChild(_screen);

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

    /// <summary>
    /// 上一次重画有没有画出左侧角色栏。只有在说明／结果面板确实盖住左栏时才省掉它；
    /// 用消耗品（结果明细为空、东西也用完了）时左栏必须照常画，否则角色和装备槽整块消失。
    /// 调试入口 <c>--baguse</c> 用它做断言。
    /// </summary>
    public bool LeftColumnDrawn { get; private set; }

    public bool DebugUsedUp { get; private set; }

    public const string Page = "bag";

    public void Open(BagPanelContext context)
    {
        _context = context;
        _selectedItem = null;
        _itemResult = null;
        _discard = null;
        _learnConfirm = null;
        _shortcutEdit = null;
        _filter = InventoryViewKind.All;
        _gridScroll = 0;
        Visible = true;
        LayoutCanvas();
        Render();
    }

    public ItemResult? DebugUse(string itemId, bool addIfMissing = false)
    {
        if (_context is not { } context) return null;

        var entry = context.Bag.Entries.FirstOrDefault(candidate => candidate.ItemId == itemId);
        if (entry is null && addIfMissing)
        {
            context.Bag.Add(itemId, 1);
            entry = context.Bag.Entries.FirstOrDefault(candidate => candidate.ItemId == itemId);
        }

        if (entry is null) return null;

        _selectedItem = entry.Id;
        _descSide = "left";
        _itemResult = UseItem(context, entry);
        // 同一实例用完（数量归零、条目被删）时，这一屏必须回到普通背包界面。
        DebugUsedUp = context.Bag.Get(entry.Id) is null;
        Render();
        return _itemResult;
    }

    public void Close()
    {
        Visible = false;
        _selectedItem = null;
        _itemResult = null;
        _discard = null;
        _learnConfirm = null;
        _shortcutEdit = null;
        CancelDrag();
    }

    public void Render()
    {
        if (_context is not { } context) return;

        ReleaseModal();
        Clear(_content);
        Clear(_overlay);
        Clear(_screen);
        Clear(_tabs);
        _drops.Clear();

        if (_shortcutEdit is not null)
        {
            DrawShortcutScreen(context);
            return;
        }

        MenuTabBar.Draw(_tabs, context.StoryAssets, context.Texture, Page, context.ShowPage);

        DrawLeftColumn(context);
        DrawBagColumn(context);
        DrawDescription(context);
        DrawOperation(context);
    }

    public bool Escape()
    {
        if (_shortcutEdit is null) return false;
        _shortcutEdit = null;
        Render();
        return true;
    }

    private void DrawShortcutScreen(BagPanelContext context)
    {
        if (_shortcutEdit is not { } edit) return;

        ShortcutScreen.Draw(_screen, _canvas.Scale.X, _canvas.Position, GetViewportRect().Size,
            new ShortcutScreenContext(context.Bar, context.Skills, context.Panels, context.ControlSkin,
                context.Texture), edit,
            AssignShortcut,
            () =>
            {
                _shortcutEdit = edit with { Page = (edit.Page + 1) % ShortcutLayout.PageCount };
                Render();
            },
            () =>
            {
                _shortcutEdit = null;
                Render();
            });
    }

    private void AssignShortcut(int slot)
    {
        if (_context is not { } context || _shortcutEdit is not { } edit) return;

        var current = context.Bar.Page(edit.Page)[slot];
        var same = edit.Kind == ShortcutEntry.SkillKind &&
                   current is { Kind: ShortcutEntry.SkillKind } &&
                   current.Id == edit.Id;
        context.Bar.Assign(edit.Page, slot, same ? null : new ShortcutEntry(edit.Kind, edit.Id));
        context.Changed?.Invoke();
        Render();
    }

    private void DrawLeftColumn(BagPanelContext context)
    {
        LeftColumnDrawn = false;
        var layout = BagLayouts.Create();
        var slots = context.Panels.EquipmentSlots;
        var equipment = context.Bag.EquipmentFor(context.Owner);

        // 左栏要不要让位给说明面板，看两个判据：
        //   showingResult = 结果明细非空（消耗品的结果没有明细，不能算结果面板）
        //   describing    = 没有结果面板、且选中的那件东西还在背包里
        // 只用「有没有 itemResult」当判据会把用完即消失的消耗品算成结果面板：
        // 面板既画不出内容（明细为空）、又因为「被面板盖住」把角色与装备槽整块省掉。
        var showingResult = HasResultDetails(context);
        var describing = !showingResult && _selectedItem is not null && context.Bag.Get(_selectedItem) is not null;
        var covered = (describing || showingResult) && _descSide == "left";
        if (covered) return;
        if (!describing) _selectedItem = null;

        var frame = layout.LeftFrame;
        Border(frame.X, frame.Y, frame.Width, frame.Height, context, _content);

        foreach (var block in new UiRect[] { new(24f, 24f, 361f, 86f), new(24f, 110f, 105f, 157f), new(286f, 110f, 99f, 157f), new(24f, 267f, 361f, 140f) })
            Fill(frame.X + block.X, block.Y, block.Width, block.Height, new Color(0f, 0f, 0f, .6f), _content);

        foreach (var (key, x, y) in new (string, float, float)[] { ("character-nw", 83f, -2f), ("character-ne", 455f, -2f), ("character-sw", 83f, 392f), ("character-se", 455f, 392f) })
            Image(context.Asset(key), x, y, 41f, 41f, _content, context);

        MenuMemberColumn.Draw(_content, context.Texture, context.Asset, context.MemberIds,
            context.MemberIconOf, context.Owner, context.SelectMember);

        if ((context.IsPlayer
                ? context.MemberIconOf(context.Owner)
                : context.Member.PartyIcon) is { } portrait &&
            context.Texture(portrait) is { } portraitTexture)
        {
            var height = Math.Min(layout.Preview.Height, layout.Preview.Width *
                portraitTexture.GetHeight() / (float)portraitTexture.GetWidth());
            var width = height * portraitTexture.GetWidth() / (float)portraitTexture.GetHeight();
            Image(portrait,
                layout.Preview.X + (layout.Preview.Width - width) / 2f,
                layout.Preview.Y + (layout.Preview.Height - height) / 2f,
                width, height, _content, context);
        }

        foreach (var slot in slots)
        {
            var id = equipment.GetValueOrDefault(slot.Id);
            var entry = id is null ? null : context.Bag.Get(id);
            var item = entry is null ? null : context.Items.Items.GetValueOrDefault(entry.ItemId);
            var x = frame.X + slot.X;
            var y = slot.Y;

            Image(context.Asset("inventory-slot"), x, y, ItemLayout.SlotSize, ItemLayout.SlotSize, _content, context);
            Image(item?.Icon ?? slot.EmptyIcon, x + (item is null ? 13f : 5f), y + (item is null ? 13f : 5f),
                item is null ? 48f : 64f, item is null ? 48f : 64f, _content, context);

            var entryId = entry?.Id;
            var button = Hit(x, y, ItemLayout.SlotSize, ItemLayout.SlotSize, () =>
            {
                if (entryId is null) return;
                Select(entryId, "right");
            }, _content);

            _drops.Add(new DropTarget { Node = button, EntryId = entryId, Slot = slot.Id });

            if (entry is not null) SlotMarkers(entry, item, x, y, _content);
        }

        TextPlate(layout.Level.X, layout.Level.Y, layout.Level.Width, context, _content);
        Centered($"Lv. {context.Level}　{context.ClassName}", layout.Level.X, layout.Level.Y + 7f,
            layout.Level.Width, 22, LevelText, _content);
        LeftColumnDrawn = true;
    }

    private void DrawBagColumn(BagPanelContext context)
    {
        var layout = BagLayouts.Create();
        var frame = layout.BagFrame;
        Border(frame.X, frame.Y, frame.Width, frame.Height, context, _content);

        var showingResult = HasResultDetails(context);
        var describing = !showingResult && _selectedItem is not null && context.Bag.Get(_selectedItem) is not null;
        var panelCoversBag = (describing || showingResult) && _descSide == "right";

        DrawGrid(context, layout, panelCoversBag);
        DrawPager(context, layout, panelCoversBag);
        DrawSideFilters(context, layout);
        DrawOrganize(context, layout);
    }

    private void DrawOrganize(BagPanelContext context, BagLayout layout)
    {
        var organize = layout.Organize;
        Hit(organize.X, organize.Y, organize.Width, organize.Height, () =>
        {
            _itemResult = null;
            _selectedItem = null;
            context.Bag.Organize();
            context.Changed?.Invoke();
            Render();
        }, _content);
        Centered("整理", organize.X, organize.Y + 8f, organize.Width, 22, Gold, _content);
    }

    private void DrawGrid(BagPanelContext context, BagLayout layout, bool panelCoversBag)
    {
        var cells = CellsOf(context.Bag, _filter);
        var (_, slots) = InventoryViews.Layout(cells, _filter);
        var grid = layout.Grid;

        var filled = CurrentSlotCount(context);
        _page = InventoryPaging.ClampPage(_page, filled);
        var (firstCell, _) = InventoryPaging.Range(_page, filled);
        var slotCount = slots.Count;

        var content = new Control
        {
            Name = "Grid",
            Position = new Vector2(grid.X, grid.Y),
            Size = new Vector2(grid.Width,
                InventoryPaging.GridHeight(grid.Stride, ItemLayout.SlotSize)),
            MouseFilter = MouseFilterEnum.Ignore,
            ClipContents = true,
        };
        _content.AddChild(content);

        // 记下网格区（屏幕坐标），手势与滚轮翻页只在它上面生效。
        // 必须用 **内容块** 的变换：网格坐标是相对 _content 的，而 _content 自己在
        // (32,172)。拿 _canvas 的变换会漏掉这个偏移——y 会算成 45 而实际在 210 附近，
        // 手势就在错误的区域里判定（自检里量出来才发现）。
        _gridRect = new Rect2(
            _content.GetGlobalTransform() * new Vector2(grid.X, grid.Y),
            new Vector2(grid.Width, grid.Height) * _content.GetGlobalTransform().Scale);

        for (var offset = 0; offset < InventoryPaging.PageSize; offset += 1)
        {
            var cellIndex = firstCell + offset;
            var slot = cellIndex < slotCount ? slots[cellIndex] : null;
            var x = offset % grid.Columns * grid.Stride;
            var y = offset / grid.Columns * grid.Stride;

            if (slot is null || slot.Entry is null)
            {
                Image(context.Asset("inventory-slot"), x, y, ItemLayout.SlotSize, ItemLayout.SlotSize, content, context);
                var blank = Hit(x, y, ItemLayout.SlotSize, ItemLayout.SlotSize, () => { }, content);
                if (slot is not null)
                    _drops.Add(new DropTarget { Node = blank, View = slot.View, Cell = slot.Cell });
                continue;
            }

            var entry = slot.Entry;
            var item = context.Items.Items.GetValueOrDefault(entry.ItemId);
            Image(context.Asset("inventory-slot"), x, y, ItemLayout.SlotSize, ItemLayout.SlotSize, content, context);
            Image(item?.Icon, x + 5f, y + 5f, 64f, 64f, content, context);
            if (entry.Id == _selectedItem)
                Image(context.Asset("inventory-selected"), x - 5f, y - 5f, 84f, 84f, content, context);

            var entryId = entry.Id;
            var button = Hit(x, y, ItemLayout.SlotSize, ItemLayout.SlotSize, () => Select(entryId, "left"), content);
            if (!panelCoversBag) button.ButtonDown += () => BeginDrag(entryId);

            _drops.Add(new DropTarget { Node = button, EntryId = entryId, View = slot.View, Cell = slot.Cell });

            SlotMarkers(entry, item, x, y, content);
        }
    }

    private void DrawPager(BagPanelContext context, BagLayout layout, bool panelCoversBag)
    {
        DebugPagerDrawn = true;
        var filled = CurrentSlotCount(context);
        var pages = InventoryPaging.PageCount(filled);
        var slotCount = filled;

        if (panelCoversBag) return;

        const float arrowWidth = 32f, arrowHeight = 28f;
        const float labelWidth = 132f, gap = 6f;
        var total = arrowWidth * 2f + gap * 2f + labelWidth;

        var grid = layout.Grid;
        var startX = grid.X + (grid.Width - total) / 2f;

        var money = layout.Money;
        var centerY = money.Y + money.CoinHeight / 2f;
        var arrowY = centerY - arrowHeight / 2f;

        var previousX = startX;
        var labelX = previousX + arrowWidth + gap;
        var nextX = labelX + labelWidth + gap;

        var label = PanelGfx.CenteredAt(InventoryPaging.Label(_page, slotCount),
            labelX + labelWidth / 2f, centerY, labelWidth, 20, Gold, _content);
        label.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, .85f));
        label.AddThemeConstantOverride("shadow_offset_x", 1);
        label.AddThemeConstantOverride("shadow_offset_y", 1);
        _pagerPlate = label;

        PageArrow(context, previousX, arrowY, arrowWidth, arrowHeight, "arrow-left", _page > 0,
            () => TurnPage(-1), "上一页（也可以把网格往右拖、滚轮上滚、按 A/←）");
        PageArrow(context, nextX, arrowY, arrowWidth, arrowHeight, "arrow-right", _page < pages - 1,
            () => TurnPage(1), "下一页（也可以把网格往左拖、滚轮下滚、按 D/→）");
    }

    private sealed class PagerSwipe
    {
        public Vector2 Start;
        public bool Tracking;
    }

    private PagerSwipe? _swipe;

    private const float SwipeThreshold = 60f;

    private void PagerGestureInput(InputEvent @event)
    {
        if (_context is null || _discard is not null || _learnConfirm is not null) return;

        switch (@event)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp, Pressed: true }:
                if (PointerOnGrid()) TurnPage(-1);
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelDown, Pressed: true }:
                if (PointerOnGrid()) TurnPage(1);
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true }:
                GridPress(GetGlobalMousePosition());
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false }:
                GridRelease(GetGlobalMousePosition());
                break;
            case InputEventMouseMotion motion when _swipe is { Tracking: true }:
                _ = motion;
                break;
        }
    }

    private bool PointerOnGrid() => PointerOnGrid(GetGlobalMousePosition());

    private bool PointerOnGrid(Vector2 point) =>
        _gridRect is { } rect && rect.HasPoint(point);

    private void GridPress(Vector2 point)
    {
        if (!PointerOnGrid(point)) { _swipe = null; return; }
        _swipe = new PagerSwipe { Start = point, Tracking = true };
    }

    private void GridRelease(Vector2 point) => EndSwipe(point);

    private void EndSwipe(Vector2 end)
    {
        if (_swipe is not { Tracking: true } swipe) return;
        _swipe = null;

        var delta = end - swipe.Start;
        if (MathF.Abs(delta.X) < SwipeThreshold) return;
        if (MathF.Abs(delta.X) < MathF.Abs(delta.Y)) return;

        TurnPage(delta.X < 0 ? 1 : -1);
    }

    private Rect2? _gridRect;

    public bool PagerKey(Key key)
    {
        switch (key)
        {
            case Key.A:
            case Key.Left:
                TurnPage(-1);
                return true;
            case Key.D:
            case Key.Right:
                TurnPage(1);
                return true;
            default:
                return false;
        }
    }

    private void PageArrow(BagPanelContext context, float x, float y, float width, float height,
        string key, bool enabled, Action action, string label)
    {
        var path = context.ControlSkin.Asset(key);
        if (enabled)
        {
            var button = Hit(x, y, width, height, action, _content);
            Image(path, 0f, 0f, width, height, button, context);
            button.TooltipText = label;
            return;
        }

        var dim = PanelGfx.Image(context.Texture, path, x, y, width, height, _content);
        if (dim is not null) dim.Modulate = new Color(1f, 1f, 1f, .35f);
    }

    /// <summary>
    /// 当前分类下「用到第几格」，翻页的页数与夹取都以它为准。
    /// 不能直接用 <see cref="InventoryViews.Layout"/> 返回的格数：那会把空背包也补到 16 格，
    /// 于是 5 件物品也会显示两页、第二页全是空槽。
    /// </summary>
    private int CurrentSlotCount(BagPanelContext context) =>
        InventoryViews.UsedCells(CellsOf(context.Bag, _filter));

    private void TurnPage(int delta)
    {
        if (_context is not { } context) return;
        var count = CurrentSlotCount(context);
        var target = InventoryPaging.ClampPage(_page + delta, count);
        if (target == _page) return;
        var direction = target > _page ? 1 : -1;
        _page = target;
        _gridScroll = 0;
        Render();
        FlashPager(direction);
    }

    private void FlashPager(int direction)
    {
        if (_pagerPlate is null || !GodotObject.IsInstanceValid(_pagerPlate)) return;
        _pagerPlate.Modulate = new Color(1f, 1f, 1f, 1f);
        var tween = CreateTween();
        tween.TweenProperty(_pagerPlate, "modulate", new Color(1.35f, 1.15f, .6f), .06f);
        tween.TweenProperty(_pagerPlate, "modulate", new Color(1f, 1f, 1f), .22f);
        _ = direction;
    }

    private void DrawSideFilters(BagPanelContext context, BagLayout layout)
    {
        var side = layout.SideButtons;
        var filters = InventoryViews.SideFilters;

        for (var index = 0; index < filters.Length; index += 1)
        {
            var filter = filters[index];
            var x = side.X;
            var y = side.Y + side.Offsets[index];

            var button = Hit(x, y, side.Width, side.Height, () =>
            {
                _filter = filter;
                _itemResult = null;
                _selectedItem = null;
                _gridScroll = 0;
                _page = 0;
                Render();
            }, _content);

            Image(context.Asset("page-button-frame"), -5f, -12f, 0f, 0f, button, context);
            if (_filter == filter) Image(context.Asset("page-button-selected"), -5f, -12f, 0f, 0f, button, context);
            Centered(InventoryViews.LabelOf(filter), 0f, side.Height / 2f - 12f, side.Width,
                BagLayouts.SideFilterTextSize, Gold, button);
        }

        if (_filter == filters[0])
            Image(context.Asset("column-cap-top"), side.X - 19f, side.Y - 12f, 21f, 34f, _content, context);
        if (_filter == filters[^1])
            Image(context.Asset("column-cap-bottom"), side.X - 19f, side.Y + side.Offsets[^1] + 38f, 21f, 34f, _content, context);
    }

    private bool HasResultDetails(BagPanelContext context) => ResultLines(context).Count > 0;

    private List<string> ResultLines(BagPanelContext context) =>
        _itemResult is { } result
            ? ItemDetailLines.ResultDetails(result, context.Items,
                id => context.Skills.Skills.GetValueOrDefault(id)?.Name ?? id,
                id => context.ClassNames.GetValueOrDefault(id) ?? id)
            : new List<string>();

    private void DrawDescription(BagPanelContext context)
    {
        var ids = ResultLines(context);

        string? selectedId = null;
        if (ids.Count == 0 && _selectedItem is { } candidate && context.Bag.Get(candidate) is { } existing)
            selectedId = existing.Id;

        if (ids.Count == 0 && selectedId is null) return;

        var layout = BagLayouts.Create();
        var covered = _descSide == "left" ? layout.LeftFrame : layout.BagFrame;
        var panel = BagLayouts.DescriptionPanel(_descSide == "left" ? covered.Width : BagLayouts.BagDescriptionWidth);
        var left = covered.X;
        var top = panel.Y;

        Fill(left + panel.Inset, top + panel.Inset, panel.Width - panel.Inset * 2, panel.Height - panel.Inset * 2,
            new Color(0f, 0f, 0f, panel.BackgroundAlpha), _overlay);
        Border(left, top, panel.Width, panel.Height, context, _overlay);

        if (ids.Count > 0)
        {
            BodyText(string.Join("\n", ids.Select(Escape)), panel, left, top);
        }
        else
        {
            var entry = context.Bag.Get(selectedId!)!;
            var item = context.Items.Get(entry.ItemId);
            var worn = BagMenu.WornSlot(context.Bag, entry, context.Owner);
            var body = ItemDetailLines.Build(item, entry, context.Stats.Equipment.GetValueOrDefault(entry.ItemId),
                context.Stats, context.Shops.Grades,
                new DescriptionMember(context.ClassId, context.Level), context.ClassNames,
                entry.Skill is { } skill && context.Items.SkillDescriptions.TryGetValue(skill, out var info)
                    ? new DescriptionSkill(info.Name, info.ClassName)
                    : null);

            Text(body.Title, left + panel.Title.X, top + panel.Title.Y, (int)panel.Title.Size,
                body.Color is null ? White : new Color(body.Color), _overlay);

            BodyText(string.Join("\n", body.Rows.Select(row => BuildBbcode(row).TrimEnd('\n'))
                .Where(text => text.Length > 0)), panel, left, top);

            DrawDescriptionMenu(context, panel, left, top, entry, worn);
        }

        var close = panel.Close;
        Hit(left + close.X, top + close.Y, close.Width, close.Height, () =>
        {
            _itemResult = null;
            _selectedItem = null;
            Render();
        }, _overlay);
        Image(context.Asset("close"), left + close.X, top + close.Y, close.Width, close.Height, _overlay, context);
    }

    private void DrawDescriptionMenu(BagPanelContext context, DescriptionPanelLayout panel,
        float left, float top, ItemInstance entry, string? worn)
    {
        var shortcut = context.Bar.Assigned(ShortcutEntry.ItemKind, entry.ItemId);
        var actions = BagMenu.Build(context.Bag, entry, context.Owner, context.ClassId, context.Level,
            slot => context.Panels.SlotName(slot), shortcut, context.IsPlayer);

        var offset = panel.Menu.Offsets[Math.Min(actions.Count, 3)];
        var rowX = left + panel.Menu.X + offset;
        var rowY = top + panel.Menu.Y;

        for (var index = 0; index < actions.Count; index += 1)
        {
            var action = actions[index];
            var x = rowX + index * panel.Menu.ButtonStride;
            var button = Hit(x, rowY, panel.Menu.ButtonWidth, panel.Menu.ButtonHeight,
                () => Run(context, action, entry), _overlay, action.Disabled);
            // 按钮图本身就把「使用／快捷键／粉碎」印在图里（所以只贴图，label 只当无障碍标签）。
            Image(context.Asset(ActionImage(action.Kind)), -12f, -9f, 149f, 75f, button, context);
        }
    }

    private static string ActionImage(BagActionKind kind) => kind switch
    {
        BagActionKind.Unequip => "item-unequip",
        BagActionKind.Equip => "item-equip",
        BagActionKind.Use => "item-use",
        BagActionKind.Discard => "item-discard",
        BagActionKind.Shortcut => "skill-shortcut",
        BagActionKind.ClearShortcut => "skill-shortcut-cancel",
        _ => "item-use",
    };

    private ItemResult UseItem(BagPanelContext context, ItemInstance entry)
    {
        var use = context.Items.Items.GetValueOrDefault(entry.ItemId)?.Use;

        if (use?.Kind == ItemUseRules.Recruit)
        {
            var party = context.Party
                        ?? throw new InvalidOperationException("当前会话没有队伍，无法招募");
            var (member, result) = party.Recruit(entry.Id);
            context.Recruited?.Invoke(member.Id);
            return result;
        }

        // 恢复药要按全队结算（区间式回复全队同一数值、冷却按逻辑帧），
        // 所以在战斗侧算完再把结论拿回来。
        if (use?.Kind is ItemUseRules.RecoverHealth or ItemUseRules.Regenerate)
        {
            var recovery = context.UseRecovery
                           ?? throw new InvalidOperationException("当前无法结算恢复药");
            var result = recovery(entry.Id);
            if (!result.Used) throw new InvalidOperationException(RecoveryProblem(result.Reason));
            var definition = context.Items.Items.GetValueOrDefault(entry.ItemId);
            return new ItemResult
            {
                Kind = use!.Kind,
                Title = definition?.Name ?? entry.ItemId,
                Message = result.Amount > 0 ? $"恢复了 {result.Amount} 点" : string.Empty,
            };
        }

        if (use?.Kind == ItemUseRules.Revive)
        {
            var party = context.Party
                        ?? throw new InvalidOperationException("当前会话没有队伍，无法复活");
            var target = context.ReviveTarget?.Invoke() ?? party.ControlledId;
            return party.Revive(target, entry.Id);
        }

        // 用神灯：消耗归背包（上面的 Bag.Use），召唤归场景（原版 CHAR_UseItemEx 用途
        // 0x3e 的分支——CHARSYSTEM_Produce(2,7) 造 npc-7、CHAR_AppearFromCharacter 让他
        // 在主角身边现身）。背包只发通知，场景收到后关背包页、立商人；不触发就维持旧行为。
        if (use?.Kind == ItemUseRules.Merchant)
        {
            var result = context.Bag.Use(entry.Id);
            context.MerchantSummoned?.Invoke();
            return result;
        }

        return context.Bag.Use(entry.Id);
    }

    private static string RecoveryProblem(string? reason) => reason switch
    {
        "blocked" => "现在不能使用恢复药",
        "cooldown" => "恢复药还在冷却中",
        "full-health" => "生命值已满，不需要恢复",
        _ => "现在无法使用恢复药",
    };

    private void Run(BagPanelContext context, BagMenuAction action, ItemInstance entry)    {
        try
        {
            switch (action.Kind)
            {
                case BagActionKind.Equip when action.Slot is { } slot:
                    context.Bag.Equip(entry.Id, slot, context.Owner, context.ClassId, context.Level);
                    break;
                case BagActionKind.Unequip when action.Slot is { } slot:
                    context.Bag.Unequip(slot, context.Owner);
                    break;
                case BagActionKind.Use:
                    if (context.Items.Get(entry.ItemId)?.Use?.Kind == ItemUseRules.Learn)
                    {
                        _learnConfirm = entry.Id;
                        Render();
                        return;
                    }

                    _itemResult = UseItem(context, entry);
                    break;
                case BagActionKind.Discard:
                    _discard = (entry.Id, 1);
                    Render();
                    return;
                case BagActionKind.Shortcut:
                    CancelDrag();
                    _shortcutEdit = new ShortcutEdit(ShortcutEntry.ItemKind, entry.ItemId, 0);
                    Render();
                    return;
                case BagActionKind.ClearShortcut:
                    context.Bar.ClearObject(ShortcutEntry.ItemKind, entry.ItemId);
                    break;
            }
        }
        catch (Exception error)
        {
            GD.PushWarning($"背包操作失败：{action.Kind} {entry.ItemId} — {error.Message}");
            return;
        }

        context.Changed?.Invoke();
        Render();
    }

    private void DrawOperation(BagPanelContext context)
    {
        if (_learnConfirm is not null) { DrawLearnConfirm(context); return; }
        if (_discard is not { } pending) return;
        if (context.Bag.Get(pending.Id) is not { } entry)
        {
            _discard = null;
            return;
        }

        var item = context.Items.Get(entry.ItemId);
        var box = TradeLayouts.Quantity;
        var modal = NewModal(box.Width, box.Height);
        PanelGfx.PopupPanel(context.Texture, context.Asset, box.Width, box.Height, modal);

        var inner = box.Plate;
        PanelGfx.Text("丢弃物品", inner.X, inner.Y + 5f, 26, Gold, modal);

        var body = new RichTextLabel
        {
            BbcodeEnabled = true,
            ScrollActive = true,
            Position = new Vector2(inner.X, inner.Y + 37f),
            Size = new Vector2(inner.Width, 112f),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = MouseFilterEnum.Stop,
            Text = $"[font_size=25]{Escape(item.Name)}[/font_size]\n" +
                   $"[font_size=22]{Escape(string.IsNullOrEmpty(item.Description) ? "确认后执行本次操作。" : item.Description)}[/font_size]\n" +
                   $"[font_size=22]确定丢弃 {Escape(item.Name)}？[/font_size]",
        };
        body.AddThemeFontSizeOverride("normal_font_size", 22);
        body.AddThemeColorOverride("default_color", White);
        body.AddThemeConstantOverride("line_separation", 10);
        foreach (var state in new[] { "scroll", "grabber", "grabber_highlight", "grabber_pressed" })
            body.GetVScrollBar().AddThemeStyleboxOverride(state, PanelGfx.EmptyStyle());
        modal.AddChild(body);

        if (entry.Quantity > 1)
        {
            var row = inner.Y + 161f;
            var quantity = pending.Quantity;

            var minus = Hit(inner.X, row, 80f, 48f, () =>
            {
                _discard = (pending.Id, Math.Max(1, pending.Quantity - 1));
                Render();
            }, modal, quantity <= 1);
            TextButton(context, minus, "−", 80f, 48f);

            PanelGfx.Text(quantity.ToString(), inner.X + 108f, row + 8f, 24, White, modal);

            var plus = Hit(inner.X + 190f, row, 80f, 48f, () =>
            {
                _discard = (pending.Id, Math.Min(entry.Quantity, pending.Quantity + 1));
                Render();
            }, modal, quantity >= entry.Quantity);
            TextButton(context, plus, "＋", 80f, 48f);

            var all = Hit(inner.X + 260f, row, inner.Width - 260f, 48f, () =>
            {
                _discard = (pending.Id, entry.Quantity);
                Render();
            }, modal);
            TextButton(context, all, "全部", inner.Width - 260f, 48f);
        }

        var confirm = Hit(box.Button.Left, box.Button.Y, box.Button.Width, box.Button.Height, () =>
        {
            var amount = pending.Quantity;
            _discard = null;
            _selectedItem = null;
            try
            {
                context.Bag.Discard(pending.Id, amount);
            }
            catch (Exception error)
            {
                GD.PushWarning($"丢弃失败：{error.Message}");
            }

            context.Changed?.Invoke();
            Render();
        }, modal);
        TextButton(context, confirm, "确定", box.Button.Width, box.Button.Height);

        var cancel = Hit(box.Button.Right, box.Button.Y, box.Button.Width, box.Button.Height, () =>
        {
            _discard = null;
            Render();
        }, modal);
        TextButton(context, cancel, "取消", box.Button.Width, box.Button.Height);
    }

    private void DrawLearnConfirm(BagPanelContext context)
    {
        if (context.Bag.Get(_learnConfirm!) is not { } entry)
        {
            _learnConfirm = null;
            return;
        }

        var skillName = "未知技能";
        if (entry.Skill is { } skill && context.Items.SkillDescriptions.TryGetValue(skill, out var info))
            skillName = $"{info.Name}（{info.ClassName}）";

        var box = TradeLayouts.Quantity;
        var modal = NewModal(box.Width, box.Height);
        PanelGfx.PopupPanel(context.Texture, context.Asset, box.Width, box.Height, modal);

        var inner = box.Plate;
        PanelGfx.Text("学习技能", inner.X, inner.Y + 5f, 26, Gold, modal);

        var body = new RichTextLabel
        {
            BbcodeEnabled = true,
            ScrollActive = false,
            Position = new Vector2(inner.X, inner.Y + 37f),
            Size = new Vector2(inner.Width, 112f),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = MouseFilterEnum.Stop,
            Text = $"[font_size=25]{Escape(skillName)}[/font_size]\n" +
                   $"[font_size=22]{Escape("使用技能书提高技能等级。")}[/font_size]\n" +
                   "[font_size=22]是否学习该技能？[/font_size]",
        };
        body.AddThemeFontSizeOverride("normal_font_size", 22);
        body.AddThemeColorOverride("default_color", White);
        body.AddThemeConstantOverride("line_separation", 10);
        modal.AddChild(body);

        var confirm = Hit(box.Button.Left, box.Button.Y, box.Button.Width, box.Button.Height, () =>
        {
            var entryId = _learnConfirm!;
            _learnConfirm = null;
            try
            {
                if (context.Bag.Get(entryId) is { } current)
                {
                    _itemResult = UseItem(context, current);
                    _selectedItem = null;
                }
            }
            catch (Exception error)
            {
                _itemResult = new ItemResult
                {
                    Kind = ItemUseRules.Learn,
                    Title = "学习技能",
                    Message = error.Message,
                    Item = context.Bag.Get(entryId)?.ItemId,
                };
            }

            context.Changed?.Invoke();
            Render();
        }, modal);
        TextButton(context, confirm, "确定", box.Button.Width, box.Button.Height);

        var cancel = Hit(box.Button.Right, box.Button.Y, box.Button.Width, box.Button.Height, () =>
        {
            _learnConfirm = null;
            Render();
        }, modal);
        TextButton(context, cancel, "取消", box.Button.Width, box.Button.Height);
    }

    private Control NewModal(float width, float height)
    {
        var modal = new Control
        {
            Name = "Modal",
            MouseFilter = MouseFilterEnum.Ignore,
            Position = new Vector2((DesignWidth - width) / 2f, (DesignHeight - height) / 2f),
        };
        _canvas.AddChild(modal);
        _modal = modal;
        return modal;
    }

    private void ReleaseModal()
    {
        if (_modal is null) return;
        if (GodotObject.IsInstanceValid(_modal))
        {
            _canvas.RemoveChild(_modal);
            _modal.QueueFree();
        }

        _modal = null;
    }

    private static void TextButton(BagPanelContext context, Button button, string label, float width, float height)
    {
        PanelGfx.ButtonSkin(context.Texture, width, height, button);
        PanelGfx.CenteredAt(label, width / 2f, height / 2f, width - 12f,
            (int)Math.Min(22f, height * .42f), Gold, button);
    }

    private void BeginDrag(string entryId)
    {
        if (_context is null || _discard is not null || _learnConfirm is not null || _itemResult is not null)
            return;

        CancelDrag();
        _drag = new DragState
        {
            EntryId = entryId,
            Start = _canvas.GetLocalMousePosition(),
        };
    }

    public override void _Process(double delta)
    {
        if (_drag is not { } drag) return;

        var pointer = _canvas.GetLocalMousePosition();
        if (!drag.Active)
        {
            if (pointer.DistanceTo(drag.Start) > DragSlop) { CancelDrag(); return; }

            drag.Held += (float)delta;
            if (drag.Held >= DragHoldSeconds) ActivateDrag(drag, pointer);
            return;
        }

        if (drag.Ghost is not null && GodotObject.IsInstanceValid(drag.Ghost))
            drag.Ghost.Position = pointer - new Vector2(DragGhostSize / 2f, DragGhostSize / 2f);
    }

    public override void _Input(InputEvent @event)
    {
        // 手势翻页要先看：一条滑动与「拖动格子」可能同时起手，靠阈值区分
        // （横向超过 60 像素算翻页，否则交给下面的拖放逻辑）。
        if (_drag is null) PagerGestureInput(@event);

        if (_drag is null) return;
        if (@event is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false }) return;

        // 这一次按下交给拖动逻辑了，起手时挂着的那条手势一并作废——
        // 否则松手时它还挂着，下一次点击的位移会被算成翻页。
        _swipe = null;
        if (_drag.Active) EndDrag();
        else CancelDrag();
    }

    private bool MoveToCell(InventoryViewKind view, int cell, string entryId)
    {
        if (_context is not { } context) return false;
        bool moved;
        try
        {
            moved = context.Bag.PlaceInCell(entryId, view, cell);
        }
        catch (Exception error)
        {
            GD.PushWarning($"背包落格失败：{error.Message}");
            return false;
        }

        if (!moved) return false;
        context.UiSound?.Invoke("itemMove");
        context.Changed?.Invoke();
        Render();
        return true;
    }

    internal bool DebugMoveToCell(InventoryViewKind view, int cell, string entryId) =>
        MoveToCell(view, cell, entryId);

    private void ActivateDrag(DragState drag, Vector2 pointer)
    {
        if (_context is not { } context) return;
        if (context.Bag.Get(drag.EntryId) is not { } entry) { CancelDrag(); return; }

        drag.Active = true;
        _selectedItem = null;
        _itemResult = null;
        Render();

        var item = context.Items.Items.GetValueOrDefault(entry.ItemId);
        var ghost = PanelGfx.Image(context.Texture, item?.Icon,
            pointer.X - DragGhostSize / 2f, pointer.Y - DragGhostSize / 2f,
            DragGhostSize, DragGhostSize, _canvas);
        if (ghost is null) return;

        ghost.Modulate = new Color(1f, 1f, 1f, .8f);
        drag.Ghost = ghost;
    }

    private void EndDrag(Vector2? dropPoint = null)
    {
        var drag = _drag;
        if (drag is null) return;

        var entryId = drag.EntryId;
        var point = dropPoint ?? GetGlobalMousePosition();
        DropTarget? target = null;
        if (drag.Active)
        {
            foreach (var drop in _drops)
            {
                if (!GodotObject.IsInstanceValid(drop.Node)) continue;
                if (drop.EntryId == entryId) continue;
                if (!drop.Node.GetGlobalRect().HasPoint(point)) continue;
                target = drop;
                break;
            }
        }

        CancelDrag();
        if (target is null || _context is not { } context) return;
        if (context.Bag.Get(entryId) is not { } entry) return;

        if (target.EntryId is not null && context.Bag.CanApply(entryId, target.EntryId))
        {
            ApplyItem(context, entryId, target.EntryId);
            return;
        }

        if (target.View is { } view && target.Cell is { } cell)
        {
            MoveToCell(view, cell, entryId);
            return;
        }

        if (target.Slot is { } slot && context.Items.Items.GetValueOrDefault(entry.ItemId)?.Equipment is not null)
        {
            if (context.Bag.EquipProblem(entryId, slot, context.Owner, context.ClassId, context.Level) is not null)
            {
                Render();
                return;
            }

            ChangeEquipment(context, entryId, slot);
            return;
        }

        Render();
    }

    private void CancelDrag()
    {
        if (_drag?.Ghost is { } ghost && GodotObject.IsInstanceValid(ghost))
        {
            _canvas.RemoveChild(ghost);
            ghost.QueueFree();
        }

        _swipe = null;
        _drag = null;
    }

    private void ApplyItem(BagPanelContext context, string source, string target)
    {
        try
        {
            _itemResult = context.Bag.Use(source, target);
            context.UiSound?.Invoke("enchant");
        }
        catch (Exception error)
        {
            GD.PushWarning($"使用物品失败：{error.Message}");
        }

        context.Changed?.Invoke();
        Render();
    }

    private void ChangeEquipment(BagPanelContext context, string entryId, string slot)
    {
        try
        {
            context.Bag.Equip(entryId, slot, context.Owner, context.ClassId, context.Level);
            _selectedItem = null;
        }
        catch (Exception error)
        {
            GD.PushWarning($"换装失败：{error.Message}");
        }

        context.Changed?.Invoke();
        Render();
    }

    private static void Clear(Node node) => PanelGfx.Clear(node);

    private static void Image(string? path, float x, float y, float width, float height,
        Control parent, BagPanelContext context, bool flipH = false, bool flipV = false) =>
        PanelGfx.Image(context.Texture, path, x, y, width, height, parent, flipH, flipV);

    private static void Fill(float x, float y, float width, float height, Color color, Control parent,
        bool beforeContent = false) => PanelGfx.Fill(x, y, width, height, color, parent, beforeContent);

    private static Button Hit(float x, float y, float width, float height, Action action,
        Control parent, bool disabled = false) =>
        PanelGfx.Hit(x, y, width, height, action, parent, disabled);

    private static void Border(float x, float y, float width, float height, BagPanelContext context,
        Control parent) =>
        PanelGfx.Border(context.Texture, context.Asset, x, y, width, height, parent);

    private void TextPlate(float x, float y, float width, BagPanelContext context, Control parent) =>
        PanelGfx.TextPlate(context.Texture, context.Asset, x, y, width, parent);

    private Label Text(string value, float x, float y, int size, Color color, Control parent) =>
        PanelGfx.Text(value, x, y, size, color, parent);

    private void Centered(string value, float x, float y, float width, int size, Color color, Control parent) =>
        PanelGfx.Centered(value, x, y, width, size, color, parent);

    private static StyleBoxEmpty EmptyStyle() => PanelGfx.EmptyStyle();

    public bool SelectById(string id, string side = "left")
    {
        if (_context?.Bag.Get(id) is null) return false;
        Select(id, side);
        return true;
    }

    public bool BeginDiscard(string id)
    {
        if (_context?.Bag.Get(id) is null) return false;
        _discard = (id, 1);
        Render();
        return true;
    }

    private Label? _pagerPlate;

    public bool DebugPagerDrawn { get; private set; }

    public int DebugPage => _page;

    public string DebugPageLabel =>
        _context is { } context ? InventoryPaging.Label(_page, CurrentSlotCount(context)) : string.Empty;

    public int DebugSlotCount => _context is { } context ? CurrentSlotCount(context) : 0;

    public void DebugTurnPage(int delta) => TurnPage(delta);

    public bool DebugSwipe(float dx)
    {
        if (_gridRect is not { } rect) return false;
        var start = rect.GetCenter();
        _swipe = new PagerSwipe { Start = start, Tracking = true };
        EndSwipe(start + new Vector2(dx, 0f));
        return true;
    }

    public void DebugPressGrid()
    {
        if (_gridRect is not { } rect) return;
        GridPress(rect.GetCenter());
        CancelDrag();
    }

    public void DebugClick(Vector2 point)
    {
        GridPress(point);
        GridRelease(point);
    }

    public Rect2? DebugGridRect => _gridRect;

    public bool DebugDrag(string id, InventoryViewKind view, int cell)
    {
        if (_context?.Bag.Get(id) is null) return false;

        var target = _drops.FirstOrDefault(drop => drop.View == view && drop.Cell == cell);
        if (target is null || !GodotObject.IsInstanceValid(target.Node)) return false;

        _drag = new DragState { EntryId = id, Active = true };
        EndDrag(target.Node.GetGlobalRect().GetCenter());

        var cells = CellsOf(_context!.Bag, view);
        return cell < cells.Count && cells[cell]?.Id == id;
    }

    public bool DebugShortcut(string id)
    {
        if (_context?.Bag.Get(id) is not { } entry) return false;
        _shortcutEdit = new ShortcutEdit(ShortcutEntry.ItemKind, entry.ItemId, 0);
        Render();
        return true;
    }

    public bool DebugAssign(int slot)
    {
        if (_shortcutEdit is not { } edit || _context is null) return false;
        AssignShortcut(slot);
        var row = _context.Bar.Page(edit.Page);
        GD.Print($"[bag] 快捷位 {slot + 1} → {(row[slot] is { } entry ? $"{entry.Kind}:{entry.Id}" : "空")}");
        return true;
    }

    private void Select(string id, string side)
    {
        _itemResult = null;
        _descSide = side;
        _selectedItem = _selectedItem == id ? null : id;
        Render();
    }

    private static IReadOnlyList<ItemInstance?> CellsOf(Bag bag, InventoryViewKind view)
    {
        if (view == InventoryViewKind.All) return bag.AllCells;
        var pouch = view switch
        {
            InventoryViewKind.Weapon => InventoryPouch.Weapon,
            InventoryViewKind.Armor => InventoryPouch.Armor,
            InventoryViewKind.Consumable => InventoryPouch.Consumable,
            InventoryViewKind.Material => InventoryPouch.Material,
            _ => InventoryPouch.Quest,
        };
        return bag.PouchCells.TryGetValue(pouch, out var cells) ? cells : [];
    }

    private void SlotMarkers(ItemInstance entry, ItemDefinition? item, float x, float y, Control parent)
    {
        if (item is { Stackable: true })
        {
            var label = new Label
            {
                Text = entry.Quantity.ToString(),
                Position = new Vector2(x + 27f, y + 47f),
                Size = new Vector2(40f, 20f),
                HorizontalAlignment = HorizontalAlignment.Right,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            label.AddThemeFontSizeOverride("font_size", 20);
            label.AddThemeColorOverride("font_color", White);
            parent.AddChild(label);
        }

        if (entry.Enhancement != 0)
            Text($"+{entry.Enhancement}", x + 2f, y + 2f, 17, Gold, parent);
        if (entry.Gems.Count > 0)
            Text(string.Concat(Enumerable.Repeat("◆", entry.Gems.Count)), x + 2f, y + 52f, 16, White, parent);
    }

    private void BodyText(string bbcode, DescriptionPanelLayout panel, float left, float top)
    {
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
