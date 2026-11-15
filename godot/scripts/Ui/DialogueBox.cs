using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Inotia4.Domain.Story;
using Inotia4.Domain.Ui;

namespace Inotia4.Ui;

public partial class DialogueBox : Control
{
    private const int BoxTextSize = 18;
    private const int ScreenTextSize = 28;
    private const int SpeakerSize = 15;

    private const float PortraitHeight = 516f;

    private const float PortraitBottom = 606f;

    private const float SideEdge = 56f;

    private const float PortraitGutter = 422f;

    public bool AutoAdvance { get; set; }

    private Panel _panel = null!;

    private TextureRect _portrait = null!;

    private Func<string, Texture2D?>? _texture;

    private bool _hasPortrait;

    private bool _portraitLeft;
    private VBoxContainer _column = null!;
    private Label _speaker = null!;
    private RichTextLabel _text = null!;
    private Label _hint = null!;
    private TaskCompletionSource<bool>? _advance;
    private Tween? _blink;

    private readonly TypeProgress _typed = new();

    private bool _typing;

    private string _currentText = string.Empty;

    private bool _currentScreen;

    private List<DialoguePage> _pages = [];

    private int _pageIndex;

    private int _pageTypedLength;

    private int[] _typedToVisible = [0];

    public override void _Ready()
    {
        // 目标行为是「点画面任意处翻页」。这里整块都不吃鼠标（Panel/RichTextLabel 默认是
        // MouseFilter.Stop，点在框上事件就到不了 _UnhandledInput，翻不了页），
        // 所以框本身只负责画，翻页统一由 _UnhandledInput 收。
        MouseFilter = MouseFilterEnum.Ignore;

        _panel = new Panel { Name = "Panel", MouseFilter = MouseFilterEnum.Ignore };
        _panel.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _panel.AddThemeStyleboxOverride("panel", CreateStyle());
        AddChild(_panel);

        _portrait = new TextureRect
        {
            Name = "Portrait",
            Visible = false,
            TopLevel = true,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        AddChild(_portrait);

        var margin = new MarginContainer { Name = "Margin", MouseFilter = MouseFilterEnum.Ignore };
        margin.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 18);
        AddChild(margin);

        _column = new VBoxContainer { Name = "Column", MouseFilter = MouseFilterEnum.Ignore };
        _column.AddThemeConstantOverride("separation", 6);
        margin.AddChild(_column);

        _speaker = new Label
        {
            Name = "Speaker", Visible = false, Text = string.Empty, MouseFilter = MouseFilterEnum.Ignore,
        };
        _speaker.AddThemeFontSizeOverride("font_size", SpeakerSize);
        _speaker.AddThemeColorOverride("font_color", new Color(1f, 0.84f, 0.45f));
        _column.AddChild(_speaker);

        _text = new RichTextLabel
        {
            Name = "Text",
            BbcodeEnabled = true,
            ScrollActive = false,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Text = string.Empty,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _text.AddThemeColorOverride("default_color", new Color(0.96f, 0.95f, 0.92f));
        _column.AddChild(_text);

        _hint = new Label
        {
            Name = "Hint",
            Text = "▼",
            HorizontalAlignment = HorizontalAlignment.Right,
            SizeFlagsVertical = SizeFlags.ShrinkEnd,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _hint.AddThemeFontSizeOverride("font_size", 14);
        _hint.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.8f));
        _column.AddChild(_hint);

        ApplyLayout(false);
        Visible = false;
    }

    internal void DebugPortrait(string? path, bool left) => SetPortrait(path, left);

    internal bool PortraitVisible => _portrait is { Visible: true };

    /// <summary>自检用：立绘落在屏幕上的矩形（TopLevel，所以是视口坐标）。</summary>
    internal Rect2 PortraitBounds => _portrait.GetGlobalRect();

    public DialogueBox Texture(Func<string, Texture2D?> texture)
    {
        _texture = texture;
        return this;
    }

    public async Task ShowAsync(string speaker, string text, bool screen = false,
        string? portraitPath = null, bool portraitLeft = false)
    {
        SetPortrait(screen ? null : portraitPath, portraitLeft);
        ApplyLayout(screen);

        Visible = true;
        _speaker.Visible = !screen && speaker.Length > 0;
        _speaker.Text = speaker;
        _currentText = text;
        _currentScreen = screen;

        if (AutoAdvance)
        {
            var full = StoryText.Bbcode(text);
            _text.Text = screen ? $"[center]{full}[/center]" : full;
            _text.VisibleCharacters = -1;
            _pages = [];
            _typing = false;
            StartBlink();
            return;
        }

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Paginate(screen);
        ShowPage(0);

        StartBlink();

        _advance = new TaskCompletionSource<bool>();
        await _advance.Task;
    }

    public async Task<(List<DialoguePage> Pages, float Width, int Lines)> PaginateForCheck(
        string text, bool screen = false)
    {
        // 自检只看分页，不带立绘：上一句留下的让位不能影响这里量到的宽度。
        _hasPortrait = false;
        _portraitLeft = false;
        if (_portrait is not null) _portrait.Visible = false;
        ApplyLayout(screen);
        Visible = true;
        _currentText = text;
        _currentScreen = screen;
        // 与真实流程一样先让 VBox 排完，否则量到的是 0×0 的旧尺寸。
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Paginate(screen);
        return (_pages, _checkWidth, _checkLines);
    }

    private float _checkWidth;

    private int _checkLines;

    public IReadOnlyList<int> LastPageLines => _lastPageLines;

    private List<int> _lastPageLines = [];

    private void Paginate(bool screen)
    {
        var size = screen ? ScreenTextSize : BoxTextSize;
        var font = _text.GetThemeFont("normal_font") ?? ThemeDB.FallbackFont;
        var lineHeight = Mathf.Max(1f, font.GetHeight(size));

        var width = _text.Size.X > 8f ? _text.Size.X - 4f : (screen ? 836f : 808f);
        // 全屏叙述用的是 FitContent，高度随文字变化，不能拿来算行数——固定按 4 行排。
        var lines = screen
            ? 4
            : Mathf.Max(1, Mathf.FloorToInt(_text.Size.Y / lineHeight));

        _checkWidth = Mathf.Max(1f, width);
        _checkLines = lines;
        float Measure(string value) => font.GetStringSize(value, HorizontalAlignment.Left, -1, size).X;
        _pages = DialoguePager.Pages(_currentText, _checkWidth, lines, Measure);
        if (_pages.Count == 0) _pages = [new DialoguePage(_currentText, _currentText.Length)];

        _lastPageLines = _pages
            .Select(page => DialoguePager.WrapText(DialoguePager.VisibleIndex(page.Text).Text, _checkWidth, Measure).Count)
            .ToList();
    }

    private void ShowPage(int index)
    {
        _pageIndex = index;
        var page = _pages[index];

        var body = StoryText.Bbcode(page.Text);
        _text.Text = _currentScreen ? $"[center]{body}[/center]" : body;

        _typedToVisible = DialoguePager.TypedToVisibleMap(page.Text);
        _pageTypedLength = _typedToVisible.Length - 1;

        _typed.Reset();
        _typing = _pageTypedLength > 0;
        _text.VisibleCharacters = _typing ? 0 : -1;
    }

    /// <summary>
    /// 逐字显示：每 24 毫秒出 2 个字，余数留到下一次。
    /// 控制码和显式换行不占进度，所以数的是 RichTextLabel 的可见字数。
    /// </summary>
    public override void _Process(double delta)
    {
        if (!_typing || !Visible) return;

        var total = _pageTypedLength;
        if (total <= 0)
        {
            _typing = false;
            _text.VisibleCharacters = -1;
            return;
        }

        if (_typed.Advance(delta * 1000.0, total) <= 0) return;

        _text.VisibleCharacters = _typedToVisible[Math.Min(_typed.Revealed, total)];
        if (_typed.Revealed >= total) _typing = false;
    }

    private bool CompleteTyping()
    {
        if (!_typing) return false;
        _typing = false;
        _typed.Complete(_pageTypedLength);
        _text.VisibleCharacters = -1;
        return true;
    }

    public void Close()
    {
        Visible = false;
        _typing = false;
        StopBlink();
        if (_portrait is not null) _portrait.Visible = false;
        _hasPortrait = false;
    }

    public void Skip()
    {
        CompleteTyping();
        if (_advance is null) return;
        var pending = _advance;
        _advance = null;
        pending.TrySetResult(true);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (_advance is null || !Visible) return;

        var advance = @event switch
        {
            InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } => true,
            InputEventKey { Pressed: true, Keycode: Key.Space or Key.Enter or Key.Z } => true,
            _ => false,
        };

        if (!advance) return;

        GetViewport().SetInputAsHandled();
        if (CompleteTyping()) return;

        if (_pageIndex + 1 < _pages.Count)
        {
            ShowPage(_pageIndex + 1);
            return;
        }

        var pending = _advance;
        _advance = null;
        pending.TrySetResult(true);
    }

    /// <summary>全屏叙述与底部对话框只差锚点和一点字号，切换时统一在这里改。</summary>
    /// <summary>
    /// 摆立绘：高度固定 516、宽度按原图比例，底边压在 606，主人放左边左对齐、其他人放右边右对齐。
    /// 取不到图就当没有立绘（正文回到整宽），不抛错——旧档引用的立绘没了不该让整句对白演不下去。
    /// </summary>
    private void SetPortrait(string? path, bool left)
    {
        _portraitLeft = left;
        _hasPortrait = false;
        if (_portrait is null) return;

        Texture2D? image = null;
        if (path is not null && _texture is not null)
        {
            // 贴图缺失时 TextureCache 可能直接抛；立绘没有就没有，不该让整句对白演不下去。
            try { image = _texture(path); }
            catch { image = null; }
        }

        if (image is null || image.GetHeight() <= 0)
        {
            _portrait.Visible = false;
            return;
        }

        var width = PortraitHeight * image.GetWidth() / image.GetHeight();
        var screenWidth = GetViewportRect().Size.X;
        _portrait.Texture = image;
        _portrait.Size = new Vector2(width, PortraitHeight);
        _portrait.Position = new Vector2(
            left ? SideEdge : screenWidth - SideEdge - width,
            PortraitBottom - PortraitHeight);
        _portrait.Visible = true;
        _hasPortrait = true;
    }

    private void ApplyLayout(bool screen)
    {
        _panel.Visible = !screen;
        _hint.Visible = !screen;
        _column.Alignment = screen ? BoxContainer.AlignmentMode.Center : BoxContainer.AlignmentMode.Begin;
        _text.AddThemeFontSizeOverride("normal_font_size", screen ? ScreenTextSize : BoxTextSize);

        _text.FitContent = screen;
        _text.SizeFlagsVertical = screen ? SizeFlags.ShrinkCenter : SizeFlags.ExpandFill;

        if (screen)
        {
            SetAnchorsPreset(LayoutPreset.FullRect);
            OffsetLeft = 60f;
            OffsetRight = -60f;
            OffsetTop = 0f;
            OffsetBottom = 0f;
            return;
        }

        var gutter = _hasPortrait ? PortraitGutter : 0f;
        SetAnchorsPreset(LayoutPreset.BottomWide);
        OffsetLeft = SideEdge + (_portraitLeft ? gutter : 0f);
        OffsetRight = -(SideEdge + (_portraitLeft ? 0f : gutter));
        OffsetTop = -176f;
        OffsetBottom = -32f;
    }

    private void StartBlink()
    {
        StopBlink();
        _hint.Modulate = new Color(1f, 1f, 1f, 1f);
        _blink = CreateTween().SetLoops();
        _blink.TweenProperty(_hint, "modulate:a", 0.25f, 0.6f);
        _blink.TweenProperty(_hint, "modulate:a", 1f, 0.6f);
    }

    private void StopBlink()
    {
        _blink?.Kill();
        _blink = null;
    }

    private static StyleBoxFlat CreateStyle()
    {
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.06f, 0.07f, 0.10f, 0.88f),
            BorderColor = new Color(0.62f, 0.55f, 0.36f, 0.9f),
            ContentMarginLeft = 0f,
            ContentMarginTop = 0f,
            ContentMarginRight = 0f,
            ContentMarginBottom = 0f,
        };

        style.SetBorderWidthAll(2);
        style.SetCornerRadiusAll(6);
        return style;
    }
}
