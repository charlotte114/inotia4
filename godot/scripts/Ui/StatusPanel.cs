using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Inotia4.Domain.Character;
using Inotia4.Domain.Combat;
using Inotia4.Domain.Content;
using Inotia4.Domain.Ui;

namespace Inotia4.Ui;

public sealed record StatusPanelContext(
    PartyMemberView Member,
    CharacterStatsCatalog Stats,
    ItemCatalog Items,
    StoryClassCatalog Classes,
    MenuPanelsCatalog Panels,
    IReadOnlyDictionary<string, string> StoryAssets,
    Func<PrimaryAttributes, CharacterPanel> PanelFor,
    Func<string, Texture2D?> Texture,
    IReadOnlyList<string> MemberIds,
    Func<string, string?> MemberIconOf,
    Action<string>? SelectMember = null,
    Action? Changed = null,
    Action<string>? ShowPage = null,
    Func<string, IReadOnlyList<AttributeModifier>?>? CombatModifiers = null)
{
    public string? Asset(string key) => Panels.Asset(key);

    public bool IsPlayer => Member.IsPlayer;

    public IAllocationOwner? HeroAttributes => Member.Attributes;

    public int NextLevelExperience => Stats.ExperienceToNext(Member.Level) ?? 0;
}

public partial class StatusPanel : Control
{
    private const float DesignWidth = 1024f;

    private const float DesignHeight = 640f;

    private static readonly Vector2 ContentOrigin = new(32f, 172f);

    private static readonly Vector2 ContentSize = new(960f, 431f);

    private static readonly Color Gold = new("#f3deb0");

    private static readonly Color AreaText = new("#e2cb9e");

    private static readonly Color Added = new("#e1823b");

    private static readonly Color ErrorText = new("#e6a191");

    private StatusPanelContext? _context;

    private Control _canvas = null!;
    private Control _content = null!;

    private Control _tabs = null!;

    private bool _adjusting;

    private AttributeAllocation? _allocation;

    private string _allocationError = string.Empty;

    private HoldState? _hold;

    private sealed class HoldState
    {
        public string Stat = string.Empty;
        public int Delta;
        public double Remaining;
        public bool Started;
    }

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

    public const string Page = "status";

    public void Open(StatusPanelContext context)
    {
        _context = context;
        DiscardAllocation();
        Visible = true;
        LayoutCanvas();
        Render();
    }

    public void Close()
    {
        Visible = false;
        DiscardAllocation();
    }

    public void Render()
    {
        if (_context is not { } context) return;

        PanelGfx.Clear(_content);
        PanelGfx.Clear(_tabs);

        MenuTabBar.Draw(_tabs, context.StoryAssets, context.Texture, Page, context.ShowPage);

        DrawPortrait(context);
        DrawHeader(context);
        DrawGauges(context);
        DrawAttributes(context);
        DrawCombat(context);
        DrawPoints(context);

        if (_allocationError.Length > 0)
            PanelGfx.Text(_allocationError, 85f, StatusLayouts.PointsErrorY, 18, ErrorText, _content);
    }

    private void DrawPortrait(StatusPanelContext context)
    {
        var frame = StatusLayouts.LeftFrame;
        PanelGfx.Border(context.Texture, context.Asset, frame.X, frame.Y, frame.Width, frame.Height, _content);

        foreach (var (key, x, y) in StatusLayouts.Corners)
            PanelGfx.Image(context.Texture, context.Asset(key), x, y,
                StatusLayouts.PortraitSize, StatusLayouts.PortraitSize, _content);

        if (context.Member.PortraitImage is { } portrait &&
            context.Texture(portrait) is { } portraitTexture)
        {
            var box = StatusLayouts.Portrait;
            var scale = Math.Min(box.Width / portraitTexture.GetWidth(),
                box.Height / portraitTexture.GetHeight());
            var width = portraitTexture.GetWidth() * scale;
            var height = portraitTexture.GetHeight() * scale;
            PanelGfx.Image(context.Texture, portrait,
                box.X + (box.Width - width) / 2f, box.Y + (box.Height - height) / 2f,
                width, height, _content);
        }

        MenuMemberColumn.Draw(_content, context.Texture, context.Asset, context.MemberIds,
            context.MemberIconOf, context.Member.Id,
            id => RequestMember(context, id));
    }

    private void RequestMember(StatusPanelContext context, string id)
    {
        if (context.SelectMember is null) return;
        if (_adjusting && !CommitAllocation()) return;
        context.SelectMember(id);
    }

    private void DrawHeader(StatusPanelContext context)
    {
        PanelGfx.Right($"等级 {context.Member.Level}", StatusLayouts.TextRight, StatusLayouts.LevelY,
            StatusLayouts.TextSize, AreaText, _content);
        PanelGfx.Right(context.Member.ClassName, StatusLayouts.TextRight, StatusLayouts.ClassY,
            StatusLayouts.TextSize, AreaText, _content);
        PanelGfx.Right(context.Member.ActorName, StatusLayouts.TextRight, StatusLayouts.NameY,
            StatusLayouts.NameSize, AreaText, _content);
    }

    /// <summary>生命／魔法／经验三条槽。填充是「按比例裁切」而不是拉伸，所以套一层裁剪容器。</summary>
    private void DrawGauges(StatusPanelContext context)
    {
        var labels = context.Panels.Character.Labels;
        var player = context.Member.Player;

        var rows = new (string Key, string Label, int Value, int Max)[]
        {
            ("health", labels.Health, player.Health, player.MaxHealth),
            ("mana", labels.Mana, player.Mana, player.MaxMana),
            ("experience", labels.Experience, player.Experience, context.NextLevelExperience),
        };

        for (var index = 0; index < rows.Length; index += 1)
        {
            var (key, label, value, max) = rows[index];
            var top = StatusLayouts.GaugeTop + index * StatusLayouts.GaugeStride;
            var ratio = key == "experience" && max == 0
                ? 1f
                : max > 0 ? Math.Clamp(value / (float)max, 0f, 1f) : 0f;

            PanelGfx.Image(context.Texture, context.Asset("character-gauge"),
                StatusLayouts.GaugePlate.X, top + StatusLayouts.GaugePlate.Y,
                StatusLayouts.GaugePlate.Width, StatusLayouts.GaugePlate.Height, _content);

            PanelGfx.Text(label, StatusLayouts.GaugeLabelX, top + StatusLayouts.GaugeLabelY,
                StatusLayouts.GaugeLabelSize, Gold, _content);

            PanelGfx.Centered(max == 0 ? "已满级" : $"{value} / {max}",
                StatusLayouts.GaugeValueCenterX - StatusLayouts.GaugeValueWidth / 2f, top,
                StatusLayouts.GaugeValueWidth, StatusLayouts.GaugeValueSize, Gold, _content);

            PanelGfx.Right($"{(int)Math.Floor(ratio * 100)}%", StatusLayouts.GaugePercentRight,
                top + StatusLayouts.GaugeLabelY, StatusLayouts.GaugeValueSize, Gold, _content, 60f);

            var width = StatusLayouts.GaugeFill.Width * ratio;
            if (width <= 0f) continue;

            var clip = new Control
            {
                Position = new Vector2(StatusLayouts.GaugeFill.X, top + StatusLayouts.GaugeFill.Y),
                Size = new Vector2(width, StatusLayouts.GaugeFill.Height),
                ClipContents = true,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            _content.AddChild(clip);
            PanelGfx.Image(context.Texture, context.Asset($"character-{key}-fill"),
                0f, 0f, StatusLayouts.GaugeFill.Width, StatusLayouts.GaugeFill.Height, clip);

            PanelGfx.Image(context.Texture, context.Asset($"character-{key}-start"),
                StatusLayouts.GaugeFill.X, top + StatusLayouts.GaugeFill.Y,
                StatusLayouts.GaugeCapWidth, StatusLayouts.GaugeFill.Height, _content);
            PanelGfx.Image(context.Texture, context.Asset($"character-{key}-end"),
                StatusLayouts.GaugeFill.X + Math.Max(0f, width - StatusLayouts.GaugeCapWidth),
                top + StatusLayouts.GaugeFill.Y,
                StatusLayouts.GaugeCapWidth, StatusLayouts.GaugeFill.Height, _content);
        }
    }

    private void DrawAttributes(StatusPanelContext context)
    {
        var adjusting = _adjusting && _allocation is not null;
        var width = adjusting ? StatusLayouts.AttributeWideWidth : StatusLayouts.AttributeNarrowWidth;

        PanelGfx.SolidBorder(context.Texture, context.Asset, StatusLayouts.AttributeX, 0f,
            width, StatusLayouts.AttributeHeaderHeight, _content);
        PanelGfx.Border(context.Texture, context.Asset, StatusLayouts.AttributeX,
            StatusLayouts.AttributeBodyY, width, StatusLayouts.AttributeBodyHeight, _content);

        var rows = context.Classes.Class(context.Member.ClassId)?.Attributes ?? [];
        var values = adjusting ? _allocation!.Attributes : context.Member.Panel.Attributes;
        var allocated = context.HeroAttributes?.Allocated;
        var stride = adjusting ? StatusLayouts.AttributeAdjustStride : StatusLayouts.AttributeStride;
        var band = adjusting ? StatusLayouts.AttributeAdjustBand : StatusLayouts.AttributeBand;

        for (var index = 0; index < PrimaryStats.All.Length; index += 1)
        {
            var stat = PrimaryStats.All[index];
            var top = index * stride;

            PanelGfx.RoundBand(context.Texture, context.Asset, band.X, top + band.Y, band.Width, band.Height,
                StatusLayouts.AttributeBandStrength, _content);

            var nameX = adjusting ? StatusLayouts.AttributeAdjustNameX : StatusLayouts.AttributeNameX;
            var name = index < rows.Length ? rows[index].Name : stat;
            PanelGfx.Text(name, nameX, top + StatusLayouts.AttributeRowTextOffset,
                StatusLayouts.AttributeTextSize, Gold, _content);

            var valueRight = adjusting ? StatusLayouts.AttributeAdjustValueRight : StatusLayouts.AttributeValueRight;
            PanelGfx.Right(PrimaryAttributeMath.Get(values, stat).ToString(), valueRight,
                top + StatusLayouts.AttributeRowValueOffset, StatusLayouts.AttributeTextSize, Gold, _content, 160f);

            if (!adjusting) continue;

            var added = (allocated is null ? 0 : PrimaryAttributeMath.Get(allocated, stat))
                        + _allocation!.PendingOf(stat);
            if (added >= 1)
                PanelGfx.Text($"+{added}", StatusLayouts.AttributeAddedX,
                    top + StatusLayouts.AttributeRowValueOffset, StatusLayouts.AttributeTextSize, Added, _content);

            DrawStepper(context, stat, -1, index);
            DrawStepper(context, stat, 1, index);
        }

        var buttonX = adjusting ? StatusLayouts.AdjustButtonWideX : StatusLayouts.AdjustButtonNarrowX;
        var image = adjusting ? "confirm" : "character-adjust";
        var toggle = PanelGfx.Hit(buttonX, 0f, StatusLayouts.AdjustButtonSize, StatusLayouts.AdjustButtonSize,
            ToggleAllocation, _content, disabled: !context.IsPlayer);
        PanelGfx.Image(context.Texture, context.Asset(image),
            0f, 0f, StatusLayouts.AdjustButtonSize, StatusLayouts.AdjustButtonSize, toggle);
    }

    private void DrawStepper(StatusPanelContext context, string stat, int delta, int index)
    {
        var x = delta < 0 ? StatusLayouts.StepMinusX : StatusLayouts.StepPlusX;
        var top = index * StatusLayouts.AttributeAdjustStride + StatusLayouts.StepOffsetY;
        var enabled = delta > 0
            ? _allocation!.RemainingPoints >= 1
            : _allocation!.PendingOf(stat) >= 1;

        var button = PanelGfx.Hit(x, top, StatusLayouts.StepWidth, StatusLayouts.StepHeight,
            () => Change(stat, delta), _content, !enabled);

        PanelGfx.Image(context.Texture, context.Asset("character-step"),
            0f, 0f, StatusLayouts.StepWidth, StatusLayouts.StepHeight, button, flipH: delta > 0);
        var pressed = new Control
        {
            MouseFilter = MouseFilterEnum.Ignore,
            Visible = false,
            Material = new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add },
        };
        button.AddChild(pressed);
        PanelGfx.Image(context.Texture, context.Asset("character-step-pressed"),
            0f, 0f, StatusLayouts.StepWidth, StatusLayouts.StepHeight, pressed, flipH: delta > 0);
        button.ButtonDown += () =>
        {
            pressed.Visible = true;
            StartHold(stat, delta);
        };
        button.ButtonUp += () =>
        {
            pressed.Visible = false;
            _hold = null;
        };
        button.MouseExited += () =>
        {
            pressed.Visible = false;
            _hold = null;
        };

        if (!enabled) button.Modulate = new Color(1f, 1f, 1f, .4f);
    }

    private void DrawCombat(StatusPanelContext context)
    {
        if (_adjusting && _allocation is not null)
        {
            var reset = StatusLayouts.ResetButton;
            var button = PanelGfx.Hit(reset.X, reset.Y, reset.Width, reset.Height, ResetAttributes, _content);
            TextButton(context, button, "能力初始化", reset.Width, reset.Height);
            return;
        }

        var frame = StatusLayouts.CombatFrame;
        PanelGfx.Border(context.Texture, context.Asset, frame.X, frame.Y, frame.Width, frame.Height, _content);

        var panel = context.IsPlayer
            ? context.PanelFor(context.HeroAttributes!.Allocated)
            : context.Member.Panel;
        var modifiers = context.CombatModifiers?.Invoke(context.Member.Id);
        if (modifiers is { Count: > 0 }) panel = panel.WithPassives(context.Member.Level, modifiers);
        var values = CharacterValues.CombatValues(panel);
        var labels = context.Panels.Character.CombatLabels;

        for (var index = 0; index < labels.Length && index < values.Length; index += 1)
        {
            var y = StatusLayouts.CombatTop + index * StatusLayouts.CombatStride;
            PanelGfx.Text(labels[index], StatusLayouts.CombatLabelX, y, StatusLayouts.CombatSize, Gold, _content);
            PanelGfx.Right(values[index], StatusLayouts.CombatValueRight, y,
                StatusLayouts.CombatSize, Gold, _content, 120f);
        }
    }

    private void DrawPoints(StatusPanelContext context)
    {
        var points = _allocation?.RemainingPoints ?? context.Member.StatPoints;
        var x = _adjusting ? StatusLayouts.PointsAdjustX : StatusLayouts.PointsX;
        PanelGfx.Text($"{context.Panels.Character.Labels.StatPoints} {points}", x, StatusLayouts.PointsY,
            StatusLayouts.PointsSize, AreaText, _content);
    }

    private void ToggleAllocation()
    {
        if (_adjusting)
        {
            if (!CommitAllocation()) return;
            Reload();
            return;
        }

        if (_context is not { } context) return;
        if (!context.IsPlayer || context.HeroAttributes is null) return;

        _allocation = new AttributeAllocation(context.HeroAttributes);
        _allocationError = string.Empty;
        _adjusting = true;
        Render();
    }

    private bool CommitAllocation()
    {
        if (_allocation is not null && _allocation.HasChanges)
        {
            try
            {
                _allocation.Commit();
            }
            catch (Exception error)
            {
                _allocationError = error.Message;
                Render();
                return false;
            }

            _context?.Changed?.Invoke();
        }

        DiscardAllocation();
        return true;
    }

    private void Reload()
    {
        if (_context is not { } context) return;
        if (context.SelectMember is null)
        {
            Render();
            return;
        }

        context.SelectMember(context.Member.Id);
    }

    private void DiscardAllocation()
    {
        _hold = null;
        _allocation?.Cancel();
        _allocation = null;
        _allocationError = string.Empty;
        _adjusting = false;
    }

    private void ResetAttributes()
    {
        if (_context is not { } context) return;
        if (!context.IsPlayer || context.HeroAttributes is null) return;   // 非主角没有可重置的账本
        context.HeroAttributes.ResetAttributes();
        _allocation = new AttributeAllocation(context.HeroAttributes);
        _allocationError = string.Empty;
        context.Changed?.Invoke();
        Render();
    }

    private void Change(string stat, int delta)
    {
        if (_allocation is null) return;

        try
        {
            _allocation.Change(stat, delta);
        }
        catch (Exception error)
        {
            _allocationError = error.Message;
            Render();
            return;
        }

        _allocationError = string.Empty;
        _context?.Changed?.Invoke();
        Render();
    }

    private void StartHold(string stat, int delta)
    {
        _hold = new HoldState { Stat = stat, Delta = delta, Remaining = StatusLayouts.HoldDelaySeconds };
    }

    public override void _Process(double delta)
    {
        if (_hold is not { } hold) return;

        if (!_adjusting || _allocation is null || !Input.IsMouseButtonPressed(MouseButton.Left))
        {
            _hold = null;
            return;
        }

        var canChange = hold.Delta > 0 ? _allocation.RemainingPoints >= 1 : _allocation.PendingOf(hold.Stat) >= 1;
        if (!canChange)
        {
            _hold = null;
            return;
        }

        hold.Remaining -= delta;
        if (hold.Remaining > 0) return;

        Change(hold.Stat, hold.Delta);
        // 每一步都会重画并换掉按钮，所以这里只改计时器，不去碰已经释放的控件。
        if (_hold is null) return;
        _hold.Remaining += StatusLayouts.HoldRepeatSeconds;
        _hold.Started = true;
    }

    private static void TextButton(StatusPanelContext context, Button button, string label, float width,
        float height)
    {
        PanelGfx.ButtonSkin(context.Texture, width, height, button);
        PanelGfx.CenteredAt(label, width / 2f, height / 2f, width - 12f,
            (int)Math.Min(22f, height * .42f), Gold, button);
    }

    public bool DebugAdjust()
    {
        if (_context is null) return false;
        if (!_adjusting) ToggleAllocation();
        return _adjusting;
    }

    public bool DebugChange(string stat, int delta)
    {
        if (!_adjusting || _allocation is null) return false;
        Change(stat, delta);
        return true;
    }

    public bool DebugCommit()
    {
        if (!_adjusting || _allocation is null || !_allocation.HasChanges) return false;
        if (!CommitAllocation()) return false;
        Reload();
        return true;
    }

    public int RemainingPoints => _allocation?.RemainingPoints ?? _context?.Member.StatPoints ?? 0;

    public string DescribeAllocation()
    {
        if (_context is not { } context) return string.Empty;
        var allocated = context.HeroAttributes?.Allocated;
        var pending = _allocation is null
            ? null
            : string.Join(" ", PrimaryStats.All
                .Where(stat => _allocation.PendingOf(stat) != 0)
                .Select(stat => $"{stat}+{_allocation.PendingOf(stat)}"));
        return string.Join(" ", PrimaryStats.All.Select(stat =>
            $"{stat}={(allocated is null ? 0 : PrimaryAttributeMath.Get(allocated, stat))}"))
            + (pending is { Length: > 0 } ? $"（暂存 {pending}）" : string.Empty)
            + $" 剩余={RemainingPoints}";
    }
}
