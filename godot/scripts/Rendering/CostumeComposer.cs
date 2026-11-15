using System;
using System.Collections.Generic;
using Godot;
using Inotia4.Domain.Character;
using Inotia4.Domain.Content;

namespace Inotia4.Rendering;

public sealed record ComposedClip(Texture2D Texture, string TextureKey, ActorClip Clip);

public static class CostumeComposer
{
    public static ActorClip? Measure(WardrobeCatalog catalog, CostumeRecipe recipe, string poseId, string textureKey)
    {
        var fit = Fit(catalog, recipe, poseId);
        return fit is null ? null : BuildClip(fit, textureKey);
    }

    public static Texture2D? Render(WardrobeCatalog catalog, CostumeRecipe recipe, string poseId,
        string textureKey, Func<string, Image?> atlas)
    {
        var fit = Fit(catalog, recipe, poseId);
        if (fit is null) return null;
        return ImageTexture.CreateFromImage(Strip(catalog, recipe, fit, atlas));
    }

    public static ComposedClip? Compose(WardrobeCatalog catalog, CostumeRecipe recipe, string poseId,
        Func<string, Image?> atlas, string textureKey)
    {
        var fit = Fit(catalog, recipe, poseId);
        if (fit is null) return null;
        return new ComposedClip(ImageTexture.CreateFromImage(Strip(catalog, recipe, fit, atlas)),
            textureKey, BuildClip(fit, textureKey));
    }

    private sealed record PoseFit(int FrameWidth, int FrameHeight, int FrameCount, float Left, float Top,
        CostumePose Pose);

    private static CostumePiece? Source(WardrobeCatalog catalog, CostumeRecipe recipe, CostumePart part)
    {
        if (!recipe.Variants.TryGetValue(part.Part, out var variant)) return null;
        if (!catalog.Variants.TryGetValue(variant, out var pieces)) return null;
        if (part.Piece < 0 || part.Piece >= pieces.Count) return null;
        var key = pieces[part.Piece];
        if (key is null) return null;
        return catalog.Sprites.GetValueOrDefault(key);
    }

    private static PoseFit? Fit(WardrobeCatalog catalog, CostumeRecipe recipe, string poseId)
    {
        if (!catalog.Poses.TryGetValue(poseId, out var pose)) return null;

        var points = new List<(float X, float Y)>();
        foreach (var frame in pose.Frames)
        foreach (var part in frame)
        {
            var piece = Source(catalog, recipe, part);
            if (piece is null) continue;
            var angle = part.Angle * MathF.PI / 180f;
            var cos = MathF.Cos(angle);
            var sin = MathF.Sin(angle);
            foreach (var (px, py) in Corners(piece))
            {
                var u = (px - piece.AnchorX) * part.ScaleX;
                var v = (py - piece.AnchorY) * part.ScaleY;
                points.Add((part.X + u * cos - v * sin, part.Y + u * sin + v * cos));
            }
        }

        if (points.Count == 0) points.Add((0f, 0f));

        var left = MathF.Floor(Min(points, p => p.X)) - 1f;
        var top = MathF.Floor(Min(points, p => p.Y)) - 1f;
        var width = MathF.Ceiling(Max(points, p => p.X)) - left + 1f;
        var height = MathF.Ceiling(Max(points, p => p.Y)) - top + 1f;
        if (width <= 0f || height <= 0f) return null;

        var frameCount = pose.Frames.Count;
        if (frameCount <= 0) return null;

        return new PoseFit((int)width, (int)height, frameCount, left, top, pose);
    }

    private static Image Strip(WardrobeCatalog catalog, CostumeRecipe recipe, PoseFit fit,
        Func<string, Image?> atlas)
    {
        var frameWidth = fit.FrameWidth;
        var frameHeight = fit.FrameHeight;
        var frameCount = fit.FrameCount;
        var left = fit.Left;
        var top = fit.Top;
        var pose = fit.Pose;

        var strip = new byte[frameWidth * frameCount * frameHeight * 4];
        var atlases = new Dictionary<string, AtlasPixels>();

        for (var index = 0; index < frameCount; index++)
        {
            var frame = new byte[frameWidth * frameHeight * 4];
            foreach (var part in pose.Frames[index])
            {
                var piece = Source(catalog, recipe, part);
                if (piece is null) continue;

                if (!atlases.TryGetValue(piece.Texture, out var pixels))
                {
                    var image = atlas(piece.Texture);
                    if (image is null) continue;
                    pixels = new AtlasPixels(image.GetData(), image.GetWidth(), image.GetHeight());
                    atlases[piece.Texture] = pixels;
                }

                Stamp(frame, frameWidth, frameHeight, pixels, piece, part, left, top);
            }

            for (var y = 0; y < frameHeight; y++)
            {
                var source = y * frameWidth * 4;
                var destination = (y * frameWidth * frameCount + index * frameWidth) * 4;
                Array.Copy(frame, source, strip, destination, frameWidth * 4);
            }
        }

        return Image.CreateFromData(frameWidth * frameCount, frameHeight, false, Image.Format.Rgba8, strip);
    }

    /// <summary>
    /// 片段定义。锚点是「人物原点落在这一帧的哪个位置」：合成时整体挪了 (-left, -top)，
    /// 所以按帧宽高把它折成 0..1 的比例。
    /// </summary>
    private static ActorClip BuildClip(PoseFit fit, string textureKey) => new()
    {
        FrameWidth = fit.FrameWidth,
        FrameHeight = fit.FrameHeight,
        Frames = fit.FrameCount,
        OriginX = -fit.Left / fit.FrameWidth,
        OriginY = -fit.Top / fit.FrameHeight,
        FrameRate = fit.Pose.FrameRate,
        MirrorX = fit.Pose.MirrorX,
        Layers = [new ActorClipLayer { Key = textureKey, File = string.Empty, Blend = "normal" }],
    };

    private static (float X, float Y)[] Corners(CostumePiece piece) =>
    [
        (0f, 0f),
        (piece.Width, 0f),
        (0f, piece.Height),
        (piece.Width, piece.Height),
    ];

    private static float Min(List<(float X, float Y)> points, Func<(float X, float Y), float> pick)
    {
        var value = float.MaxValue;
        foreach (var point in points) value = MathF.Min(value, pick(point));
        return value;
    }

    private static float Max(List<(float X, float Y)> points, Func<(float X, float Y), float> pick)
    {
        var value = float.MinValue;
        foreach (var point in points) value = MathF.Max(value, pick(point));
        return value;
    }

    private static void Stamp(byte[] frame, int frameWidth, int frameHeight, AtlasPixels atlas,
        CostumePiece piece, CostumePart part, float left, float top)
    {
        var angle = part.Angle * MathF.PI / 180f;
        var cos = MathF.Cos(angle);
        var sin = MathF.Sin(angle);
        var scaleX = part.ScaleX;
        var scaleY = part.ScaleY;
        if (MathF.Abs(scaleX) < 0.0001f || MathF.Abs(scaleY) < 0.0001f) return;
        if (part.Alpha <= 0f) return;

        var minX = float.MaxValue;
        var minY = float.MaxValue;
        var maxX = float.MinValue;
        var maxY = float.MinValue;
        foreach (var (px, py) in Corners(piece))
        {
            var u = (px - piece.AnchorX) * scaleX;
            var v = (py - piece.AnchorY) * scaleY;
            var fx = part.X + u * cos - v * sin - left;
            var fy = part.Y + u * sin + v * cos - top;
            minX = MathF.Min(minX, fx);
            maxX = MathF.Max(maxX, fx);
            minY = MathF.Min(minY, fy);
            maxY = MathF.Max(maxY, fy);
        }

        var x0 = Math.Max(0, (int)MathF.Floor(minX));
        var x1 = Math.Min(frameWidth - 1, (int)MathF.Ceiling(maxX));
        var y0 = Math.Max(0, (int)MathF.Floor(minY));
        var y1 = Math.Min(frameHeight - 1, (int)MathF.Ceiling(maxY));
        if (x1 < x0 || y1 < y0) return;

        for (var dy = y0; dy <= y1; dy++)
        for (var dx = x0; dx <= x1; dx++)
        {
            var fx = dx + left - part.X;
            var fy = dy + top - part.Y;
            var u = (fx * cos + fy * sin) / scaleX;
            var v = (-fx * sin + fy * cos) / scaleY;
            var px = piece.AnchorX + u;
            var py = piece.AnchorY + v;

            var alpha = atlas.Sample(piece.X + px, piece.Y + py, out var r, out var g, out var b);
            if (alpha <= 0f) continue;
            alpha *= part.Alpha;
            if (alpha <= 0f) continue;

            var index = (dy * frameWidth + dx) * 4;
            var destinationAlpha = frame[index + 3] / 255f;
            var outAlpha = alpha + destinationAlpha * (1f - alpha);
            if (outAlpha <= 0f) continue;

            frame[index] = Mix(frame[index] / 255f, r, alpha, destinationAlpha, outAlpha);
            frame[index + 1] = Mix(frame[index + 1] / 255f, g, alpha, destinationAlpha, outAlpha);
            frame[index + 2] = Mix(frame[index + 2] / 255f, b, alpha, destinationAlpha, outAlpha);
            frame[index + 3] = (byte)Math.Clamp((int)MathF.Round(outAlpha * 255f), 0, 255);
        }
    }

    private static byte Mix(float destination, float source, float alpha, float destinationAlpha, float outAlpha)
    {
        var value = (source * alpha + destination * destinationAlpha * (1f - alpha)) / outAlpha;
        return (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
    }

    private sealed record AtlasPixels(byte[] Data, int Width, int Height)
    {
        /// <summary>
        /// 最近邻取样。
        ///
        /// 像素画素材不能用双线性：换装数据里的变换只有整数平移、±1 翻转与 90° 步进的旋转，
        /// 每个目标像素都恰好对得上一个源像素——这里若做双线性插值，正好落在两格中间，
        /// 等于给整张人物糊了一层 50/50 混色（实测待机首帧 244 个半透明像素，原始素材是 0 个）。
        ///
        /// 另外按最近邻取单个像素，也天然不会把图集透明区的色键绿 (33,146,33,0) 平均进边缘。
        /// </summary>
        public float Sample(float x, float y, out float r, out float g, out float b)
        {
            var sx = (int)MathF.Floor(x + 0.5f);
            var sy = (int)MathF.Floor(y + 0.5f);

            r = 0f;
            g = 0f;
            b = 0f;
            if (sx < 0 || sy < 0 || sx >= Width || sy >= Height) return 0f;

            var index = (sy * Width + sx) * 4;
            var alpha = Data[index + 3] / 255f;
            if (alpha <= 0f) return 0f;

            r = Data[index] / 255f;
            g = Data[index + 1] / 255f;
            b = Data[index + 2] / 255f;
            return alpha;
        }
    }
}
