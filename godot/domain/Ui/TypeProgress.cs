using System;

namespace Inotia4.Domain.Ui;

/// <summary>
/// 逐字显示的进度：每 24 毫秒出 2 个字，
/// 不足一档的毫秒留到下一次——余数不能攒进这个字里，否则帧率一变打字节奏就飘。
///
/// 计量单位是**可见字数**，控制码零宽不计（调用方把 RichTextLabel 数出来的总数传进来）。
/// </summary>
public sealed class TypeProgress
{
    public const double IntervalMs = 24.0;

    public const int CharsPerInterval = 2;

    private double _elapsed;

    public int Revealed { get; private set; }

    public void Reset()
    {
        _elapsed = 0;
        Revealed = 0;
    }

    public int Advance(double deltaMs, int total)
    {
        if (deltaMs <= 0 || Revealed >= total) return 0;

        _elapsed += deltaMs;
        var count = (int)(_elapsed / IntervalMs) * CharsPerInterval;
        _elapsed %= IntervalMs;
        if (count <= 0) return 0;

        var next = Math.Min(total, Revealed + count);
        var added = next - Revealed;
        Revealed = next;
        return added;
    }

    public void Complete(int total)
    {
        Revealed = total;
        _elapsed = 0;
    }
}
