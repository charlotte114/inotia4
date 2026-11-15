using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Inotia4.Domain.Character;
using Inotia4.Domain.Content;
using Inotia4.Domain.Ui;

namespace Inotia4.Ui;

public sealed record HudShortcutSlot(
    ShortcutEntry? Entry, string? Icon, int Quantity, bool Enabled,
    Inotia4.Domain.Combat.Cooldown? Cooldown = null)
{
    public static readonly HudShortcutSlot Empty = new(null, null, 0, false);

    public float? CooldownProgress =>
        Cooldown is { RemainingMs: > 0, DurationMs: > 0 } cooldown
            ? Mathf.Clamp(1f - cooldown.RemainingMs / (float)cooldown.DurationMs, 0f, 1f)
            : null;
}

public sealed record ActionControlsHandlers(Action Attack, Action<string> Item, Action Swap)
{
    
    public Action<string>? Skill { get; init; }
}

public partial class ActionControls : Control
{
    private const float SlotIconSize = 75f;
    private const float ItemIconSize = 64f;
    private const float SlotSize = 94f;
    private const float SlotHitSize = 80f;

    private ControlSkinCatalog _skin = null!;
    private Func<string, Texture2D?> _texture = null!;
    private ActionControlsHandlers _handlers = null!;
    private ControlLayoutPreferences? _preferences;
    private IDisposable? _preferencesSubscription;
    private readonly ActionPressGate _pressGate = new();

    private readonly List<HudControl> _controls = [];
    private HudControl _attack = null!;
    private HudControl _swap = null!;
    private HudShortcutSlot[] _slots = [HudShortcutSlot.Empty, HudShortcutSlot.Empty, HudShortcutSlot.Empty,
        HudShortcutSlot.Empty, HudShortcutSlot.Empty, HudShortcutSlot.Empty];

    public ActionControls Skin(ControlSkinCatalog skin) { _skin = skin; return this; }

    public ActionControls TextureOf(Func<string, Texture2D?> texture) { _texture = texture; return this; }

    public ActionControls With(ActionControlsHandlers handlers) { _handlers = handlers; return this; }

    public ActionControls Preferences(ControlLayoutPreferences? preferences)
    {
        _preferences = preferences;
        return this;
    }

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;

        _attack = BuildControl(ControlLayouts.AttackWidth, ControlLayouts.AttackHeight,
            ["button"], "button-pressed", () => _handlers.Attack(), badge: false);
        for (var index = 0; index < ShortcutLayout.SlotCount; index += 1)
        {
            var slotIndex = index;
            var slot = BuildControl(SlotSize, SlotSize,
                ["slot-shadow", "slot-ring"], "slot-pressed", () => Activate(slotIndex), badge: true);
            _controls.Add(slot);
        }

        _swap = BuildControl(75f, 75f, ["swap"], "swap-pressed", () => _handlers.Swap(), badge: false);
        _controls.Insert(0, _attack);
        _controls.Add(_swap);

        Resized += Layout;
        _preferencesSubscription = _preferences?.Subscribe(Layout);
        VisibilityChanged += () => { if (!IsVisibleInTree()) ResetInput(); };
        Layout();
    }

    public override void _ExitTree()
    {
        _preferencesSubscription?.Dispose();
        _preferencesSubscription = null;
        ResetInput();
    }

    private HudControl BuildControl(float size, float height, string[] images, string pressed, Action action,
        bool badge)
    {
        var root = new Control { MouseFilter = MouseFilterEnum.Ignore };
        AddChild(root);
        foreach (var key in images)
            PanelGfx.Image(_texture, _skin.Asset(key), -size / 2f, -height / 2f, size, height, root);

        var pressedPath = _skin.Asset(pressed);
        var highlight = new TextureRect
        {
            Texture = pressedPath is null ? null : _texture(pressedPath),
            Position = new Vector2(-size / 2f, -height / 2f),
            Size = new Vector2(size, height),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = MouseFilterEnum.Ignore,
            Visible = false,
            Material = new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add },
        };
        root.AddChild(highlight);

        Control? badgeRoot = null;
        Label? quantity = null;
        CooldownFan? fan = null;
        if (badge)
        {
            fan = new CooldownFan { Name = "Cooldown" };
            root.AddChild(fan);

            badgeRoot = new Control
            {
                Position = new Vector2(32f - 19f, 23f - 20f),
                ZIndex = 4,
                MouseFilter = MouseFilterEnum.Ignore,
                Visible = false,
            };
            root.AddChild(badgeRoot);
            PanelGfx.Image(_texture, _skin.Asset("quantity-badge"), 0f, 0f, 38f, 40f, badgeRoot);
            quantity = new Label
            {
                Text = string.Empty,
                Position = new Vector2(0f, 8f),
                Size = new Vector2(38f, 24f),
                HorizontalAlignment = HorizontalAlignment.Center,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            quantity.AddThemeFontSizeOverride("font_size", 15);
            quantity.AddThemeColorOverride("font_color", Colors.White);
            badgeRoot.AddChild(quantity);
        }

        var half = Math.Min(size, height) / 2f;
        var hitRadius = badge ? 40f : half;
        var button = PanelGfx.Hit(-half, -half, half * 2f, half * 2f, () => { }, root, hitRadius: hitRadius);
        highlight.ZIndex = 3;
        var control = new HudControl(root, button, highlight, height)
        {
            Badge = badgeRoot,
            QuantityLabel = quantity,
            Fan = fan,
        };
        button.GuiInput += input =>
        {
            var pointer = input switch
            {
                InputEventScreenTouch { Pressed: true } touch => touch.Index,
                InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } => -1,
                _ => (int?)null,
            };
            if (pointer is not { } id || button.Disabled || !_pressGate.Press(id)) return;
            control.Pointer = id;
            highlight.Visible = true;
            button.AcceptEvent();
            action();
        };
        return control;
    }

    public override void _Input(InputEvent input)
    {
        var pointer = input switch
        {
            InputEventScreenTouch { Pressed: false } touch => touch.Index,
            InputEventMouseButton { Pressed: false, ButtonIndex: MouseButton.Left } => -1,
            _ => (int?)null,
        };
        if (pointer is not { } id) return;
        _pressGate.Release(id);
        foreach (var control in _controls)
        {
            if (control.Pointer != id) continue;
            control.Pointer = null;
            control.Highlight.Visible = false;
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationApplicationFocusOut) ResetInput();
    }

    public void ResetInput()
    {
        _pressGate.Reset();
        foreach (var control in _controls)
        {
            control.Pointer = null;
            control.Highlight.Visible = false;
        }
    }

    private void Activate(int index)
    {
        var entry = index < _slots.Length ? _slots[index].Entry : null;
        if (entry is null) return;
        if (entry.Kind == ShortcutEntry.SkillKind)
        {
            _handlers.Skill?.Invoke(entry.Id);
            return;
        }

        _handlers.Item(entry.Id);
    }

    public IReadOnlyList<HudShortcutSlot> Slots => _slots;

    public (bool FanActive, float FanProgress, float IconAlpha, bool Disabled) DebugSlot(int index)
    {
        var control = _controls[index + 1];
        var alpha = control.Icon?.Modulate.A ?? control.Badge?.Modulate.A
                    ?? control.QuantityLabel?.Modulate.A ?? 1f;
        return (control.Fan?.Active == true, control.Fan?.Progress ?? 0f, alpha, control.Button.Disabled);
    }

    public Vector2 DebugSlotCenter(int index) => _controls[index + 1].Root.GlobalPosition;

    /// <summary>画当前这一组的六格。每帧都会被调，所以内部按内容差量重画。</summary>
    public void SetShortcutPage(IReadOnlyList<HudShortcutSlot> slots)
    {
        for (var index = 0; index < _slots.Length; index += 1)
        {
            _slots[index] = index < slots.Count ? slots[index] : HudShortcutSlot.Empty;
            PaintSlot(_controls[index + 1], _slots[index]);
        }
    }

    private void PaintSlot(HudControl control, HudShortcutSlot slot)
    {
        var entry = slot.Entry;
        var isItem = entry?.Kind == ShortcutEntry.ItemKind;

        var iconSize = isItem ? ItemIconSize : SlotIconSize;
        var file = entry is null ? null : slot.Icon;
        if (control.IconKey != file || control.IconSize != iconSize)
        {
            control.IconKey = file;
            control.IconSize = iconSize;
            if (control.Icon is { } old)
            {
                old.QueueFree();
                control.Icon = null;
            }

            if (file is not null)
                control.Icon = PanelGfx.Image(_texture, file,
                    -iconSize / 2f, -iconSize / 2f, iconSize, iconSize, control.Root);
        }

        var showBadge = entry is not null && isItem;
        if (control.Badge is not null) control.Badge.Visible = showBadge;
        var quantity = showBadge ? slot.Quantity.ToString() : string.Empty;
        if (control.QuantityLabel is not null && control.QuantityLabel.Text != quantity)
            control.QuantityLabel.Text = quantity;

        var cooling = slot.Cooldown is { RemainingMs: > 0 };
        control.Fan?.Set(slot.CooldownProgress, iconSize / 2f);

        var usable = entry is not null && !cooling && (isItem ? slot.Quantity > 0 : slot.Enabled);
        var dim = entry is not null && !usable && !cooling;
        var alpha = dim ? 0.4f : 1f;
        if (control.Icon is not null) control.Icon.Modulate = new Color(1f, 1f, 1f, alpha);
        if (control.Badge is not null) control.Badge.Modulate = new Color(1f, 1f, 1f, alpha);
        if (control.QuantityLabel is not null) control.QuantityLabel.Modulate = new Color(1f, 1f, 1f, alpha);
        control.Button.Disabled = !usable;
    }

    private void Layout()
    {
        var size = GetViewportRect().Size;
        var placements = ControlLayoutResolver.Resolve(
            new ControlViewport(size.X, size.Y, 0f, 0f, 0f, 0f), _preferences?.Snapshot,
            _preferences?.SizeScale ?? 1f);

        void Place(HudControl control, string id)
        {
            if (!placements.TryGetValue(id, out var point)) return;
            control.Root.Position = new Vector2(point.X, point.Y);
            control.Root.Scale = new Vector2(point.Scale, point.Scale);
        }

        Place(_attack, "attack");
        for (var index = 0; index < ShortcutLayout.SlotCount; index += 1)
            Place(_controls[index + 1], $"slot-{index}");
        Place(_swap, "swap");
    }

    private sealed class HudControl(
        Control root, Button button, TextureRect highlight, float iconSize)
    {
        public Control Root { get; } = root;
        public Button Button { get; } = button;
        public TextureRect Highlight { get; } = highlight;
        public int? Pointer { get; set; }
        public TextureRect? Icon { get; set; }
        public string? IconKey { get; set; }
        public float IconSize { get; set; } = iconSize;
        public Control? Badge { get; set; }
        public Label? QuantityLabel { get; set; }
        public CooldownFan? Fan { get; set; }
    }
}
