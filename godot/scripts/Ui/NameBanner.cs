using System;
using Godot;
using Inotia4.Domain.Ui;

namespace Inotia4.Ui;

public sealed partial class NameBanner : Control
{
    public const float FadeInSeconds = 0.15f;

    public const float HoldSeconds = 2f;

    public const float FadeOutSeconds = 0.4f;

    private const float TextPadding = 32f;

    private const float BannerTop = 24f;
    private static readonly Vector2 BannerSize = new(480f, 34f);

    private enum Phase
    {
        Hidden,
        FadeIn,
        Hold,
        FadeOut,
    }

    private ColorRect _background = null!;
    private Label _label = null!;
    private bool _built;
    private string _mapName = string.Empty;
    
    private bool _mapPending = true;
    private string? _kind;
    private bool _enabled;
    private Phase _phase = Phase.Hidden;
    private float _elapsed;

    public NameBanner()
    {
        Name = "NameBanner";
        SetAnchorsPreset(LayoutPreset.CenterTop);
        OffsetLeft = -BannerSize.X * 0.5f;
        OffsetRight = BannerSize.X * 0.5f;
        OffsetTop = BannerTop;
        OffsetBottom = BannerTop + BannerSize.Y;
        MouseFilter = MouseFilterEnum.Ignore;
        Visible = false;
    }

    public string Text => _built ? _label.Text : string.Empty;

    public bool Showing => Visible && _kind is not null;

    public float Alpha => Modulate.A;

    public float TextScale => _built ? _label.Scale.X : 1f;

    public (Vector2 Position, Vector2 Size) Backdrop => (_background.Position, _background.Size);

    public override void _Ready() => EnsureBuilt();

    /// <summary>
    /// 换图时调：记住新地图名，让「进图报一次」重新挂上。
    /// 原版每张图新建一份名条，这里共用一份，所以状态要在这里复位。
    /// </summary>
    public NameBanner Setup(string mapName)
    {
        EnsureBuilt();
        _mapName = mapName ?? string.Empty;
        _mapPending = true;
        _kind = null;
        _phase = Phase.Hidden;
        _elapsed = 0f;
        Visible = false;
        SetAlpha(1f);
        SetText(string.Empty);
        return this;
    }

    public void Sync(bool enabled, string? targetName)
    {
        EnsureBuilt();
        _enabled = enabled;
        if (!enabled)
        {
            Visible = false;
            return;
        }

        if (targetName is not null)
        {
            if (_kind == "target" && _label.Text == targetName)
            {
                Visible = true;
                return;
            }

            CancelAnimation();
            _mapPending = false;
            _kind = "target";
            SetText(targetName);
            SetAlpha(1f);
            Visible = true;
            return;
        }

        if (_kind == "target")
        {
            _kind = null;
            Visible = false;
            return;
        }

        if (_mapPending)
        {
            _mapPending = false;
            _kind = "map";
            SetText(_mapName);
            SetAlpha(0f);
            _phase = Phase.FadeIn;
            _elapsed = 0f;
            Visible = true;
            return;
        }

        Visible = _kind is not null && _phase != Phase.Hidden;
    }

    public override void _Process(double delta) => Advance((float)delta);

    public void Advance(float delta)
    {
        EnsureBuilt();
        if (!_enabled || _phase == Phase.Hidden) return;

        _elapsed += delta;
        switch (_phase)
        {
            case Phase.FadeIn:
                SetAlpha(Mathf.Clamp(_elapsed / FadeInSeconds, 0f, 1f));
                if (_elapsed >= FadeInSeconds)
                {
                    _phase = Phase.Hold;
                    _elapsed = 0f;
                    SetAlpha(1f);
                }

                break;
            case Phase.Hold:
                if (_elapsed >= HoldSeconds)
                {
                    _phase = Phase.FadeOut;
                    _elapsed = 0f;
                }

                break;
            case Phase.FadeOut:
                SetAlpha(Mathf.Clamp(1f - _elapsed / FadeOutSeconds, 0f, 1f));
                if (_elapsed >= FadeOutSeconds)
                {
                    _phase = Phase.Hidden;
                    _kind = null;
                    SetAlpha(1f);
                    Visible = false;
                }

                break;
        }
    }

    private void EnsureBuilt()
    {
        if (_built) return;
        _built = true;

        _background = new ColorRect
        {
            Name = "Backdrop",
            Color = new Color(0f, 0f, 0f, 0.55f),
            MouseFilter = MouseFilterEnum.Ignore,
            Position = Vector2.Zero,
            Size = new Vector2(NameBannerRules.MinWidth, NameBannerRules.Height),
        };
        AddChild(_background);

        _label = new Label
        {
            Name = "Label",
            Text = string.Empty,
            Position = Vector2.Zero,
            Size = BannerSize,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _label.AddThemeFontSizeOverride("font_size", 18);
        _label.AddThemeColorOverride("font_color", new Color("fff4dc"));
        // 等比缩小是以文字中心为原点缩的，否则会往左上角缩。
        _label.PivotOffset = BannerSize / 2f;
        AddChild(_label);
    }

    private void CancelAnimation()
    {
        _phase = Phase.Hidden;
        _elapsed = 0f;
    }

    private void SetText(string text)
    {
        _label.Text = text;
        LayoutBackdrop(FitText());
    }

    private float FitText()
    {
        _label.Scale = Vector2.One;
        if (_label.Text.Length == 0) return 0f;

        var font = _label.GetThemeFont("font");
        if (font is null) return 0f;

        var width = font.GetStringSize(_label.Text, HorizontalAlignment.Left, -1, 18).X;
        var limit = MathF.Max(1f, BannerSize.X - TextPadding);
        if (width <= limit) return width;

        var scale = limit / width;
        _label.Scale = new Vector2(scale, scale);
        return width * scale;
    }

    private void LayoutBackdrop(float textWidth)
    {
        var width = NameBannerRules.BackdropWidth(textWidth);
        _background.Size = new Vector2(width, NameBannerRules.Height);
        _background.Position = new Vector2(NameBannerRules.BackdropLeft(textWidth, BannerSize.X),
            NameBannerRules.BackdropTop(BannerSize.Y));
    }

    private void SetAlpha(float alpha) =>
        Modulate = new Color(Modulate.R, Modulate.G, Modulate.B, alpha);
}
