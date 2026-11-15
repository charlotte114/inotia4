using System;
using System.Threading.Tasks;
using Godot;

namespace Inotia4.Ui;

public partial class DefeatPanel : Control
{
    public bool AutoConfirm { get; set; }

    public Func<int>? PrayerCount { get; set; }

    public Func<bool>? Revive { get; set; }

    public Func<Task>? Restore { get; set; }

    public bool IsOpen { get; private set; }

    public string? Error => _error;

    private const string PaletteGold = "#e4c58b";
    private const string PaletteText = "#f5eedf";
    private const string PaletteError = "#eb9587";

    private ColorRect _backdrop = null!;
    private PanelContainer _panel = null!;
    private Label _title = null!;
    private Label _message = null!;
    private VBoxContainer _actions = null!;
    private HBoxContainer _confirmation = null!;
    private Button _prayer = null!;
    private Button _checkpoint = null!;
    private Button _cancel = null!;
    private Button _confirm = null!;

    private bool _confirmingPrayer;

    private bool _confirming;
    private bool _busy;
    private string? _error;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;

        _backdrop = new ColorRect
        {
            Name = "Backdrop",
            Color = new Color(0f, 0f, 0f, 0.65f),
            MouseFilter = MouseFilterEnum.Stop,
        };
        _backdrop.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_backdrop);

        _panel = new PanelContainer { Name = "Panel", CustomMinimumSize = new Vector2(676f, 356f) };
        _panel.AddThemeStyleboxOverride("panel", CreateStyle());
        AddChild(_panel);

        var margin = new MarginContainer();
        foreach (var side in new[] { "margin_left", "margin_right" })
            margin.AddThemeConstantOverride(side, 34);
        margin.AddThemeConstantOverride("margin_top", 20);
        margin.AddThemeConstantOverride("margin_bottom", 26);
        _panel.AddChild(margin);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 16);
        margin.AddChild(column);

        _title = new Label
        {
            Name = "Title",
            Text = "队伍全员阵亡",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _title.AddThemeFontSizeOverride("font_size", 32);
        _title.AddThemeColorOverride("font_color", Color.FromHtml(PaletteGold));
        column.AddChild(_title);

        _message = new Label
        {
            Name = "Message",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            HorizontalAlignment = HorizontalAlignment.Center,
            CustomMinimumSize = new Vector2(0f, 64f),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _message.AddThemeFontSizeOverride("font_size", 22);
        column.AddChild(_message);

        _actions = new VBoxContainer { Name = "Actions" };
        _actions.AddThemeConstantOverride("separation", 14);
        column.AddChild(_actions);

        _prayer = MakeButton("使用复活圣文");
        _prayer.Pressed += () => Select(prayer: true);
        _actions.AddChild(_prayer);

        _checkpoint = MakeButton("返回检查点");
        _checkpoint.Pressed += () => Select(prayer: false);
        _actions.AddChild(_checkpoint);

        _confirmation = new HBoxContainer { Name = "Confirmation", Visible = false };
        _confirmation.AddThemeConstantOverride("separation", 50);
        _confirmation.Alignment = BoxContainer.AlignmentMode.Center;
        column.AddChild(_confirmation);

        _cancel = MakeButton("取消");
        _cancel.CustomMinimumSize = new Vector2(220f, 52f);
        _cancel.Pressed += CancelConfirmation;
        _confirmation.AddChild(_cancel);

        _confirm = MakeButton("确认");
        _confirm.CustomMinimumSize = new Vector2(220f, 52f);
        _confirm.Pressed += () => _ = ConfirmAsync();
        _confirmation.AddChild(_confirm);

        Visible = false;
    }

    public override void _Notification(int what)
    {
        if (what == NotificationResized) Recenter();
    }

    public void Open()
    {
        if (IsOpen) return;

        IsOpen = true;
        _confirming = false;
        _busy = false;
        _error = null;
        Visible = true;
        Recenter();
        Refresh();

        if (AutoConfirm)
        {
            Select(prayer: false);
            _ = ConfirmAsync();
        }
    }

    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        Visible = false;
        _confirming = false;
        _busy = false;
    }

    public void Select(bool prayer)
    {
        if (!IsOpen || _busy) return;
        if (prayer && (PrayerCount?.Invoke() ?? 0) < 1) return;

        _confirming = true;
        _confirmingPrayer = prayer;
        _error = null;
        Refresh();
    }

    public void CancelConfirmation()
    {
        if (!IsOpen || _busy) return;
        _confirming = false;
        _error = null;
        Refresh();
    }

    public async Task ConfirmAsync()
    {
        if (!IsOpen || !_confirming || _busy) return;

        var prayer = _confirmingPrayer;
        _busy = true;
        _error = null;
        Refresh();

        try
        {
            if (prayer)
            {
                if ((PrayerCount?.Invoke() ?? 0) < 1 || !(Revive?.Invoke() ?? false))
                    throw new InvalidOperationException("当前无法使用复活圣文");
            }
            else if (Restore is not null)
            {
                await Restore();
            }
        }
        catch (Exception error)
        {
            if (!IsOpen) return;
            _busy = false;
            _confirming = false;
            _error = error.Message;
            Refresh();
            return;
        }

        _busy = false;
        Close();
    }

    public void ReportError(string message)
    {
        _error = message;
        _busy = false;
        _confirming = false;
        Refresh();
    }

    public async Task ConfirmForCheck(bool prayer)
    {
        if (!IsOpen) return;
        Select(prayer);
        await ConfirmAsync();
    }

    private void Refresh()
    {
        var count = PrayerCount?.Invoke() ?? 0;
        _actions.Visible = !_confirming;
        _confirmation.Visible = _confirming;

        if (_confirming)
        {
            _message.Text = _confirmingPrayer
                ? "消耗 1 张复活圣文，在原地恢复全队生命与魔法？"
                : "返回最近的检查点？此后的物品、经验、金钱和任务进度将回退。";
            _message.AddThemeColorOverride("font_color", Color.FromHtml(PaletteText));
            _cancel.Disabled = _busy;
            _confirm.Disabled = _busy;
            _confirm.Text = _busy ? "正在恢复…" : "确认";
            return;
        }

        _message.Text = _error ?? "选择复活方式";
        _message.AddThemeColorOverride("font_color", Color.FromHtml(_error is null ? PaletteText : PaletteError));
        _prayer.Text = $"使用复活圣文（{count}）";
        _prayer.Disabled = count < 1 || _busy;
        _checkpoint.Disabled = _busy;
    }

    private void Recenter()
    {
        if (_panel is null) return;
        var size = _panel.GetCombinedMinimumSize();
        _panel.Size = size;
        _panel.Position = (GetViewportRect().Size - size) / 2f;
    }

    private static Button MakeButton(string text)
    {
        var button = new Button
        {
            Text = text,
            CustomMinimumSize = new Vector2(0f, 52f),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        button.AddThemeFontSizeOverride("font_size", 20);
        button.Pressed += PanelGfx.Select;
        return button;
    }

    private static StyleBoxFlat CreateStyle()
    {
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.06f, 0.09f, 0.11f, 0.97f),
            BorderColor = new Color(0.62f, 0.55f, 0.36f, 0.95f),
        };

        style.SetBorderWidthAll(2);
        style.SetCornerRadiusAll(6);
        return style;
    }
}
