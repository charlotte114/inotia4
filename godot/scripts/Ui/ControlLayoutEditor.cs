using System;
using System.Collections.Generic;
using Godot;
using Inotia4.Domain.Character;
using Inotia4.Domain.Content;
using Inotia4.Domain.Ui;

namespace Inotia4.Ui;

public partial class ControlLayoutEditor : Control
{
    private const float SlotSize = 94f;
    private const float SlotIconSize = 75f;
    private const float ItemIconSize = 64f;
    private const float ToolbarButtonWidth = 220f;
    private const float ToolbarButtonHeight = 66f;
    private const float SizeButtonSize = 66f;
    private const float SizeValueWidth = 110f;

    private ControlSkinCatalog _skin = null!;
    private Func<string, Texture2D?> _texture = null!;
    private Func<string, string?> _asset = null!;
    private ControlLayoutPreferences _preferences = null!;
    private ControlSettings _settings = null!;

    private Control _toolbar = null!;
    private Button _resetButton = null!;
    private Button _sizeMinus = null!;
    private Button _sizePlus = null!;
    private Label _sizeValue = null!;
    private IDisposable? _preferencesSubscription;
    private IDisposable? _settingsSubscription;

    private readonly Dictionary<string, Control> _controls = [];

    private string? _dragging;
    private (float X, float Y)? _dragPoint;

    public bool IsOpen { get; private set; }

    public IReadOnlyDictionary<string, Vector2> Positions
    {
        get
        {
            var positions = new Dictionary<string, Vector2>();
            foreach (var (id, preview) in _controls) positions[id] = preview.Position;
            return positions;
        }
    }

    public float DebugScale(string id) => _controls.TryGetValue(id, out var preview) ? preview.Scale.X : 0f;

    public Vector2 DebugSize(string id)
    {
        if (!_controls.TryGetValue(id, out var preview)) return Vector2.Zero;
        return new Vector2((float)preview.GetMeta("width", 94f), (float)preview.GetMeta("height", 94f))
               * preview.Scale.X;
    }

    public bool DebugPressSize(int direction)
    {
        var button = direction > 0 ? _sizePlus : _sizeMinus;
        if (button.Disabled) return false;
        button.EmitSignal(BaseButton.SignalName.Pressed);
        return true;
    }

    public string DebugSizeText => _sizeValue.Text;

    public Action? Closed { get; set; }

    public Func<IReadOnlyList<HudShortcutSlot>>? Preview { get; set; }

    public ControlLayoutEditor Skin(ControlSkinCatalog skin)
    {
        _skin = skin;
        return this;
    }

    public ControlLayoutEditor TextureOf(Func<string, Texture2D?> texture)
    {
        _texture = texture;
        return this;
    }

    public ControlLayoutEditor Assets(Func<string, string?> asset)
    {
        _asset = asset;
        return this;
    }

    public ControlLayoutEditor Preferences(ControlLayoutPreferences preferences)
    {
        _preferences = preferences;
        return this;
    }

    public ControlLayoutEditor Settings(ControlSettings settings)
    {
        _settings = settings;
        return this;
    }

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        Visible = false;

        _toolbar = new Control { Name = "Toolbar", MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_toolbar);

        PanelGfx.NativeImageButton(_texture, _asset, "close", 12f, 12f, 67f, 67f, Close, _toolbar);

        _resetButton = PanelGfx.Hit(0f, 12f, ToolbarButtonWidth, ToolbarButtonHeight,
            () =>
            {
                Cancel();
                _preferences.Reset();
            }, _toolbar);
        PanelGfx.ButtonSkin(_texture, ToolbarButtonWidth, ToolbarButtonHeight, _resetButton);
        PanelGfx.CenteredAt("恢复默认", ToolbarButtonWidth / 2f, ToolbarButtonHeight / 2f,
            ToolbarButtonWidth - 12f, 28, new Color("#f3deb0"), _resetButton);

        var sizeColor = new Color("#f3deb0");
        _sizeMinus = PanelGfx.Hit(0f, 12f, SizeButtonSize, SizeButtonSize,
            () => _preferences.NudgeSizeScale(-1), _toolbar);
        PanelGfx.ButtonSkin(_texture, SizeButtonSize, SizeButtonSize, _sizeMinus);
        PanelGfx.CenteredAt("-", SizeButtonSize / 2f, SizeButtonSize / 2f,
            SizeButtonSize - 12f, 32, sizeColor, _sizeMinus);

        _sizePlus = PanelGfx.Hit(0f, 12f, SizeButtonSize, SizeButtonSize,
            () => _preferences.NudgeSizeScale(1), _toolbar);
        PanelGfx.ButtonSkin(_texture, SizeButtonSize, SizeButtonSize, _sizePlus);
        PanelGfx.CenteredAt("+", SizeButtonSize / 2f, SizeButtonSize / 2f,
            SizeButtonSize - 12f, 32, sizeColor, _sizePlus);

        _sizeValue = PanelGfx.CenteredAt("100%", SizeValueWidth / 2f, 12f + SizeButtonSize / 2f,
            SizeValueWidth, 28, sizeColor, _toolbar);

        _preferencesSubscription = _preferences.Subscribe(Layout);
        _settingsSubscription = _settings.Subscribe(_ => Layout());
    }

    public override void _ExitTree()
    {
        _preferencesSubscription?.Dispose();
        _preferencesSubscription = null;
        _settingsSubscription?.Dispose();
        _settingsSubscription = null;
    }

    public void Open()
    {
        Cancel();
        foreach (var child in _controls.Values) child.QueueFree();
        _controls.Clear();

        var slots = Preview?.Invoke() ?? [];
        foreach (var id in ControlIds.All)
        {
            var preview = new Control { Name = id, MouseFilter = MouseFilterEnum.Ignore };
            AddChild(preview);
            MoveChild(preview, 0);
            _controls[id] = preview;
            BuildPreview(preview, id, slots);
        }

        IsOpen = true;
        Visible = true;
        Layout();
    }

    public void Close()
    {
        IsOpen = false;
        Cancel();
        Visible = false;
        _dragging = null;
        _dragPoint = null;
        Closed?.Invoke();
    }

    public void Cancel()
    {
        _dragging = null;
        _dragPoint = null;
        Layout();
    }

    private void BuildPreview(Control preview, string id, IReadOnlyList<HudShortcutSlot> slots)
    {
        float width = SlotSize, height = SlotSize;

        if (id == "move")
        {
            if (_settings.MoveStyle == MoveControlStyle.Dpad)
            {
                width = height = 274f;
                var ring = Icon(preview, _skin.Asset("stick-base"), null, null, 0f, 0f);
                ring.Scale = new Vector2(1.08f, 1.08f);
                ring.Modulate = new Color(1f, 1f, 1f, 0.75f);
                Icon(preview, _skin.Asset("arrow-up"), 71f, 51f, 0f, -73f).Scale = new Vector2(1.14f, 1.14f);
                Icon(preview, _skin.Asset("arrow-down"), 71f, 51f, 0f, 73f).Scale = new Vector2(1.14f, 1.14f);
                Icon(preview, _skin.Asset("arrow-left"), 71f, 51f, -73f, 0f).Scale = new Vector2(1.14f, 1.14f);
                Icon(preview, _skin.Asset("arrow-right"), 71f, 51f, 73f, 0f).Scale = new Vector2(1.14f, 1.14f);
            }
            else
            {
                width = height = 210f;
                Icon(preview, _skin.Asset("stick-base"), 210f, 210f, 0f, 0f);
                Icon(preview, _skin.Asset("button"), 70f, 70f, 0f, 0f);
            }
        }
        else if (id == "attack")
        {
            width = 159f;
            height = 160f;
            Icon(preview, _skin.Asset("button"), width, height, 0f, 0f);
        }
        else if (id == "swap")
        {
            width = height = 75f;
            Icon(preview, _skin.Asset("swap"), 75f, 75f, 0f, 0f);
        }
        else
        {
            var index = int.Parse(id.AsSpan(5));
            var slot = index < slots.Count ? slots[index] : HudShortcutSlot.Empty;
            Icon(preview, _skin.Asset("slot-shadow"), SlotSize, SlotSize, 0f, 0f);
            Icon(preview, _skin.Asset("slot-ring"), SlotSize, SlotSize, 0f, 0f);

            var isItem = slot.Entry is { Kind: ShortcutEntry.ItemKind };
            var size = isItem ? ItemIconSize : SlotIconSize;
            if (slot.Entry is not null && slot.Icon is { Length: > 0 } iconPath)
                Icon(preview, iconPath, size, size, 0f, 0f);

            if (isItem)
            {
                Icon(preview, _skin.Asset("quantity-badge"), 38f, 40f,
                    13f - SlotSize / 2f, 3f - SlotSize / 2f);
                PanelGfx.CenteredAt(slot.Quantity.ToString(), 32f - SlotSize / 2f, 23f - SlotSize / 2f,
                    32f, 18, new Color("#ffffff"), preview);
            }
        }

        preview.SetMeta("width", width);
        preview.SetMeta("height", height);
    }

    private TextureRect Icon(Control parent, string? path, float? width, float? height,
        float centerX, float centerY)
    {
        var rect = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        parent.AddChild(rect);
        if (path is null) return rect;

        var texture = _texture(path);
        if (texture is not null) rect.Texture = texture;

        var (w, h) = (width ?? texture?.GetWidth() ?? 0f, height ?? texture?.GetHeight() ?? 0f);
        rect.Size = new Vector2(w, h);
        rect.PivotOffset = new Vector2(w / 2f, h / 2f);
        rect.Position = new Vector2(centerX - w / 2f, centerY - h / 2f);
        return rect;
    }

    public void Layout()
    {
        if (!IsInsideTree() || _controls.Count == 0) return;

        var viewport = ControlViewports.Current(this);
        var sizeScale = _preferences.SizeScale;
        var placements = ControlLayoutResolver.Resolve(viewport, _preferences.Snapshot, sizeScale);
        foreach (var (id, preview) in _controls)
        {
            if (_dragging == id) continue;
            var placement = placements[id];
            preview.Position = new Vector2(placement.X, placement.Y);
            preview.Scale = new Vector2(placement.Scale, placement.Scale);
        }

        // 工具条自己不吃「按键大小」：调大按键不该把关闭钮和工具条一起撑大，
        // 它只跟屏幕适配走（与 resolver 里的 layoutScale 同一条口径）。
        var toolbarScale = ControlLayouts.ActionScale(viewport.Safe.Width, viewport.Safe.Height);
        var (left, top, safeWidth, _) = viewport.Safe;
        _toolbar.Position = new Vector2(left, top);
        _toolbar.Scale = new Vector2(toolbarScale, toolbarScale);

        var toolbarWidth = safeWidth / toolbarScale;
        _resetButton.Position = new Vector2(toolbarWidth / 2f - ToolbarButtonWidth / 2f, 12f);

        var minusX = 100f;
        var plusX = minusX + SizeButtonSize + 12f + SizeValueWidth + 12f;
        _sizeMinus.Position = new Vector2(minusX, 12f);
        _sizeValue.Position = new Vector2(minusX + SizeButtonSize + 12f, 28f);
        _sizePlus.Position = new Vector2(plusX, 12f);

        _sizeValue.Text = $"{MathF.Round(sizeScale * 100f)}%";
        _sizeMinus.Disabled = sizeScale <= ControlLayoutPreferences.MinSizeScale + 0.001f;
        _sizePlus.Disabled = sizeScale >= ControlLayoutPreferences.MaxSizeScale - 0.001f;
    }

    public override void _Input(InputEvent @event)
    {
        if (!IsOpen || !Visible) return;

        switch (@event)
        {
            case InputEventMouseButton mouse when mouse.ButtonIndex == MouseButton.Left:
                if (mouse.Pressed) BeginDrag(mouse.Position);
                else if (_dragging is not null) EndDrag();
                break;

            case InputEventScreenTouch touch:
                if (touch.Pressed) BeginDrag(touch.Position);
                else if (_dragging is not null) EndDrag();
                break;

            case InputEventMouseMotion motion when _dragging is not null:
                Drag(motion.Position);
                break;

            case InputEventScreenDrag drag when _dragging is not null:
                Drag(drag.Position);
                break;
        }
    }

    public bool DebugDrag(string id, Vector2 from, Vector2 to)
    {
        BeginDrag(from);
        if (_dragging != id)
        {
            Cancel();
            return false;
        }

        Drag(to);
        EndDrag();
        return true;
    }

    private void BeginDrag(Vector2 point)
    {
        foreach (var (id, preview) in _controls)
        {
            var width = (float)preview.GetMeta("width", 94f);
            var height = (float)preview.GetMeta("height", 94f);
            var half = new Vector2(width, height) * preview.Scale.X / 2f;
            var rect = new Rect2(preview.Position - half, half * 2f);
            if (!rect.HasPoint(point)) continue;

            _dragging = id;
            _dragPoint = null;
            Drag(point);
            return;
        }
    }

    private void Drag(Vector2 point)
    {
        if (_dragging is not { } id) return;

        var viewport = ControlViewports.Current(this);
        var positions = _preferences.Snapshot;
        var sizeScale = _preferences.SizeScale;
        var normalized = ControlLayoutResolver.Normalize(viewport, id, point.X, point.Y, positions, sizeScale);
        _dragPoint = normalized;

        var overrides = new Dictionary<string, (float X, float Y)>(positions) { [id] = normalized };
        var placement = ControlLayoutResolver.Resolve(viewport, overrides, sizeScale)[id];
        _controls[id].Position = new Vector2(placement.X, placement.Y);
    }

    private void EndDrag()
    {
        var id = _dragging;
        var point = _dragPoint;
        _dragging = null;
        _dragPoint = null;

        if (id is not null && point is { } committed) _preferences.Set(id, committed.X, committed.Y);
        else Layout();
    }
}
