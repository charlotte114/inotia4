using System;
using System.Collections.Generic;
using Godot;
using Inotia4.Domain;
using Inotia4.Domain.Audio;
using Inotia4.Domain.Content;
using Inotia4.Domain.Ui;

namespace Inotia4.Ui;

public sealed record SystemPanelContext(
    SaveSlots Slots,
    MenuPanelsCatalog Panels,
    IReadOnlyDictionary<string, string> StoryAssets,
    Func<string, Texture2D?> Texture,
    /// <summary>摘要与快照的唯一捕获口；返回 null 表示当前不能存，据此给出「无法存档」的提示。</summary>
    Func<SaveGame?> Capture,
    Func<SaveSlotRef, string?> Load,
    Action<SaveSlotRef>? Saved = null,
    Action<SaveSlotRef>? Deleted = null,
    Action<string>? ShowPage = null,
    Action? CloseRequested = null,
    Action? ExitToTitle = null,
    DisplaySettings? Display = null,
    ControlSettings? Controls = null,
    AudioSettings? Audio = null,
    Action<string, bool>? SetAudio = null,
    Action? OpenControlEditor = null,
    Action<string>? Notice = null)
{
    public string? Asset(string key) => Panels.Asset(key);

    public string? StoryAsset(string key) => StoryAssets.GetValueOrDefault(key);
}

/// <summary>
/// 系统页，以及它下面的存档槽页、读取存档页：一页四个入口按钮，另外两页各是一张
/// 自动存档行在上、手动槽滚动区在下的列表；覆盖、读取、删除三种不可逆操作各弹一次确认框。
/// 存档槽版面（几何与行绘制）搬进了
/// <see cref="SaveBoard"/>：面板 668×530 居中、行框 668×184，面板不画底图，
/// 界面的样子全来自三张行框图。
/// </summary>
public partial class SystemPanel : Control
{
    private const float DesignWidth = 1024f;

    private const float DesignHeight = 640f;

    private static readonly Vector2 ContentOrigin = new(32f, 172f);

    private static readonly Vector2 ContentSize = new(960f, 431f);

    private const string SaveDone = "保存成功";

    private const string SaveFailed = "保存失败";

    private const string SaveUnavailable = "在当前的地图中无法进行存档。";

    private SystemPanelContext? _context;

    private Control _canvas = null!;

    private Control _content = null!;

    private Control _tabs = null!;

    private Control _modal = null!;

    private string _page = "system";

    /// <summary>三种不可逆操作各自停在哪个槽上。</summary>
    private SaveSlotRef? _loadConfirm;

    private SaveSlotRef? _saveConfirm;

    private SaveSlotRef? _deleteConfirm;

    private string? _notice;

    private int _slotsScroll;

    private float _settingsScroll;

    private ScrollContainer? _settingsScrollNode;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        Visible = false;

        _canvas = new Control { Name = "Canvas", MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_canvas);
        DesignStage.Center(_canvas);

        _tabs = new Control { Name = "Tabs", Size = new Vector2(DesignWidth, DesignHeight), MouseFilter = MouseFilterEnum.Ignore };
        _canvas.AddChild(_tabs);

        _content = new Control
        {
            Name = "Content",
            Position = ContentOrigin,
            Size = ContentSize,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _canvas.AddChild(_content);

        // 弹层另起一层、盖在内容上，每次开弹层前先清空这一层。
        // 这一层自己必须忽略鼠标：它整块盖住 1024×640，默认的 STOP 会把页签、内容块上的
        // 所有按钮都挡在下面（点哪都没反应）；要挡点击时由 BeginModal 加的 Shield 负责。
        _modal = new Control
        {
            Name = "Modal",
            Size = new Vector2(DesignWidth, DesignHeight),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _canvas.AddChild(_modal);

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

    public Vector2 ContentPointToViewport(Vector2 point) => _content.GetGlobalTransformWithCanvas() * point;

    public Vector2 CanvasPointToViewport(Vector2 point) => _tabs.GetGlobalTransformWithCanvas() * point;

    public void SetSuppressed(bool suppressed) => _canvas.Visible = !suppressed;

    public const string PageSystem = "system";

    public const string PageSave = "save";

    public const string PageLoad = "load";

    public const string PageSettings = "settings";

    public void Open(SystemPanelContext context, string page)
    {
        _context = context;
        _page = page;
        _loadConfirm = _saveConfirm = _deleteConfirm = null;
        _notice = null;
        _slotsScroll = 0;
        Visible = true;
        LayoutCanvas();
        Render();
    }

    public void ShowPage(string page)
    {
        _page = page;
        _loadConfirm = _saveConfirm = _deleteConfirm = null;
        _notice = null;
        _slotsScroll = 0;
        Render();
    }

    /// <summary>
    /// 顶部页签：系统页签留在本面板里换子页，其余页签交给场景层切页
    /// （页签是全局的一排，不属于本面板，点「任务」这类页签不该改本面板的子页状态）。
    ///
    /// 之前把本面板的 <see cref="ShowPage"/> 直接传给了页签，于是点「任务」也会把 <c>_page</c>
    /// 设成 <c>quests</c>，<see cref="Render"/> 落到 else 分支又画出存档槽——
    /// 看起来就是「点别的页签反而回到存档界面」。
    /// </summary>
    private void SwitchTab(string page)
    {
        if (page is PageSystem or PageSave or PageLoad or PageSettings)
        {
            ShowPage(page);
            return;
        }

        _context?.ShowPage?.Invoke(page);
    }

    public void Close()
    {
        Visible = false;
        SetSuppressed(false);
        _page = PageSystem;
        _loadConfirm = _saveConfirm = _deleteConfirm = null;
        _notice = null;
    }

    public void Refresh()
    {
        if (!Visible) return;
        _loadConfirm = _saveConfirm = _deleteConfirm = null;
        Render();
    }

    private void Render()
    {
        if (_context is not { } context) return;
        PanelGfx.Clear(_tabs);
        PanelGfx.Clear(_content);
        PanelGfx.Clear(_modal);

        // 页签只在系统页与设置页画（存档页与读档页不画这一排）；
        // 设置页算系统页签的一份子页，所以高亮的是「系统」那一格。
        if (_page is PageSystem or PageSettings)
        {
            var highlight = _page == PageSettings ? PageSystem : _page;
            MenuTabBar.Draw(_tabs, context.StoryAssets, context.Texture, highlight, SwitchTab);
        }

        if (_page == PageSystem) RenderSystem(context);
        else if (_page == PageSettings) RenderSettings(context);
        else RenderSaveSlots(context);

        RenderModals(context);
    }

    private void RenderSystem(SystemPanelContext context)
    {
        var actions = new List<(string Label, Action Action)>
        {
            ("保存游戏", () => ShowPage(PageSave)),
            ("读取存档", () => ShowPage(PageLoad)),
            ("设置", () => ShowPage(PageSettings)),
            ("返回主菜单", () =>
            {
                if (context.ExitToTitle is { } exit) exit();
                else GD.Print("[menu] 返回主菜单还没接（标题页流程未迁移）");
            }),
        };
        var gap = Math.Min(95f, (398f - 66f) / Math.Max(1, actions.Count - 1));
        for (var index = 0; index < actions.Count; index += 1)
        {
            var (label, action) = actions[index];
            var button = PanelGfx.Hit(320f, 70f + index * gap, 300f, 66f, action, _content);
            PanelGfx.ButtonSkin(context.Texture, 300f, 66f, button);
            PanelGfx.CenteredAt(label, 150f, 33f, 288f, 22, Gold, button);
        }
    }

    private void RenderSettings(SystemPanelContext context)
    {
        var scroll = new ScrollContainer
        {
            Name = "Settings",
            Position = Vector2.Zero,
            Size = new Vector2(ContentSize.X, ContentSize.Y),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        _content.AddChild(scroll);

        var content = new Control
        {
            CustomMinimumSize = new Vector2(ContentSize.X, 880f),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        scroll.AddChild(content);

        var display = context.Display;
        var controls = context.Controls;
        var audio = context.Audio;

        if (audio is not null)
        {
            Row(content, context, "音乐", 80f, audio.Music ? "开启" : "关闭", () =>
            {
                context.SetAudio?.Invoke("music", !audio.Music);
                Render();
            });

            Row(content, context, "音效", 170f, audio.Effects ? "开启" : "关闭", () =>
            {
                context.SetAudio?.Invoke("effects", !audio.Effects);
                Render();
            });
        }

        Row(content, context, "帧率显示", 260f, display?.Fps != false ? "开启" : "关闭",
            () =>
            {
                if (display is null) return;
                display.SetFps(!display.Fps);
                Render();
            });

        Row(content, context, "暴击光效", 350f, display?.CriticalDecorations != false ? "开启" : "关闭",
            () =>
            {
                if (display is null) return;
                display.SetCriticalDecorations(!display.CriticalDecorations);
                Render();
            });

        var mode = DisplayServer.WindowGetMode();
        var fullscreen = mode is DisplayServer.WindowMode.ExclusiveFullscreen or DisplayServer.WindowMode.Fullscreen;
        Row(content, context, "全屏", 440f, fullscreen ? "开启" : "关闭", () =>
        {
            DisplayServer.WindowSetMode(fullscreen
                ? DisplayServer.WindowMode.Windowed
                : DisplayServer.WindowMode.Fullscreen);
            Render();
        });

        var style = controls?.MoveStyle ?? MoveControlStyle.Stick;
        Row(content, context, "移动方式", 530f, ControlSettings.Describe(style), () =>
        {
            if (controls is null) return;
            controls.SetMoveStyle(ControlSettings.Next(style));
            Render();
        });

        // 高帧率（默认开）：开＝跟屏幕刷新率走，关＝锁 60 并开 vsync。只改渲染上限，
        // 游戏逻辑本来就是 60，所以关掉不会「变慢」，只是不再多画。
        // 按钮上顺带显示当前上限：ROM 不放开高刷时，这里会明明白白写着 60。
        var refreshLabel = display?.HighRefresh == false ? "关闭" : $"开启 {Engine.MaxFps}帧";
        Row(content, context, "高帧率", 620f, refreshLabel, () =>
        {
            if (display is null) return;
            display.SetHighRefresh(!display.HighRefresh);
            var summary = Inotia4.Application.Main.ApplyHighRefresh(display.HighRefresh);
            context.Notice?.Invoke(summary);
            Render();
        });

        Row(content, context, "震动", 710f, display?.Haptics != false ? "开启" : "关闭", () =>
        {
            if (display is null) return;
            display.SetHaptics(!display.Haptics);
            Render();
        });

        PanelGfx.Text("操作设置", 260f, 800f + 15f, 25, new Color("#e2cb9e"), content);
        var openEditor = PanelGfx.Hit(485f, 800f, 220f, 66f,
            () => context.OpenControlEditor?.Invoke(), content);
        PanelGfx.ButtonSkin(context.Texture, 220f, 66f, openEditor);
        PanelGfx.CenteredAt("编辑键位", 110f, 33f, 208f, 25, Gold, openEditor);

        _settingsScrollNode = scroll;
        scroll.ScrollVertical = (int)_settingsScroll;
        scroll.GetVScrollBar().ValueChanged += value => _settingsScroll = (float)value;
    }

    private static void Row(Control content, SystemPanelContext context, string label, float top,
        string value, Action action)
    {
        PanelGfx.Text(label, 260f, top + 15f, 25, new Color("#e2cb9e"), content);
        var button = PanelGfx.Hit(485f, top, 220f, 66f, action, content);
        PanelGfx.ButtonSkin(context.Texture, 220f, 66f, button);
        PanelGfx.CenteredAt(value, 110f, 33f, 208f, 25, Gold, button);
    }

    /// <summary>
    /// 调试用：把设置页滚到某个纵向位置（配 <c>--settingsscroll</c> 截图核对下半页）。
    /// 内容高度要到下一帧布局完才算得出来，所以先把值记着、再推一次；否则
    /// <c>ScrollVertical</c> 会被夹回 0，而滚动条的 ValueChanged 又会把字段改回 0。
    /// </summary>
    public void DebugScrollSettings(float offset)
    {
        _settingsScroll = offset;
        Render();
        Callable.From(() =>
        {
            _settingsScroll = offset;
            if (_settingsScrollNode is { } node) node.ScrollVertical = (int)offset;
        }).CallDeferred();
    }

    private void RenderSaveSlots(SystemPanelContext context)
    {
        var loading = _page == PageLoad;

        var page = new Control
        {
            Name = "SavePage",
            Position = -ContentOrigin,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _content.AddChild(page);

        PanelGfx.CenteredAt(loading ? "读取存档" : "存档槽", DesignWidth / 2f, 16f, 400f, 28,
            new Color("#e2cb9e"), page);

        var origin = SaveBoard.Origin(DesignWidth);

        DrawRow(context, origin.X, SaveBoard.AutoTop, RowOf(context, SaveSlotRef.Auto, loading), page);

        var scroll = new ScrollContainer
        {
            Name = "Slots",
            Position = new Vector2(origin.X, SaveBoard.ListTop),
            Size = new Vector2(SaveBoard.PanelWidth, SaveBoard.ListHeight),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        page.AddChild(scroll);
        var rows = new Control
        {
            CustomMinimumSize = new Vector2(SaveBoard.PanelWidth,
                (SaveGameStore.ManualSaveSlotCount - 1) * SaveBoard.RowStep + SaveBoard.FrameHeight),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        scroll.AddChild(rows);

        var slots = SaveBoard.NewestFirstSlots(slot => context.Slots.Peek(slot));
        for (var index = 0; index < slots.Length; index += 1)
        {
            var row = RowOf(context, slots[index], loading);
            var top = index * SaveBoard.RowStep;
            DrawRow(context, 0f, top, row, rows);
        }

        scroll.ScrollVertical = _slotsScroll;
        scroll.GetVScrollBar().ValueChanged += value => _slotsScroll = (int)value;
    }

    private SaveRowView.Row RowOf(SystemPanelContext context, SaveSlotRef reference, bool loading)
    {
        var reading = context.Slots.Peek(reference);
        var title = SaveBoard.RowTitle(reference, reading);
        var auto = reference.IsAuto;

        Action? action;
        if (loading)
            action = reading is SlotSummaryReading.Ok ? () => RequestLoad(reference) : null;
        else if (auto) action = null;
        else action = () => RequestSave(context, reference);

        var delete = reading switch
        {
            SlotSummaryReading.Ok when !auto => (Action?)(() => RequestDelete(context, reference)),
            SlotSummaryReading.Unreadable when !auto => () => RequestDelete(context, reference),
            _ => null,
        };

        return new SaveRowView.Row(reading, action, title, auto, delete);
    }

    private void DrawRow(SystemPanelContext context, float originX, float top, SaveRowView.Row row,
        Control? parent = null)
    {
        SaveRowView.Draw(context.Texture, context.StoryAsset("save-slot"), context.StoryAsset("save-empty"),
            originX, top, row, parent ?? _content);
    }

    private void RequestLoad(SaveSlotRef reference)
    {
        if (_context is not { } context) return;
        if (context.Slots.Peek(reference) is not SlotSummaryReading.Ok) return;
        _loadConfirm = reference;
        Render();
    }

    /// <summary>点一个手动槽：空槽直接存，已有存档先问一次再覆盖（不可逆操作都要先确认）。</summary>
    private void RequestSave(SystemPanelContext context, SaveSlotRef reference)
    {
        if (reference.IsAuto) return;
        if (context.Slots.Peek(reference) is SlotSummaryReading.Empty)
        {
            SaveToSlot(context, reference);
            return;
        }
        _saveConfirm = reference;
        Render();
    }

    private void SaveToSlot(SystemPanelContext context, SaveSlotRef reference)
    {
        if (reference.IsAuto) return;
        var captured = context.Capture();
        if (captured is null)
        {
            _notice = SaveUnavailable;
            Render();
            return;
        }
        try
        {
            context.Slots.Write(reference, captured);
            context.Saved?.Invoke(reference);
            _notice = SaveDone;
        }
        catch (Exception error)
        {
            GD.PushWarning("存档写入失败：", error.Message);
            _notice = SaveFailed;
        }
        Render();
    }

    private void RequestDelete(SystemPanelContext context, SaveSlotRef reference)
    {
        if (reference.IsAuto) return;
        if (context.Slots.Peek(reference) is SlotSummaryReading.Empty) return;
        _deleteConfirm = reference;
        Render();
    }

    private void DeleteSlot(SystemPanelContext context, SaveSlotRef reference)
    {
        try
        {
            context.Slots.Erase(reference);
            context.Deleted?.Invoke(reference);
        }
        catch (Exception error)
        {
            GD.PushWarning("删除存档失败：", error.Message);
            _notice = SaveFailed;
        }
        _deleteConfirm = null;
        Render();
    }

    private void RenderModals(SystemPanelContext context)
    {
        if (_notice is not null)
        {
            BeginModal();
            PanelGfx.NativeMessage(context.Texture, context.Asset, _notice,
                accept: () => { _notice = null; Render(); }, cancel: null, parent: _modal);
        }

        if (_loadConfirm is { } loadSlot)
        {
            BeginModal();
            PanelGfx.NativeMessage(context.Texture, context.Asset,
                "读取存档将丢失当前未保存的进度，确定读取？",
                accept: () => { _loadConfirm = null; LoadFromSlot(context, loadSlot); },
                cancel: () => { _loadConfirm = null; Render(); },
                parent: _modal);
        }

        if (_saveConfirm is { } saveSlot)
        {
            BeginModal();
            PanelGfx.NativeMessage(context.Texture, context.Asset,
                "这个存档槽里已经有存档，确定覆盖？",
                accept: () => { _saveConfirm = null; SaveToSlot(context, saveSlot); },
                cancel: () => { _saveConfirm = null; Render(); },
                parent: _modal);
        }

        if (_deleteConfirm is { } deleteSlot)
        {
            BeginModal();
            PanelGfx.NativeMessage(context.Texture, context.Asset,
                "删除后无法恢复，确定删除这个存档？",
                accept: () => DeleteSlot(context, deleteSlot),
                cancel: () => { _deleteConfirm = null; Render(); },
                parent: _modal);
        }
    }

    private void BeginModal()
    {
        var shield = new Control
        {
            Name = "Shield",
            Size = new Vector2(DesignWidth, DesignHeight),
            MouseFilter = MouseFilterEnum.Stop,
        };
        _modal.AddChild(shield);
    }

    private void LoadFromSlot(SystemPanelContext context, SaveSlotRef reference)
    {
        var error = context.Load(reference);
        if (error is not null)
        {
            _notice = error;
            Render();
        }
    }

    private static readonly Color Gold = new("#f3deb0");
}
