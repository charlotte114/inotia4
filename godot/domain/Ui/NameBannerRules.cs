using System;

namespace Inotia4.Domain.Ui;

public static class NameBannerRules
{
    public const float MaxWidth = 480f;

    public const float MinWidth = 120f;

    public const float Padding = 16f;

    public const float Height = 28f;

    public static float BackdropWidth(float textWidth) =>
        Math.Clamp(textWidth + Padding * 2f, MinWidth, MaxWidth);

    public static float BackdropLeft(float textWidth, float bannerWidth = MaxWidth) =>
        (bannerWidth - BackdropWidth(textWidth)) / 2f;

    public static float BackdropTop(float bannerHeight = 34f) =>
        (bannerHeight - Height) / 2f;
}
