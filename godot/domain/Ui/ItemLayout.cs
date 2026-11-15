namespace Inotia4.Domain.Ui;

public readonly record struct UiRect(float X, float Y, float Width, float Height);

public readonly record struct SlotInset(float X, float Y, float Size);

public readonly record struct UiPoint(float X, float Y);

public readonly record struct MoneyLayout(
    float Stride, float CoinWidth, float CoinHeight, float DigitX, float DigitY,
    string Color, UiPoint Offset, UiRect PlateOffset);

public static class ItemLayout
{
    public const float SlotSize = 74f;

    public static readonly SlotInset SlotIcon = new(5f, 5f, 64f);

    public static readonly SlotInset SlotSelection = new(-5f, -5f, 84f);

    public const float SlotStride = 83f;

    public static readonly MoneyLayout Money = new(
        Stride: 103f, CoinWidth: 44f, CoinHeight: 40f, DigitX: 44f, DigitY: 6f,
        Color: "#ffffff",
        Offset: new UiPoint(34f, 365f),
        PlateOffset: new UiRect(34f, 368f, 323f, 34f));

    public static readonly float[] SideButtonOffsets = [0f, 70f, 139f, 208f, 277f, 347f];
}
