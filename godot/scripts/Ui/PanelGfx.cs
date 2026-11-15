using System;
using Godot;
using Inotia4.Domain.Ui;

namespace Inotia4.Ui;

/// <summary>
/// 菜单面板共用的绘制原语：贴图、纯色块、透明命中区、文字、图框、文本底板。
///
/// 背包页与商店页用的是同一批界面素材（<c>menu-panels.json</c> 的图框、<c>story-interface.json</c>
/// 的币图、<c>shops.json</c> 的分类图），所以这些「一张图 + 一个矩形命中区」的画法只留一份。
/// </summary>
internal static class PanelGfx
{
    public static Action<string>? UiSound { get; set; }

    public static void Clear(Node node)
    {
        foreach (var child in node.GetChildren())
        {
            node.RemoveChild(child);
            child.QueueFree();
        }
    }

    public static StyleBoxEmpty EmptyStyle() => new();

    public static TextureRect? Image(Func<string, Texture2D?> texture, string? path, float x, float y,
        float width, float height, Control parent, bool flipH = false, bool flipV = false)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (texture(path) is not { } source) return null;

        var size = width <= 0f || height <= 0f
            ? new Vector2(source.GetWidth(), source.GetHeight())
            : new Vector2(width, height);

        var rect = new TextureRect
        {
            Texture = source,
            Position = new Vector2(x, y),
            Size = size,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            FlipH = flipH,
            FlipV = flipV,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        parent.AddChild(rect);
        // 入树这一步会把 Size 按最小尺寸重算（纹理原尺寸），所以挂上去之后再定一次。
        rect.Position = new Vector2(x, y);
        rect.Size = size;
        return rect;
    }

    public static void Fill(float x, float y, float width, float height, Color color, Control parent,
        bool beforeContent = false)
    {
        var rect = new ColorRect
        {
            Position = new Vector2(x, y),
            Size = new Vector2(width, height),
            Color = color,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        parent.AddChild(rect);
        if (beforeContent) parent.MoveChild(rect, 0);
    }

    public static void Select() => UiSound?.Invoke("select");

    /// <summary>透明的命中区。</summary>
    /// <param name="hitRadius">给了就改成圆形命中区：
    /// 圆形按钮四角不该能点，操作区那六格的半径 40 也不等于外框一半 47。</param>
    public static Button Hit(float x, float y, float width, float height, Action action,
        Control parent, bool disabled = false, float? hitRadius = null)
    {
        var button = hitRadius is { } radius ? new CircleHitButton(radius) : new Button();
        button.Position = new Vector2(x, y);
        button.Size = new Vector2(width, height);
        button.Flat = true;
        button.Disabled = disabled;
        button.FocusMode = Control.FocusModeEnum.None;
        button.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
        foreach (var state in new[] { "normal", "hover", "pressed", "disabled", "focus" })
            button.AddThemeStyleboxOverride(state, EmptyStyle());
        button.Pressed += () =>
        {
            Select();
            action();
        };
        parent.AddChild(button);
        return button;
    }

    public static Label Text(string value, float x, float y, int size, Color color, Control parent)
    {
        var label = new Label
        {
            Text = value,
            Position = new Vector2(x, y),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color);
        parent.AddChild(label);
        return label;
    }

    public static Label Centered(string value, float x, float y, float width, int size, Color color,
        Control parent)
    {
        var label = new Label
        {
            Text = value,
            Position = new Vector2(x, y),
            Size = new Vector2(width, size + 6f),
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color);
        parent.AddChild(label);
        return label;
    }

    public static Label CenteredAt(string value, float cx, float cy, float width, int size, Color color,
        Control parent)
    {
        var label = new Label
        {
            Text = value,
            Position = new Vector2(cx - width / 2f, cy - (size + 6f) / 2f),
            Size = new Vector2(width, size + 6f),
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color);
        parent.AddChild(label);
        return label;
    }

    /// <summary>
    /// 图框：四边各 24 像素宽、四角 24 见方（原生 <c>UI_DrawBorderRect</c>）。只画框不铺底，
    /// 面板里看到的是黑色遮罩下的世界画面，所以调用方不要在这之上再刷一层填充色。
    /// </summary>
    public static void Border(Func<string, Texture2D?> texture, Func<string, string?> asset,
        float x, float y, float width, float height, Control parent)
    {
        Image(texture, asset("edge-horizontal"), x + 24f, y, width - 48f, 24f, parent);
        Image(texture, asset("edge-horizontal"), x + 24f, y + height - 24f, width - 48f, 24f, parent, flipV: true);
        Image(texture, asset("edge-vertical"), x, y + 24f, 24f, height - 48f, parent);
        Image(texture, asset("edge-vertical"), x + width - 24f, y + 24f, 24f, height - 48f, parent, flipH: true);
        Image(texture, asset("corner-nw"), x, y, 24f, 24f, parent);
        Image(texture, asset("corner-ne"), x + width - 24f, y, 24f, 24f, parent);
        Image(texture, asset("corner-sw"), x, y + height - 24f, 24f, 24f, parent);
        Image(texture, asset("corner-se"), x + width - 24f, y + height - 24f, 24f, 24f, parent);
    }

    public static void TextPlate(Func<string, Texture2D?> texture, Func<string, string?> asset,
        float x, float y, float width, Control parent)
    {
        Image(texture, asset("text-plate-left"), x, y, 9f, 34f, parent);
        Image(texture, asset("text-plate-right"), x + width - 9f, y, 9f, 34f, parent);
        Fill(x + 9f, y, width - 18f, 34f, new Color(0x69 / 255f, 0x50 / 255f, 0x31 / 255f, .8f), parent);
    }

    public static Label Right(string value, float rightX, float y, int size, Color color, Control parent,
        float boxWidth = 220f)
    {
        var label = Text(value, rightX - boxWidth, y, size, color, parent);
        label.Size = new Vector2(boxWidth, 0f);
        label.HorizontalAlignment = HorizontalAlignment.Right;
        return label;
    }

    /// <summary>
    /// 压暗的圆角带（原生 <c>UI_FillRoundRect</c>）：四角是四块 15×15 圆角件，
    /// 中间用 RGB(111,111,111) 铺满九宫格的五条；强度同时是填充与四角的不透明度。
    /// 人物属性行与队伍页的条带用强度 50。
    /// </summary>
    public static void RoundBand(Func<string, Texture2D?> texture, Func<string, string?> asset,
        float x, float y, float width, float height, int strength, Control parent)
    {
        var alpha = strength / 100f;
        var color = new Color(111 / 255f, 111 / 255f, 111 / 255f, alpha);

        void Corner(string key, float cx, float cy)
        {
            if (Image(texture, asset(key), cx, cy, 15f, 15f, parent) is { } corner)
                corner.Modulate = new Color(1f, 1f, 1f, alpha);
        }

        Corner("round-nw", x, y);
        Corner("round-ne", x + width - 15f, y);
        Corner("round-sw", x, y + height - 15f);
        Corner("round-se", x + width - 15f, y + height - 15f);

        foreach (var (left, top, bandWidth, bandHeight) in new (float, float, float, float)[]
                 {
                     (x + 15f, y, width - 30f, 15f),
                     (x + 15f, y + height - 15f, width - 30f, 15f),
                     (x, y + 15f, 15f, height - 30f),
                     (x + width - 15f, y + 15f, 15f, height - 30f),
                     (x + 15f, y + 15f, width - 30f, height - 30f),
                 })
            Fill(left, top, bandWidth, bandHeight, color, parent);
    }

    public static void SolidBorder(Func<string, Texture2D?> texture, Func<string, string?> asset,
        float x, float y, float width, float height, Control parent)
    {
        var span = width - 48f;
        Image(texture, asset("solid-nw"), x, y, 24f, 27f, parent);
        Image(texture, asset("solid-ne"), x + width - 24f, y, 24f, 27f, parent);
        Image(texture, asset("solid-sw"), x, y + height - 26f, 24f, 26f, parent);
        Image(texture, asset("solid-se"), x + width - 24f, y + height - 26f, 24f, 26f, parent);
        Image(texture, asset("solid-edge"), x + 24f, y, span, 27f, parent);
        Image(texture, asset("solid-edge"), x + 24f, y + height - 26f, span, 26f, parent, flipV: true);
    }

    public static Control? ButtonSkin(Func<string, Texture2D?> texture, float width, float height, Button button,
        bool selected = false)
    {
        Skin(texture, CommonButton.Image, width, height, button);

        // 按下层走叠加混合（源 alpha、目标加一）。Godot 的材质不会
        // 从父节点继承，所以必须把材质直接放到画皮节点上，挂在空壳上不生效。
        var addBlend = new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add };
        var pressed = new Control
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = selected,
        };
        button.AddChild(pressed);
        Skin(texture, CommonButton.Pressed, width, height, pressed, addBlend);
        button.ButtonDown += () => pressed.Visible = true;
        button.ButtonUp += () => pressed.Visible = selected;
        button.MouseExited += () => pressed.Visible = selected;
        return pressed;
    }

    private static void Skin(Func<string, Texture2D?> texture, string path, float width, float height,
        Control parent, Material? material = null) =>
        Patch(texture, path, width, height, parent, CommonButton.Cap, material);

    /// <summary>
    /// 按给定「两端不拉伸的像素宽度」铺一张九宫格底，返回铺好的补丁节点（贴图缺失时是 null）。
    /// 融合器菜单行自带一枚胶囊（<see cref="CraftingMenuRow"/>），两端要比通用按钮皮窄得多，
    /// 所以把 cap 放出来单独给。
    /// </summary>
    public static NinePatchRect? Patch(Func<string, Texture2D?> texture, string path, float width,
        float height, Control parent, float cap, Material? material = null)
    {
        if (texture(path) is not { } source) return null;

        var sourceHeight = source.GetHeight();
        var scale = Math.Min(height / sourceHeight, width / (cap * 2f));
        var span = width / scale;
        var top = (height - sourceHeight * scale) / 2f;

        var root = new Control
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Position = new Vector2(0f, top),
            Scale = new Vector2(scale, scale),
            Size = new Vector2(span, sourceHeight),
        };
        parent.AddChild(root);

        var patch = new NinePatchRect
        {
            Texture = source,
            Position = Vector2.Zero,
            Size = new Vector2(span, sourceHeight),
            PatchMarginLeft = (int)cap,
            PatchMarginRight = (int)cap,
            AxisStretchHorizontal = NinePatchRect.AxisStretchMode.Stretch,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Material = material,
        };
        root.AddChild(patch);
        return patch;
    }

    /// <summary>
    /// 弹层的底与图框（数量输入框与消息弹窗共用同一种画法）：
    /// 先画四角与四条边，再铺一块内缩 35 的不透明黑底。坐标相对框左上角，翅角会画到框外，
    /// 所以调用方给的容器要按框原点对齐。
    /// </summary>
    public static void PopupPanel(Func<string, Texture2D?> texture, Func<string, string?> asset,
        float width, float height, Control parent)
    {
        const float size = PopupFrame.CornerSize;
        const float outer = PopupFrame.CornerOuter;
        const float inner = PopupFrame.Inset;

        Image(texture, asset("popup-corner-nw"), -outer, -outer, size, size, parent);
        Image(texture, asset("popup-corner-ne"), width - inner, -outer, size, size, parent);
        Image(texture, asset("popup-corner-sw"), -outer, height - inner, size, size, parent);
        Image(texture, asset("popup-corner-se"), width - inner, height - inner, size, size, parent);

        var span = width - inner * 2;
        var reach = height - inner * 2;
        Image(texture, asset("popup-edge-horizontal"), inner, PopupFrame.HorizontalTop, span,
            PopupFrame.HorizontalHeight, parent);
        Image(texture, asset("popup-edge-horizontal"), inner,
            height + PopupFrame.HorizontalBottom - PopupFrame.HorizontalHeight, span,
            PopupFrame.HorizontalHeight, parent, flipV: true);
        Image(texture, asset("popup-edge-vertical"), PopupFrame.VerticalLeft, PopupFrame.VerticalTop,
            PopupFrame.VerticalWidth, reach, parent);
        Image(texture, asset("popup-edge-vertical"),
            width + PopupFrame.VerticalRight - PopupFrame.VerticalWidth, PopupFrame.VerticalTop,
            PopupFrame.VerticalWidth, reach, parent, flipH: true);

        Fill(inner, inner, span, reach, new Color(0f, 0f, 0f, 1f), parent);
    }

    /// <summary>
    /// 原生提示：居中的黑色横带、上下各排一排 240 宽的花纹、底部一枚原生按钮。
    /// 横带横向铺满整屏（这里就是 1024 宽的设计画布），纵向是文字高度加 150。
    /// 按钮图自带「确认／是／否」的字，所以不再叠文字。
    /// </summary>
    public static void NativeMessage(Func<string, Texture2D?> texture, Func<string, string?> asset,
        string message, Action accept, Action? cancel, Control parent)
    {
        var layout = CraftingLayouts.Message;

        // 半透明米色整屏压一层（0xaa966e、13%）。
        Fill(0f, 0f, layout.Width, layout.Height,
            new Color(0xaa / 255f, 0x96 / 255f, 0x6e / 255f, .13f), parent);

        var textHeight = MeasureText(parent, message, layout.TextWidth, layout.TextSize);
        var bandHeight = textHeight + layout.BandExtra;
        var top = (layout.Height - bandHeight) / 2f;

        Fill(0f, top, layout.Width, bandHeight, new Color(0f, 0f, 0f, 1f), parent);

        for (var x = 0f; x < layout.Width; x += layout.EdgeWidth)
        {
            Image(texture, asset("message-edge"), x, top - layout.EdgeOffset, layout.EdgeWidth,
                layout.EdgeHeight, parent);
            Image(texture, asset("message-edge"), x,
                top + bandHeight + layout.EdgeOffset - layout.EdgeHeight, layout.EdgeWidth,
                layout.EdgeHeight, parent, flipV: true);
        }

        var label = new Label
        {
            Text = message,
            Position = new Vector2(layout.Center - layout.TextWidth / 2f, top + 30f),
            Size = new Vector2(layout.TextWidth, textHeight),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        label.AddThemeFontSizeOverride("font_size", layout.TextSize);
        label.AddThemeColorOverride("font_color", new Color("#ffffff"));
        parent.AddChild(label);

        var buttonY = top + bandHeight - layout.ButtonFromBottom;
        if (cancel is not null)
        {
            NativeImageButton(texture, asset, "message-yes", layout.Center - layout.YesLeft, buttonY,
                layout.ButtonWidth, layout.ButtonHeight, accept, parent);
            NativeImageButton(texture, asset, "message-no", layout.Center - layout.NoLeft, buttonY,
                layout.ButtonWidth, layout.ButtonHeight, cancel, parent);
        }
        else
        {
            NativeImageButton(texture, asset, "message-ok", layout.Center - layout.OkLeft, buttonY,
                layout.ButtonWidth, layout.ButtonHeight, accept, parent);
        }
    }

    /// <summary>
    /// 原生按钮：命中区给多大就是多大，图是原图、按原始尺寸居中盖上去，
    /// 按下时叠同一张图的 <c>-pressed</c> 版（叠加混合，所以亮而不是换一张）。
    /// </summary>
    public static void NativeImageButton(Func<string, Texture2D?> texture, Func<string, string?> asset,
        string key, float x, float y, float width, float height, Action action, Control parent)
    {
        var button = Hit(x, y, width, height, action, parent);
        NativeFace(texture, asset(key), button, width, height);

        var pressed = new Control
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
            Material = new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add },
        };
        button.AddChild(pressed);
        NativeFace(texture, asset($"{key}-pressed"), pressed, width, height);
        button.ButtonDown += () => pressed.Visible = true;
        button.ButtonUp += () => pressed.Visible = false;
        button.MouseExited += () => pressed.Visible = false;
    }

    private static void NativeFace(Func<string, Texture2D?> texture, string? path, Control parent,
        float width, float height)
    {
        if (string.IsNullOrEmpty(path) || texture(path) is not { } source) return;
        Image(texture, path, MathF.Floor((width - source.GetWidth()) / 2f),
            MathF.Floor((height - source.GetHeight()) / 2f), source.GetWidth(), source.GetHeight(), parent);
    }

    private static float MeasureText(Control parent, string text, float width, int fontSize)
    {
        var probe = new Label { Text = string.Empty, MouseFilter = Control.MouseFilterEnum.Ignore };
        probe.AddThemeFontSizeOverride("font_size", fontSize);
        parent.AddChild(probe);
        var font = probe.GetThemeFont("font");
        var size = font.GetMultilineStringSize(text, HorizontalAlignment.Center, width, fontSize);
        probe.QueueFree();
        return MathF.Ceiling(size.Y);
    }
}
