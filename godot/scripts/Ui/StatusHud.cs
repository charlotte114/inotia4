using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Inotia4.Domain.Combat;
using Inotia4.Domain.Content;
using Inotia4.Domain.Ui;

namespace Inotia4.Ui;

public sealed record StatusMemberValues(int Level, int Health, int MaxHealth, int Mana, int MaxMana,
    int Experience, int NextLevelExperience);

public sealed partial class StatusHud : Control
{
    private StatusHudSkin _skin = null!;
    private Func<string, Texture2D?> _texture = null!;
    private Func<StatusMemberValues> _readControlled = null!;
    private Func<IReadOnlyList<(string Id, StatusMemberValues Values, bool Living, string? Portrait)>> _readMembers = null!;
    private Func<string, IReadOnlyList<ActiveCondition>>? _readConditions;
    private Func<string>? _readControlledId;

    private Func<string?>? _readControlledPortrait;

    private Func<long>? _tick;
    private Action<string> _onSelect = _ => { };

    /// <summary>
    /// 主角面板被点：有选中目标就锁定他，否则回到跟随被控者。
    /// 没接时是空操作、面板也不吃点击。
    /// </summary>
    private static readonly Action NoFocus = () => { };

    private Action _onFocus = NoFocus;

    private Action? _onTap;

    private const ulong LongPressMs = 400;

    private ulong _portraitPressedAt;

    private readonly List<TextureRect> _playerIcons = [];

    private string _playerIconSignature = string.Empty;

    private Gauge? _health;
    private Gauge? _mana;
    private Gauge? _experience;
    private Label? _level;

    private sealed class MemberPanel
    {
        public required Control Root;
        public required TextureRect Portrait;
        public required Label Level;
        public required Gauge Health;
        public required Gauge Mana;
        public required Gauge Experience;
        public string? Id;

        public readonly List<TextureRect> Icons = [];

        public string IconSignature = string.Empty;
    }

    private readonly List<MemberPanel> _panels = [];

    /// <summary>注入皮肤、贴图与数据源；必须在加入场景树之前调用。</summary>
    public StatusHud Skin(StatusHudSkin skin, Func<string, Texture2D?> texture)
    {
        _skin = skin;
        _texture = texture;
        return this;
    }

    public StatusHud Data(Func<StatusMemberValues> readControlled,
        Func<IReadOnlyList<(string Id, StatusMemberValues Values, bool Living, string? Portrait)>> readMembers,
        Action<string> onSelect,
        Func<string, IReadOnlyList<ActiveCondition>>? readConditions = null,
        Func<string>? controlledId = null,
        Func<long>? tick = null,
        Action? onFocus = null,
        Func<string?>? controlledPortrait = null,
        Action? onTap = null)
    {
        _readControlled = readControlled;
        _readMembers = readMembers;
        _onSelect = onSelect;
        _readConditions = readConditions;
        _readControlledId = controlledId;
        _tick = tick;
        _onFocus = onFocus ?? NoFocus;
        _readControlledPortrait = controlledPortrait;
        _onTap = onTap;
        // 面板在 _Ready 里才建（Data() 早于 AddChild），这里不能直接改 _playerRoot 的
        // MouseFilter——由 _Ready 建面板时按「有没有给 onFocus」决定。
        return this;
    }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        BuildTargetPanel();
        BuildPlayerPanel();
        foreach (var _ in _skin.Member.Slots)
        {
            var panel = BuildMemberPanel();
            _panels.Add(panel);
            AddChild(panel.Root);
        }
        Layout();
        GetViewport().SizeChanged += Layout;
    }

    public override void _ExitTree() => GetViewport().SizeChanged -= Layout;

    /// <summary>
    /// 当前视口尺寸。canvas_items 拉伸下控件坐标就是视口坐标：桌面是 960×640，
    /// 手机横向比这更宽（stretch/aspect=expand），所以布局一律按实际视口算——
    /// 写死 960／640 的话，右上角面板会停在屏幕中间偏左。
    /// </summary>
    private Vector2 Viewport => GetViewportRect().Size;

    private float HudScale => MathF.Min(Viewport.X / 800f, Viewport.Y / 480f);

    private void Layout()
    {
        var size = HudScale;
        var width = Viewport.X;
        var height = Viewport.Y;
        var playerEdge = _skin.Player.Width - _skin.Player.Inset.Right;
        _playerRoot.Position = new Vector2(width - playerEdge * size, 0f);
        _playerRoot.Scale = new Vector2(size, size);

        _targetRoot.Position = new Vector2((width - _skin.Target.Width * size) / 2f, 0f);
        _targetRoot.Scale = new Vector2(size, size);

        var count = _panels.Count(panel => panel.Root.Visible);        var columnGap = 10f * size;
        var rowGap = 8f * size;
        var start = _skin.Player.Height * size + columnGap;
        var limit = height - 8f * size;
        var available = MathF.Max(0f, limit - start);
        var gap = count > 1 ? MathF.Min(rowGap, available / (count - 1)) : 0f;
        var scale = count == 0 ? 1f
            : MathF.Min(1f, MathF.Max(0f, (available - (count - 1) * gap) / (count * _skin.Member.Height * size)));
        var memberEdge = _skin.Member.Width - _skin.Member.Inset.Right;
        var row = _skin.Member.Height * size * scale;
        var columnScale = size * scale;
        var top = start;
        foreach (var panel in _panels)
        {
            if (!panel.Root.Visible) continue;
            panel.Root.Scale = new Vector2(columnScale, columnScale);
            panel.Root.Position = new Vector2(width - memberEdge * columnScale, top);
            top += row + gap;
        }
    }

    private Control _targetRoot = null!;
    private Label _targetName = null!;
    private Gauge _targetGauge = null!;
    private string _targetSignature = string.Empty;

    private void BuildTargetPanel()
    {
        var layout = _skin.Target;
        _targetRoot = new Control
        {
            Name = "Target",
            Size = new Vector2(layout.Width, layout.Height),
            MouseFilter = MouseFilterEnum.Ignore,
            Visible = false,
        };
        AddChild(_targetRoot);

        // 名字框以名字矩形的中心点为原点，所以这里按半宽往左挪；两边宽度不一样时自动居中。
        var (anchorX, anchorY) = StatusTargetRules.NameAnchor(_skin);
        _targetName = HudText(18);
        _targetName.Position = new Vector2(anchorX - layout.Name.Width / 2f, anchorY);
        _targetName.Size = new Vector2(layout.Name.Width, layout.Name.Height);
        _targetName.VerticalAlignment = VerticalAlignment.Top;
        _targetName.AddThemeColorOverride("font_color", Colors.White);
        _targetRoot.AddChild(_targetName);

        _targetGauge = new Gauge(layout.Gauge, part => _skin.Assets.GetValueOrDefault($"target-{part}"), _texture);
        _targetRoot.AddChild(_targetGauge.Fill);
        _targetRoot.AddChild(_targetGauge.Start);
        _targetRoot.AddChild(_targetGauge.End);
    }

    public bool TargetVisible => _targetRoot.Visible;

    public string TargetText => _targetName.Text;

    public float TargetFillWidth => _targetGauge.Width;

    public void SetTarget(TargetStatus? target)
    {
        var playerLevel = _readControlled?.Invoke().Level ?? 1;
        var signature = target is null
            ? string.Empty
            : $"{target.Id}|{target.Name}|{target.Level}|{target.Health}/{target.MaxHealth}|{target.SceneLevel}|{playerLevel}";
        if (signature == _targetSignature) return;
        _targetSignature = signature;

        _targetRoot.Visible = target is not null;
        if (target is null) return;

        _targetName.Text = $"{target.Name} (Lv.{target.Level})";
        var color = StatusTargetRules.NameColor(_skin, playerLevel, target);
        _targetName.AddThemeColorOverride("font_color", color is null ? Colors.White : new Color(color));
        _targetGauge.Update(target.Health, target.MaxHealth);
    }

    private Control _playerRoot = null!;

    private TextureRect _playerPortrait = null!;

    private string? _playerPortraitPath;

    private void BuildPlayerPanel()
    {
        var layout = _skin.Player;
        _playerRoot = new Control
        {
            Size = new Vector2(layout.Width, layout.Height),
            MouseFilter = ReferenceEquals(_onFocus, NoFocus) && _onTap is null
                ? MouseFilterEnum.Ignore
                : MouseFilterEnum.Stop,
        };
        _playerRoot.GuiInput += @event =>
        {
            if (@event is not InputEventMouseButton { ButtonIndex: MouseButton.Left } click) return;

            if (_onTap is null)
            {
                if (click.Pressed) _onFocus();
                return;
            }

            if (click.Pressed)
            {
                _portraitPressedAt = Time.GetTicksMsec();
                return;
            }

            if (_portraitPressedAt == 0) return;
            var held = Time.GetTicksMsec() - _portraitPressedAt;
            _portraitPressedAt = 0;
            if (held >= LongPressMs) _onFocus();
            else _onTap();
        };
        AddChild(_playerRoot);

        _playerPortraitPath = _skin.Assets.GetValueOrDefault("player-portrait");
        _playerPortrait = Picture(_playerPortraitPath, layout.Portrait);
        _playerRoot.AddChild(_playerPortrait);
        var frame = Picture(_skin.Assets.GetValueOrDefault("player-frame"),
            new Domain.Content.StatusRectSkin { Width = layout.Width, Height = layout.Height });
        _playerRoot.AddChild(frame);

        _level = HudText(17);
        _playerRoot.AddChild(_level);
        PlaceLevel(_level, layout.Level);

        _health = BuildGauge(_playerRoot, "health", layout.Gauges);
        _mana = BuildGauge(_playerRoot, "mana", layout.Gauges);
        _experience = BuildGauge(_playerRoot, "experience", layout.Gauges);
    }

    private MemberPanel BuildMemberPanel()
    {
        var layout = _skin.Member;
        var root = new Control
        {
            Size = new Vector2(layout.Width, layout.Height),
            MouseFilter = MouseFilterEnum.Stop,
        };

        var portrait = Picture(_skin.Assets.GetValueOrDefault("member-portrait"), layout.Portrait);
        root.AddChild(portrait);
        var frame = Picture(_skin.Assets.GetValueOrDefault("member-frame"),
            new Domain.Content.StatusRectSkin { Width = layout.Width, Height = layout.Height });
        root.AddChild(frame);

        var level = HudText(14);
        root.AddChild(level);
        PlaceLevel(level, layout.Level);

        var panel = new MemberPanel
        {
            Root = root,
            Portrait = portrait,
            Level = level,
            Health = BuildGauge(root, "health", layout.Gauges)!,
            Mana = BuildGauge(root, "mana", layout.Gauges)!,
            Experience = BuildGauge(root, "experience", layout.Gauges)!,
        };

        root.GuiInput += @event =>
        {
            if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } &&
                panel.Id is { } id)
                _onSelect(id);
        };
        return panel;
    }

    public void Refresh()
    {
        if (_health is null) return;
        var controlled = _readControlled();
        _health.Update(controlled.Health, controlled.MaxHealth);
        _mana?.Update(controlled.Mana, controlled.MaxMana);
        _experience?.Update(controlled.Experience, controlled.NextLevelExperience);
        if (_level is not null)
        {
            var text = controlled.Level.ToString();
            if (_level.Text != text)
            {
                _level.Text = text;
                PlaceLevel(_level, _skin.Player.Level);
            }
        }

        if (_readControlledPortrait?.Invoke() is { Length: > 0 } portraitFile &&
            portraitFile != _playerPortraitPath && _texture(portraitFile) is { } portraitTexture)
        {
            _playerPortraitPath = portraitFile;
            _playerPortrait.Texture = portraitTexture;
        }

        var members = _readMembers();
        for (var index = 0; index < _panels.Count; index++)
        {
            var panel = _panels[index];
            if (index >= members.Count)
            {
                panel.Root.Visible = false;
                panel.Id = null;
                continue;
            }

            var (id, values, living, portrait) = members[index];
            panel.Root.Visible = true;
            panel.Id = id;
            var portraitPath = living
                ? portrait ?? _skin.Assets.GetValueOrDefault("member-portrait")
                : _skin.Assets.GetValueOrDefault(id == "player" ? "player-portrait-down" : "member-portrait-down");
            var texture = portraitPath is null ? null : _texture(portraitPath);
            if (texture is not null) panel.Portrait.Texture = texture;
            var levelText = values.Level.ToString();
            if (panel.Level.Text != levelText)
            {
                panel.Level.Text = levelText;
                PlaceLevel(panel.Level, _skin.Member.Level);
            }
            panel.Health.Update(values.Health, values.MaxHealth);
            panel.Mana.Update(values.Mana, values.MaxMana);
            panel.Experience.Update(values.Experience, values.NextLevelExperience);
            panel.Root.Modulate = living ? Colors.White : new Color(1f, 1f, 1f, 0.55f);
        }

        RefreshIcons();
        Layout();
    }

    private void PaintIcons(List<TextureRect> icons, ref string signature, Control root,
        IReadOnlyList<ActiveCondition> conditions, float panelHeight, bool buffsOnly)
    {
        var skin = _skin.BuffIcons;
        if (skin.Size <= 0f || skin.Step <= 0f) return;

        var entries = conditions
            .Where(condition => (!buffsOnly || condition.Kind == "buff") && skin.Catalog.ContainsKey(condition.Id))
            .ToList();

        while (icons.Count < entries.Count)
        {
            var icon = new TextureRect
            {
                Size = new Vector2(skin.Size, skin.Size),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale,
                MouseFilter = MouseFilterEnum.Ignore,
                Visible = false,
            };
            root.AddChild(icon);
            icons.Add(icon);
        }

        // 贴图与位置只在组合变化时重设；剩余时长每帧都在变，所以那一段照旧每帧写。
        var current = string.Join("|", entries.Select(entry => entry.Id));
        var rebuild = current != signature;
        if (rebuild) signature = current;

        for (var index = 0; index < icons.Count; index++)
        {
            var icon = icons[index];
            if (index >= entries.Count)
            {
                icon.Visible = false;
                icon.RemoveMeta("remainingMs");
                continue;
            }

            var entry = entries[index];
            if (rebuild)
            {
                var (x, y) = BuffIconRules.Position(skin.Size, skin.Step, panelHeight, index);
                var texture = _texture(skin.Catalog[entry.Id]);
                if (texture is not null) icon.Texture = texture;
                icon.Position = new Vector2(x, y);
            }

            icon.Visible = true;
            // 闪烁要按每帧的剩余时长算，所以把这一帧的读数挂在节点上，交给 BlinkIcons 统一处理。
            icon.SetMeta("remainingMs", entry.RemainingMs);
        }
    }

    /// <summary>
    /// 按逻辑帧切换闪烁：只剩最后 30 帧时每 4 帧暗一次，暗的时候只是不画、槽位保留，
    /// 所以后面的图标不会跟着往前挪。
    private void BlinkIcons(List<TextureRect> icons, long tick)
    {
        foreach (var icon in icons)
        {
            if (!icon.HasMeta("remainingMs")) continue;

            var frames = BuffIconRules.RemainingFrames((double)icon.GetMeta("remainingMs"), CombatClock.LogicFrameMs);
            icon.Visible = !BuffIconRules.Blinking(frames, tick);
        }
    }

    private void RefreshIcons()
    {
        if (_readConditions is null) return;

        var tick = _tick?.Invoke() ?? 0L;

        if (_readControlledId?.Invoke() is { Length: > 0 } controlled)
            PaintIcons(_playerIcons, ref _playerIconSignature, _playerRoot, _readConditions(controlled),
                _skin.Player.Height, buffsOnly: true);
        BlinkIcons(_playerIcons, tick);

        foreach (var panel in _panels)
        {
            if (!panel.Root.Visible || panel.Id is not { } id)
            {
                foreach (var icon in panel.Icons) icon.Visible = false;
                continue;
            }

            PaintIcons(panel.Icons, ref panel.IconSignature, panel.Root, _readConditions(id),
                _skin.Member.Height, buffsOnly: false);
            BlinkIcons(panel.Icons, tick);
        }
    }

    internal IReadOnlyList<TextureRect> PlayerIconsDebug => _playerIcons;

    internal string? PlayerPortraitDebug => _playerPortraitPath;

    internal IReadOnlyList<TextureRect> MemberIconsDebug(int panel) =>
        panel >= 0 && panel < _panels.Count ? _panels[panel].Icons : [];

    private TextureRect Picture(string? path, Domain.Content.StatusRectSkin layout)
    {
        var rect = new TextureRect
        {
            Position = new Vector2(layout.X, layout.Y),
            Size = new Vector2(layout.Width, layout.Height),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        var texture = path is null ? null : _texture(path);
        if (texture is not null) rect.Texture = texture;
        return rect;
    }

    private Gauge? BuildGauge(Control root, string kind, Dictionary<string, StatusGaugeSkin> gauges)
    {
        if (!gauges.TryGetValue(kind, out var layout)) return null;
        string? Asset(string part) => _skin.Assets.GetValueOrDefault($"{kind}-{part}");
        var gauge = new Gauge(layout, Asset, _texture);
        root.AddChild(gauge.Fill);
        root.AddChild(gauge.Start);
        root.AddChild(gauge.End);
        return gauge;
    }

    private static void PlaceLevel(Label label, Domain.Content.StatusRectSkin rect)
    {
        var text = label.GetMinimumSize();
        var scale = Math.Min(1f, Math.Min(
            (rect.Width - 4f) / Math.Max(1f, text.X),
            (rect.Height - 4f) / Math.Max(1f, text.Y)));
        scale = Math.Max(0.1f, scale);
        label.Scale = new Vector2(scale, scale);
        label.Position = new Vector2(
            rect.X + (rect.Width - text.X * scale) / 2f,
            rect.Y + (rect.Height - text.Y * scale) / 2f);
    }

    private static Label HudText(int size) => new()
    {
        Text = string.Empty,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        MouseFilter = MouseFilterEnum.Ignore,
    };

    private sealed class Gauge(StatusGaugeSkin layout, Func<string, string?> assetPath,
        Func<string, Texture2D?> texture)
    {
        public readonly TextureRect Fill = Part(layout, assetPath, texture, "fill",
            layout.X + layout.FillOffset, layout.FillWidth);
        public readonly TextureRect Start = Part(layout, assetPath, texture, "start", layout.X, layout.StartWidth);
        public readonly TextureRect End = Part(layout, assetPath, texture, "end",
            layout.X + layout.FillOffset + layout.FillWidth, layout.EndWidth);

        private float _width = -1f;

        public float Width => _width;

        private static TextureRect Part(StatusGaugeSkin layout, Func<string, string?> assetPath,
            Func<string, Texture2D?> texture, string part, float x, float width)
        {
            var rect = new TextureRect
            {
                Position = new Vector2(x, layout.Y),
                Size = new Vector2(width, layout.Height),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            var path = assetPath(part);
            var image = path is null ? null : texture(path);
            if (image is not null) rect.Texture = image;
            return rect;
        }

        public void Update(int value, int max)
        {
            var width = GaugeRules.Width(value, max, layout.FillWidth);
            if (MathF.Abs(width - _width) < 0.5f) return;
            _width = width;
            Fill.Size = new Vector2(width, layout.Height);
            Fill.Position = new Vector2(layout.X + layout.FillOffset, layout.Y);
            End.Position = new Vector2(layout.X + layout.FillOffset + width, layout.Y);
            var visible = width > 0.5f;
            Fill.Visible = visible;
            Start.Visible = visible;
            End.Visible = visible;
        }
    }
}
