using System;
using System.Collections.Generic;
using Godot;
using Inotia4.Application;
using Inotia4.Domain.Content;
using Inotia4.Rendering;

namespace Inotia4.Ui;

public partial class ClassSelectScene : Control
{
    private const float DesignWidth = 960f;
    private const float DesignHeight = 640f;
    private const float SurfacePadding = 8f;

    private const int NameFontSize = 28;
    private const int BodyFontSize = 23;
    private const int AttributeFontSize = 22;

    private static readonly Color BodyColor = Colors.White;
    private static readonly Color AttributeNameColor = new("#989898");

    private static readonly (float X, float Y)[] IconPositions =
    [
        (37f, 118f), (70f, 251f), (198f, 313f),
        (648f, 313f), (776f, 251f), (809f, 118f),
    ];

    private const float IconWidth = 116f;
    private const float IconHeight = 114f;

    private static string CloseArt => "assets/ui/menus/close.png";
    private static string ClosePressedArt => "assets/ui/menus/close-pressed.png";

    public Action<string>? ClassChosen { get; set; }

    public Action? BackRequested { get; set; }

    public string? AutoPlayClass { get; set; }

    public string? InitialClass { get; set; }

    private StoryClassCatalog _catalog = null!;
    private TextureCache _textures = null!;
    private TextureLease _lease = null!;
    private Control _canvas = null!;
    private Control _surface = null!;
    private StoryClassInfo? _selected;
    private Node2D? _preview;
    private bool _busy;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        GameTheme.Apply(this);

        _textures = new TextureCache();
        _lease = _textures.Lease();
        _catalog = new ContentLoader().StoryClasses;

        var backdrop = new ColorRect
        {
            Color = Colors.Black,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        backdrop.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(backdrop);

        var viewport = GetViewportRect().Size;
        var factor = MathF.Min((viewport.X - SurfacePadding * 2f) / DesignWidth,
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

        _surface = new Control
        {
            Size = new Vector2(DesignWidth, DesignHeight),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _canvas.AddChild(_surface);

        var initial = _catalog.Class(InitialClass ?? string.Empty) ?? _catalog.Default;
        if (initial is null)
        {
            GD.PushWarning("[class-select] 职业表是空的");
            return;
        }

        Choose(initial);

        if (AutoPlayClass is not null)
        {
            var target = _catalog.Class(AutoPlayClass) ?? initial;
            CallDeferred(nameof(ChooseAndStart), target.Id);
        }
    }

    public override void _ExitTree() => _lease?.Dispose();

    private void ChooseAndStart(string classId)
    {
        var target = _catalog.Class(classId);
        if (target is not null) Choose(target);
        Start();
    }

    private void Choose(StoryClassInfo entry)
    {
        _selected = entry;
        PanelGfx.Clear(_surface);
        _preview?.QueueFree();
        _preview = null;

        var assets = _catalog.Assets;

        PanelGfx.Image(_lease.Texture, _catalog.Background, 80f, 0f, 800f, 640f, _surface);
        PanelGfx.Image(_lease.Texture, assets.Title, 263f, 20f, 434f, 56f, _surface);
        Text(entry.Name, 480f, 31f, NameFontSize, BodyColor, _surface, TextAlign.Center, 400f);

        ImageButton("", 12f, 16f, 67f, 67f, CloseArt, ClosePressedArt, Back, _surface);

        for (var index = 0; index < _catalog.Classes.Length && index < IconPositions.Length; index += 1)
        {
            var character = _catalog.Classes[index];
            var (x, y) = IconPositions[index];
            var button = ImageButton("", x, y, IconWidth, IconHeight, character.Icon, null,
                () => Choose(character), _surface);
            if (character.Id != entry.Id) continue;

            var highlight = new TextureRect
            {
                Position = Vector2.Zero,
                Size = new Vector2(IconWidth, IconHeight),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale,
                MouseFilter = MouseFilterEnum.Ignore,
                Material = new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add },
            };
            if (_lease.Texture(assets.Selected) is { } selectedTexture) highlight.Texture = selectedTexture;
            button.AddChild(highlight);
        }

        ImageButton("", 334f, 388f, 292f, 77f, assets.Start, assets.StartPressed, Start, _surface);

        PanelGfx.Fill(42f, 464f, 699f, 174f, new Color(0f, 0f, 0f, 0.4f), _surface);
        PanelGfx.Image(_lease.Texture, assets.DescriptionTop, 71f, 456f, 0f, 0f, _surface);
        PanelGfx.Image(_lease.Texture, assets.PanelLeft, 32f, 455f, 0f, 0f, _surface);
        PanelGfx.Image(_lease.Texture, assets.PanelRight, 712f, 455f, 0f, 0f, _surface);
        var body = new RichTextLabel
        {
            BbcodeEnabled = false,
            ScrollActive = false,
            Position = new Vector2(80f, 485f),
            Size = new Vector2(622f, 132f),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Text = entry.Description,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        body.AddThemeFontSizeOverride("normal_font_size", BodyFontSize);
        body.AddThemeColorOverride("default_color", BodyColor);
        _surface.AddChild(body);

        PanelGfx.Fill(767f, 464f, 151f, 174f, new Color(0f, 0f, 0f, 0.4f), _surface);
        PanelGfx.Image(_lease.Texture, assets.AttributesTop, 796f, 455f, 0f, 0f, _surface);
        PanelGfx.Image(_lease.Texture, assets.PanelLeft, 757f, 454f, 0f, 0f, _surface);
        PanelGfx.Image(_lease.Texture, assets.PanelRight, 888f, 454f, 0f, 0f, _surface);
        for (var index = 0; index < entry.Attributes.Length; index += 1)
        {
            var attribute = entry.Attributes[index];
            var top = 482f + index * 28f;
            Text(attribute.Name, 782f, top, AttributeFontSize, AttributeNameColor, _surface);
            Text(attribute.Value.ToString(), 834f, top, AttributeFontSize, BodyColor, _surface,
                TextAlign.Right, 60f);
        }

        if (entry.PreviewActor.Clips.Count > 0)
        {
            var visual = new CharacterVisual();
            visual.Build(entry.PreviewActor, _lease, "idle-south", false);
            var preview = new Node2D { Name = "Preview", Position = new Vector2(480f, 290f) };
            preview.Scale = new Vector2(3f, 3f);
            preview.AddChild(visual);
            visual.Play();
            _surface.AddChild(preview);
            _preview = preview;
        }
    }

    private void Start()
    {
        if (_busy || _selected is null) return;
        _busy = true;
        ClassChosen?.Invoke(_selected.Id);
    }

    private void Back()
    {
        if (_busy) return;
        _busy = true;
        BackRequested?.Invoke();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape }) Back();
    }

    private enum TextAlign { Left, Center, Right }

    private static Label Text(string value, float x, float y, int size, Color color, Control parent,
        TextAlign align = TextAlign.Left, float width = 0f)
    {
        var label = PanelGfx.Text(value, x, y, size, color, parent);
        if (width <= 0f) return label;

        label.HorizontalAlignment = align switch
        {
            TextAlign.Center => HorizontalAlignment.Center,
            TextAlign.Right => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Left,
        };
        label.Position = new Vector2(align == TextAlign.Center ? x - width / 2f : x, y);
        label.Size = new Vector2(width, label.Size.Y);
        return label;
    }

    private Button ImageButton(string label, float x, float y, float width, float height,
        string art, string? pressedArt, Action action, Control parent)
    {
        var button = PanelGfx.Hit(x, y, width, height, action, parent);
        PanelGfx.Image(_lease.Texture, art, 0f, 0f, width, height, button);

        if (!string.IsNullOrEmpty(pressedArt))
        {
            var pressed = new Control
            {
                Position = Vector2.Zero,
                Size = new Vector2(width, height),
                Visible = false,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            button.AddChild(pressed);
            PanelGfx.Image(_lease.Texture, pressedArt, 0f, 0f, width, height, pressed);
            button.ButtonDown += () => pressed.Visible = true;
            button.ButtonUp += () => pressed.Visible = false;
            button.MouseExited += () => pressed.Visible = false;
        }

        if (label.Length > 0)
            Text(label, width / 2f, height / 2f, 20, BodyColor, button, TextAlign.Center, width - 12f);

        return button;
    }
}
