using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Inotia4.Domain;
using Inotia4.Domain.Character;
using Inotia4.Domain.Content;
using Inotia4.Domain.Ui;

namespace Inotia4.Ui;

/// <summary>
/// 合成页要用的外部依赖：合成规则、物品与属性表、品质表、面板文案、会话与主角身份。
/// 标题前的「太古的」「死亡」这类前缀靠品质分档表取，所以整份 <see cref="ShopCatalog"/>
/// 也要传进来。
/// </summary>
public sealed record CraftingPanelContext(
    EquipmentCrafting Crafting,
    ItemCatalog Items,
    CharacterStatsCatalog Stats,
    ShopCatalog Shops,
    MenuPanelsCatalog Panels,
    Session Session,
    string ActorName,
    string ClassId,
    string ClassName,
    int Level,
    IReadOnlyDictionary<string, string> ClassNames,
    Func<string, Texture2D?> Texture,
    FusionBench? Fusion = null,
    Func<double>? Random = null,
    Action? Changed = null,
    Action<string>? UiSound = null)
{
    public string? Asset(string key) => Panels.Asset(key);

    public string Message(string key) => Panels.Message(key);

    public Bag Bag => Fusion?.Bag ?? Crafting.Bag;
}

public partial class CraftingPanel : Control
{
    private const float DesignWidth = 1024f;

    private const float DesignHeight = 640f;

    private static readonly Vector2 ContentOrigin = new(32f, 172f);

    private static readonly Vector2 ContentSize = new(960f, 431f);

    private static readonly Color Gold = new("#f3deb0");

    private static readonly Color White = new("#ffffff");

    private static readonly Color AreaColor = new(ShopLayouts.AreaColor);

    private static readonly Color Short = new("#ff0000");

    private static readonly string[] Tiers = [CraftingCatalog.ChaosTier, CraftingCatalog.EpicChaosTier];

    private CraftingPanelContext? _context;

    private Control _canvas = null!;
    private Control _content = null!;
    private Control _top = null!;
    private Control? _modal;

    /// <summary>
    /// 当前菜单页：五类之一，取 <c>fusion.json</c> 的菜单 id。五类的先后与内存文本池里那五条
    /// 菜单文案一致（药水合成、宝石强化、宝石孔生成、混沌合成、传说装备，见 SO_REVERSE §17）。
    /// </summary>
    private string _page = FusionCatalog.ChaosMenu;

    private string _tab = CraftingCatalog.ChaosTier;

    /// <summary>
    /// 左栏当前是哪一副面孔。三套控件叠在同一块区域、靠显隐切换（依据：反汇编读出的控件树，
    /// 见 SO_REVERSE §17.11）：先看五类菜单，选一类看这一类的配方，
    /// 选中一条才看加工区。
    /// </summary>
    private LeftView _view = LeftView.Menu;

    private string? _recipe;

    private int _batch = 1;

    private enum LeftView
    {
        Menu,
        Recipes,
        Detail,
    }

    private string? _target;
    private InventoryViewKind _filter = InventoryViewKind.All;
    private int _offset;

    private string? _message;

    private CraftingQuote? _confirmation;

    private FusionQuote? _fusionConfirmation;

    private bool _committing;

    private bool IsChaosPage => _page == FusionCatalog.ChaosMenu;

    private FusionRecipe? RecipeOf(CraftingPanelContext context) =>
        IsChaosPage || _recipe is null || context.Fusion is null
            ? null
            : context.Fusion.Catalog.Recipe(_recipe);

    /// <summary>当前页签下，这件物品能不能当目标；能就返回 <c>null</c>。</summary>
    private string? TargetProblemOf(CraftingPanelContext context, string? id)
    {
        if (IsChaosPage) return context.Crafting.TargetProblem(_tab, id);
        if (RecipeOf(context) is { NeedsTarget: true } recipe && context.Fusion is { } bench)
            return bench.TargetProblem(recipe, id);
        return CraftingCatalog.Notice.Target;
    }

    private bool NeedsTargetOf(CraftingPanelContext context) =>
        IsChaosPage || RecipeOf(context) is { NeedsTarget: true };

    private int BatchTimes(CraftingPanelContext context) =>
        RecipeOf(context) is { Product: not null } ? Math.Max(1, _batch) : 1;

    private int MaxBatchOf(CraftingPanelContext context) =>
        RecipeOf(context) is { Product: not null } recipe && context.Fusion is { } bench
            ? bench.MaxBatch(recipe.Id)
            : 1;

    private sealed class DropTarget
    {
        public Control Node = null!;
        public string? EntryId;
        public InventoryViewKind? View;
        public int? Cell;
        public bool Craft;
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

        _content = new Control
        {
            Name = "Content",
            Position = ContentOrigin,
            Size = ContentSize,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _canvas.AddChild(_content);

        _top = new Control
        {
            Name = "Top",
            Position = Vector2.Zero,
            Size = new Vector2(DesignWidth, DesignHeight),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _canvas.AddChild(_top);

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

    public bool ModalOpen =>
        _message is not null || _confirmation is not null || _fusionConfirmation is not null;

    public void Open(CraftingPanelContext context)
    {
        _context = context;
        _page = FusionCatalog.PotionMenu;
        _tab = CraftingCatalog.ChaosTier;
        _recipe = null;
        _view = LeftView.Menu;
        _target = null;
        _batch = 1;
        _filter = InventoryViewKind.All;
        _offset = 0;
        _message = null;
        _confirmation = null;
        _fusionConfirmation = null;
        _committing = false;
        CancelDrag();
        Visible = true;
        LayoutCanvas();
        Render();
    }

    public void Close()
    {
        Visible = false;
        _message = null;
        _confirmation = null;
        CancelDrag();
    }

    /// <summary>关闭键：有提示／确认框就先收它，否则关掉整页（Esc 走同一条次序）。</summary>
    public void Escape()
    {
        if (ModalOpen)
        {
            Cancel();
            return;
        }

        Close();
    }

    public void Choose(string tab)
    {
        _page = tab;
        _target = null;
        _recipe = null;
        _batch = 1;
        _tab = IsChaosPage ? CraftingCatalog.ChaosTier : tab;

        if (_context?.Fusion is { } bench && !IsChaosPage && bench.Recipes(tab) is { Count: 1 } single)
            _recipe = single[0].Id;

        _view = NeedsRecipeList() ? LeftView.Recipes : LeftView.Detail;

        Cancel();
    }

    private bool NeedsRecipeList()
    {
        if (IsChaosPage) return Tiers.Count(tier => _context?.Crafting.Catalog.Operations.ContainsKey(tier) == true) > 1;
        return _context?.Fusion is { } bench && bench.Recipes(_tab).Count > 1;
    }

    public void OpenMenu()
    {
        if (ModalOpen || _committing) return;
        _view = LeftView.Menu;
        Render();
    }

    public void PickTier(string tier)
    {
        if (ModalOpen || _committing) return;
        _tab = tier;
        _view = LeftView.Detail;
        _target = null;
        Render();
    }

    public void Pick(string recipeId)
    {
        if (ModalOpen || _committing) return;
        _recipe = recipeId;
        _batch = 1;
        _view = LeftView.Detail;
        Render();
    }

    public void SetBatch(int times)
    {
        if (ModalOpen || _committing || _context is not { } context) return;
        if (RecipeOf(context) is not { Product: not null }) return;
        _batch = Math.Clamp(times, 1, MaxBatchOf(context));
        Render();
    }

    /// <summary>
    /// 现在能当目标的道具（背包里的顺序）。目标格两侧那两枚三角就在这份名单里循环：
    /// 三角本身只做序号 ±1 回绕，真正的「能不能用」仍由 <see cref="TargetProblemOf"/> 判，
    /// 这里先筛掉不能用的，免得转过一圈全是提示。
    /// </summary>
    private List<string> TargetCandidates(CraftingPanelContext context) =>
        context.Bag.Entries
            .Where(entry => TargetProblemOf(context, entry.Id) is null)
            .Select(entry => entry.Id)
            .ToList();

    public void CycleTarget(int direction)
    {
        if (_context is not { } context || ModalOpen || _committing) return;
        if (_view != LeftView.Detail || !NeedsTargetOf(context)) return;

        var candidates = TargetCandidates(context);
        if (candidates.Count == 0) return;

        var index = _target is null ? -1 : candidates.IndexOf(_target);
        var next = index < 0
            ? (direction >= 0 ? 0 : candidates.Count - 1)
            : ((index + direction) % candidates.Count + candidates.Count) % candidates.Count;

        _target = candidates[next];
        Render();
    }

    public bool DebugBatch(int times)
    {
        if (_context is not { } context || RecipeOf(context) is not { Product: not null }) return false;
        SetBatch(times);
        return _batch == times;
    }

    /// <summary>选目标：先问能不能合成这一档，不能就把理由放进提示。</summary>
    public void Select(string target)
    {
        if (_context is not { } context) return;
        if (ModalOpen || _committing) return;

        // 药水、宝石两页是「选配方」而不是「选装备」，点背包格子不该弹「请选择道具」。
        // 混沌合成还停在两档列表上时同理：那一格还没出来。
        if (!NeedsTargetOf(context)) return;
        if (_view != LeftView.Detail) return;

        var problem = TargetProblemOf(context, target);

        if (problem is not null) _message = context.Message(problem);
        else _target = target;

        Render();
    }

    public void Begin()
    {
        if (_context is not { } context) return;
        if (ModalOpen || _committing) return;

        if (IsChaosPage)
        {
            if (_view != LeftView.Detail)
            {
                _message = context.Message(CraftingCatalog.Notice.Target);
                Render();
                return;
            }

            try
            {
                _confirmation = context.Crafting.Quote(_tab, _target);
            }
            catch (CraftingError error)
            {
                _message = context.Message(error.Notice);
            }
        }
        else if (context.Fusion is { } bench && RecipeOf(context) is { } recipe)
        {
            try
            {
                _fusionConfirmation = bench.Quote(recipe.Id, recipe.NeedsTarget ? _target : null,
                    BatchTimes(context));
            }
            catch (FusionError error)
            {
                _message = context.Message(error.Notice);
            }
        }
        else
        {
            _message = context.Message(CraftingCatalog.Notice.Target);
        }

        Render();
    }

    public void Confirm()
    {
        if (_context is not { } context) return;

        var quote = _confirmation;
        var fusion = _fusionConfirmation;
        if ((quote is null && fusion is null) || _committing) return;

        _committing = true;
        _confirmation = null;
        _fusionConfirmation = null;
        var target = _target;
        _target = null;

        var committed = false;
        try
        {
            if (quote is not null) context.Crafting.Commit(quote, context.Random ?? Random.Shared.NextDouble);
            else context.Fusion!.Commit(fusion!);

            committed = true;
            context.UiSound?.Invoke("craft");
        }
        catch (CraftingError error)
        {
            _target = target;
            _message = context.Message(error.Notice);
        }
        catch (FusionError error)
        {
            _target = target;
            _message = context.Message(error.Notice);
        }
        finally
        {
            _committing = false;
        }

        if (committed)
        {
            _batch = Math.Clamp(_batch, 1, Math.Max(1, MaxBatchOf(context)));
            context.Changed?.Invoke();
        }

        Render();
    }

    public void Cancel()
    {
        _message = null;
        _confirmation = null;
        _fusionConfirmation = null;
        Render();
    }

    public bool SelectById(string id)
    {
        if (_context?.Bag.Get(id) is null) return false;
        Select(id);
        return _target == id;
    }

    public bool DebugSelect()
    {
        if (_context is not { } context) return false;
        _view = LeftView.Detail;

        foreach (var entry in context.Bag.Entries)
            if (TargetProblemOf(context, entry.Id) is null)
            {
                _target = entry.Id;
                Render();
                return true;
            }

        return false;
    }

    public bool DebugQuote()
    {
        if (_context is not { } context) return false;
        _view = LeftView.Detail;

        foreach (var entry in context.Bag.Entries)
            if (TargetProblemOf(context, entry.Id) is null)
            {
                _target = entry.Id;
                Begin();
                return _confirmation is not null || _fusionConfirmation is not null;
            }

        return false;
    }

    public void Render()
    {
        if (_context is not { } context) return;

        PanelGfx.Clear(_content);
        PanelGfx.Clear(_top);
        if (_modal is not null)
        {
            _canvas.RemoveChild(_modal);
            _modal.QueueFree();
            _modal = null;
        }

        _drops.Clear();

        if (_target is not null && context.Bag.Entries.All(entry => entry.Id != _target))
            _target = null;

        DrawFrames(context);
        DrawHeader(context);
        if (_view == LeftView.Menu) DrawMenuList(context);
        else DrawLeftColumn(context);
        DrawBag(context);

        if (ModalOpen) DrawMessage(context);
    }

    private void DrawFrames(CraftingPanelContext context)
    {
        PanelGfx.Border(context.Texture, context.Asset,
            CraftingLayouts.LeftFrame.X, CraftingLayouts.LeftFrame.Y,
            CraftingLayouts.LeftFrame.Width, CraftingLayouts.LeftFrame.Height, _content);
        PanelGfx.Border(context.Texture, context.Asset,
            CraftingLayouts.BagFrame.X, CraftingLayouts.BagFrame.Y,
            CraftingLayouts.BagFrame.Width, CraftingLayouts.BagFrame.Height, _content);

    }

    private void DrawHeader(CraftingPanelContext context)
    {
        var tiers = CraftingLayouts.Tiers;
        PanelGfx.Centered(context.ActorName, tiers.Name.X - 300f, tiers.Name.Y, 600f, tiers.NameSize,
            Gold, _top);
    }

    private void DrawMenuList(CraftingPanelContext context)
    {
        var menus = CraftingLayouts.Menus;
        var area = menus.Area;
        var tabs = Tabs(context);
        var stride = menus.RowHeight + menus.RowGap;
        var left = Math.Max(0f, (area.Width - menus.RowWidth) / 2f);

        var scroll = new ScrollContainer
        {
            Name = "MenuList",
            Position = new Vector2(area.X, area.Y),
            Size = new Vector2(area.Width, area.Height),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.ShowNever,
            ClipContents = true,
        };
        scroll.AddThemeStyleboxOverride("panel", PanelGfx.EmptyStyle());
        _content.AddChild(scroll);

        var list = new Control
        {
            CustomMinimumSize = new Vector2(area.Width, Math.Max(1, tabs.Count) * stride),
            Size = new Vector2(area.Width, Math.Max(1, tabs.Count) * stride),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        scroll.AddChild(list);

        for (var index = 0; index < tabs.Count; index += 1)
        {
            var (key, label) = tabs[index];
            var top = index * stride;
            var selected = _page == key;
            var button = PanelGfx.Hit(left, top, menus.RowWidth, menus.RowHeight, () => Choose(key), list);

            var plate = PanelGfx.Patch(context.Texture, CraftingMenuRow.Image, menus.RowWidth,
                menus.RowHeight, button, CraftingMenuRow.Cap);

            if (selected && plate is not null)
            {
                var addBlend = new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add };
                var lit = PanelGfx.Patch(context.Texture, CraftingMenuRow.Image, menus.RowWidth,
                    menus.RowHeight, button, CraftingMenuRow.Cap, addBlend);
                if (lit is not null) lit.Modulate = new Color(1f, 0.78f, 0.52f, 0.55f);
            }

            PanelGfx.CenteredAt(label, menus.RowWidth / 2f, menus.RowHeight / 2f, menus.RowWidth - 24f,
                menus.TextSize, selected ? Gold : AreaColor, button);
        }
    }

    private List<(string Key, string Label)> Tabs(CraftingPanelContext context)
    {
        var tabs = new List<(string Key, string Label)>();

        if (context.Fusion is { } bench)
        {
            foreach (var menu in bench.Catalog.Menus)
            {
                if (menu.Id != FusionCatalog.LegendaryMenu && bench.Recipes(menu.Id).Count == 0) continue;
                tabs.Add((menu.Id, menu.Name));
            }

            return tabs;
        }

        foreach (var tier in Tiers)
            if (context.Crafting.Catalog.Operations.TryGetValue(tier, out var operation))
                tabs.Add((tier, operation.Name));

        return tabs;
    }

    private void DrawLeftColumn(CraftingPanelContext context)
    {
        DrawMenuButton(context);

        if (_view == LeftView.Recipes)
        {
            if (IsChaosPage) DrawChaosTierList(context);
            else DrawRecipeList(context);
            return;
        }

        DrawDetail(context);
    }

    private void DrawDetail(CraftingPanelContext context)
    {
        var recipe = RecipeOf(context);
        var times = BatchTimes(context);
        DrawProductBlock(context, recipe, times);

        var operation = IsChaosPage ? context.Crafting.Catalog.Operations.GetValueOrDefault(_tab) : null;
        var materials = IsChaosPage
            ? operation?.Materials.Select(material => (material.Item, material.Quantity)).ToArray()
            : recipe?.Materials.Select(material => (material.Item, material.Quantity)).ToArray();
        if (materials is null) return;

        // 材料格上的「已有／需要」按批量放大：做 3 次就要 9 片叶子，玩家看到的必须是 9。
        DrawMaterials(context, materials, times);

        var bag = context.Bag;
        var entry = _target is null ? null : bag.Get(_target);
        var perLevel = IsChaosPage ? operation?.CostPerLevel ?? 0 : recipe?.CostPerLevel ?? 0;
        var cost = entry is null ? 0 : context.Items.Get(entry.ItemId).Level * perLevel;
        if (cost > 0) DrawCost(context, cost);

        // 批量那一行只给固定产出类：改造类（宝石孔）一次只能改一件装备。
        // 它和花费行抢同一条带（花费在 306、批量在 318），而固定产出类的 costPerLevel 目前
        // 全是 0（合成免费），所以不会真的撞上；日后要收费就把花费挪到材料组右侧再打开批量。
        if (recipe is { Product: not null } && cost <= 0) DrawBatchRow(context, times);

        DrawCraftButton(context);
    }

    private void DrawBatchRow(CraftingPanelContext context, int times)
    {
        var layout = CraftingLayouts.Batch;
        var max = MaxBatchOf(context);

        static void SmallButton(CraftingPanelContext context, float x, float y, float width,
            float height, string glyph, Color color, Action action, Control parent)
        {
            var hit = PanelGfx.Hit(x, y, width, height, action, parent);
            PanelGfx.Patch(context.Texture, CraftingMenuRow.Image, width, height, hit, 10f);
            PanelGfx.CenteredAt(glyph, width / 2f, height / 2f, width - 6f, 24, color, hit);
        }

        SmallButton(context, layout.X, layout.Y, layout.Step, layout.Height, "−",
            times > 1 ? Gold : Short, () => SetBatch(times - 1), _content);
        SmallButton(context, layout.X + layout.Width - layout.Step, layout.Y, layout.Step,
            layout.Height, "＋", times < max ? Gold : Short, () => SetBatch(times + 1), _content);

        var middle = PanelGfx.Hit(layout.X + layout.Step, layout.Y,
            layout.Width - layout.Step * 2f, layout.Height, () => SetBatch(max), _content);
        middle.TooltipText = $"材料够做 {max} 次（点这里直接跳到上限）";
        PanelGfx.CenteredAt($"批量 ×{times}", (layout.Width - layout.Step * 2f) / 2f,
            layout.Height / 2f, layout.Width - layout.Step * 2f - 8f, layout.TextSize,
            times > 1 ? Gold : AreaColor, middle);
    }

    private void DrawChaosTierList(CraftingPanelContext context)
    {
        var layout = CraftingLayouts.Recipes;
        var area = layout.Area;
        var bench = context.Bag;

        var tiers = Tiers
            .Where(tier => context.Crafting.Catalog.Operations.ContainsKey(tier))
            .Select(tier => (Key: tier, context.Crafting.Catalog.Operations[tier]))
            .ToArray();

        var scroll = new ScrollContainer
        {
            Position = new Vector2(area.X, area.Y),
            Size = new Vector2(area.Width, area.Height),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.ShowNever,
            ClipContents = true,
        };
        scroll.AddThemeStyleboxOverride("panel", PanelGfx.EmptyStyle());
        _content.AddChild(scroll);

        var list = new Control
        {
            CustomMinimumSize = new Vector2(area.Width, tiers.Length * layout.RowHeight),
            Size = new Vector2(area.Width, tiers.Length * layout.RowHeight),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        scroll.AddChild(list);

        for (var index = 0; index < tiers.Length; index += 1)
        {
            var (tier, operation) = tiers[index];
            var y = index * layout.RowHeight;
            var ready = operation.Materials.All(material => bench.Count(material.Item) >= material.Quantity);
            var row = PanelGfx.Hit(0f, y, area.Width, layout.RowHeight, () => PickTier(tier), list);
            var color = _tab == tier ? Gold : ready ? AreaColor : Short;
            PanelGfx.Text(operation.Name, 12f, (layout.RowHeight - layout.TextSize) / 2f, layout.TextSize,
                color, row);
        }
    }

    private void DrawMenuButton(CraftingPanelContext context)
    {
        const float x = 10f, y = 6f, width = 116f, height = 36f;
        var button = PanelGfx.Hit(x, y, width, height, OpenMenu, _content);
        PanelGfx.ButtonSkin(context.Texture, width, height, button);
        PanelGfx.CenteredAt("菜单", width / 2f, height / 2f, width - 12f, 20, Gold, button);
        button.TooltipText = "回到合成菜单";
    }

    private void DrawProductBlock(CraftingPanelContext context, FusionRecipe? recipe, int times = 1)
    {
        var bag = context.Bag;
        var target = CraftingLayouts.Target;
        var entry = _target is null ? null : bag.Get(_target);
        var item = entry is null ? null : context.Items.Items.GetValueOrDefault(entry.ItemId);
        var product = recipe?.Product is { } productId
            ? context.Items.Items.GetValueOrDefault(productId)
            : null;
        var needsTarget = recipe is null || recipe.NeedsTarget;

        var slot = PanelGfx.Hit(target.Slot.X, target.Slot.Y, target.Slot.Width, target.Slot.Height, () =>
        {
            if (!needsTarget || _target is null) return;
            _target = null;
            Render();
        }, _content);
        PanelGfx.Image(context.Texture, context.Asset("inventory-slot"), 0f, 0f, target.Slot.Width,
            target.Slot.Height, slot);
        if (needsTarget)
        {
            _drops.Add(new DropTarget { Node = slot, Craft = true });
            DrawTargetArrows(context);
        }

        if (product is not null)
        {
            PanelGfx.Image(context.Texture, product.Icon, 5f, 5f, 64f, 64f, slot);

            if (times > 1)
                PanelGfx.Text($"×{times}", target.Slot.Width - 30f, target.Slot.Height - 24f, 16,
                    Gold, slot);

            PanelGfx.Centered(product.Name, target.Name.X - target.TextWidth / 2f, target.Name.Y,
                target.TextWidth, target.NameSize, AreaColor, _content);
            return;
        }

        if (item is not null)
            PanelGfx.Image(context.Texture, item.Icon, 5f, 5f, 64f, 64f, slot);

        if (item is null || entry is null) return;

        var numbers = context.Stats.Equipment.GetValueOrDefault(entry.ItemId);
        var body = ItemDetailLines.Build(item, entry, numbers, context.Stats, context.Shops.Grades,
            new DescriptionMember(context.ClassId, context.Level), context.ClassNames);
        PanelGfx.Centered(body.Title, target.Name.X - target.TextWidth / 2f, target.Name.Y,
            target.TextWidth, target.NameSize, body.Color is null ? White : new Color(body.Color), _content);

        if (numbers is null) return;

        var power = EquipmentPowerCalculator.Power(numbers, entry);
        var text = item.PriceKind == "weapon"
            ? $"等级 {ChaosEquipment.EquipmentLevel(item.Level, entry)}　" +
              $"物理攻击 {power.Attack}　魔法攻击 {power.MagicAttack}"
            : $"等级 {ChaosEquipment.EquipmentLevel(item.Level, entry)}　防御力 {power.Defense}";
        PanelGfx.Centered(text, target.Power.X - target.TextWidth / 2f, target.Power.Y,
            target.TextWidth, target.PowerSize, AreaColor, _content);
    }

    private void DrawTargetArrows(CraftingPanelContext context)
    {
        var enabled = TargetCandidates(context).Count > 1;
        var size = Math.Min(CraftingLayouts.TargetPrev.Width, CraftingLayouts.TargetPrev.Height) - 10f;

        void Arrow(UiRect rect, bool flip, int direction, string tip)
        {
            var hit = PanelGfx.Hit(rect.X, rect.Y, rect.Width, rect.Height,
                () => CycleTarget(direction), _content);
            var icon = PanelGfx.Image(context.Texture, CraftingLayouts.TargetArrowImage,
                (rect.Width - size) / 2f, (rect.Height - size) / 2f, size, size, hit, flipH: flip);
            if (icon is not null && !enabled) icon.Modulate = new Color(1f, 1f, 1f, 0.45f);
            hit.TooltipText = tip;
        }

        Arrow(CraftingLayouts.TargetPrev, false, -1, "上一件可合成的道具");
        Arrow(CraftingLayouts.TargetNext, true, 1, "下一件可合成的道具");
    }

    private void DrawRecipeList(CraftingPanelContext context)
    {
        var layout = CraftingLayouts.Recipes;
        var recipes = _context?.Fusion is { } bench ? bench.Recipes(_tab) : [];
        var bag = context.Bag;
        var area = layout.Area;

        if (recipes.Count == 0)
        {
            var hint = PanelGfx.Text(context.Message(CraftingCatalog.Notice.NoRecipe).Trim(), 12f,
                layout.RowHeight * 0.5f, layout.TextSize, AreaColor, _content);
            hint.Position = new Vector2(area.X, area.Y + layout.RowHeight * 0.5f);
            hint.Size = new Vector2(area.Width - 24f, layout.RowHeight * 3f);
            hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            return;
        }

        var scroll = new ScrollContainer
        {
            Position = new Vector2(area.X, area.Y),
            Size = new Vector2(area.Width, area.Height),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.ShowNever,
            ClipContents = true,
        };
        scroll.AddThemeStyleboxOverride("panel", PanelGfx.EmptyStyle());
        _content.AddChild(scroll);

        var list = new Control
        {
            CustomMinimumSize = new Vector2(area.Width, recipes.Count * layout.RowHeight),
            Size = new Vector2(area.Width, recipes.Count * layout.RowHeight),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        scroll.AddChild(list);

        for (var index = 0; index < recipes.Count; index += 1)
        {
            var recipe = recipes[index];
            var y = index * layout.RowHeight;
            var ready = recipe.Materials.All(material => bag.Count(material.Item) >= material.Quantity);

            var row = PanelGfx.Hit(0f, y, area.Width, layout.RowHeight, () => Pick(recipe.Id), list);

            var color = recipe.Id == _recipe ? Gold : ready ? AreaColor : Short;
            PanelGfx.Text(recipe.Name, 12f, (layout.RowHeight - layout.TextSize) / 2f, layout.TextSize,
                color, row);
        }
    }

    private void DrawMaterials(CraftingPanelContext context, (string Item, int Quantity)[] materials,
        int times = 1)
    {
        var bag = context.Bag;
        var layout = CraftingLayouts.Materials;

        for (var index = 0; index < materials.Length; index += 1)
        {
            var (item, quantity) = materials[index];
            var x = layout.X + index * layout.Stride;
            var available = bag.Count(item);
            var required = quantity * Math.Max(1, times);
            var definition = context.Items.Items.GetValueOrDefault(item);

            PanelGfx.Image(context.Texture, context.Asset("inventory-slot"), x, layout.Y,
                layout.SlotSize, layout.SlotSize, _content);
            PanelGfx.Image(context.Texture, definition?.Icon, x + layout.Icon.X,
                layout.Y + layout.Icon.Y, layout.Icon.Size, layout.Icon.Size, _content);
            PanelGfx.Centered($"{available}/{required}", x, layout.Y + layout.Count.Y, layout.SlotSize,
                layout.TextSize, available < required ? Short : AreaColor, _content);
        }
    }

    private void DrawCost(CraftingPanelContext context, int cost)
    {
        var layout = CraftingLayouts.Cost;
        var coins = MoneyDisplay.SplitMoney(cost);
        var units = new (string Key, int Amount)[] { ("gold", coins.Gold), ("silver", coins.Silver), ("copper", coins.Copper) };
        for (var index = 0; index < units.Length; index += 1)
        {
            var x = layout.X + index * layout.Stride;
            PanelGfx.Image(context.Texture, context.Asset($"craft-money-{units[index].Key}"), x, layout.Y,
                layout.CoinWidth, layout.CoinHeight, _content);
            PanelGfx.Text(units[index].Amount.ToString(), x + layout.Digit.X, layout.Y + layout.Digit.Y,
                layout.TextSize, White, _content);
        }
    }

    private void DrawCraftButton(CraftingPanelContext context)
    {
        var box = CraftingLayouts.CraftButton;
        PanelGfx.NativeImageButton(context.Texture, context.Asset, "craft-button", box.X, box.Y, box.Width,
            box.Height, Begin, _content);
    }

    private void DrawBag(CraftingPanelContext context)
    {
        var bag = context.Bag;
        var layout = BagLayouts.Create(CraftingLayouts.BagFrame.X);
        var grid = layout.Grid;
        var cells = CellsOf(bag, _filter);
        var (slotCount, slots) = InventoryViews.Layout(cells, _filter);
        var rows = (slotCount + grid.Columns - 1) / grid.Columns;

        var scroll = new ScrollContainer
        {
            Position = new Vector2(grid.X, grid.Y),
            Size = new Vector2(grid.Width, grid.Height),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.ShowNever,
            ClipContents = true,
        };
        scroll.AddThemeStyleboxOverride("panel", PanelGfx.EmptyStyle());
        _content.AddChild(scroll);

        var list = new Control
        {
            CustomMinimumSize = new Vector2(grid.Width, rows * grid.Stride),
            Size = new Vector2(grid.Width, rows * grid.Stride),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        scroll.AddChild(list);

        for (var index = 0; index < slotCount; index += 1)
        {
            var cell = slots[index];
            var x = index % grid.Columns * grid.Stride;
            var y = index / grid.Columns * grid.Stride;

            if (cell.Entry is null)
            {
                var blank = PanelGfx.Hit(x, y, ItemLayout.SlotSize, ItemLayout.SlotSize, () => { }, list);
                PanelGfx.Image(context.Texture, context.Asset("inventory-slot"), 0f, 0f,
                    ItemLayout.SlotSize, ItemLayout.SlotSize, blank);
                _drops.Add(new DropTarget { Node = blank, View = cell.View, Cell = cell.Cell });
                continue;
            }

            var entry = cell.Entry;
            var item = context.Items.Items.GetValueOrDefault(entry.ItemId);
            var button = PanelGfx.Hit(x, y, ItemLayout.SlotSize, ItemLayout.SlotSize,
                () => Select(entry.Id), list);
            PanelGfx.Image(context.Texture, context.Asset("inventory-slot"), 0f, 0f, ItemLayout.SlotSize,
                ItemLayout.SlotSize, button);
            PanelGfx.Image(context.Texture, item?.Icon, 5f, 5f, 64f, 64f, button);
            if (entry.Id == _target)
                PanelGfx.Image(context.Texture, context.Asset("inventory-selected"), -5f, -5f, 84f, 84f,
                    button);

            if (item is { Stackable: true })
            {
                var badge = new Label
                {
                    Text = entry.Quantity.ToString(),
                    Position = new Vector2(layout.Badge.X - 40f, layout.Badge.Y),
                    Size = new Vector2(40f, 20f),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    MouseFilter = MouseFilterEnum.Ignore,
                };
                badge.AddThemeFontSizeOverride("font_size", 20);
                badge.AddThemeColorOverride("font_color", new Color(layout.Badge.Color));
                button.AddChild(badge);
            }

            if (entry.Enhancement != 0)
                PanelGfx.Text($"+{entry.Enhancement}", 2f, 2f, 17, Gold, button);

            var entryId = entry.Id;
            button.ButtonDown += () => BeginDrag(entryId);
            _drops.Add(new DropTarget { Node = button, EntryId = entryId, View = cell.View, Cell = cell.Cell });
        }

        scroll.ScrollVertical = _offset;
        scroll.GetVScrollBar().ValueChanged += value => _offset = (int)value;

        DrawSideFilters(context, layout);

        var money = layout.Money;
        var wallet = MoneyDisplay.SplitMoney(context.Session.Money);
        var walletUnits = new (string Key, int Amount)[]
        {
            ("gold", wallet.Gold), ("silver", wallet.Silver), ("copper", wallet.Copper),
        };
        for (var index = 0; index < walletUnits.Length; index += 1)
        {
            var x = money.X + index * money.Stride;
            PanelGfx.Image(context.Texture, context.Asset($"craft-money-{walletUnits[index].Key}"), x, money.Y,
                money.CoinWidth, money.CoinHeight, _content);
            PanelGfx.Text(walletUnits[index].Amount.ToString(), x + money.Digit.X, money.Y + money.Digit.Y,
                20, White, _content);
        }
    }

    private void DrawSideFilters(CraftingPanelContext context, BagLayout layout)
    {
        var side = layout.SideButtons;
        var filters = InventoryViews.SideFilters;

        for (var index = 0; index < filters.Length; index += 1)
        {
            var filter = filters[index];
            var button = PanelGfx.Hit(side.X, side.Y + side.Offsets[index], side.Width, side.Height, () =>
            {
                _filter = filter;
                _offset = 0;
                Render();
            }, _content);

            PanelGfx.Image(context.Texture, context.Asset("page-button-frame"), -5f, -12f, 0f, 0f, button);
            if (_filter == filter)
                PanelGfx.Image(context.Texture, context.Asset("page-button-selected"), -5f, -12f, 0f, 0f,
                    button);
            PanelGfx.CenteredAt(InventoryViews.LabelOf(filter), side.Width / 2f, side.Height / 2f,
                side.Width - 6f, BagLayouts.SideFilterTextSize, Gold, button);
        }

        if (_filter == filters[0])
            PanelGfx.Image(context.Texture, context.Asset("column-cap-top"), side.X - 19f, side.Y - 12f, 21f,
                34f, _content);
        if (_filter == filters[^1])
            PanelGfx.Image(context.Texture, context.Asset("column-cap-bottom"), side.X - 19f,
                side.Y + side.Offsets[^1] + 38f, 21f, 34f, _content);
    }

    private void DrawMessage(CraftingPanelContext context)
    {
        var modal = new Control
        {
            Name = "Modal",
            MouseFilter = MouseFilterEnum.Ignore,
            Position = Vector2.Zero,
            Size = new Vector2(DesignWidth, DesignHeight),
        };
        _canvas.AddChild(modal);
        _modal = modal;

        // 弹层开着时整页按钮不该还能点（所以底下先铺一层挡板）。
        // 挡板先加、横带后加，横带自己的按钮仍然在上层、照常可点。
        var shield = new Control
        {
            Name = "Shield",
            Position = Vector2.Zero,
            Size = new Vector2(DesignWidth, DesignHeight),
            MouseFilter = MouseFilterEnum.Stop,
        };
        modal.AddChild(shield);

        var message = _message ?? context.Message("craft-confirm");

        if (_message is null && _fusionConfirmation is { Batch: > 1 } pending)
            message = $"{message}\n连续合成 {pending.Batch} 次";

        PanelGfx.NativeMessage(context.Texture, context.Asset, message,
            accept: _message is not null ? Cancel : Confirm,
            cancel: _confirmation is not null || _fusionConfirmation is not null ? Cancel : null,
            parent: modal);
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

    private void BeginDrag(string entryId)
    {
        if (_context is null || ModalOpen) return;

        CancelDrag();
        _drag = new DragState { EntryId = entryId, Start = _canvas.GetLocalMousePosition() };
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
        if (_drag is null) return;
        if (@event is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false }) return;

        if (_drag.Active) EndDrag();
        else CancelDrag();
    }

    private void ActivateDrag(DragState drag, Vector2 pointer)
    {
        if (_context is not { } context) return;
        if (context.Bag.Get(drag.EntryId) is not { } entry) { CancelDrag(); return; }

        drag.Active = true;
        Render();

        var item = context.Items.Items.GetValueOrDefault(entry.ItemId);
        var ghost = PanelGfx.Image(context.Texture, item?.Icon,
            pointer.X - DragGhostSize / 2f, pointer.Y - DragGhostSize / 2f, DragGhostSize, DragGhostSize,
            _canvas);
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

        if (target.Craft)
        {
            Select(entryId);
            return;
        }

        if (target.Cell is { } cell && target.View is { } view)
        {
            context.Bag.PlaceInCell(entryId, view, cell);
            context.Changed?.Invoke();
            Render();
        }
    }

    private void CancelDrag()
    {
        if (_drag?.Ghost is { } ghost && GodotObject.IsInstanceValid(ghost))
        {
            _canvas.RemoveChild(ghost);
            ghost.QueueFree();
        }

        _drag = null;
    }

    public bool DebugDrag(string entryId)
    {
        if (_context is null) return false;

        if (_view != LeftView.Detail)
        {
            _view = LeftView.Detail;
            Render();
        }

        var slot = _drops.FirstOrDefault(drop => drop.Craft);
        if (slot is null) return false;

        _drag = new DragState { EntryId = entryId, Active = true };
        EndDrag(slot.Node.GetGlobalRect().GetCenter());
        return _target == entryId;
    }

    public List<(string Key, string Label)> DebugTabs() =>
        _context is { } context ? Tabs(context) : [];

    public bool DebugMenuOpen => _view == LeftView.Menu;

    public int DebugRecipeCount(string tab)
    {
        if (_context?.Fusion is not { } bench) return 0;
        if (tab == FusionCatalog.ChaosMenu)
            return Tiers.Count(tier => _context.Crafting.Catalog.Operations.ContainsKey(tier));
        if (tab is CraftingCatalog.ChaosTier or CraftingCatalog.EpicChaosTier) return 1;
        return bench.Recipes(tab).Count;
    }

    public bool DebugChaosList => _view == LeftView.Recipes && IsChaosPage;

    public string? DebugPickFirst(string tab)
    {
        Choose(tab);
        if (_context?.Fusion is not { } bench) return null;
        var first = bench.Recipes(tab).FirstOrDefault();
        if (first is null) return null;
        Pick(first.Id);
        return _recipe;
    }

    public string? DebugMessage => _message;

    public string? DebugTarget => _target;

    public bool DebugCycleTarget(int direction)
    {
        var before = _target;
        CycleTarget(direction);
        return _target is not null && _target != before;
    }

    public int DebugTargetCount()
    {
        if (_context is not { } context) return 0;
        return TargetCandidates(context).Count;
    }
}
