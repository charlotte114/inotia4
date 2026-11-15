using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using Inotia4.Domain.Story;

namespace Inotia4.Ui;

public partial class QuestPopup : Control
{
    private const float PanelWidth = 540f;

    private const float MaxPanelHeight = 437f;

    private const float MinPanelHeight = 180f;

    private const float Padding = 22f;

    private const float Gap = 10f;

    private const float ButtonHeight = 34f;

    private const float MeasureWidth = 480f;

    private const int TitleSize = 19;

    private const int BodySize = 16;

    public bool AutoConfirm { get; set; }

    public Func<string, string>? ItemName { get; set; }

    public Action<string>? UiSound { get; set; }

    private PanelContainer _panel = null!;
    private Control _titleBox = null!;
    private RichTextLabel _title = null!;
    private RichTextLabel _body = null!;
    private ScrollContainer _bodyScroll = null!;
    private string _titlePlain = string.Empty;
    private string _bodyPlain = string.Empty;
    private TaskCompletionSource<bool>? _confirm;

    public string BodyText => _body.Text;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;

        _panel = new PanelContainer { Name = "Panel" };
        _panel.AddThemeStyleboxOverride("panel", CreateStyle());
        AddChild(_panel);

        var margin = new MarginContainer();
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, (int)Padding);
        _panel.AddChild(margin);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", (int)Gap);
        margin.AddChild(column);

        // 标题里有 $A/$B 颜色码，必须是会解析 BBCode 的控件；普通 Label 会把
        // 「[color=#bf0000]」原样显示出来。
        // FitContent 的 RichTextLabel 不能直接进 VBox：文本刚设置、控件还没拿到
        // 宽度时，它按「每字一行」算最小高度，会把整个面板撑到 500+，之后
        // 宽度正常了 Size 也不会自己缩回来。放进一个固定高度的盒子里隔离掉，
        // 盒子的高度由 Layout() 按测量值给。
        _titleBox = new Control
        {
            Name = "TitleBox",
            ClipContents = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        column.AddChild(_titleBox);

        _title = new RichTextLabel
        {
            Name = "Title",
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
        };
        _title.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _title.AddThemeFontSizeOverride("normal_font_size", TitleSize);
        _title.AddThemeColorOverride("default_color", new Color(1f, 0.84f, 0.45f));
        _titleBox.AddChild(_title);

        _bodyScroll = new ScrollContainer
        {
            Name = "BodyScroll",
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            ClipContents = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        _bodyScroll.AddThemeStyleboxOverride("panel", PanelGfx.EmptyStyle());
        HideScrollbar(_bodyScroll);
        column.AddChild(_bodyScroll);

        _body = new RichTextLabel
        {
            Name = "Body",
            BbcodeEnabled = true,
            ScrollActive = false,
            FitContent = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _body.AddThemeFontSizeOverride("normal_font_size", BodySize);
        _body.AddThemeColorOverride("default_color", new Color(0.95f, 0.94f, 0.9f));
        _bodyScroll.AddChild(_body);

        var confirm = new Button
        {
            Name = "Confirm",
            Text = "确定",
            CustomMinimumSize = new Vector2(120f, ButtonHeight),
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
        };
        confirm.AddThemeFontSizeOverride("font_size", 16);
        confirm.Pressed += Confirm;
        column.AddChild(confirm);

        Visible = false;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (_confirm is null || !Visible) return;

        // 只认键盘：鼠标走按钮自己那条路，否则「点一下翻台词」的同一个
        // 事件会连带把刚弹出来的窗一起收掉。
        if (@event is not InputEventKey { Pressed: true, Keycode: Key.Space or Key.Enter or Key.Z }) return;

        GetViewport().SetInputAsHandled();
        Confirm();
    }

    private void Confirm()
    {
        if (_confirm is null) return;

        PanelGfx.Select();

        Visible = false;
        var pending = _confirm;
        _confirm = null;
        pending.TrySetResult(true);
    }

    public Task ShowAsync(QuestDefinition quest, string kind) => ShowAsync(quest, kind, receipt: null);

    public async Task ShowAsync(QuestDefinition quest, string kind, RewardReceipt? receipt)
    {
        if (AutoConfirm) return;

        UiSound?.Invoke(kind == "complete" ? "questComplete" : "questStart");

        var heading = kind == "complete" ? $"$A<{quest.Title}>$B 完成" : $"$A<{quest.Title}>$B 获得";
        var body = kind == "start" ? quest.Description : string.Empty;

        if (receipt is not null)
        {
            var lines = new List<string>();
            if (receipt.Experience > 0) lines.Add($"经验值: {receipt.Experience}");
            if (receipt.Money > 0) lines.Add($"金币: {receipt.Money}");
            foreach (var item in receipt.Items)
            {
                var name = ItemName?.Invoke(item.Item) ?? item.Item;
                lines.Add(item.Quantity > 1 ? $"{name} ×{item.Quantity}" : name);
            }

            if (lines.Count > 0) body = string.Join("\n", lines);
        }

        _titlePlain = StoryText.Plain(heading);
        _bodyPlain = StoryText.Plain(body);
        _title.Text = $"[center]{StoryText.Bbcode(heading)}[/center]";
        _body.Text = StoryText.Bbcode(body);

        Visible = true;
        Layout();
        Callable.From(Layout).CallDeferred();

        _confirm = new TaskCompletionSource<bool>();
        await _confirm.Task;
    }

    private void Layout()
    {
        var titleHeight = Measure(_titlePlain, MeasureWidth, TitleSize);
        var bodyHeight = Measure(_bodyPlain, MeasureWidth, BodySize);
        var chrome = Padding * 2 + Gap * 2 + ButtonHeight;
        var height = Math.Clamp(chrome + titleHeight + bodyHeight, MinPanelHeight, MaxPanelHeight);
        var bodyBox = MathF.Max(1f, height - chrome - titleHeight);

        _titleBox.CustomMinimumSize = new Vector2(0f, titleHeight);
        _bodyScroll.CustomMinimumSize = new Vector2(0f, bodyBox);
        _panel.CustomMinimumSize = new Vector2(PanelWidth, height);
        _panel.Size = new Vector2(PanelWidth, height);
        _panel.Position = ((GetViewportRect().Size - _panel.Size) / 2f).Floor();
    }

    private float Measure(string text, float width, int fontSize)
    {
        var probe = new Label { Text = string.Empty, MouseFilter = MouseFilterEnum.Ignore };
        probe.AddThemeFontSizeOverride("font_size", fontSize);
        AddChild(probe);
        var font = probe.GetThemeFont("font");
        var size = font.GetMultilineStringSize(text, HorizontalAlignment.Left, width, fontSize);
        probe.QueueFree();
        return MathF.Ceiling(size.Y);
    }

    private static void HideScrollbar(ScrollContainer scroll)
    {
        var bar = scroll.GetVScrollBar();
        foreach (var state in new[] { "scroll", "grabber", "grabber_highlight", "grabber_pressed" })
            bar.AddThemeStyleboxOverride(state, PanelGfx.EmptyStyle());
    }

    private static StyleBoxFlat CreateStyle()
    {
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.07f, 0.08f, 0.11f, 0.96f),
            BorderColor = new Color(0.62f, 0.55f, 0.36f, 0.95f),
        };

        style.SetBorderWidthAll(2);
        style.SetCornerRadiusAll(6);
        return style;
    }
}
