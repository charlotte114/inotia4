using System;
using System.Collections.Generic;
using Godot;
using Inotia4.Application;
using Inotia4.Domain;
using Inotia4.Domain.Audio;
using Inotia4.Domain.Content;
using Inotia4.Domain.Ui;

namespace Inotia4.Ui;

public partial class TitleScene : Control
{
    private const float DesignWidth = 1200f;
    private const float DesignHeight = 640f;
    private const float SurfacePadding = 12f;
    private const float ModalWidth = 1024f;
    private const float ModalHeight = 640f;

    private static string Art(string name) => $"assets/ui/title-menu/{name}.png";

    private SaveSlots _saves = null!;
    private MenuPanelsCatalog _panels = null!;
    private TextureLease _lease = null!;

    private GameAudio? _audio;

    private AudioSettings _audioSettings = null!;

    private readonly DisplaySettings _display = new(UserStorage.Read, UserStorage.Write);

    private readonly ControlSettings _controls = new(UserStorage.Read, UserStorage.Write);

    public Action<SaveSlotRef?, int?>? StartRequested { get; set; }

    public string? InitialPage { get; set; }

    public string? AutoPlay { get; set; }

    private Control _canvas = null!;
    private Control _pageContent = null!;
    private Control _modal = null!;
    private string _page = "home";
    private SaveSlotRef? _deleteConfirm;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;

        _lease = new TextureCache().Lease();
        _saves = new SaveSlots(new FileSaveStorage());

        // 标题页也按偏好定帧率：设置里关过高帧率，回标题页不该又冲回 144。
        Inotia4.Application.Main.ApplyHighRefresh(_display.HighRefresh);

        var loader = new ContentLoader();
        _panels = loader.MenuPanels;

        _audioSettings = new AudioSettings(UserStorage.Read, UserStorage.Write);
        _audio = new GameAudio(this, loader.Audio, _audioSettings, () => false);
        _audio.PrepareUi();
        _audio.EnterMenu();
        PanelGfx.UiSound = name => _audio?.Ui(name);

        // 背景：原版是「两层图集 + 六层振荡烟雾」的叠层（见 SO_REVERSE.md §19），
        // 不是一张静态图。TitleBackdrop 负责魔法阵与烟雾，固定元素（logo、副标题）
        // 仍由本类按设计坐标画。
        var backdrop = new TitleBackdrop
        {
            Name = "Backdrop",
            DesignSize = new Vector2(DesignWidth, DesignHeight),
        };
        backdrop.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(backdrop);

        var viewport = GetViewportRect().Size;
        var factor = Math.Min((viewport.X - SurfacePadding * 2f) / DesignWidth,
            (viewport.Y - SurfacePadding * 2f) / DesignHeight);
        _canvas = new Control
        {
            Position = new Vector2(
                SurfacePadding + (viewport.X - SurfacePadding * 2f - DesignWidth * factor) / 2f,
                SurfacePadding + (viewport.Y - SurfacePadding * 2f - DesignHeight * factor) / 2f),
            Scale = new Vector2(factor, factor),
            Size = new Vector2(DesignWidth, DesignHeight),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        AddChild(_canvas);
        _pageContent = new Control
        {
            Size = new Vector2(DesignWidth, DesignHeight),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _canvas.AddChild(_pageContent);
        _modal = new Control
        {
            Position = new Vector2((DesignWidth - ModalWidth) / 2f, 0f),
            Size = new Vector2(ModalWidth, ModalHeight),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _canvas.AddChild(_modal);

        _page = InitialPage is { Length: > 0 } ? InitialPage : "home";
        Render();

        if (AutoPlay is not { Length: > 0 } play) return;
        if (play == "new") StartRequested?.Invoke(null, 0);
        else if (SaveSlotRef.TryParse(play, out var slot)) StartRequested?.Invoke(slot, null);
    }

    public override void _ExitTree()
    {
        _audio?.Shutdown();
        _audio = null;
        _lease?.Dispose();
    }

    private void Render()
    {
        foreach (var child in _pageContent.GetChildren())
        {
            _pageContent.RemoveChild(child);
            child.QueueFree();
        }
        foreach (var child in _modal.GetChildren())
        {
            _modal.RemoveChild(child);
            child.QueueFree();
        }

        if (_page == "home") RenderHome();
        else if (_page == "slots") RenderSlots();
        else RenderSettings();

        if (_deleteConfirm is { } deleteSlot)
        {
            Shield();
            PanelGfx.NativeMessage(Tex, key => _panels.Asset(key),
                "删除后无法恢复，确定删除这个存档？",
                accept: () => DeleteSlot(deleteSlot),
                cancel: () => { _deleteConfirm = null; Render(); },
                parent: _modal);
        }
    }

    private void RenderHome()
    {
        Image(Art("logo"), 291f, 52f, 617f, 353f);
        Image(Art("subtitle-left"), 305f, 385f, null, 122f);
        Image(Art("subtitle-right"), 601f, 385f, null, 122f);

        Button("开始游戏", 416f, 457f, 369f, 124f, 36, OpenSlots);
    }

    private void RenderSlots()
    {
        var close = PanelGfx.Hit(28f, 21f, 67f, 67f, ShowHome, _pageContent);
        PanelGfx.Image(Tex, "assets/ui/menus/close.png", 0f, 0f, 67f, 67f, close);
        AddPressedLayer(close, "assets/ui/menus/close-pressed.png", 67f);

        Centered("存档槽", 600f, 20f, 760f, 49);

        var origin = SaveBoard.Origin(DesignWidth);
        var frame = Art("slot");
        var empty = Art("slot-empty");

        var autoReading = _saves.Peek(SaveSlotRef.Auto);
        SaveRowView.Draw(Tex, frame, empty, origin.X, SaveBoard.AutoTop, new SaveRowView.Row(
            autoReading,
            Action: autoReading is SlotSummaryReading.Ok ? () => Resume(SaveSlotRef.Auto) : null,
            Title: SaveBoard.RowTitle(SaveSlotRef.Auto, autoReading),
            Auto: true,
            Delete: null), _pageContent);

        var scroll = new ScrollContainer
        {
            Name = "Slots",
            Position = new Vector2(origin.X, SaveBoard.ListTop),
            Size = new Vector2(SaveBoard.PanelWidth, SaveBoard.ListHeight),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        _pageContent.AddChild(scroll);
        var rows = new Control
        {
            CustomMinimumSize = new Vector2(SaveBoard.PanelWidth,
                (SaveGameStore.ManualSaveSlotCount - 1) * SaveBoard.RowStep + SaveBoard.FrameHeight),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        scroll.AddChild(rows);

        var slots = SaveBoard.NewestFirstSlots(slot => _saves.Peek(slot));
        for (var index = 0; index < slots.Length; index += 1)
        {
            var slot = slots[index];
            var reading = _saves.Peek(slot);
            var reference = new SaveSlotRef(slot);
            var action = reading switch
            {
                SlotSummaryReading.Ok => (Action?)(() => Resume(reference)),
                SlotSummaryReading.Empty => () => StartRequested?.Invoke(null, slot),
                _ => null,
            };
            var delete = reading switch
            {
                SlotSummaryReading.Ok => (Action?)(() => RequestDelete(reference)),
                SlotSummaryReading.Unreadable => () => RequestDelete(reference),
                _ => null,
            };
            SaveRowView.Draw(Tex, frame, empty, 0f, index * SaveBoard.RowStep, new SaveRowView.Row(
                reading, action, SaveBoard.RowTitle(reference, reading), false, delete), rows);
        }
    }

    private void RenderSettings()
    {
        var close = PanelGfx.Hit(28f, 21f, 67f, 67f, ShowHome, _pageContent);
        PanelGfx.Image(Tex, "assets/ui/menus/close.png", 0f, 0f, 67f, 67f, close);
        AddPressedLayer(close, "assets/ui/menus/close-pressed.png", 67f);

        Centered("环境设置", 600f, 20f, 760f, 49);

        const float panelX = 200f, panelY = 126f, panelWidth = 800f, panelHeight = 425f;
        Image(Art("slot"), panelX, panelY, panelWidth, panelHeight);
        var scroll = new ScrollContainer
        {
            Position = new Vector2(panelX + 40f, panelY + 64f),
            Size = new Vector2(panelWidth - 80f, panelHeight - 128f),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        _pageContent.AddChild(scroll);
        var content = new Control { MouseFilter = MouseFilterEnum.Ignore };
        scroll.AddChild(content);

        var rows = new List<(string Label, string Value, Action Action)>();
        foreach (var (channel, label) in new[] { ("music", "背景音乐"), ("effects", "音效") })
        {
            var enabled = _audioSettings.Enabled(channel);
            rows.Add((label, enabled ? "开" : "关", () =>
            {
                _audio?.SetEnabled(channel, !enabled);
                Render();
            }));
        }

        var decorations = _display.CriticalDecorations;
        rows.Add(("暴击光效", decorations ? "开" : "关", () =>
        {
            _display.SetCriticalDecorations(!decorations);
            Render();
        }));

        var mode = DisplayServer.WindowGetMode();
        var fullscreen = mode is DisplayServer.WindowMode.ExclusiveFullscreen or DisplayServer.WindowMode.Fullscreen;
        rows.Add(("全屏", fullscreen ? "开" : "关", () =>
        {
            DisplayServer.WindowSetMode(fullscreen ? DisplayServer.WindowMode.Windowed
                : DisplayServer.WindowMode.Fullscreen);
            Render();
        }));

        var style = _controls.MoveStyle;
        rows.Add(("移动方式", ControlSettings.Describe(style), () =>
        {
            _controls.SetMoveStyle(ControlSettings.Next(style));
            Render();
        }));

        var highRefresh = _display.HighRefresh;
        rows.Add(("高帧率", highRefresh ? "开" : "关", () =>
        {
            _display.SetHighRefresh(!highRefresh);
            Inotia4.Application.Main.ApplyHighRefresh(_display.HighRefresh);
            Render();
        }));

        var haptics = _display.Haptics;
        rows.Add(("震动", haptics ? "开" : "关", () =>
        {
            _display.SetHaptics(!haptics);
            Render();
        }));

        for (var index = 0; index < rows.Count; index += 1)
        {
            var (label, value, action) = rows[index];
            var top = 20 + index * 100;
            var labelText = new Label
            {
                Text = label,
                Position = new Vector2(38f, top + 12f),
                MouseFilter = MouseFilterEnum.Ignore,
            };
            labelText.AddThemeFontSizeOverride("font_size", 34);
            labelText.AddThemeColorOverride("font_color", Gold);
            content.AddChild(labelText);
            Button(value, 270f, top, 190f, 67f, value.Length > 2 ? 30 : 34, action, parent: content);
        }
    }

    private void OpenSlots()
    {
        _deleteConfirm = null;
        _page = "slots";
        Render();
    }

    private void ShowHome()
    {
        _deleteConfirm = null;
        _page = "home";
        Render();
    }

    private void Resume(SaveSlotRef reference) => StartRequested?.Invoke(reference, null);

    private void RequestDelete(SaveSlotRef reference)
    {
        if (reference.IsAuto || _saves.Peek(reference) is SlotSummaryReading.Empty) return;
        _deleteConfirm = reference;
        Render();
    }

    private void DeleteSlot(SaveSlotRef reference)
    {
        try
        {
            _saves.Erase(reference);
        }
        catch (Exception error)
        {
            GD.PushWarning($"删除存档失败：{error.Message}");
        }

        _deleteConfirm = null;
        Render();
    }

    private static readonly Color Gold = new("#f3deb0");

    private Texture2D? Tex(string path) => _lease.Texture(path);

    private void Image(string path, float x, float y, float? width, float? height, Control? parent = null)
    {
        if (Tex(path) is not { } source) return;
        Vector2 size;
        if (width is { } w && height is { } h) size = new Vector2(w, h);
        else if (width is { } onlyWidth) size = new Vector2(onlyWidth, onlyWidth * source.GetHeight() / source.GetWidth());
        else if (height is { } onlyHeight) size = new Vector2(onlyHeight * source.GetWidth() / source.GetHeight(), onlyHeight);
        else size = new Vector2(source.GetWidth(), source.GetHeight());
        var rect = new TextureRect
        {
            Texture = source,
            Position = new Vector2(x, y),
            Size = size,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        (parent ?? _pageContent).AddChild(rect);
        rect.Position = new Vector2(x, y);
        rect.Size = size;
    }

    private void Button(string label, float x, float y, float width, float height, int fontSize,
        Action action, bool disabled = false, Control? parent = null)
    {
        var button = PanelGfx.Hit(x, y, width, height, action, parent ?? _pageContent, disabled);
        PanelGfx.ButtonSkin(Tex, width, height, button);
        PanelGfx.CenteredAt(label, width / 2f, height / 2f, width - 12f, fontSize, Gold, button);
        if (disabled) button.Modulate = new Color(1f, 1f, 1f, 0.4f);
    }

    private void Centered(string text, float x, float y, float width, int size)
    {
        var label = new Label
        {
            Text = text,
            Position = new Vector2(x - width / 2f, y),
            Size = new Vector2(width, size + 14f),
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", Gold);
        _pageContent.AddChild(label);
    }

    private void AddPressedLayer(Button button, string path, float size)
    {
        var pressed = new TextureRect
        {
            Texture = Tex(path),
            Size = new Vector2(size, size),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = MouseFilterEnum.Ignore,
            Visible = false,
            Material = new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add },
        };
        button.AddChild(pressed);
        button.ButtonDown += () => pressed.Visible = true;
        button.ButtonUp += () => pressed.Visible = false;
    }

    private void Shield()
    {
        var shield = new Control
        {
            Name = "Shield",
            Size = new Vector2(DesignWidth, DesignHeight),
            MouseFilter = MouseFilterEnum.Stop,
        };
        _modal.AddChild(shield);
    }
}
