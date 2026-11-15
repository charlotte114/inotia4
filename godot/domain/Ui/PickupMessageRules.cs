namespace Inotia4.Domain.Ui;

public static class PickupMessageRules
{
    public const int Limit = 32;

    public const int SpacingMs = 120;

    public const int HoldMs = 1440;

    public const int LiftMs = 960;

    public const float Lift = 16f;

    public const float RowSpacing = 12f;

    public const float BaseOffsetY = -37f;

    public const float RightOffsetX = 20f;

    public const float LeftOffsetX = -99f;

    public const float EdgeMargin = 100f;

    public static int TotalMs => HoldMs + LiftMs;

    public static float LiftAt(int elapsedMs) =>
        elapsedMs <= HoldMs ? 0f : Math.Min(Lift, (elapsedMs - HoldMs) / (float)LiftMs * Lift);

    public static int RowFor(IEnumerable<int> occupiedRows)
    {
        var occupied = new HashSet<int>(occupiedRows);
        int row = 0;
        while (occupied.Contains(row)) row++;
        return row;
    }

    public static float AnchorX(float x, float viewLeft, float viewWidth, float headX) =>
        x - viewLeft > viewWidth - EdgeMargin ? x - headX + LeftOffsetX : x + headX + RightOffsetX;

    public static float AnchorY(float y, float headY, int row, float lift) =>
        y + headY + row * RowSpacing + BaseOffsetY - lift;

    public static string? ItemNameColor(int? rarity) => rarity switch
    {
        null or 0 => null,
        1 => "#4eff00",
        2 => "#00b4ff",
        3 => "#ffe600",
        4 => "#9c00ff",
        _ => null,
    };
}
