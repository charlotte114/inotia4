using System;
using System.Collections.Generic;
using Godot;
using Inotia4.Domain.Content;
using Inotia4.Domain.Ui;

namespace Inotia4.Ui;

/// <summary>
/// 人物头顶的血蓝双条。
///
/// 整块挂在角色节点底下，所以它天然跟着人物走、跟着镜头缩放；
/// 位置取内容里的 <c>headAnchor</c>（相对脚点、y 为负），
/// 挂上之后就不再需要每帧对世界坐标。
///
/// 绘制顺序与皮肤一致：队长标记在最下、底框压住它、两条进度色块在最上。
/// 宽度一律走 <see cref="GaugeRules.Width"/>：
/// 向下取整、空槽整条隐藏。
/// </summary>
public sealed partial class OverheadStatus : Node2D
{
    private StatusOverheadSkin _skin = new();

    private IReadOnlyDictionary<string, string> _assets = new Dictionary<string, string>();

    private Sprite2D? _frame;

    private Sprite2D? _leader;

    private ColorRect? _health;

    private ColorRect? _mana;

    private Func<(int Health, int MaxHealth, int Mana, int MaxMana)?> _read = () => null;

    private bool _hasValues;

    private Vector2? _anchor;

    private bool _shown = true;

    private float _healthWidth = -1f;

    private float _manaWidth = -1f;

    public float HealthFillWidth => _healthWidth;

    public float ManaFillWidth => _manaWidth;

    public bool Shown => Visible;

    public Vector2? Anchor => _anchor;

    public OverheadStatus Build(StatusHudSkin hud, Func<string, Texture2D?> texture)
    {
        _skin = hud.Overhead;
        _assets = hud.Assets;
        _leader = Part(texture, "overhead-leader", _skin.Leader);
        _frame = Part(texture, "overhead-frame", _skin.Frame);
        _health = Bar("health", _skin.Health);
        _mana = Bar("mana", _skin.Mana);
        return this;
    }

    public OverheadStatus Data(Func<(int Health, int MaxHealth, int Mana, int MaxMana)?> read)
    {
        _read = read;
        return this;
    }

    private Sprite2D Part(Func<string, Texture2D?> texture, string key, StatusRectSkin rect)
    {
        var sprite = new Sprite2D
        {
            Name = key,
            Centered = false,
            Position = new Vector2(rect.X, rect.Y),
        };
        var path = _assets.GetValueOrDefault(key);
        var image = path is null ? null : texture(path);
        if (image is not null) sprite.Texture = image;
        AddChild(sprite);
        return sprite;
    }

    private ColorRect Bar(string name, StatusBarSkin rect)
    {
        var bar = new ColorRect
        {
            Name = name,
            Position = new Vector2(rect.X, rect.Y),
            Size = new Vector2(0f, rect.Height),
            Color = new Color(rect.Color),
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
        };
        AddChild(bar);
        return bar;
    }

    public void Refresh()
    {
        var values = _read();
        _hasValues = values is not null;
        var health = values is { } h ? GaugeRules.Width(h.Health, h.MaxHealth, _skin.Health.Width) : 0f;
        var mana = values is { } m ? GaugeRules.Width(m.Mana, m.MaxMana, _skin.Mana.Width) : 0f;
        if (MathF.Abs(health - _healthWidth) >= 0.5f) Apply(_health, _skin.Health, health, ref _healthWidth);
        if (MathF.Abs(mana - _manaWidth) >= 0.5f) Apply(_mana, _skin.Mana, mana, ref _manaWidth);
    }

    private static void Apply(ColorRect? bar, StatusBarSkin rect, float width, ref float cached)
    {
        if (bar is null) return;
        cached = width;
        bar.Size = new Vector2(width, rect.Height);
        bar.Visible = width > 0.5f;
    }

    /// <summary>
    /// 每帧统一对一次显隐与锚点：换装会换掉 <c>headAnchor</c>，所以锚点不是只在建节点时取一次。
    /// </summary>
    public void Sync(bool shown, AnchorPoint? anchor)
    {
        _shown = shown;
        _anchor = anchor is null ? null : new Vector2(anchor.X, anchor.Y);
        if (_anchor is { } point) Position = point;
        Visible = shown && _hasValues && _anchor is not null;
    }

    public bool ShownRequested => _shown;
}
