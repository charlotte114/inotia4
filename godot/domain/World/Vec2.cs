namespace Inotia4.Domain.World;

/// <summary>
/// 二维向量。领域层不引用引擎，所以这里自带一个最小实现，
/// 引擎层在边界处转换成 Godot 的 Vector2。
/// </summary>
public readonly record struct Vec2(float X, float Y)
{
    public static Vec2 Zero => new(0f, 0f);

    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);

    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);

    public static Vec2 operator *(Vec2 a, float scale) => new(a.X * scale, a.Y * scale);

    public float Length => MathF.Sqrt(X * X + Y * Y);
}

public readonly record struct Rect(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;

    public float Bottom => Y + Height;

    public static Rect AroundPoint(float x, float y, float radius) =>
        new(x - radius, y - radius, radius * 2f, radius * 2f);
}
