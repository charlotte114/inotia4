namespace Inotia4.Domain.World;

/// <summary>
/// 移动速度折算：基准速度 × 地图／GM 乘数 × <c>moveSpeed</c> 属性千分比，水格再减半。
///
/// 三个要点都不能省：乘数按「基准 × GM × 属性/1000」的顺序、水格减半乘在最后、
/// 只在末尾向零截断一次——中途取整会差出 1 点速度，走位的手感就对不上。
/// </summary>
public static class MovementSpeedRule
{
    public const int NeutralMoveSpeed = 1000;

    public static float Apply(double baseSpeed, double gmMultiplier, int moveSpeedAttribute, bool inWater)
    {
        var speed = baseSpeed * gmMultiplier * moveSpeedAttribute / 1000.0;
        if (inWater) speed *= 0.5;
        return (float)Math.Max(0.0, Math.Truncate(speed));
    }
}
