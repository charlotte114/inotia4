using Inotia4.Domain.Character;

namespace Inotia4.Domain.Ui;

public readonly record struct DescriptionPanelLayout(
    float Y, float Width, float Height,
    float Inset, uint Background, float BackgroundAlpha,
    DescriptionTitle Title, UiRect Scroll, UiRect Close, DescriptionMenu Menu)
{
    public float LeftX { get; init; }

    public float RightX { get; init; }

    public DescriptionPanelLayout WithWidth(float width)
    {
        var shrink = Width - width;
        return this with
        {
            Width = width,
            Scroll = Scroll with { Width = Scroll.Width - shrink },
            Close = Close with { X = Close.X - shrink },
            Menu = Menu with { Width = Menu.Width - shrink },
        };
    }
}

public readonly record struct DescriptionTitle(float X, float Y, float Right, float Size);

public readonly record struct DescriptionMenu(
    float X, float Y, float Width, float Height,
    float ButtonWidth, float ButtonHeight, float ButtonStride,
    IReadOnlyDictionary<int, float> Offsets);

public readonly record struct SideButtonColumn(
    float X, float Y, float Width, float Height, float[] Offsets);

public readonly record struct LevelRow(float X, float Y, float Width);

public readonly record struct BagGrid(
    float X, float Y, float Width, float Height, float Stride, int Columns);

public readonly record struct MoneySlot(
    float X, float Y, UiRect Plate, float Stride, float CoinWidth, float CoinHeight,
    UiPoint Digit, string Color);

public readonly record struct CellBadge(float X, float Y, string Color);

public readonly record struct BagLayout(
    UiRect LeftFrame, UiRect Preview, LevelRow Level, UiRect BagFrame, BagGrid Grid,
    SideButtonColumn SideButtons, UiRect Organize, MoneySlot Money, CellBadge Badge,
    DescriptionPanelLayout Description);

public static class BagLayouts
{
    public const string DescriptionText = "#ffffff";

    public static readonly string[] EquipmentSlotOrder =
        ["head", "neck", "ring", "accessory", "body", "back", "main-hand", "off-hand", "hands", "feet"];

    public const int SideFilterTextSize = 19;

    private const float DescriptionMenuButtonStride = 126f;

    public static readonly DescriptionPanelLayout Description =
        new(Y: 0f, Width: 442f, Height: 431f,
            Inset: 10f, Background: 0x000000u, BackgroundAlpha: .85f,
            Title: new DescriptionTitle(X: 20f, Y: 22f, Right: 82f, Size: 24f),
            Scroll: new UiRect(20f, 60f, 358f, 258f),
            Close: new UiRect(381f, -6f, 67f, 67f),
            Menu: new DescriptionMenu(X: 15f, Y: 362f, Width: 377f, Height: 55f,
                ButtonWidth: 125f, ButtonHeight: 55f, ButtonStride: DescriptionMenuButtonStride,
                Offsets: new Dictionary<int, float> { [1] = DescriptionMenuButtonStride, [2] = 64f, [3] = 0f }))
        {
            LeftX = 85f,
            RightX = 496f,
        };

    /// <summary>
    /// 说明面板按它盖住的那一栏取宽：建面板时给的宽度就是栏宽，之后只改 x 与 y。
    /// 面板比 442 宽时同样要跟（分类商店的货架栏 462），所以按差值双向调整。
    /// </summary>
    public static DescriptionPanelLayout DescriptionPanel(float width) => Description.WithWidth(width);

    public const float BagDescriptionWidth = 384f;

    public static BagLayout Create() => Create(496f);

    /// <summary>
    /// 指定右框左边缘。融合器那一页的右框在 484（`UIMix_CreateMainControl` 的实数），
    /// 比背包页左移 12——「网格 34、分类列 389」都是相对右框的，所以整列跟着走。
    /// </summary>
    public static BagLayout Create(float left)
    {
        var bagFrame = new UiRect(left, 0f, 395f, 431f);
        var money = ItemLayout.Money;
        return new BagLayout(
            LeftFrame: new UiRect(85f, 0f, 409f, 431f),
            Preview: new UiRect(85f + 129f, 110f, 157f, 157f),
            Level: new LevelRow(131f, 368f, 323f),
            BagFrame: bagFrame,
            Grid: new BagGrid(bagFrame.X + 34f, bagFrame.Y + 27f, 323f, 323f,
                ItemLayout.SlotStride, 4),
            SideButtons: new SideButtonColumn(bagFrame.X + 389f, bagFrame.Y + 12f, 57f, 57f,
                ItemLayout.SideButtonOffsets),
            Organize: new UiRect(bagFrame.X + 389f + (57f - 84f) / 2f, bagFrame.Y + 12f - 68f, 84f, 44f),
            Money: new MoneySlot(bagFrame.X + money.Offset.X, bagFrame.Y + money.Offset.Y,
                new UiRect(bagFrame.X + money.PlateOffset.X, bagFrame.Y + money.PlateOffset.Y,
                    money.PlateOffset.Width, money.PlateOffset.Height),
                money.Stride, money.CoinWidth, money.CoinHeight,
                new UiPoint(money.DigitX, money.DigitY), money.Color),
            Badge: new CellBadge(67f, 47f, "#ffffff"),
            Description: Description);
    }
}
