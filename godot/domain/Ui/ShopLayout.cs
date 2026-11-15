namespace Inotia4.Domain.Ui;

public readonly record struct ShopTitle(float X, float Y, float Width, float Height);

public readonly record struct ShopColumnCap(float X, float Y, float Bottom, float Width, float Height);

public readonly record struct ShopCategoryColumn(ShopColumnCap Cap, UiRect Divider);

public readonly record struct ShopShelf(float X, float Y);

public readonly record struct ShopCategoryButton(float Width, float Height, float Stride);

public readonly record struct ShopCategoryArea(
    UiRect Scroll, ShopCategoryButton Button, UiRect Plate, UiPoint Name);

public readonly record struct ShopBagGrid(
    float X, float Y, float Width, float Height, float Stride, int Columns);

public readonly record struct ShopSlot(float Size, float Stride, SlotInset Icon, SlotInset Selection);

public readonly record struct ShopLayout(
    UiRect LeftFrame,
    UiRect BagFrame,
    ShopTitle Title,
    UiPoint TitleName,
    ShopCategoryColumn CategoryColumn,
    ShopShelf Shelf,
    SideButtonColumn SideButtons,
    ShopCategoryArea Category,
    ShopBagGrid Bag,
    ShopSlot Slot,
    CellBadge Badge,
    MoneySlot Money);

/// <summary>
/// 商店页的版面常量。<c>UIStore_CreateMain @0x165a08</c>（两栏、分类按钮 88×69、4×4 货架、
/// 背包网格）、<c>UIStore_Draw @0x166d30</c>（分类名 <c>frame+278,+43</c> 居中、分类名底板
/// <c>frame+117,+41,323</c>、标题框 <c>frame+131</c> 或 <c>frame+(宽−288)/2</c>、名称居中于标题框
/// <c>+144</c>）、<c>UIStore_DrawInvenBackground @0x1671fc</c>（背包底板、金额 <c>+34,+365</c> 与底板
/// <c>+34,+368,323</c>）、<c>MONEY_DrawWithUnit @0x1b8b30</c>（先币后数、单位间距 103、
/// 币 44×40、数字左对齐于币 x+44、y+6、白色）、<c>ITEM_DrawPorting @0x1a219c</c>
/// （图标 +5/+5 64×64、数量角标 +67/+47 白色右对齐）。
/// </summary>
public static class ShopLayouts
{
    public const int ShelfSlots = 16;

    public const string AreaColor = "#e2cb9e";

    public static ShopLayout Create(bool classified)
    {
        var leftFrame = classified ? new UiRect(18f, 0f, 462f, 431f) : new UiRect(85f, 0f, 395f, 431f);
        var bagFrame = new UiRect(484f, 0f, 395f, 431f);

        var titleX = classified
            ? leftFrame.X + 131f
            : leftFrame.X + (float)Math.Truncate((leftFrame.Width - 288f) / 2f);
        var title = new ShopTitle(titleX, leftFrame.Y - 35f, 288f, 82f);

        var money = ItemLayout.Money;

        return new ShopLayout(
            LeftFrame: leftFrame,
            BagFrame: bagFrame,
            Title: title,
            TitleName: new UiPoint(title.X + 144f, title.Y + title.Height / 2f),
            CategoryColumn: new ShopCategoryColumn(
                Cap: new ShopColumnCap(leftFrame.X, leftFrame.Y, leftFrame.Y + 407f, 103f, 24f),
                Divider: new UiRect(leftFrame.X + 93f, leftFrame.Y + 10f, 14f, 411f)),
            Shelf: classified
                ? new ShopShelf(leftFrame.X + 117f, leftFrame.Y + 86f)
                : new ShopShelf(leftFrame.X + 36f, leftFrame.Y + 80f),
            SideButtons: new SideButtonColumn(bagFrame.X + 389f, bagFrame.Y + 12f, 57f, 57f,
                ItemLayout.SideButtonOffsets),
            Category: new ShopCategoryArea(
                Scroll: new UiRect(leftFrame.X + 10f, leftFrame.Y + 10f, 88f, 412f),
                Button: new ShopCategoryButton(88f, 69f, 69f),
                Plate: new UiRect(leftFrame.X + 117f, leftFrame.Y + 41f, 323f, 34f),
                // `GRPX_DrawStringWithFont(名字, 框左+278, 框顶+43, 居中, 居中)`：43 是 30 像素字号的
                // 上沿，落到 34 高的底板里就是纵向居中，所以这里直接给底板中线。
                Name: new UiPoint(leftFrame.X + 278f, leftFrame.Y + 58f)),
            Bag: new ShopBagGrid(bagFrame.X + 34f, bagFrame.Y + 27f, 323f, 323f,
                ItemLayout.SlotStride, 4),
            Slot: new ShopSlot(ItemLayout.SlotSize, ItemLayout.SlotStride,
                ItemLayout.SlotIcon, ItemLayout.SlotSelection),
            Badge: new CellBadge(67f, 47f, "#ffffff"),
            Money: new MoneySlot(bagFrame.X + money.Offset.X, bagFrame.Y + money.Offset.Y,
                new UiRect(bagFrame.X + money.PlateOffset.X, bagFrame.Y + money.PlateOffset.Y,
                    money.PlateOffset.Width, money.PlateOffset.Height),
                money.Stride, money.CoinWidth, money.CoinHeight,
                new UiPoint(money.DigitX, money.DigitY), money.Color));
    }
}

/// <summary>
/// 弹层黑底的内缩：<c>GRPX_FillRect(x+35, y+35, 宽−70, 高−70)</c>，数量框与确认框相同。
/// </summary>
public static class PopupFrame
{
    public const float Inset = 35f;

    public const float CornerSize = 81f;

    public const float CornerOuter = 46f;

    public const float HorizontalHeight = 81f;

    public const float HorizontalTop = -46f;

    public const float HorizontalBottom = 45f;

    public const float VerticalWidth = 81f;

    public const float VerticalLeft = -46f;

    public const float VerticalRight = 45f;

    public const float VerticalTop = 35f;
}

public readonly record struct TradeStepButton(float Width, float Height, float Left, float Right);

public readonly record struct TradeQuantityLayout(
    float Width, float Height, UiRect Plate, UiRect Field, TradeStepButton Step,
    SlotInset Icon, UiPoint Count, string CountColor,
    UiRect TextPlate, UiPoint Money, TradeButtonRow Button);

public readonly record struct TradeButtonRow(float Width, float Height, float Y, float Left, float Right);

public readonly record struct TradeMessageLayout(
    float Width, float Height, UiRect Plate, UiPoint Text, float TextStep,
    UiRect TextPlate, UiPoint Money, TradeButtonRow Button);

/// <summary>
/// 交易弹层的版面。框体、计数区与按钮来自 <c>UIInputItemCount_CreateBaseControl @0x14ec30</c>，
/// 框内其余内容来自 <c>UIInputItemCount_Draw @0x14f4c8</c>；确认框的框高来自
/// <c>UIPopupMsg_SetLayout @0x15dbb0</c>、按钮来自 <c>UIPopupMsg_CreateButtonControl @0x15d5c4</c>。
/// </summary>
public static class TradeLayouts
{
    private const float QuantityPlateWidth = 323f;

    private const float MessagePlateWidth = 377f;

    private const float MessageStep = 34f;

    private const float MessageGap = 12f;

    private const float PlateHeight = 34f;

    public static readonly TradeQuantityLayout Quantity = new(
        Width: 450f,
        Height: 350f,
        Plate: new UiRect(PopupFrame.Inset, PopupFrame.Inset,
            450f - PopupFrame.Inset * 2, 350f - PopupFrame.Inset * 2),
        Field: new UiRect(95f, 50f, 260f, 58f),
        Step: new TradeStepButton(51f, 58f, 0f, 209f),
        Icon: new SlotInset(150f, 47f, 64f),
        Count: new UiPoint(233f, 56f),
        CountColor: "#e2cb9e",
        TextPlate: new UiRect(63f, 168f, QuantityPlateWidth, PlateHeight),
        Money: new UiPoint(63f, 165f),
        Button: new TradeButtonRow(125f, 55f, 275f, 45f, 280f));

    public static TradeMessageLayout Message(int lines, bool hasMoney)
    {
        const float width = 450f;
        var block = Math.Max(1, lines) * MessageStep + (hasMoney ? MessageGap + PlateHeight : 0f);
        var height = Math.Max(235f, block) + 115f;
        var textY = (float)Math.Floor(PopupFrame.Inset + Math.Max(0f, 235f - PopupFrame.Inset - block) / 2f + .5f);
        var plateY = textY + block - (hasMoney ? PlateHeight : 0f);
        var plateX = (float)Math.Floor((width - MessagePlateWidth) / 2f + .5f);

        return new TradeMessageLayout(
            Width: width,
            Height: height,
            Plate: new UiRect(PopupFrame.Inset, PopupFrame.Inset,
                width - PopupFrame.Inset * 2, height - PopupFrame.Inset * 2),
            Text: new UiPoint(width / 2f, textY),
            TextStep: MessageStep,
            TextPlate: new UiRect(plateX, plateY, MessagePlateWidth, PlateHeight),
            Money: new UiPoint(plateX, plateY + 3f),
            Button: new TradeButtonRow(125f, 55f, 275f, 45f, 280f));
    }
}
