using System.Linq;
using System.Text.Json;

namespace Inotia4.Domain.Ui;

public static class ControlLayouts
{
    public static readonly (float X, float Y)[] ShortcutOffsets =
    [
        (-342f, 33f), (-242f, 33f), (-142f, 33f),
        (-129f, -57f), (-61f, -120f), (33f, -123f),
    ];

    public static readonly (float X, float Y) SwapOffset = (42f, -210f);

    public static readonly (float X, float Y) PendingOffset = (-382f, -183f);

    public const float AttackWidth = 159f;

    public const float AttackHeight = 160f;

    public static float ActionScale(float width, float height) =>
        Math.Min(width / 960f, height / 640f);

    public static (float X, float Y) AttackPoint(float width, float height)
    {
        var scale = ActionScale(width, height);
        return (width - 104f * scale, height - 80f * scale);
    }

    public static (float X, float Y) ShortcutPoint(float width, float height, int slot)
    {
        var scale = ActionScale(width, height);
        var attack = AttackPoint(width, height);
        var offset = ShortcutOffsets[Math.Clamp(slot, 0, ShortcutOffsets.Length - 1)];
        return (attack.X + offset.X * scale, attack.Y + offset.Y * scale);
    }

    public static (float X, float Y) SwapPoint(float width, float height)
    {
        var scale = ActionScale(width, height);
        var attack = AttackPoint(width, height);
        return (attack.X + SwapOffset.X * scale, attack.Y + SwapOffset.Y * scale);
    }

    public static (float X, float Y) PendingPoint(float width, float height)
    {
        var scale = ActionScale(width, height);
        var attack = AttackPoint(width, height);
        return (attack.X / scale + PendingOffset.X, attack.Y / scale + PendingOffset.Y);
    }
}

public static class ControlIds
{
    public static readonly string[] All =
    [
        "move", "attack", "slot-0", "slot-1", "slot-2", "slot-3", "slot-4", "slot-5", "swap",
    ];
}

/// <summary>计算键位用的视口：尺寸与四周的安全区内缩（刘海、圆角那一圈不可压）。</summary>
public sealed record ControlViewport(
    float Width, float Height,
    float InsetLeft, float InsetRight, float InsetTop, float InsetBottom)
{
    public (float Left, float Top, float Width, float Height) Safe => (
        InsetLeft, InsetTop,
        Math.Max(1f, Width - InsetLeft - InsetRight),
        Math.Max(1f, Height - InsetTop - InsetBottom));
}

public sealed record ControlPlacement(float X, float Y, float Scale, float Width, float Height);

/// <summary>
/// 完整的键位解析：默认位置加玩家改过的覆盖项。
/// 覆盖项是安全区里的 0–1 归一化坐标；先限制图片完整留在安全区，再落点。
///
/// <paramref name="sizeScale"/> 是玩家在键位编辑器里调的「按键大小」：
/// 只放大每个控件的尺寸和它们之间的间距，右下角那个锚点本身不挪——
/// 否则调大按键会连带把普攻按钮从角落里往外顶。
/// </summary>
public static class ControlLayoutResolver
{
    public static IReadOnlyDictionary<string, ControlPlacement> Resolve(ControlViewport viewport,
        IReadOnlyDictionary<string, (float X, float Y)>? overrides = null, float sizeScale = 1f)
    {
        var (left, top, safeWidth, safeHeight) = viewport.Safe;
        var fit = sizeScale > 0f && float.IsFinite(sizeScale) ? sizeScale : 1f;
        var layoutScale = Math.Min(safeWidth / 960f, safeHeight / 640f);
        var moveLayoutScale = Math.Min(viewport.Width / 960f, viewport.Height / 640f);
        var actionScale = layoutScale * fit;
        var moveScale = moveLayoutScale * fit;
        var attackX = viewport.Width - viewport.InsetRight - 104f * layoutScale;
        var attackY = viewport.Height - viewport.InsetBottom - 80f * layoutScale;

        ControlPlacement Place(string id, float x, float y, float scale, float width, float height)
        {
            (float X, float Y)? custom =
                overrides is not null && overrides.TryGetValue(id, out var point) ? point : null;
            var (px, py) = custom is { } over
                ? Fit(viewport, left + over.X * safeWidth, top + over.Y * safeHeight, width * scale, height * scale)
                : Fit(viewport, x, y, width * scale, height * scale);
            return new ControlPlacement(px, py, scale, width * scale, height * scale);
        }

        var result = new Dictionary<string, ControlPlacement>
        {
            ["move"] = Place("move",
                left + 129f * moveLayoutScale, viewport.Height - viewport.InsetBottom - 129f * moveLayoutScale,
                moveScale, 210f, 210f),
            ["attack"] = Place("attack", attackX, attackY, actionScale,
                ControlLayouts.AttackWidth, ControlLayouts.AttackHeight),
            ["swap"] = Place("swap",
                attackX + ControlLayouts.SwapOffset.X * actionScale,
                attackY + ControlLayouts.SwapOffset.Y * actionScale,
                actionScale, 75f, 75f),
        };
        for (var index = 0; index < ControlLayouts.ShortcutOffsets.Length; index += 1)
            result[$"slot-{index}"] = Place($"slot-{index}",
                attackX + ControlLayouts.ShortcutOffsets[index].X * actionScale,
                attackY + ControlLayouts.ShortcutOffsets[index].Y * actionScale,
                actionScale, 94f, 94f);
        return result;
    }

    private static (float X, float Y) Fit(ControlViewport viewport, float x, float y, float width, float height)
    {
        var (left, top, safeWidth, safeHeight) = viewport.Safe;
        var halfX = Math.Min(safeWidth, width) / 2f;
        var halfY = Math.Min(safeHeight, height) / 2f;
        return (
            Math.Max(left + halfX, Math.Min(left + safeWidth - halfX, x)),
            Math.Max(top + halfY, Math.Min(top + safeHeight - halfY, y)));
    }

    public static (float X, float Y) Normalize(ControlViewport viewport, string id, float x, float y,
        IReadOnlyDictionary<string, (float X, float Y)>? overrides = null, float sizeScale = 1f)
    {
        var (left, top, safeWidth, safeHeight) = viewport.Safe;
        var size = Resolve(viewport, overrides, sizeScale)[id];
        var (px, py) = Fit(viewport, x, y, size.Width, size.Height);
        return ((px - left) / safeWidth, (py - top) / safeHeight);
    }
}

public sealed class ControlLayoutPreferences
{
    private const int Format = 1;

    public const float MinSizeScale = 0.7f;

    public const float MaxSizeScale = 1.4f;

    public const float SizeScaleStep = 0.1f;

    private readonly Func<string, string?> _read;
    private readonly Action<string, string> _write;

    private readonly Dictionary<string, (float X, float Y)> _positions = [];

    private readonly List<Action> _listeners = [];

    private float _sizeScale = 1f;

    public ControlLayoutPreferences(Func<string, string?> read, Action<string, string> write)
    {
        _read = read;
        _write = write;
        Load();
    }

    private static string StorageKey => "control-layout.json";

    public IReadOnlyDictionary<string, (float X, float Y)> Snapshot => _positions;

    public float SizeScale => _sizeScale;

    public void Set(string id, float x, float y)
    {
        if (Array.IndexOf(ControlIds.All, id) < 0) return;
        if (!float.IsFinite(x) || !float.IsFinite(y) || x is < 0f or > 1f || y is < 0f or > 1f) return;
        _positions[id] = (x, y);
        Save();
    }

    /// <summary>
    /// 改按键大小：先夹到档位范围、再对齐到 10% 的整数档。
    /// 与当前值相同就什么都不做——拖动条反复撞到上下限时不该一直写盘。
    /// </summary>
    public void SetSizeScale(float scale)
    {
        if (!float.IsFinite(scale)) return;
        var clamped = Math.Clamp(scale, MinSizeScale, MaxSizeScale);
        var stepped = MathF.Round(clamped / SizeScaleStep) * SizeScaleStep;
        stepped = Math.Clamp(stepped, MinSizeScale, MaxSizeScale);
        if (MathF.Abs(stepped - _sizeScale) < 0.0001f) return;
        _sizeScale = stepped;
        Save();
    }

    public void NudgeSizeScale(int steps) => SetSizeScale(_sizeScale + steps * SizeScaleStep);

    public void Reset()
    {
        _positions.Clear();
        _sizeScale = 1f;
        Save();
    }

    public IDisposable Subscribe(Action listener)
    {
        _listeners.Add(listener);
        return new Unsubscribe(this, listener);
    }

    private void Load()
    {
        try
        {
            var raw = _read(StorageKey);
            if (string.IsNullOrEmpty(raw)) return;
            var saved = JsonSerializer.Deserialize<SavedLayout>(raw, SaveGameStore.JsonOptions);
            if (saved is null || saved.Version != Format) return;

            // 尺寸是后加的字段：旧档里没有它就是默认 1，单独夹范围，不和位置一起弃用——
            // 位置那份坏掉也不该连带把按键大小退回默认。
            if (float.IsFinite(saved.SizeScale))
                _sizeScale = Math.Clamp(saved.SizeScale, MinSizeScale, MaxSizeScale);

            if (saved.Positions is null) return;

            foreach (var (id, point) in saved.Positions)
            {
                if (Array.IndexOf(ControlIds.All, id) < 0) return;
                if (!float.IsFinite(point.X) || point.X is < 0f or > 1f) return;
                if (!float.IsFinite(point.Y) || point.Y is < 0f or > 1f) return;
            }

            _positions.Clear();
            foreach (var (id, point) in saved.Positions) _positions[id] = (point.X, point.Y);
        }
        catch
        {
            // 存储不可读时使用默认布局。
            _positions.Clear();
            _sizeScale = 1f;
        }
    }

    private void Save()
    {
        try
        {
            var positions = _positions.ToDictionary(
                pair => pair.Key, pair => new ControlPointRecord(pair.Value.X, pair.Value.Y));
            _write(StorageKey, JsonSerializer.Serialize(
                new SavedLayout { Version = Format, SizeScale = _sizeScale, Positions = positions },
                SaveGameStore.JsonOptions));
        }
        catch
        {
        }

        foreach (var listener in _listeners) listener();
    }

    private sealed class Unsubscribe(ControlLayoutPreferences owner, Action listener) : IDisposable
    {
        public void Dispose() => owner._listeners.Remove(listener);
    }

    private sealed class SavedLayout
    {
        public int Version { get; set; }

        public float SizeScale { get; set; } = 1f;

        public Dictionary<string, ControlPointRecord>? Positions { get; set; }
    }
}

public sealed record ControlPointRecord(float X, float Y);
