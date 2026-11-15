using System;

namespace Inotia4.Domain.Ui;

/// <summary>
/// 选项面板的竖向约束：底边固定在 620，装不下时从列表里扣。
///
/// 原版的面板总是从底边往上长：底边固定在 y=620（960×640 画布），顶边最多长到 y=20，
/// 也就是最高 600。选项再多也不能顶出画面——放不下时从选项列表的视口里扣，
/// 最少留一行的高度，其余靠滚动看。
/// </summary>
public static class ChoiceGeometry
{
    public const float Bottom = 620f;

    public const float TopLimit = 20f;

    public const float MaxPanelHeight = Bottom - TopLimit;

    public static float PanelHeight(float contentHeight) => MathF.Min(MaxPanelHeight, contentHeight);

    public static float Top(float contentHeight) => Bottom - PanelHeight(contentHeight);

    public static float ListViewport(float available, float contentHeight, float optionHeight) =>
        MathF.Max(optionHeight, MathF.Min(available, contentHeight));

    public static float CenteredTop(float panelHeight, float screenHeight)
    {
        var height = PanelHeight(panelHeight);
        var centered = (screenHeight - height) / 2f;
        var ceiling = MathF.Max(TopLimit, Bottom - height);
        return Math.Clamp(centered, TopLimit, ceiling);
    }
}
