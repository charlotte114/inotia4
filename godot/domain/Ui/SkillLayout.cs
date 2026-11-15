namespace Inotia4.Domain.Ui;

public static class SkillLayouts
{
    public const float LeftFrameX = 85f;

    public const float FrameY = 0f;

    public const float LeftFrameWidth = 409f;

    public const float FrameHeight = 431f;

    public static readonly UiRect LeftFrame = new(85f, 0f, 409f, 431f);

    public static readonly UiRect RightTitle = new(537f, 0f, 409f, 53f);

    public static readonly UiRect RightFrame = new(537f, 53f, 409f, 378f);

    public const float PartyFrameX = PartyColumnLayout.FrameX;

    public const float PartyFrameTop = PartyColumnLayout.FrameTop;

    public const float PartyStride = PartyColumnLayout.Stride;

    public const float PartyFrameWidth = PartyColumnLayout.FrameWidth;

    public const float PartyFrameHeight = PartyColumnLayout.FrameHeight;

    public const float PartyHitHeight = PartyColumnLayout.HitHeight;

    public const float PartyIconX = PartyColumnLayout.IconX;

    public const float PartyIconY = PartyColumnLayout.IconY;

    public const float PartyIconWidth = PartyColumnLayout.IconWidth;

    public const float PartyIconHeight = PartyColumnLayout.IconHeight;

    public const int PartySlots = PartyColumnLayout.Slots;

    public const float TabX = 486f;

    public const float TabTop = 38f;

    public const float TabStride = 118f;

    public const float TabWidth = 46f;

    public const float TabHeight = 118f;

    public const float DisabledAlpha = .4f;

    public const float HeaderX = 561f;

    public const float HeaderY = 14f;

    public const float HeaderWidth = 350f;

    public const int HeaderSize = 19;

    public const float ScrollX = 116f;

    public const float ScrollY = 24f;

    public const float ScrollWidth = 364f;

    public const float ScrollHeight = 367f;

    /// <summary>选中框画在格内 (−14,−14)，滚动区要留出这一圈，否则首行首列会被切边。</summary>
    public const float ScrollBleed = 16f;

    public const float ColumnImageY = 0f;

    public const float ColumnImageWidth = 72f;

    public const float ColumnImageHeight = 17f;

    public const float CellColumnStride = 91f;

    public const float CellTop = 20f;

    public const float CellRowStride = 89f;

    public const float CellSize = 75f;

    public const float SlotRingInset = -9f;

    public const float SlotRingSize = 93f;

    public const float RankPlateX = 57f;

    public const float RankPlateY = 51f;

    public const float RankPlateWidth = 24f;

    public const float RankPlateHeight = 25f;

    public const float RankDigitX = 63f;

    public const float RankDigitY = 57f;

    public const float RankDigitWidth = 10f;

    public const float RankDigitHeight = 13f;

    public const float SelectionInset = -14f;

    public const float SelectionWidth = 106f;

    public const float SelectionHeight = 102f;

    public const float DescriptionX = 565f;

    public const float DescriptionY = 71f;

    public const float DescriptionWidth = 353f;

    public const float DescriptionHeight = 279f;

    public const float DescriptionAiHeight = 342f;

    public const float DescriptionLineHeight = 22f;

    public const int DescriptionSize = 22;

    public const int DescriptionLineSeparation = 5;

    public const float SeparatorStride = 8f;

    public const float SeparatorDash = 5f;

    public const float SeparatorGap = 5f;

    public const float SeparatorAdvance = 20f;

    public const float PreviewRowHeight = 52f;

    public const float PreviewValueOffset = 26f;

    public const float PreviewArrowOffsetX = -10.5f;

    public const float PreviewArrowOffsetY = -1f;

    public const float PreviewArrowWidth = 21f;

    public const float PreviewArrowHeight = 24f;

    public const int PreviewSize = 19;

    public const string ManaColor = "#0f7cf9";

    public const string TextColor = "#ffffff";

    public const string EffectValueColor = "#ff0000";

    public static readonly UiRect AiBandLeft = new(19f, 39f, 37f, 487f);

    public static readonly UiRect AiBandRight = new(201f, 39f, 37f, 397f);

    public const float AiCellColumnStride = 182f;

    public const float AiFrequencyOffsetX = 85f;

    public const float AiFrequencyOffsetY = 16f;

    public const float AiFrequencyWidth = 82f;

    public const float AiFrequencyHeight = 42f;

    public const float SettingsTitleX = 289f;

    public const float SettingsTitleY = 40f;

    public const int SettingsTitleSize = 24;

    public const float SettingsButtonX = 147f;

    public const float SettingsButtonTop = 110f;

    public const float SettingsButtonStride = 102f;

    public const float SettingsButtonWidth = 82f;

    public const float SettingsButtonHeight = 42f;

    public const float ResetButtonX = 161f;

    public const float ResetButtonY = 290f;

    public const float ResetButtonWidth = 257f;

    public const float ResetButtonHeight = 63f;

    public static readonly DescriptionMenu Menu = BagLayouts.Description.Menu;

    public const float MenuImageOffsetX = -12f;

    public const float MenuImageOffsetY = -9f;

    public const float MenuImageWidth = 149f;

    public const float MenuImageHeight = 75f;

    public static float ContentHeight(IEnumerable<SkillPosition> positions)
    {
        var height = CellTop + CellSize;
        foreach (var position in positions)
            height = Math.Max(height, CellTop + position.Row * CellRowStride + CellSize);
        return height;
    }
}

public sealed record SkillPosition(int Column, int Row)
{
    public static SkillPosition Parse(int column, int row) => new(column, row);
}
