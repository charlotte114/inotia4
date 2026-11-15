using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Inotia4.Domain.Content;
using Inotia4.Domain.Story;
using Inotia4.Domain.Ui;

namespace Inotia4.Ui;

public sealed record QuestPanelContext(
    CampaignContent Campaign,
    QuestBook Book,
    MenuPanelsCatalog Panels,
    IReadOnlyDictionary<string, string> StoryAssets,
    Func<string, string> ActorName,
    Func<string, Texture2D?> Texture,
    Action? Changed = null,
    Action<string>? ShowPage = null)
{
    public string? Asset(string key) => Panels.Asset(key);
}

public partial class QuestPanel : Control
{
    private const float DesignWidth = 1024f;

    private const float DesignHeight = 640f;

    private static readonly Vector2 ContentOrigin = new(32f, 172f);

    private static readonly Vector2 ContentSize = new(960f, 431f);

    private static readonly Color TitleColor = new("#e2cb9e");

    private QuestPanelContext? _context;

    private Control _canvas = null!;

    private Control _content = null!;

    private Control _tabs = null!;

    private string? _selectedGroup;

    private bool _detailVisible;

    private int _listScroll;

    private int _detailScroll;

    private ScrollContainer? _detail;

    private (int Version, IReadOnlyList<QuestJournalView.Group> Groups)? _groups;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        Visible = false;

        _canvas = new Control { Name = "Canvas", MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_canvas);
        DesignStage.Center(_canvas);

        _tabs = new Control
        {
            Name = "Tabs",
            Size = new Vector2(DesignWidth, DesignHeight),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _canvas.AddChild(_tabs);

        _content = new Control
        {
            Name = "Content",
            Position = ContentOrigin,
            Size = ContentSize,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _canvas.AddChild(_content);

        Resized += LayoutCanvas;
        LayoutCanvas();
    }

    private void LayoutCanvas()
    {
        var viewport = GetViewportRect().Size;
        if (viewport.X <= 0f || viewport.Y <= 0f) return;

        var scale = Math.Min(viewport.X / DesignWidth, viewport.Y / DesignHeight);
        _canvas.Scale = new Vector2(scale, scale);
        _canvas.Position = new Vector2(
            (viewport.X - DesignWidth * scale) / 2f,
            (viewport.Y - DesignHeight * scale) / 2f);
    }

    public bool IsOpen => Visible;

    public const string Page = "quests";

    public void Open(QuestPanelContext context)
    {
        _context = context;
        _selectedGroup = null;
        _detailVisible = false;
        _listScroll = 0;
        _detailScroll = 0;
        _groups = null;
        Visible = true;
        LayoutCanvas();
        Render();
    }

    public void Close()
    {
        Visible = false;
        _selectedGroup = null;
        _detailVisible = false;
        _groups = null;
    }

    public bool Escape() => false;

    public void Render()
    {
        if (_context is not { } context) return;

        PanelGfx.Clear(_content);
        PanelGfx.Clear(_tabs);
        MenuTabBar.Draw(_tabs, context.StoryAssets, context.Texture, Page, context.ShowPage);

        var groups = _groups?.Version == context.Book.Version
            ? _groups!.Value.Groups
            : QuestJournalView.Groups(context.Campaign, context.Book.ProgressEntries());
        _groups = (context.Book.Version, groups);

        if (groups.All(group => group.Definition.Id != _selectedGroup))
        {
            _selectedGroup = groups.FirstOrDefault()?.Definition.Id;
            _detailVisible = groups.Count > 0;
            _detailScroll = 0;
        }

        PanelGfx.Border(context.Texture, context.Asset,
            QuestLayouts.ListFrameX, QuestLayouts.ListFrameY,
            QuestLayouts.ListFrameWidth, QuestLayouts.ListFrameHeight, _content);
        PanelGfx.Border(context.Texture, context.Asset,
            QuestLayouts.DetailFrameX, QuestLayouts.DetailFrameY,
            QuestLayouts.DetailFrameWidth, QuestLayouts.DetailFrameHeight, _content);

        DrawList(context, groups);
        DrawDetail(context, groups);
    }

    private void DrawList(QuestPanelContext context, IReadOnlyList<QuestJournalView.Group> groups)
    {
        var scroll = new ScrollContainer
        {
            Name = "QuestList",
            Position = new Vector2(QuestLayouts.ListX, QuestLayouts.ListY),
            Size = new Vector2(QuestLayouts.ListWidth, QuestLayouts.ListHeight),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            ClipContents = true,
        };
        scroll.AddThemeStyleboxOverride("panel", PanelGfx.EmptyStyle());
        HideScrollBar(scroll);
        _content.AddChild(scroll);

        var height = Math.Max(0f, groups.Count * QuestLayouts.RowStride - 8f);
        var rows = new Control
        {
            CustomMinimumSize = new Vector2(QuestLayouts.ListWidth, height),
            Size = new Vector2(QuestLayouts.ListWidth, height),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        scroll.AddChild(rows);

        for (var index = 0; index < groups.Count; index += 1)
        {
            var group = groups[index];
            var main = group.Definition.Kind == "main";
            var selected = group.Definition.Id == _selectedGroup;
            var top = index * QuestLayouts.RowStride;

            var target = group.Definition.Id;
            var row = PanelGfx.Hit(0f, top, QuestLayouts.ListWidth, QuestLayouts.RowHeight,
                () => Select(target), rows);
            PanelGfx.Image(context.Texture, context.Asset(selected ? "quest-row-selected" : "quest-row"),
                0f, 0f, QuestLayouts.ListWidth, QuestLayouts.RowHeight, row);
            AddPressedLayer(row, context, 0f, 0f, QuestLayouts.ListWidth, QuestLayouts.RowHeight);

            if (main)
                PanelGfx.Image(context.Texture, context.Asset("quest-main"),
                    QuestLayouts.MainIconX, QuestLayouts.MainIconY,
                    QuestLayouts.MainIconWidth, QuestLayouts.MainIconHeight, row);

            var titleX = main ? 70f : 10f;
            var titleWidth = group.Ready
                ? (main ? 381f : 441f)
                : (main ? 434f : 494f);
            PanelGfx.Text(group.Definition.Title, titleX, QuestLayouts.TitleTop,
                QuestLayouts.TitleSize, TitleColor, row)
                .Size = new Vector2(titleWidth, 0f);

            if (group.Ready)
                PanelGfx.Image(context.Texture, context.Asset("quest-ready"),
                    QuestLayouts.ReadyIconX, QuestLayouts.ReadyIconY,
                    QuestLayouts.ReadyIconWidth, QuestLayouts.ReadyIconHeight, row);
        }

        scroll.SetDeferred(ScrollContainer.PropertyName.ScrollVertical, _listScroll);
        scroll.GetVScrollBar().ValueChanged += value =>
        {
            if ((int)value == _listScroll) return;
            _listScroll = (int)value;
            _detailVisible = false;
            if (_detail is { }) _detail.Visible = false;
        };
    }

    private void DrawDetail(QuestPanelContext context, IReadOnlyList<QuestJournalView.Group> groups)
    {
        var selected = groups.FirstOrDefault(group => group.Definition.Id == _selectedGroup);
        if (selected is null || !_detailVisible) return;

        _detail = new ScrollContainer
        {
            Name = "QuestDetail",
            Position = new Vector2(QuestLayouts.DetailX, QuestLayouts.DetailY),
            Size = new Vector2(QuestLayouts.DetailWidth, QuestLayouts.DetailHeight),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            ClipContents = true,
        };
        _detail.AddThemeStyleboxOverride("panel", PanelGfx.EmptyStyle());
        HideScrollBar(_detail);
        _content.AddChild(_detail);

        var text = QuestJournalView.Text(selected, context.ActorName);
        var probe = new Label { Text = string.Empty, MouseFilter = MouseFilterEnum.Ignore };
        probe.AddThemeFontSizeOverride("font_size", QuestLayouts.DetailTextSize);
        _content.AddChild(probe);
        var font = probe.GetThemeFont("font");
        var lineHeight = font.GetHeight(QuestLayouts.DetailTextSize) + 5f;
        probe.QueueFree();

        float Measure(string value) => font.GetStringSize(value, HorizontalAlignment.Left, -1,
            QuestLayouts.DetailTextSize).X;
        var lines = QuestTextLayout.Layout(QuestTextLayout.Parts(text), QuestLayouts.DetailWidth, Measure);

        var content = new Control
        {
            CustomMinimumSize = new Vector2(QuestLayouts.DetailWidth, lines.Count * lineHeight - 5f),
            Size = new Vector2(QuestLayouts.DetailWidth, lines.Count * lineHeight - 5f),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _detail.AddChild(content);

        for (var index = 0; index < lines.Count; index += 1)
        {
            var line = lines[index];
            foreach (var run in line.Runs)
            {
                if (run.Text.Length == 0) continue;
                PanelGfx.Text(run.Text, run.X, index * lineHeight, QuestLayouts.DetailTextSize,
                    new Color(QuestTextLayout.ColorOf(run.Tone)), content);
            }
        }

        _detail.SetDeferred(ScrollContainer.PropertyName.ScrollVertical, _detailScroll);
        _detail.GetVScrollBar().ValueChanged += value => _detailScroll = (int)value;
    }

    private void Select(string id)
    {
        if (_context is not { } context) return;
        _selectedGroup = id;
        _detailVisible = true;
        _detailScroll = 0;
        _detail = null;
        Render();
        context.Changed?.Invoke();
    }

    private static void AddPressedLayer(Button button, QuestPanelContext context,
        float x, float y, float width, float height)
    {
        var pressed = new Control
        {
            MouseFilter = MouseFilterEnum.Ignore,
            Visible = false,
        };
        button.AddChild(pressed);
        PanelGfx.Image(context.Texture, context.Asset("quest-row-pressed"), x, y, width, height, pressed);
        button.ButtonDown += () => pressed.Visible = true;
        button.ButtonUp += () => pressed.Visible = false;
        button.MouseExited += () => pressed.Visible = false;
    }

    private static void HideScrollBar(ScrollContainer scroll)
    {
        var bar = scroll.GetVScrollBar();
        if (bar is not null) bar.Modulate = new Color(1f, 1f, 1f, 0f);
    }
}
