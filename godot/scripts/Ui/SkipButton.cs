using System;
using Godot;
using Inotia4.Domain.Content;

namespace Inotia4.Ui;

public partial class SkipButton : Control
{
    private const float Art = 1.2f;
    private const float Width = 136f * Art;
    private const float Height = 62f * Art;
    private const float RightMargin = 32f * Art;
    private const float TopMargin = 16f * Art;

    public event Action? Pressed;

    private TextureRect? _highlight;
    private Button _hit = null!;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.TopRight);
        OffsetLeft = -(Width + RightMargin);
        OffsetRight = -RightMargin;
        OffsetTop = TopMargin;
        OffsetBottom = TopMargin + Height;
    }

    public void Setup(ControlSkinCatalog? skin, Func<string, Texture2D?> texture)
    {
        PanelGfx.Image(texture, skin?.Asset("skip"), 0f, 0f, Width, Height, this);

        _highlight = PanelGfx.Image(texture, skin?.Asset("skip-pressed"), 0f, 0f, Width, Height, this);
        if (_highlight is not null)
        {
            _highlight.Visible = false;
            _highlight.Material = new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add };
            _highlight.MouseFilter = MouseFilterEnum.Ignore;
        }

        _hit = new Button
        {
            Name = "Hit",
            Flat = true,
            FocusMode = FocusModeEnum.None,
        };
        _hit.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _hit.ButtonDown += () => { if (_highlight is not null) _highlight.Visible = true; };
        _hit.ButtonUp += () => { if (_highlight is not null) _highlight.Visible = false; };
        _hit.MouseExited += () => { if (_highlight is not null) _highlight.Visible = false; };
        _hit.Pressed += () =>
        {
            PanelGfx.Select();
            Pressed?.Invoke();
        };
        AddChild(_hit);

        Visible = false;
    }
}
