namespace Inotia4.Domain.Story;

/// <summary>
/// 剧情镜头滚动的时长。
///
/// 按「一次滚动要走几帧」算：近距离每逻辑帧挪 12 像素，超过 120 像素改按距离的十分之一，
/// 每帧 60 毫秒。所以镜头是**匀速直线滚过去**，不是缓动也不是瞬移；
/// 距离不足 0.01 视为不用滚（瞬间到位）。
/// </summary>
public static class StoryCamera
{
    public const int FrameMs = 60;

    public const double ShortStep = 12;

    public const double LongDistance = 120;

    public static int DurationMs(double distance)
    {
        if (distance < 0.01) return 0;

        var step = distance > LongDistance ? Math.Floor(distance / 10) : ShortStep;
        return (int)(Math.Ceiling(distance / step) * FrameMs);
    }
}
