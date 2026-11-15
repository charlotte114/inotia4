using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using Inotia4.Domain.Story;
using Inotia4.Domain.Ui;

namespace Inotia4.Ui;

public partial class ChoiceBox : Control
{
    private const float DescriptionMaxHeight = 53f;

    public bool AutoPick { get; set; }

    private PanelContainer _panel = null!;
    private VBoxContainer _options = null!;
    private ScrollContainer _optionsScroll = null!;
    private ScrollContainer _descriptionScroll = null!;
    private RichTextLabel _description = null!;
    private RichTextLabel _prompt = null!;
    private TaskCompletionSource<string>? _picked;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;

        _panel = new PanelContainer { Name = "Panel" };
        _panel.AddThemeStyleboxOverride("panel", CreateStyle());
        AddChild(_panel);

        var margin = new MarginContainer();
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 20);
        _panel.AddChild(margin);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 12);
        margin.AddChild(column);

        _prompt = new RichTextLabel
        {
            Name = "Prompt",
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(420f, 0f),
        };
        _prompt.AddThemeFontSizeOverride("normal_font_size", 17);
        column.AddChild(_prompt);

        _descriptionScroll = new ScrollContainer
        {
            Name = "DescriptionScroll",
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            CustomMinimumSize = new Vector2(420f, 0f),
        };
        _description = new RichTextLabel
        {
            Name = "Description",
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _description.AddThemeFontSizeOverride("normal_font_size", 15);
        _description.AddThemeColorOverride("default_color", new Color(0.78f, 0.78f, 0.76f));
        _descriptionScroll.AddChild(_description);
        column.AddChild(_descriptionScroll);

        _optionsScroll = new ScrollContainer
        {
            Name = "OptionScroll",
            FollowFocus = true,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            CustomMinimumSize = new Vector2(420f, 0f),
        };
        _options = new VBoxContainer { Name = "Options", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _options.AddThemeConstantOverride("separation", 8);
        _optionsScroll.AddChild(_options);
        column.AddChild(_optionsScroll);

        Visible = false;
    }

    public async Task<string> AskAsync(string prompt, IReadOnlyList<(string Label, string Value)> options,
        string description = "")
    {
        if (options.Count == 0) return string.Empty;

        if (AutoPick)
        {
            Visible = false;
            return options[0].Value;
        }

        Reset();
        _prompt.Text = StoryText.Bbcode(prompt);

        var hasDescription = !string.IsNullOrEmpty(description);
        _descriptionScroll.Visible = hasDescription;
        _description.Text = hasDescription ? StoryText.Bbcode(description) : string.Empty;

        foreach (var (label, value) in options)
        {
            var button = new Button
            {
                Text = StoryText.Plain(label),
                CustomMinimumSize = new Vector2(420f, 34f),
            };
            button.AddThemeFontSizeOverride("font_size", 16);

            var picked = value;
            button.Pressed += () =>
            {
                PanelGfx.Select();
                Settle(picked);
            };
            _options.AddChild(button);
        }

        Callable.From(FitViewport).CallDeferred();

        Visible = true;
        _picked = new TaskCompletionSource<string>();

        if (_options.GetChildCount() > 0 && _options.GetChild(0) is Button first) first.GrabFocus();

        return await _picked.Task;
    }

    /// <summary>
    /// 量完内容再把两个视口收紧，并把面板夹进画面的上下边界：
    /// 描述最多 53 高、选项列表按「面板最高 600（顶边 20、底边 620）」扣掉其余部分，
    /// 其余滚动。
    /// 必须在按钮与文字都进树之后再量，否则拿到的是 0。
    /// </summary>
    private void FitViewport()
    {
        if (_descriptionScroll.Visible)
        {
            var textHeight = (float)_description.GetContentHeight();
            if (textHeight <= 0) textHeight = EstimateHeight(_description.Text);
            _descriptionScroll.CustomMinimumSize = new Vector2(420f, Mathf.Min(textHeight, DescriptionMaxHeight));
        }

        var listHeight = _options.GetCombinedMinimumSize().Y;
        _optionsScroll.CustomMinimumSize = new Vector2(420f, listHeight);

        var overflow = _panel.GetCombinedMinimumSize().Y - ChoiceGeometry.MaxPanelHeight;
        if (overflow > 0f)
            _optionsScroll.CustomMinimumSize = new Vector2(420f,
                ChoiceGeometry.ListViewport(listHeight - overflow, listHeight, OptionHeight()));

        Recenter();
    }

    private float OptionHeight() =>
        _options.GetChildCount() > 0 && _options.GetChild(0) is Control first
            ? first.GetCombinedMinimumSize().Y
            : 34f;

    private static float EstimateHeight(string text)
    {
        if (text.Length == 0) return 0f;
        var lines = 0;
        foreach (var paragraph in text.Split('\n'))
            lines += System.Math.Max(1, (paragraph.Length + 27) / 28);
        return lines * 22f;
    }

    private void Settle(string value)
    {
        Visible = false;
        var pending = _picked;
        _picked = null;
        pending?.TrySetResult(value);
    }

    private void Reset()
    {
        // 同帧打开下一组选项时，旧按钮必须先离树，焦点才能交给新列表第一项。
        PanelGfx.Clear(_options);
    }

    private void Recenter()
    {
        var screen = GetViewportRect().Size;
        var size = _panel.GetCombinedMinimumSize();
        _panel.Size = size;
        _panel.Position = new Vector2(
            (screen.X - size.X) / 2f,
            ChoiceGeometry.CenteredTop(size.Y, screen.Y));
    }

    private static StyleBoxFlat CreateStyle()
    {
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.07f, 0.08f, 0.11f, 0.95f),
            BorderColor = new Color(0.62f, 0.55f, 0.36f, 0.95f),
        };

        style.SetBorderWidthAll(2);
        style.SetCornerRadiusAll(6);
        return style;
    }
}
