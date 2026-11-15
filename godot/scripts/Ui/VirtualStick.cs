using System;
using System.Collections.Generic;
using Godot;
using Inotia4.Domain.Content;
using Inotia4.Domain.Ui;
using Inotia4.Domain.World;

namespace Inotia4.Ui;

/// <summary>
/// 屏幕左下角的移动控制器：
/// 经典虚拟摇杆（自由 360°）与符文四向十字按键（严格四向互斥 + 发光反馈）两套视图
/// 预挂在同一容器里，切换移动方式时只切显隐与热区半径。
///
/// 键盘（WASD／方向键）也由这里处理：方向键的历史由本控件自己记，
/// 世界场景只读它的 <see cref="Axis"/>，所以按下方向键时摇杆的中位与十字箭头同样跟着亮。
/// </summary>
public partial class VirtualStick : Control
{
    private const float StickBaseRadius = 105f;
    private const float StickKnobRadius = 35f;
    private const float StickKnobLimit = StickBaseRadius * 0.6f;
    private const float StickDeadZone = 8.75f;

    private const float DpadBaseRadius = 137f;
    private const float DpadRuneScale = 1.08f;
    private const float DpadRuneAlpha = 0.75f;
    private const float DpadArrowOffset = 73f;
    private const float DpadArrowScaleNorm = 1.14f;
    private const float DpadArrowScaleGlow = 1.25f;
    private const float DpadDeadZone = 12f;

    private const int MousePointer = -2;
    private const int NoPointer = -1;

    private enum Cardinal
    {
        Up,
        Down,
        Left,
        Right,
    }

    private ControlSkinCatalog _skin = null!;
    private Func<string, Texture2D?> _texture = null!;
    private ControlLayoutPreferences? _preferences;
    private ControlSettings? _settings;
    private IDisposable? _settingsSubscription;
    private IDisposable? _preferencesSubscription;

    private TextureRect _stickBase = null!;
    private TextureRect _stickKnob = null!;
    private TextureRect _dpadBase = null!;
    private readonly TextureRect[] _arrows = new TextureRect[4];

    private static readonly Vector2[] ArrowCenters =
    [
        new(0f, -DpadArrowOffset), new(0f, DpadArrowOffset),
        new(-DpadArrowOffset, 0f), new(DpadArrowOffset, 0f),
    ];

    private readonly string[] _arrowKeys = ["", "", "", ""];

    private readonly List<Cardinal> _keyHistory = [];

    private Vector2 _axis = Vector2.Zero;
    private int _pointerId = NoPointer;
    private float _hitRadius = StickBaseRadius;

    public Vector2 Axis => _axis;

    public Vector2 Direction => _axis;

    public bool DpadActive => _dpadBase is not null && _dpadBase.Visible;

    public VirtualStick Skin(ControlSkinCatalog skin)
    {
        _skin = skin;
        return this;
    }

    public VirtualStick TextureOf(Func<string, Texture2D?> texture)
    {
        _texture = texture;
        return this;
    }

    public VirtualStick Preferences(ControlLayoutPreferences? preferences)
    {
        _preferences = preferences;
        return this;
    }

    public VirtualStick Settings(ControlSettings? settings)
    {
        _settings = settings;
        return this;
    }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        Size = new Vector2(1f, 1f);

        _stickBase = Image(_skin.Asset("stick-base"), StickBaseRadius * 2f, StickBaseRadius * 2f);
        _stickKnob = Image(_skin.Asset("button"), StickKnobRadius * 2f, StickKnobRadius * 2f);

        _dpadBase = Image(_skin.Asset("stick-base"), null, null);
        _dpadBase.Scale = new Vector2(DpadRuneScale, DpadRuneScale);
        _dpadBase.Modulate = new Color(1f, 1f, 1f, DpadRuneAlpha);
        _arrows[0] = Image(_skin.Asset("arrow-up"), null, null);
        _arrows[1] = Image(_skin.Asset("arrow-down"), null, null);
        _arrows[2] = Image(_skin.Asset("arrow-left"), null, null);
        _arrows[3] = Image(_skin.Asset("arrow-right"), null, null);
        CenterAt(_arrows[0], new Vector2(0f, -DpadArrowOffset));
        CenterAt(_arrows[1], new Vector2(0f, DpadArrowOffset));
        CenterAt(_arrows[2], new Vector2(-DpadArrowOffset, 0f));
        CenterAt(_arrows[3], new Vector2(DpadArrowOffset, 0f));

        ApplyStyle(_settings?.MoveStyle ?? MoveControlStyle.Stick);

        _preferencesSubscription = _preferences?.Subscribe(Layout);
        _settingsSubscription = _settings?.Subscribe(ApplyStyle);
        Resized += Layout;
        Layout();
    }

    public override void _ExitTree()
    {
        _preferencesSubscription?.Dispose();
        _preferencesSubscription = null;
        _settingsSubscription?.Dispose();
        _settingsSubscription = null;
    }

    public override void _Notification(int what)
    {
        if (what != (int)NotificationApplicationFocusOut) return;
        _keyHistory.Clear();
        if (_pointerId == NoPointer) SyncKeyAxis();
    }

    /// <summary>一张居中摆放的操作区贴图；给宽高时按给定尺寸铺，否则用原图尺寸。</summary>
    private TextureRect Image(string? path, float? width, float? height)
    {
        var rect = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        AddChild(rect);
        if (path is null || _texture(path) is not { } source) return rect;

        rect.Texture = source;
        var (w, h) = (width ?? source.GetWidth(), height ?? source.GetHeight());
        rect.Size = new Vector2(w, h);
        // 所有贴图的原点都在中心：Godot 的 TextureRect 以左上角为原点，
        // 所以摆位要减去半宽半高，缩放要绕中心（PivotOffset）。
        rect.PivotOffset = new Vector2(w / 2f, h / 2f);
        CenterAt(rect, Vector2.Zero);
        return rect;
    }

    private static void CenterAt(TextureRect rect, Vector2 center) =>
        rect.Position = center - rect.Size / 2f;

    private void ApplyStyle(MoveControlStyle style)
    {
        Reset();
        var isDpad = style == MoveControlStyle.Dpad;
        _hitRadius = isDpad ? DpadBaseRadius : StickBaseRadius;

        _stickBase.Visible = !isDpad;
        _stickKnob.Visible = !isDpad;
        _dpadBase.Visible = isDpad;
        foreach (var arrow in _arrows) arrow.Visible = isDpad;
    }

    public void Layout()
    {
        if (!IsInsideTree()) return;
        var viewport = ControlViewports.Current(this);
        var placement = ControlLayoutResolver.Resolve(viewport, _preferences?.Snapshot,
            _preferences?.SizeScale ?? 1f)["move"];
        Size = new Vector2(viewport.Width, viewport.Height);
        Position = new Vector2(placement.X, placement.Y);
        Scale = new Vector2(placement.Scale, placement.Scale);
        Reset();
    }

    public void SetStickVisible(bool visible)
    {
        if (Visible == visible) return;
        Visible = visible;
        if (!visible) Reset();
    }

    public override void _Input(InputEvent @event)
    {
        if (!Visible) return;

        switch (@event)
        {
            case InputEventScreenTouch touch when touch.Pressed:
                if (InRange(touch.Position)) StartPointer(touch.Index, touch.Position);
                break;

            case InputEventScreenTouch touch when !touch.Pressed && touch.Index == _pointerId:
                Reset();
                break;

            case InputEventScreenDrag drag when drag.Index == _pointerId:
                Track(drag.Position);
                break;

            case InputEventMouseButton mouse when mouse.ButtonIndex == MouseButton.Left:
                if (mouse.Pressed)
                {
                    if (InRange(mouse.Position)) StartPointer(MousePointer, mouse.Position);
                }
                else if (_pointerId == MousePointer)
                {
                    Reset();
                }

                break;

            case InputEventMouseMotion motion when _pointerId == MousePointer:
                Track(motion.Position);
                break;

            case InputEventKey key:
                OnKey(key);
                break;
        }
    }

    private bool InRange(Vector2 point) => point.DistanceTo(Position) <= _hitRadius * Scale.X;

    private void StartPointer(int id, Vector2 point)
    {
        if (_pointerId != NoPointer && _settings?.MoveStyle != MoveControlStyle.Dpad) return;
        _pointerId = id;
        Track(point);
        GetViewport().SetInputAsHandled();
    }

    private void OnKey(InputEventKey key)
    {
        var direction = CardinalOf(key);
        if (direction is not { } card) return;

        var index = _keyHistory.IndexOf(card);
        if (key.Pressed)
        {
            if (key.Echo) return;
            if (index >= 0) _keyHistory.RemoveAt(index);
            _keyHistory.Add(card);
        }
        else
        {
            if (index < 0) return;
            _keyHistory.RemoveAt(index);
        }

        if (_pointerId == NoPointer) SyncKeyAxis();
    }

    private static Cardinal? CardinalOf(InputEventKey key)
    {
        switch (key.Keycode)
        {
            case Key.Up: return Cardinal.Up;
            case Key.Down: return Cardinal.Down;
            case Key.Left: return Cardinal.Left;
            case Key.Right: return Cardinal.Right;
        }

        return key.PhysicalKeycode switch
        {
            Key.W => Cardinal.Up,
            Key.S => Cardinal.Down,
            Key.A => Cardinal.Left,
            Key.D => Cardinal.Right,
            _ => null,
        };
    }

    private static Vector2 VectorOf(Cardinal direction) => direction switch
    {
        Cardinal.Up => new Vector2(0f, -1f),
        Cardinal.Down => new Vector2(0f, 1f),
        Cardinal.Left => new Vector2(-1f, 0f),
        _ => new Vector2(1f, 0f),
    };

    private void SyncKeyAxis()
    {
        if (_keyHistory.Count > 0 && Visible)
        {
            var vector = VectorOf(_keyHistory[^1]);
            _axis = vector;
        }
        else
        {
            _axis = Vector2.Zero;
        }

        _stickKnob.Position = _axis * StickKnobLimit - _stickKnob.Size / 2f;
        UpdateArrows();
    }

    private void Track(Vector2 point)
    {
        var local = (point - Position) / Scale.X;
        var distance = local.Length();

        var style = _settings?.MoveStyle ?? MoveControlStyle.Stick;

        if (style == MoveControlStyle.Dpad)
        {
            _axis = distance < DpadDeadZone ? Vector2.Zero : SnapCardinal(local);
            UpdateArrows();
        }
        else if (style == MoveControlStyle.FourWay)
        {
            _axis = distance >= StickDeadZone ? SnapCardinal(local) : Vector2.Zero;
            _stickKnob.Position = _axis * StickKnobLimit - _stickKnob.Size / 2f;
        }
        else
        {
            var ratio = Mathf.Min(1f, StickKnobLimit / (distance == 0f ? 1f : distance));
            var pushed = distance >= StickDeadZone;
            _axis = pushed ? local * ratio / StickKnobLimit : Vector2.Zero;
            _stickKnob.Position = (pushed ? local * ratio : Vector2.Zero) - _stickKnob.Size / 2f;
        }
    }

    private static Vector2 SnapCardinal(Vector2 local)
    {
        var snapped = MoveDirection.Snap(new Vec2(local.X, local.Y));
        return new Vector2(snapped.X, snapped.Y);
    }

    private void SetArrow(int index, string key, float scale)
    {
        var arrow = _arrows[index];
        if (_arrowKeys[index] != key)
        {
            _arrowKeys[index] = key;
            if (_texture(key) is { } source)
            {
                arrow.Texture = source;
                arrow.Size = new Vector2(source.GetWidth(), source.GetHeight());
                arrow.PivotOffset = arrow.Size / 2f;
                CenterAt(arrow, ArrowCenters[index]);
            }
        }

        arrow.Scale = new Vector2(scale, scale);
    }

    private void UpdateArrows()
    {
        var up = _axis.Y < -0.3f;
        var down = _axis.Y > 0.3f;
        var left = _axis.X < -0.3f;
        var right = _axis.X > 0.3f;
        SetArrow(0, _skin.Asset(up ? "arrow-up-glow" : "arrow-up") ?? string.Empty,
            up ? DpadArrowScaleGlow : DpadArrowScaleNorm);
        SetArrow(1, _skin.Asset(down ? "arrow-down-glow" : "arrow-down") ?? string.Empty,
            down ? DpadArrowScaleGlow : DpadArrowScaleNorm);
        SetArrow(2, _skin.Asset(left ? "arrow-left-glow" : "arrow-left") ?? string.Empty,
            left ? DpadArrowScaleGlow : DpadArrowScaleNorm);
        SetArrow(3, _skin.Asset(right ? "arrow-right-glow" : "arrow-right") ?? string.Empty,
            right ? DpadArrowScaleGlow : DpadArrowScaleNorm);
    }

    public void Reset()
    {
        _pointerId = NoPointer;
        _keyHistory.Clear();
        _axis = Vector2.Zero;

        // 摆位与缩放是节点自身的，子图标的局部坐标恒以原点为中心，所以直接归零。
        if (_stickKnob is not null) _stickKnob.Position = -_stickKnob.Size / 2f;
        if (_arrows[0] is not null) UpdateArrows();
    }
}
