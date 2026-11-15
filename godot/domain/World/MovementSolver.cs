namespace Inotia4.Domain.World;

/// <summary>
/// 一步移动的求解：先试横轴、再试纵轴，被挡住的那一轴单独放弃。
/// 这样贴着墙斜走时会顺着墙滑，而不是整个停死。
///
/// 注意这里是「先横后纵」的固定顺序，不按输入方向加权。换顺序会让贴墙斜走
/// 在某些角落表现为卡顿，所以顺序是规则的一部分，不要随手改。
/// </summary>
public static class MovementSolver
{
    public const float DefaultFootRadius = 4f;

    private const float ConstrainStep = 3f;

    /// <summary>
    /// 返回真正能走的那部分位移。调用方把结果加到自己身上即可。
    ///
    /// 分轴判定：一轴被挡时另一轴照常走，斜着贴墙会滑过去，而不是整体停住。
    ///
    /// 判定用哪把尺子取决于这一步多大：单步位移超过半个碰撞盒才按整盒判定，
    /// 否则只按 <paramref name="footRadius"/> 的窄足底判定。这条不对称是有意的——
    /// 行走、寻路和普通摆放一直走窄足底口径，别为了统一把它扩散成整盒。
    /// 一律按整盒判的后果是：一格宽的通道必须把 16×16 的身体精确对齐到格心才过得去，
    /// 摇杆推过去就永远差一点。
    /// </summary>
    public static Vec2 Step(CollisionGrid grid, Vec2 position, Vec2 delta,
        float footRadius = DefaultFootRadius, Rect? body = null)
    {
        var wholeBox = body is { } box &&
                       MathF.Max(MathF.Abs(delta.X), MathF.Abs(delta.Y)) > MathF.Min(box.Width, box.Height) / 2f;

        bool Free(float x, float y) => wholeBox && body is { } box
            ? grid.CanStandInRect(new Rect(x + box.X, y + box.Y, box.Width, box.Height))
            : grid.CanStandOnTerrain(x, y, footRadius);

        var steps = Math.Max(1,
            (int)MathF.Ceiling(MathF.Max(MathF.Abs(delta.X), MathF.Abs(delta.Y)) / ConstrainStep));
        var stepX = delta.X / steps;
        var stepY = delta.Y / steps;

        var movedX = 0f;
        var movedY = 0f;
        for (var i = 0; i < steps; i++)
        {
            if (stepX != 0f && Free(position.X + movedX + stepX, position.Y + movedY))
                movedX += stepX;

            if (stepY != 0f && Free(position.X + movedX, position.Y + movedY + stepY))
                movedY += stepY;
        }

        return new Vec2(movedX, movedY);
    }

    /// <summary>
    /// 八方向输入归一化：只按方向键时速度不该比斜着按更快。
    /// </summary>
    public static Vec2 NormalizeAxis(Vec2 axis)
    {
        var lengthSquared = axis.X * axis.X + axis.Y * axis.Y;
        if (lengthSquared <= 1f) return axis;
        var length = MathF.Sqrt(lengthSquared);
        return new Vec2(axis.X / length, axis.Y / length);
    }
}
