namespace Inotia4.Domain.Ui;

public readonly record struct CraftingMaterialRow(
    float X, float Y, float Stride, float SlotSize, SlotInset Icon, UiPoint Count, int TextSize);

public readonly record struct CraftingCostRow(
    float X, float Y, float Stride, float CoinWidth, float CoinHeight, UiPoint Digit, int TextSize);

public readonly record struct CraftingTierRow(
    float X, float Y, float Width, float Height, float Stride, UiPoint Name, int NameSize);

/// <summary>
/// 左栏那列菜单：442 宽、竖排五行，行高 = 字高 + 41，五行共 305，装在
/// 442×343 的组里（<c>UIMix_CreateMainControl</c> @0x856A0，见 SO_REVERSE §17.11）。
/// 行与行之间不留缝。
/// </summary>
public readonly record struct CraftingMenuList(
    UiRect Area, float RowWidth, float RowHeight, float RowGap, int TextSize);

/// <summary>
/// 菜单行的整行底：不是通用按钮皮，而是图集 19（<c>UI_007.png</c>）里那枚深色胶囊。
/// <c>UIMix_ButtonMenuListDraw</c>（0x84FDC）先 <c>IMGSYS_GetGroup(0x13)</c> 取图集、
/// 再 <c>IMGSYS_GetLoc(0x13, 3)</c> 取第 3 号块、<c>GRPX_DrawPart</c> 铺满整行，最后在
/// 行中央写字。图集 19 的块表基址是 0x178B30，块 3 = (86,188,288,82)（SO_REVERSE §17.12），
/// 裁出来的就是 <see cref="Image"/>。
/// </summary>
public static class CraftingMenuRow
{
    public const string Image = "assets/ui/crafting/menu-row.png";

    public const float Cap = 34f;
}

public readonly record struct CraftingRecipeList(UiRect Area, float RowHeight, int TextSize);

public readonly record struct CraftingTarget(
    UiRect Slot, UiPoint Name, UiPoint Power, float TextWidth, int NameSize, int PowerSize);

public readonly record struct CraftingBatchRow(
    float X, float Y, float Width, float Height, float Step, int TextSize);

public readonly record struct NativeMessageLayout(
    float Width, float Height, float Center, float TextWidth, int TextSize,
    float BandExtra, float EdgeOffset, float EdgeWidth, float EdgeHeight,
    float ButtonWidth, float ButtonHeight, float ButtonFromBottom,
    float OkLeft, float YesLeft, float NoLeft, float ButtonTextGap);

/// <summary>
/// 合成台（融合器）的版面，**照 <c>UIMix_CreateMainControl</c>(0x856A0) 的控件树**，
/// 坐标已换算成内容块坐标（左面板的原点在内容块 x=10，见 SO_REVERSE §17.11）：
///
/// - 左面板 462×423（组尺寸就是这么高，不是 431；「组合」钮 360+62 正好贴到底边）；
/// - 菜单列表 (10,55) 442×343，五行、行高 = 字高 + 41；
/// - 配方列表 (39,90) 321 宽、行高 56（<c>UIMix_CreateRecipeGroupControl</c>）；
/// - 合成格 (213,62) 74×74，下面名称行与「等级／攻防」行；
/// - 材料组 (60,239) 380×74，四格步长 102；
/// - 「组合」钮 (139,360) 205×62（205×62 配 229×82 的图，绘制时左上角再让 12/9）；
/// - 右面板 (484,0) 395×431，网格 (34,27)、分类列 (389,12)。
/// </summary>
public static class CraftingLayouts
{
    public static readonly UiRect LeftFrame = new(10f, 0f, 462f, 431f);

    public static readonly UiRect BagFrame = new(484f, 0f, 395f, 431f);

    public static readonly CraftingMenuList Menus =
        new(Area: new UiRect(20f, 55f, 442f, 343f), RowWidth: 442f, RowHeight: 61f, RowGap: 0f,
            TextSize: 20);

    public static readonly CraftingTierRow Tiers =
        new(X: 10f, Y: 8f, Width: 442f, Height: 40f, Stride: 0f,
            Name: new UiPoint(512f, 25f), NameSize: 28);

    public static readonly CraftingRecipeList Recipes =
        new(Area: new UiRect(49f, 90f, 321f, 224f), RowHeight: 56f, TextSize: 20);

    /// <summary>
    /// 目标格两侧的切换三角。<c>UIMix_ButtonUpExe</c>（0x84F9C）与
    /// <c>UIMix_ButtonDownExe</c>（0x84FBC）只做「序号 +1 回绕 / −1 回绕」，也就是在
    /// 可当目标的道具之间切换，不用回背包点；按钮建在加工区左组 (102,62) 的 x=0 与 x=244，
    /// 各 51×58。图标是图集 15（<c>UI_003.png</c>）块 36 那枚金色 chevron，右按钮水平镜像。
    /// </summary>
    public static readonly UiRect TargetPrev = new(102f, 62f, 51f, 58f);

    public static readonly UiRect TargetNext = new(346f, 62f, 51f, 58f);

    public const string TargetArrowImage = "assets/ui/crafting/arrow-prev.png";

    public static readonly CraftingTarget Target = new(
        Slot: new UiRect(223f, 62f, 74f, 74f),
        Name: new UiPoint(260f, 147f),
        Power: new UiPoint(260f, 176f),
        TextWidth: 300f, NameSize: 22, PowerSize: 18);

    public static readonly CraftingMaterialRow Materials =
        new(X: 70f, Y: 239f, Stride: 102f, SlotSize: 74f, Icon: new SlotInset(5f, 5f, 64f),
            Count: new UiPoint(37f, 52f), TextSize: 16);

    public static readonly CraftingCostRow Cost =
        new(X: 82f, Y: 306f, Stride: 103f, CoinWidth: 44f, CoinHeight: 40f,
            Digit: new UiPoint(44f, 6f), TextSize: 20);

    public static readonly UiRect CraftButton = new(149f, 360f, 205f, 62f);

    public static readonly CraftingBatchRow Batch =
        new(X: 149f, Y: 318f, Width: 205f, Height: 34f, Step: 34f, TextSize: 18);

    public static readonly NativeMessageLayout Message = new(
        Width: 1024f, Height: 640f, Center: 512f, TextWidth: 410f, TextSize: 24,
        BandExtra: 150f, EdgeOffset: 29f, EdgeWidth: 240f, EdgeHeight: 38f,
        ButtonWidth: 94f, ButtonHeight: 50f, ButtonFromBottom: 82f,
        OkLeft: 47f, YesLeft: 194f, NoLeft: -100f, ButtonTextGap: 0f);
}
