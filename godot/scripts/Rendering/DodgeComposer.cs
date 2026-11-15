using System;
using Godot;

namespace Inotia4.Rendering;

/// <summary>
/// 颜色减淡合成：把景物按「底图 × 遮罩」算出一张新贴图。
///
/// 这是阴影的绘制方式——景物不直接叠上去，而是把下方的地面提亮，
/// 所以站在阴影里的人会跟着变亮。Godot 没有现成的混合模式能表达它
/// （<c>color-dodge</c> 不在 CanvasItemMaterial 里），只能在 CPU 上算一次。
///
/// 只有极少数景物用到（全项目 3 处），所以一次性合成的成本可以接受。
/// </summary>
public static class DodgeComposer
{
    public static ImageTexture Compose(
        Image ground, Image prop, int left, int top, int width, int height, bool flip)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        var output = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);
        BlitClipped(ground, output, left, top);

        var background = output.GetData();
        var foreground = prop.GetData();
        var propWidth = prop.GetWidth();
        var propHeight = prop.GetHeight();

        var startX = flip ? width - propWidth : 0;

        for (var y = 0; y < Math.Min(propHeight, height); y++)
        {
            for (var x = 0; x < Math.Min(propWidth, width); x++)
            {
                var destinationX = startX + x;
                if (destinationX < 0 || destinationX >= width) continue;

                var sourceX = flip ? propWidth - 1 - x : x;
                var source = (y * propWidth + sourceX) * 4;
                if (foreground[source + 3] == 0) continue;   // 景物透明处保留背景

                var target = (y * width + destinationX) * 4;
                for (var channel = 0; channel < 3; channel++)
                    background[target + channel] = Dodge(background[target + channel], foreground[source + channel]);
            }
        }

        var composed = Image.CreateFromData(width, height, false, Image.Format.Rgba8, background);
        return ImageTexture.CreateFromImage(composed);
    }

    private static byte Dodge(byte background, byte foreground)
    {
        if (background == 0) return 0;
        if (foreground == 255) return 255;
        return (byte)Math.Min(255, Math.Floor(background * 255.0 / (255 - foreground)));
    }

    private static void BlitClipped(Image source, Image destination, int left, int top)
    {
        var x0 = Math.Max(0, left);
        var y0 = Math.Max(0, top);
        var x1 = Math.Min(source.GetWidth(), left + destination.GetWidth());
        var y1 = Math.Min(source.GetHeight(), top + destination.GetHeight());
        if (x1 <= x0 || y1 <= y0) return;   // 整块都在地图外

        var region = source.GetRegion(new Rect2I(x0, y0, x1 - x0, y1 - y0));
        destination.BlitRect(region, new Rect2I(0, 0, x1 - x0, y1 - y0), new Vector2I(x0 - left, y0 - top));
    }
}
