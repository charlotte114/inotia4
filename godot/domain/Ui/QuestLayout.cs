namespace Inotia4.Domain.Ui;

/// <summary>
/// 任务页的版面常量：两栏各画一个只有描边、没有填充的框（用 <c>UI_DrawBorderRect</c>），
/// 列表与详情各是一个滚动区。
/// 坐标都相对 960×431 的内容块（(32,172) 起）。
/// </summary>
public static class QuestLayouts
{
    public const float ListFrameX = 14f;

    public const float ListFrameY = 0f;

    public const float ListFrameWidth = 556f;

    public const float ListFrameHeight = 431f;

    public const float DetailFrameX = 572f;

    public const float DetailFrameY = 0f;

    public const float DetailFrameWidth = 374f;

    public const float DetailFrameHeight = 431f;

    public const float ListX = 35f;

    public const float ListY = 27f;

    public const float ListWidth = 514f;

    public const float ListHeight = 376f;

    public const float DetailX = 594f;

    public const float DetailY = 20f;

    public const float DetailWidth = 330f;

    public const float DetailHeight = 390f;

    public const float RowStride = 64f;

    public const float RowHeight = 56f;

    public const float MainIconX = 5f;

    public const float MainIconY = 5f;

    public const float MainIconWidth = 63f;

    public const float MainIconHeight = 45f;

    public const float ReadyIconX = 459f;

    public const float ReadyIconY = 4f;

    public const float ReadyIconWidth = 54f;

    public const float ReadyIconHeight = 47f;

    public const int TitleSize = 24;

    public const float TitleTop = 13f;

    public const int DetailTextSize = 20;
}
