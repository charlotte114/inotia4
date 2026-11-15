using Godot;

namespace Inotia4.Ui;

/// <summary>
/// 加载页：全屏底幕、一行说明文字、560 像素宽的金色进度条，可选的「重新加载」与
/// 「返回主菜单」按钮。
/// 坐标与配色照原版的设计稿（文字 (480,476)、进度条 (200,531)、
/// 轨道 <c>0xeee2cc</c> 30%、填充 <c>0xe2c88d</c>）。
///
/// <para>
/// <b>观感是本项目表现层自己的主意，原版没有这些：</b>暖色径向底幕（代替纯黑）、标题
/// 「艾诺迪亚」与菱形分隔线、带描边的圆角轨道、填充顶部的高光、条上循环扫过的流光、
/// 右下角百分比。全部用代码画，不依赖任何图片素材。想退回「纯黑 + 一条细线」，
/// 删掉 <see cref="BuildBackdrop"/>、<see cref="BuildOrnament"/>、<see cref="_sheen"/>
/// 与 <see cref="_percent"/> 四处即可，其余逻辑不受影响。
/// </para>
/// </summary>
public sealed partial class LoadingOverlay : Control
{
    private const float TrackWidth = 560f;

    private const float TrackHeight = 14f;

    private const float FillInset = 2f;

    private const float FillHeight = TrackHeight - FillInset * 2f;

    private const float ProgressEaseSharpness = 10f;

    private const float ProgressSnapThreshold = 0.002f;

    private const float FadeOutSeconds = 0.2f;

    private const float IntroSeconds = 0.28f;

    private const float GlintSeconds = 1.9f;

    private const float GlintGapSeconds = 0.45f;

    private readonly Label _label;
    private readonly Label _percent;
    private readonly Panel _track;
    private readonly Panel _fill;
    private readonly ColorRect _sheen;
    private readonly ColorRect _gloss;

    private readonly Control _stage;

    private readonly ColorRect _edge;

    private Tween? _fade;

    private Tween? _intro;

    private Tween? _glint;

    private float _target;

    private float _displayed;

    public LoadingOverlay()
    {
        Name = "Loading";
        MouseFilter = MouseFilterEnum.Ignore;
        Visible = false;
        SetProcess(false);
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        AddChild(BuildBackdrop());
        // 内容用的仍是 960×640 的绝对坐标（标题 480、条 200..760），所以整块放进居中的舞台：
        // 手机横向比 3:2 宽时视口会变宽（expand），不居中就会整块贴左。
        // 底幕在舞台外面，仍然是铺满全屏的。
        _stage = new Control { Name = "Stage", MouseFilter = MouseFilterEnum.Ignore };
        DesignStage.Center(_stage);
        AddChild(_stage);

        _stage.AddChild(BuildGlow(new Vector2(620f, 280f), new Vector2(480f, 268f), new Color(0.95f, 0.82f, 0.55f, 0.10f)));
        _stage.AddChild(BuildOrnament());

        var rule = new ColorRect
        {
            Position = new Vector2(200f, 442f),
            Size = new Vector2(TrackWidth, 1f),
            Color = new Color(0.78f, 0.65f, 0.40f, 0.15f),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _stage.AddChild(rule);

        _label = new Label
        {
            Position = new Vector2(200f, 458f),
            Size = new Vector2(440f, 26f),
            HorizontalAlignment = HorizontalAlignment.Left,
            MouseFilter = MouseFilterEnum.Ignore,
            Text = string.Empty,
        };
        _label.AddThemeFontSizeOverride("font_size", 17);
        _label.AddThemeColorOverride("font_color", new Color("c9b58b"));
        // 说明文字压在暗底上不该发散：一点深色描边把它从背景里割开。
        _label.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.75f));
        _label.AddThemeConstantOverride("outline_size", 4);
        _stage.AddChild(_label);

        _track = BuildBarFrame();
        _stage.AddChild(_track);

        _fill = BuildBarFill();
        _gloss = new ColorRect
        {
            Position = Vector2.Zero,
            Size = new Vector2(0f, 3f),
            Color = new Color(1f, 0.96f, 0.82f, 0.5f),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _fill.AddChild(_gloss);
        _edge = new ColorRect
        {
            Position = new Vector2(0f, 0f),
            Size = new Vector2(3f, FillHeight),
            Color = new Color(1f, 0.95f, 0.78f, 0.62f),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _fill.AddChild(_edge);
        _sheen = new ColorRect
        {
            Position = new Vector2(-80f, 0f),
            Size = new Vector2(64f, FillHeight),
            Color = new Color(1f, 0.98f, 0.9f, 0.28f),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _fill.AddChild(_sheen);
        _track.AddChild(_fill);

        _percent = new Label
        {
            Position = new Vector2(560f, 458f),
            Size = new Vector2(200f, 26f),
            HorizontalAlignment = HorizontalAlignment.Right,
            MouseFilter = MouseFilterEnum.Ignore,
            Text = "0%",
        };
        _percent.AddThemeFontSizeOverride("font_size", 15);
        _percent.AddThemeColorOverride("font_color", new Color("a08a5f"));
        _stage.AddChild(_percent);
    }

    /// <summary>
    /// 底幕：正上方偏一点的位置往外扩散的暖色径向渐变，边缘落回纯黑。
    /// 比一整块 #000 耐看，又不至于抢走进度条的注意力。
    /// </summary>
    private static TextureRect BuildBackdrop()
    {
        var gradient = new Gradient
        {
            Offsets = new[] { 0f, 0.55f, 1f },
            Colors = new[]
            {
                new Color("1b1509"),
                new Color("0a0704"),
                new Color(0f, 0f, 0f),
            },
        };

        var texture = new GradientTexture2D
        {
            Gradient = gradient,
            Width = 480,
            Height = 320,
            Fill = GradientTexture2D.FillEnum.Radial,
            FillFrom = new Vector2(0.5f, 0.40f),
            FillTo = new Vector2(0.5f, 1.18f),
        };

        var rect = new TextureRect
        {
            Texture = texture,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        rect.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        return rect;
    }

    private static TextureRect BuildGlow(Vector2 size, Vector2 center, Color tint)
    {
        var gradient = new Gradient
        {
            Offsets = new[] { 0f, 0.45f, 1f },
            Colors = new[]
            {
                tint,
                new Color(tint.R, tint.G, tint.B, tint.A * 0.45f),
                new Color(tint.R, tint.G, tint.B, 0f),
            },
        };

        var texture = new GradientTexture2D
        {
            Gradient = gradient,
            Width = 320,
            Height = 160,
            Fill = GradientTexture2D.FillEnum.Radial,
            FillFrom = new Vector2(0.5f, 0.5f),
            FillTo = new Vector2(0.5f, 1f),
        };

        var rect = new TextureRect
        {
            Texture = texture,
            Position = center - size * 0.5f,
            Size = size,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        return rect;
    }

    private static Control BuildOrnament()
    {
        var root = new Control { MouseFilter = MouseFilterEnum.Ignore };
        root.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        var title = new Label
        {
            Text = "艾诺迪亚",
            Position = new Vector2(0f, 226f),
            Size = new Vector2(960f, 64f),
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        title.AddThemeFontSizeOverride("font_size", 46);
        title.AddThemeColorOverride("font_color", new Color("eedcb4"));
        title.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.6f));
        title.AddThemeConstantOverride("shadow_offset_y", 3);
        title.AddThemeConstantOverride("shadow_outline_size", 2);
        root.AddChild(title);

        var gold = new Color(0.80f, 0.67f, 0.42f);
        var left = new ColorRect
        {
            Position = new Vector2(480f - 78f, 302f),
            Size = new Vector2(58f, 1f),
            Color = new Color(gold.R, gold.G, gold.B, 0.42f),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        root.AddChild(left);

        var right = new ColorRect
        {
            Position = new Vector2(480f + 20f, 302f),
            Size = new Vector2(58f, 1f),
            Color = new Color(gold.R, gold.G, gold.B, 0.42f),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        root.AddChild(right);

        const float diamond = 7f;
        var mark = new ColorRect
        {
            Position = new Vector2(480f - diamond * 0.5f, 302.5f - diamond * 0.5f),
            Size = new Vector2(diamond, diamond),
            Color = new Color(gold.R, gold.G, gold.B, 0.8f),
            PivotOffset = new Vector2(diamond * 0.5f, diamond * 0.5f),
            Rotation = Mathf.Pi / 4f,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        root.AddChild(mark);

        return root;
    }

    private static Panel BuildBarFrame()
    {
        var panel = new Panel
        {
            Position = new Vector2(200f, 492f),
            Size = new Vector2(TrackWidth, TrackHeight),
            MouseFilter = MouseFilterEnum.Ignore,
        };

        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.04f, 0.02f, 0.92f),
            BorderColor = new Color(0.42f, 0.35f, 0.20f, 0.85f),
            CornerRadiusTopLeft = 3,
            CornerRadiusTopRight = 3,
            CornerRadiusBottomLeft = 3,
            CornerRadiusBottomRight = 3,
        };
        style.SetBorderWidthAll(1);
        panel.AddThemeStyleboxOverride("panel", style);
        return panel;
    }

    private static Panel BuildBarFill()
    {
        var panel = new Panel
        {
            Position = new Vector2(FillInset, FillInset),
            Size = new Vector2(0f, FillHeight),
            ClipContents = true,
            MouseFilter = MouseFilterEnum.Ignore,
        };

        var style = new StyleBoxFlat
        {
            BgColor = new Color("e2c88d"),
            ShadowColor = new Color(0.89f, 0.78f, 0.55f, 0.28f),
            ShadowSize = 8,
            CornerRadiusTopLeft = 2,
            CornerRadiusTopRight = 2,
            CornerRadiusBottomLeft = 2,
            CornerRadiusBottomRight = 2,
        };
        panel.AddThemeStyleboxOverride("panel", style);
        return panel;
    }

    public void Show(string label, float progress = 0f)
    {
        // 还在淡出时又被叫出来（切图、--loadingshot）：先把上一段淡出掐掉，否则刚亮出来又被它藏回去。
        _fade?.Kill();
        _fade = null;
        _intro?.Kill();
        _intro = null;
        _label.Text = label;

        if (!Visible)
        {
            // 刚亮出来时直接落到目标值：开局本来就该是 0.5（原版从半条起画），
            // 不该先画一条空轨道再从 0 爬上来。
            _displayed = Mathf.Clamp(progress, 0f, 1f);
            _target = _displayed;
            ApplyFillWidth();
            Visible = true;
            Modulate = new Color(1f, 1f, 1f, 0.35f);
            _intro = CreateTween();
            _intro.TweenProperty(this, "modulate:a", 1f, IntroSeconds);
        }
        else
        {
            if (Modulate.A < 1f) Modulate = Colors.White;
            SetProgress(progress);
        }

        StartGlint();
    }

    public void SetProgress(float progress)
    {
        _target = Mathf.Clamp(progress, 0f, 1f);
        SetProcess(true);
    }

    public override void _Process(double delta)
    {
        var factor = 1f - Mathf.Exp(-(float)delta * ProgressEaseSharpness);
        _displayed = Mathf.Lerp(_displayed, _target, factor);
        if (Mathf.Abs(_target - _displayed) <= ProgressSnapThreshold) _displayed = _target;

        ApplyFillWidth();
        if (Mathf.IsEqualApprox(_displayed, _target)) SetProcess(false);
    }

    public bool Settling => !Mathf.IsEqualApprox(_displayed, _target);

    /// <summary>收起指令已下（淡出中，或已经不可见）；自检用它核对「加载结束后确实收了」。</summary>
    public bool Dismissing => _fade is not null || !Visible;

    /// <summary>
    /// 填充宽度按进度走，位置不动（原版是 (200,531) 处一条 560×5 的矩形，宽度随进度缩放）。
    /// 顶部高光的宽度跟着一起变，否则填充短的时候高光会露到轨道外面。
    /// </summary>
    private void ApplyFillWidth()
    {
        var width = InnerWidth * _displayed;
        _fill.Size = new Vector2(width, FillHeight);
        _gloss.Size = new Vector2(width, 3f);
        _edge.Position = new Vector2(Mathf.Max(width - 3f, 0f), 0f);

        _percent.Text = $"{Mathf.RoundToInt(_displayed * 100f)}%";
        _percent.Modulate = new Color(1f, 1f, 1f, _displayed > 0.004f ? 1f : 0f);
    }

    private static float InnerWidth => TrackWidth - FillInset * 2f;

    /// <summary>
    /// 流光：一条浅色竖条从轨道左端扫到右端，靠 <see cref="_fill"/> 的裁剪限制在已填充范围内。
    /// 循环补间末尾把位置重置回起点，否则第二轮会从右端原地不动。
    /// </summary>
    private void StartGlint()
    {
        if (_glint is not null) return;

        _glint = CreateTween().SetLoops();
        _glint.TweenProperty(_sheen, "position:x", TrackWidth, GlintSeconds)
            .SetTrans(Tween.TransitionType.Sine)
            .SetEase(Tween.EaseType.InOut);
        _glint.TweenCallback(Callable.From(() => _sheen.Position = new Vector2(-80f, 0f)));
        _glint.TweenInterval(GlintGapSeconds);
    }

    public string Label => _label.Text;

    public float Progress => _fill.Size.X / InnerWidth;

    /// <summary>
    /// 收起加载页：0.2 秒淡出，淡完才真正不可见。
    ///
    /// 淡出期间进度条还在往 1 滑（<see cref="_Process"/> 没停），所以最后那 3% 是画完再消失的，
    /// 不是像以前那样停在 97% 直接闪没。这一层全程忽略鼠标（<see cref="MouseFilterEnum.Ignore"/>），
    /// 淡出时玩家已经能操作角色，不用担心吃输入。
    /// </summary>
    public void HideOverlay()
    {
        _fade?.Kill();
        _intro?.Kill();
        _intro = null;
        Modulate = Colors.White;
        _fade = CreateTween();
        _fade.TweenProperty(this, "modulate:a", 0f, FadeOutSeconds);
        _fade.TweenCallback(Callable.From(CompleteFade));
    }

    public void CompleteFade()
    {
        _fade?.Kill();
        _fade = null;
        _intro?.Kill();
        _intro = null;
        _glint?.Kill();
        _glint = null;
        Visible = false;
        Modulate = Colors.White;
    }
}
