using System;
using Godot;

namespace Inotia4.Ui;

/// <summary>
/// 快捷位的冷却扇形：剩余时间画成一块深色扇形，随时间顺时针收掉，剩 0 时整块消失。
///
/// 半径跟图标走（技能 75／物品 64 的一半，由调用方给），所以技能格与物品格的扇形
/// 不会一样大。几何：起始角从 12 点方向（-π/2）随进度顺时针转过整圈，收尾角固定
/// 在 1.5π 也是 12 点方向，所以扇形一直贴住 12 点、从另一侧往里收。
///
/// 绘制坐标以控件原点为中心：父节点（槽位根）的原点本身就是圆心，所以这里直接绕 (0,0) 画。
/// </summary>
public sealed partial class CooldownFan : Control
{
    private static readonly Color Fill = new(0f, 0f, 0f, 0.55f);

    /// <summary>前沿线与圆环的暖白色（0xffe6a3）。</summary>
    private static readonly Color Edge = new(0xff / 255f, 0xe6 / 255f, 0xa3 / 255f);

    private const int ArcSegments = 48;

    private bool _active;
    private float _progress;
    private float _radius;

    public CooldownFan()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        // 画在图标之上、数量角标之下（角标要压着扇形，否则冷却时看不清数量）。
        ZIndex = 1;
    }

    public void Set(float? progress, float radius)
    {
        var active = progress is not null;
        var value = active ? Math.Clamp(progress!.Value, 0f, 1f) : 0f;
        if (active == _active && Math.Abs(value - _progress) < 0.001f && Math.Abs(radius - _radius) < 0.01f)
            return;

        _active = active;
        _progress = value;
        _radius = radius;
        QueueRedraw();
    }

    public void Clear() => Set(null, _radius);

    public bool Active => _active;

    public float Progress => _progress;

    public override void _Draw()
    {
        if (!_active || _radius <= 0f) return;

        var start = -Mathf.Pi / 2f + _progress * Mathf.Pi * 2f;
        const float end = Mathf.Pi * 1.5f;

        if (_progress <= 0f)
        {
            DrawCircle(Vector2.Zero, _radius, Fill);
        }
        else if (start < end)
        {
            var points = new Vector2[ArcSegments + 2];
            points[0] = Vector2.Zero;
            for (var index = 0; index <= ArcSegments; index += 1)
            {
                var angle = Mathf.Lerp(start, end, index / (float)ArcSegments);
                points[index + 1] = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * _radius;
            }

            DrawColoredPolygon(points, Fill);
        }

        if (_progress > 0f && _progress < 1f)
        {
            DrawLine(Vector2.Zero, new Vector2(Mathf.Cos(start), Mathf.Sin(start)) * _radius,
                new Color(Edge, 0.9f), 2f, true);
        }

        DrawArc(Vector2.Zero, _radius - 1f, 0f, Mathf.Pi * 2f, ArcSegments,
            new Color(Edge, 0.45f), 1f, true);
    }
}
