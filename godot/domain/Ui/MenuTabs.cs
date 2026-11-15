namespace Inotia4.Domain.Ui;

public readonly record struct MenuTab(string Page, string Label);

public static class MenuTabs
{
    public static readonly MenuTab[] All =
    [
        new("status", "人物"),
        new("bag", "背包"),
        new("skills", "技能"),
        new("party", "队伍"),
        new("quests", "任务"),
        new("system", "系统"),
    ];

    public const float FirstX = 35f;

    public const float Stride = 149f;

    public const float Y = 25f;

    public const float Width = 102f;

    public const float Height = 116f;

    public const float ImageX = 12f;

    public const float ImageY = 0f;

    public const float ImageSize = 78f;

    public const float LabelX = 51f;

    public const float LabelY = 85f;

    public const float LabelWidth = 90f;

    public const int LabelSize = 20;

    public const float InactiveAlpha = .65f;

    public static string AssetKey(string page) => $"tab-{page}";

    public static int IndexOf(string page) => System.Array.FindIndex(All, tab => tab.Page == page);
}
