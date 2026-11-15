using System;
using Godot;
using Inotia4.Application;
using Inotia4.Domain.Ui;

namespace Inotia4.Ui;

/// <summary>
/// 标题页的动态背景（原版标题页那套叠层，依据见
/// <c>inotia4/docs/SO_REVERSE.md</c> §19）。
///
/// 原版的画法是「两层 1024×1024 图集 + 六层各自振荡的烟雾」：
///
/// 1. 图集 79 的 idx 0 与图集 80 的 idx 0 拼成 1200×640 的魔法阵（合成图已在项目里，
///    见 <see cref="Background"/>）；
/// 2. 固定元素（logo、副标题、按钮）由 <see cref="TitleScene"/> 自己按设计坐标画；
/// 3. 六层烟雾各画一次图集 80 的 idx 1 / idx 2，透明度由
///    <see cref="TitleAnimation"/> 逐帧算出（这是原版行为）。
///
/// **烟雾的落点照逆向，自转是表现层的处理**（见 §19.8 与 §19.9）：
/// 反汇编里六层只有透明度在变、落点固定（§19.2）；但只做明暗时，叠在本身已经烘着
/// 静态烟的背景图上，画面几乎看不出变化。所以每层贴图绕自身中心慢慢自转——位置不动，
/// 左簇与右簇反向，读起来就是「左右两团烟各自在缓慢旋转」。
/// </summary>
public partial class TitleBackdrop : Control
{
    private const string AtlasTwo = "assets/ui/title-menu/anim/atlas-02.png";

    private const string Background = "assets/ui/title-menu/background.png";

    private static readonly Rect2 SmokeA = new(2, 645, 248, 234);
    private static readonly Rect2 SmokeB = new(253, 645, 304, 235);

    /// <summary>
    /// 六层烟雾各自的落点、贴哪一团、原地自转多快（原版落点逐条对出来的，见 §19.9）。
    ///
    /// 落点是 `0xD7A76` 起那六个绘制块的坐标，基准是 **960×640** 的设计画布：
    /// `r6 = (W−960)/2`、`r7 = (H−640)/2`（`0xD7A00`–`0xD7A22` 现算的，不是猜的）。
    /// 六层分成左右两簇——左边 162～237、右边 730～785，各有上中下三个高度，
    /// 所以画面上看到的就是「左右两团烟」，而不是绕着法阵跑的一圈。
    ///
    /// 贴图：前四层（A–D）是图集 80 的 idx 1，后两层（E、F）是 idx 2。
    /// 这个分配是从 `IMGSYS_GetLoc(0x50, 1/2)` 的两次取值与各块 `mov r3, r8/sb` 读出来的。
    ///
    /// **自转是表现层的处理，不是逆向结论**（§19.9）：反汇编里六层只有 alpha 在变、
    /// 落点固定。只做明暗的话，叠在本来就烘着静态烟的背景图上，画面上几乎看不出变化
    /// —— 所以每层贴图绕自身中心慢慢转，左簇与右簇反向。两只烟的贴图四边 alpha 实测
    /// 最大只有 6/255（等于全透明），转起来不会露出矩形硬边。
    /// 速度单位是度/秒：8～13 度/秒 ≈ 半分钟到一分钟转一圈，看着是云在慢慢打旋。
    /// 最后一列是簇号：0 = 左簇（A／C／E），1 = 右簇（B／D／F），两簇漂移方向相反。
    /// </summary>
    private static readonly (Rect2 Source, Vector2 Position, float DegPerSecond, int Cluster)[] Plumes =
    [
        (SmokeA, new Vector2(237f, 133f), 8.0f, 0),    // A 左上
        (SmokeA, new Vector2(730f, 97f), -9.0f, 1),    // B 右上
        (SmokeA, new Vector2(197f, 470f), 10.0f, 0),   // C 左下
        (SmokeA, new Vector2(750f, 473f), -11.0f, 1),  // D 右下
        (SmokeB, new Vector2(162f, 285f), 12.0f, 0),   // E 左中
        (SmokeB, new Vector2(785f, 285f), -13.0f, 1),  // F 右中
    ];

    /// <summary>
    /// 整簇慢漂移的半径（设计像素）与一圈用时。
    ///
    /// 只让贴图自转时，动得最厉害的是云团外圈，而外圈恰恰是 alpha 渐隐到 0 的地方，
    /// 所以画面上几乎看不出来（实测两帧全屏平均差只有 0.2/255）。给每簇加上整体位移，
    /// 轮廓才会移动，肉眼才读得出「这团烟在慢慢转」。半径 42 相对烟团本身（250×235）
    /// 只有六分之一，烟团仍压在原版落点附近，构图不变。
    /// </summary>
    private const float DriftRadius = 42f;

    private const float DriftSeconds = 24f;

    private const float BreatheAmplitude = .05f;

    private const float BreatheSeconds = 17f;

    private readonly float[] _plumeAngles = new float[Plumes.Length];

    private readonly TitleAnimation _animation = new();
    private TextureLease? _lease;
    private AtlasTexture[] _plumeTextures = [];

    private static readonly int[] SlotOf = [0, 0, 2, 2, 1, 1];

    private static readonly bool[] RightSide = [false, true, false, true, false, true];

    private const float WaveSeconds = 9f;

    /// <summary>
    /// 反汇编的振荡器是「每个渲染帧推进一格」，速度取决于当年机器的帧率：六层的周期
    /// 是 21～70 格，在 2013 年的中端机上约 1.5～5 秒一轮。`--plumeexact` 用它，
    /// 按 60fps 原速推进会快到像在闪，所以折算成**每秒 8 格**。见 §19.9。
    /// </summary>
    private const float StepsPerSecond = 8f;

    private const float MaxAlpha = .95f;

    private static readonly Color PlumeTint = new(1.55f, 1.55f, 1.6f);

    private static float Smooth(float value) =>
        .5f - .5f * Mathf.Cos(Mathf.Tau * Mathf.Clamp(value, 0f, 1f));

    private ImageTexture? _magicCircle;
    private bool _debugOnce = true;
    private float _seconds;
    private float _stepBudget;

    private bool _exactOscillators;

    public Vector2 DesignSize { get; set; } = new(1200, 640);

    private static readonly Vector2 LayerCanvas = new(960, 640);

    private bool _plumesEnabled = true;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        _plumesEnabled = Array.IndexOf(OS.GetCmdlineUserArgs(), "--noplumes") < 0;
        _exactOscillators = Array.IndexOf(OS.GetCmdlineUserArgs(), "--plumeexact") >= 0;

        _lease = new TextureCache().Lease();
        var background = _lease.Texture(Background);
        _magicCircle = SliceToImage(background, new Rect2(0, 0, 1200, 640));

        var atlas2 = _lease.Texture(AtlasTwo);
        _plumeTextures = new AtlasTexture[Plumes.Length];
        for (var i = 0; i < Plumes.Length; i++) _plumeTextures[i] = Slice(atlas2, Plumes[i].Source);

        QueueRedraw();
    }

    public override void _ExitTree()
    {
        _lease?.Dispose();
        _lease = null;
    }

    public override void _Process(double delta)
    {
        var step = (float)Math.Min(delta, 0.05);
        _seconds += step;

        // 每层原地自转：位置不动，只有贴图绕自身中心转，所以是一整片云在打旋，
        // 不是云纹被拉扯（上一版让整块贴图沿轨道跑，观感很乱）。
        for (var i = 0; i < _plumeAngles.Length; i++)
            _plumeAngles[i] = (_plumeAngles[i] + Plumes[i].DegPerSecond * step * Mathf.Pi / 180f)
                              % Mathf.Tau;

        if (_exactOscillators)
        {
            _stepBudget += step * StepsPerSecond;
            while (_stepBudget >= 1f)
            {
                _stepBudget -= 1f;
                _animation.Advance();
            }
        }

        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_magicCircle is null) return;

        var viewport = GetViewportRect().Size;

        // ── 魔法阵：固定不动 ──────────────────────────────────
        // 这一层不转——原版同样不转（见 SO_REVERSE.md §19.8）。
        var baseScale = Math.Max(viewport.X / DesignSize.X, viewport.Y / DesignSize.Y);
        var baseOrigin = (viewport - DesignSize * baseScale) / 2f;
        DrawSetTransform(baseOrigin, 0f, new Vector2(baseScale, baseScale));
        DrawTextureRect(_magicCircle, new Rect2(Vector2.Zero, DesignSize), false);
        DrawSetTransform(Vector2.Zero, 0f, Vector2.One);

        var layerOrigin = (viewport - LayerCanvas * baseScale) / 2f;

        if (_debugOnce)
        {
            _debugOnce = false;
            GD.Print($"[backdrop] viewport={viewport} baseScale={baseScale:F3}");
        }

        for (var i = 0; i < _plumeTextures.Length; i++)
        {
            if (!_plumesEnabled) break;
            var texture = _plumeTextures[i];
            if (texture is null) continue;

            float alpha;
            if (_exactOscillators)
            {
                alpha = Smooth(_animation.AlphaOf(i)) * MaxAlpha;
            }
            else
            {
                var phase = SlotOf[i] * Mathf.Tau / 3f + (RightSide[i] ? Mathf.Pi : 0f);
                alpha = (.5f + .5f * Mathf.Sin(Mathf.Tau * _seconds / WaveSeconds - phase)) * MaxAlpha;
            }

            if (alpha <= 0.02f) continue;

            var driftAngle = Mathf.Tau * _seconds / DriftSeconds
                             + i * 1.1f
                             + (Plumes[i].Cluster == 0 ? 0f : Mathf.Pi);
            var drift = new Vector2(Mathf.Cos(driftAngle), Mathf.Sin(driftAngle)) * DriftRadius;

            var breathe = 1f + BreatheAmplitude
                * Mathf.Sin(Mathf.Tau * _seconds / BreatheSeconds + i * 1.9f);

            var center = layerOrigin + (Plumes[i].Position + drift) * baseScale;
            var size = Plumes[i].Source.Size * baseScale * breathe;

            DrawSetTransform(center, _plumeAngles[i], Vector2.One);
            DrawTextureRect(texture,
                new Rect2(-size / 2f, size), false,
                new Color(PlumeTint.R, PlumeTint.G, PlumeTint.B, alpha));
            DrawSetTransform(Vector2.Zero, 0f, Vector2.One);
        }
    }

    private static ImageTexture? SliceToImage(Texture2D? atlas, Rect2 region)
    {
        if (atlas is null) return null;

        var image = atlas.GetImage();
        if (image is null) return null;
        if (image.GetFormat() != Image.Format.Rgba8) image.Convert(Image.Format.Rgba8);

        var clipped = region.Intersection(new Rect2(Vector2.Zero, atlas.GetSize()));
        if (clipped.Size.X <= 0 || clipped.Size.Y <= 0)
            clipped = new Rect2(Vector2.Zero, atlas.GetSize());

        var part = image.GetRegion(new Rect2I(
            (int)clipped.Position.X, (int)clipped.Position.Y,
            (int)clipped.Size.X, (int)clipped.Size.Y));
        var texture = ImageTexture.CreateFromImage(part);
        part.Dispose();
        return texture;
    }

    private static AtlasTexture Slice(Texture2D? atlas, Rect2 region)
    {
        if (atlas is null) return null!;

        var size = atlas.GetSize();
        var clipped = region.Intersection(new Rect2(Vector2.Zero, size));
        if (clipped.Size.X <= 0 || clipped.Size.Y <= 0) clipped = new Rect2(Vector2.Zero, size);

        return new AtlasTexture { Atlas = atlas, Region = clipped };
    }
}
