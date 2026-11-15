namespace Inotia4.Domain.Ui;

/// <summary>
/// 左栏最左边那一列队员位（104×340 的栏里三个 104×113 的成员按钮）。
/// 队伍列由人物页与技能页共用，
/// 所以这里的数值也是两页共用的一份：格图 104×114、格距 113、首格 49，
/// 头像 86×96 落在格里 +(9,9)（即内容坐标 (17, top+9)）。
/// </summary>
public static class PartyColumnLayout
{
    public const float FrameX = 8f;

    public const float FrameTop = 49f;

    public const float Stride = 113f;

    public const float FrameWidth = 104f;

    public const float FrameHeight = 114f;

    public const float HitHeight = 113f;

    public const float IconX = 9f;

    public const float IconY = 9f;

    public const float IconWidth = 86f;

    public const float IconHeight = 96f;

    public const int Slots = 3;
}
