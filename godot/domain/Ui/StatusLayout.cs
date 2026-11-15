namespace Inotia4.Domain.Ui;

public static class StatusLayouts
{
    /// <summary>左框（`UI_DrawBorderRect`）。</summary>
    public static readonly UiRect LeftFrame = new(85f, 0f, 409f, 431f);

    public static readonly UiRect Portrait = new(95f, 10f, 252f, 271f);

    public const float PortraitSize = 41f;

    /// <summary>四角相对内容块的落点：`Scene_Draw_POPUP_SC_CHARACTER_INFO` 的 (83,−2)、(455,−2)、(83,392)、(455,392)。</summary>
    public static readonly (string Key, float X, float Y)[] Corners =
    [
        ("character-nw", 83f, -2f),
        ("character-ne", 455f, -2f),
        ("character-sw", 83f, 392f),
        ("character-se", 455f, 392f),
    ];

    public const float TextRight = 458f;

    public const float LevelY = 95f;

    public const float ClassY = 120f;

    public const float NameY = 152f;

    public const int TextSize = 20;

    public const int NameSize = 26;

    public const float GaugeTop = 258f;

    public const float GaugeStride = 44f;

    public static readonly UiRect GaugePlate = new(168f, 15f, 243f, 37f);

    public const float GaugeLabelX = 118f;

    public const float GaugeLabelY = 21f;

    public const int GaugeLabelSize = 19;

    public const float GaugeValueCenterX = 290f;

    public const float GaugeValueWidth = 223f;

    public const int GaugeValueSize = 17;

    public const float GaugePercentRight = 455f;

    public static readonly UiRect GaugeFill = new(178f, 27f, 223f, 13f);

    public const float GaugeCapWidth = 7f;

    public const float AttributeX = 496f;

    public const float AttributeWideWidth = 409f;

    public const float AttributeNarrowWidth = 224f;

    public const float AttributeHeaderHeight = 53f;

    public const float AttributeBodyY = 49f;

    public const float AttributeBodyHeight = 382f;

    public const float AttributeStride = 69f;

    public const float AttributeAdjustStride = 64f;

    public static readonly UiRect AttributeBand = new(513f, 75f, 190f, 48f);

    public static readonly UiRect AttributeAdjustBand = new(519f, 75f, 364f, 48f);

    public const int AttributeBandStrength = 50;

    public const float AttributeNameX = 525f;

    public const float AttributeAdjustNameX = 531f;

    public const float AttributeValueRight = 684f;

    public const float AttributeAdjustValueRight = 751f;

    public const float AttributeRowTextOffset = 85f;

    public const float AttributeRowValueOffset = 84f;

    public const int AttributeTextSize = 23;

    public const float AttributeAddedX = 781f;

    public const float StepMinusX = 630f;

    public const float StepPlusX = 822f;

    public const float StepOffsetY = 70f;

    public const float StepWidth = 51f;

    public const float StepHeight = 58f;

    public const float AdjustButtonWideX = 846f;

    public const float AdjustButtonNarrowX = 661f;

    public const float AdjustButtonSize = 67f;

    public static readonly UiRect ResetButton = new(571f, 376f, 257f, 48f);

    public static readonly UiRect CombatFrame = new(722f, 0f, 224f, 431f);

    public const float CombatLabelX = 738f;

    public const float CombatValueRight = 928f;

    public const float CombatTop = 24f;

    public const float CombatStride = 35f;

    public const int CombatSize = 19;

    public const float PointsX = 522f;

    public const float PointsAdjustX = 534f;

    public const float PointsY = 14f;

    public const int PointsSize = 20;

    public const float PointsErrorY = 437f;

    public const double HoldDelaySeconds = .4;

    public const double HoldRepeatSeconds = .09;
}
