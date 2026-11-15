using System;
using System.Text.Json.Serialization;

namespace Inotia4.Domain.Combat;

/// <summary>
/// 时钟快照（原版 <c>CombatClockState</c>）：帧号加不足一帧的余数。
/// 少了余数，读档后的下一次推进就会把这一段白送掉——所以它必须进存档。
/// </summary>
public sealed class CombatClockState
{
    [JsonPropertyName("frame")] public required long Frame { get; init; }

    [JsonPropertyName("remainderMs")] public required long RemainderMs { get; init; }
}

/// <summary>
/// 战斗时钟。原版逻辑帧固定 60 毫秒，技能起手、攻击判定、状态时长全部按帧记，
/// 所以这里只推进整数帧，余数留到下一次——不能按「这一帧过了多少毫秒」直接算，
/// 否则帧率一变判定就会漂。
/// </summary>
public sealed class CombatClock
{
    public const int LogicFrameMs = 60;

    private long _remainder;

    public long Frame { get; private set; }

    public long FractionalMs => _remainder;

    public void Advance(long deltaMs) => Advance(deltaMs, null);

    public void Advance(long deltaMs, Func<long, bool>? step)
    {
        if (deltaMs <= 0) return;

        var pending = deltaMs;
        while (pending >= LogicFrameMs - _remainder)
        {
            pending -= LogicFrameMs - _remainder;
            _remainder = 0;
            Frame += 1;
            if (step is not null && !step(Frame)) return;
        }

        _remainder += pending;
    }

    public void Restore(long frame) => Frame = Math.Max(0, frame);

    public CombatClockState CaptureState() => new() { Frame = Frame, RemainderMs = _remainder };

    /// <summary>
    /// 恢复快照。校验口径：帧号必须是非负整数，余数必须落在 [0, 60)。
    /// </summary>
    public void RestoreState(CombatClockState state)
    {
        if (state.Frame < 0 || state.RemainderMs < 0 || state.RemainderMs >= LogicFrameMs)
            throw new ArgumentOutOfRangeException(nameof(state), "战斗时钟状态无效");

        Frame = state.Frame;
        _remainder = state.RemainderMs;
    }
}

public sealed class CooldownStore
{
    private readonly Dictionary<string, long> _readyFrames = new();

    public void Start(string key, int baseFrames, int ratePerThousand, long frame)
    {
        var effective = (int)Math.Truncate(baseFrames * ratePerThousand / 1000.0);
        _readyFrames[key] = frame + effective;
    }

    public bool IsReady(string key, long frame) =>
        frame >= (_readyFrames.TryGetValue(key, out var ready) ? ready : 0);

    public int RemainingFrames(string key, long frame) =>
        Math.Max(0, (int)((_readyFrames.GetValueOrDefault(key, 0)) - frame));

    public void Reset(string key) => _readyFrames.Remove(key);

    public void ResetAll() => _readyFrames.Clear();
}
