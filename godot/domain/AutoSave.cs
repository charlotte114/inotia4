using System;
using System.Collections.Generic;
using Inotia4.Domain.Story;

namespace Inotia4.Domain;

public enum AutoSaveReason
{
    MapEntry,
    StoryEvent,
    Quest,
    Timer,
}

/// <summary>自动存档向世界索取的东西：当前世界能不能存、存到哪、什么时候不该存。
/// 领域层不引用引擎，这一层由场景实现。</summary>
public interface IAutoSavePorts
{
    SaveGame? Capture();

    /// <summary>写自动槽。失败只记日志——自动存档不该打断游戏。</summary>
    void Write(SaveGame game);

    bool Enabled();

    bool Locked() => false;
}

/// <summary>
/// 自动存档：把「进图、剧情事件完成、任务变化、定时」四种触发合并成低频写入。
///
/// 触发可能在同一秒里连着来（进图后立刻播剧情、剧情里接任务），所以 <see cref="Request"/>
/// 只记一次待写，去抖窗口结束后写一份；两次写入之间至少隔 <see cref="DefaultMinGapMs"/>，
/// 避免写风暴。
///
/// 这里不用引擎的定时器：时间由调用方把当前毫秒数喂进来
/// （<see cref="Update"/> 每帧调用一次），效果一样，还能脱离引擎测试。
/// </summary>
public sealed class AutoSaver
{
    public const int DefaultDebounceMs = 1200;
    public const int DefaultMinGapMs = 2000;
    public const int DefaultRetryMs = 2000;
    public const int DefaultIntervalMs = 300_000;

    private readonly IAutoSavePorts _ports;
    private readonly int _debounceMs;
    private readonly int _minGapMs;
    private readonly int _retryMs;

    private long _lastWrite = long.MinValue;

    private long? _pendingAt;
    private bool _waiting;
    private bool _suspended;
    private int _intervalMs;
    private long _nextTimerAt = long.MinValue;

    private readonly QuestBook? _watchedQuests;
    private int _questVersion = int.MinValue;
    private Dictionary<string, QuestStatus> _questStatuses = new();

    public AutoSaver(IAutoSavePorts ports, QuestBook? watchedQuests = null,
        int debounceMs = DefaultDebounceMs, int minGapMs = DefaultMinGapMs, int retryMs = DefaultRetryMs)
    {
        _ports = ports;
        _watchedQuests = watchedQuests;
        _debounceMs = debounceMs;
        _minGapMs = minGapMs;
        _retryMs = retryMs;
    }

    public void Suspend()
    {
        _suspended = true;
        _pendingAt = null;
        _waiting = false;
    }

    public void Resume() => _suspended = false;

    public bool IsSuspended => _suspended;

    public void Request(AutoSaveReason reason, long nowMs)
    {
        if (_suspended || !_ports.Enabled() || _pendingAt is not null) return;
        _pendingAt = nowMs + _debounceMs;
    }

    public void Flush(long nowMs)
    {
        if (_suspended || !_ports.Enabled()) return;
        if (_ports.Locked() || (_lastWrite != long.MinValue && nowMs - _lastWrite < _minGapMs))
        {
            Retry(nowMs);
            return;
        }

        var game = _ports.Capture();
        if (game is null)
        {
            Retry(nowMs);
            return;
        }

        _waiting = false;
        _lastWrite = nowMs;
        try { _ports.Write(game); }
        catch (Exception error) { Console.Error.WriteLine($"自动存档写入失败 {error.Message}"); }
    }

    public void FlushForBackground(long nowMs)
    {
        if (_suspended || !_ports.Enabled()) return;
        _pendingAt = null;
        _waiting = false;
        if (_ports.Locked()) return;
        var game = _ports.Capture();
        if (game is null) return;
        _lastWrite = nowMs;
        try { _ports.Write(game); }
        catch (Exception error) { Console.Error.WriteLine($"自动存档写入失败 {error.Message}"); }
    }

    /// <summary>
    /// 任务接取/完成时触发自动存档。任务状态也可能由导演的交互路径直接改，
    /// 不只走剧情提交，所以盯整本任务簿；纯目标进度推进（版本没动）不触发。
    /// 任务的接取与完成也可能由导演的交互路径直接改，不只走剧情提交，所以盯整本任务簿；
    /// 纯目标进度推进（版本没动）不触发。
    /// 会话本身不对外发订阅通知，所以改成每帧由 <see cref="Update"/> 调这里，
    /// 按任务簿版本号增量比较，只在状态真的变了才排队写。
    /// </summary>
    public void PollQuests(long nowMs)
    {
        if (_watchedQuests is null) return;
        var book = _watchedQuests;
        if (book.Version == _questVersion) return;
        if (_questVersion == int.MinValue)
        {
            _questVersion = book.Version;
            _questStatuses = Snapshot(book);
            return;
        }

        var previous = _questStatuses;
        _questVersion = book.Version;
        _questStatuses = Snapshot(book);
        foreach (var (id, status) in _questStatuses)
        {
            var before = previous.GetValueOrDefault(id);
            if (before != status && status is QuestStatus.Active or QuestStatus.Completed)
            {
                Request(AutoSaveReason.Quest, nowMs);
                return;
            }
        }
    }

    private static Dictionary<string, QuestStatus> Snapshot(QuestBook book)
    {
        var snapshot = new Dictionary<string, QuestStatus>();
        foreach (var (id, progress) in book.ProgressEntries()) snapshot[id] = progress.Status;
        return snapshot;
    }

    public void Update(long nowMs)
    {
        PollQuests(nowMs);
        if (_intervalMs > 0)
        {
            if (_nextTimerAt == long.MinValue) _nextTimerAt = nowMs + _intervalMs;
            else if (nowMs >= _nextTimerAt)
            {
                _nextTimerAt = nowMs + _intervalMs;
                Request(AutoSaveReason.Timer, nowMs);
            }
        }

        if (_pendingAt is { } deadline && nowMs >= deadline)
        {
            _pendingAt = null;
            _waiting = false;
            Flush(nowMs);
        }
    }

    public void Start(int intervalMs = DefaultIntervalMs)
    {
        _intervalMs = intervalMs;
        _nextTimerAt = long.MinValue;
    }

    public void Stop()
    {
        _intervalMs = 0;
        _nextTimerAt = long.MinValue;
        _pendingAt = null;
        _waiting = false;
    }

    private void Retry(long nowMs)
    {
        if (_suspended || _waiting || _pendingAt is not null) return;
        _waiting = true;
        _pendingAt = nowMs + _retryMs;
    }
}
