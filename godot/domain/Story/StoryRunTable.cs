using System;
using System.Collections.Generic;
using System.Linq;

namespace Inotia4.Domain.Story;

public enum StoryRunStatus
{
    Running,

    Waiting,

    Deferred,

    Completed,
}

public sealed class StoryRun
{
    public string Id { get; }

    public string Script { get; }

    public string? EventId { get; init; }

    public string Occurrence { get; }

    public int Sequence { get; init; }

    public string CursorScript { get; private set; }

    public string Node { get; private set; }

    public StoryRunStatus Status { get; internal set; } = StoryRunStatus.Running;

    internal StoryRun(string id, string script, string? eventId, string occurrence, int sequence, string node)
    {
        Id = id;
        Script = script;
        EventId = eventId;
        Occurrence = occurrence;
        Sequence = sequence;
        CursorScript = script;
        Node = node;
    }

    internal void MoveTo(string script, string node)
    {
        CursorScript = script;
        Node = node;
    }
}

public sealed class StoryRunSnapshot
{
    public List<StoryRunEntry> Runs { get; set; } = [];

    public List<string> CompletedEvents { get; set; } = [];

    public int Sequence { get; set; }
}

public sealed class StoryRunEntry
{
    public string Id { get; set; } = string.Empty;

    public string Script { get; set; } = string.Empty;

    public string? EventId { get; set; }

    public string Occurrence { get; set; } = string.Empty;

    public int Sequence { get; set; }

    public string CursorScript { get; set; } = string.Empty;

    public string Node { get; set; } = string.Empty;

    public string Status { get; set; } = "running";
}

public sealed class StoryRunTable
{
    private readonly List<StoryRun> _runs = [];
    private readonly List<string> _completed = [];

    public int Sequence { get; private set; }

    public StoryRun? Current => _runs.Count > 0 ? _runs[^1] : null;

    public IReadOnlyList<StoryRun> Runs => _runs;

    public IReadOnlyList<string> CompletedEvents => _completed;

    public StoryRun Begin(string script, string node, string? eventId, string occurrenceKey)
    {
        Sequence += 1;
        var occurrence = $"{occurrenceKey}@{Sequence}";
        var run = new StoryRun($"run-{Sequence}", script, eventId, occurrence, Sequence, node);
        _runs.Add(run);
        return run;
    }

    public void MoveTo(string script, string node)
    {
        Current?.MoveTo(script, node);
    }

    public void Waiting(bool waiting)
    {
        if (Current is not { Status: StoryRunStatus.Running or StoryRunStatus.Waiting } run) return;
        run.Status = waiting ? StoryRunStatus.Waiting : StoryRunStatus.Running;
    }

    public void Complete()
    {
        // 只结「正在演」的那条：等遭遇战中途被打断不算演完，事件也不能进已完成名单。
        if (Current is not { Status: StoryRunStatus.Running } run) return;
        run.Status = StoryRunStatus.Completed;
        if (run.EventId is { Length: > 0 } id && !_completed.Contains(id)) _completed.Add(id);
    }

    /// <summary>
    /// 事件状态：有活着的运行时按它的状态回答，否则看已完成名单。
    /// 与 <see cref="IStoryWorld.EventDone"/> 的区别在于「正在演」与「等战斗」不再混成一个值。
    /// </summary>
    public string StatusOf(string eventId)
    {
        for (var index = _runs.Count - 1; index >= 0; index--)
        {
            var run = _runs[index];
            if (run.EventId != eventId) continue;
            if (run.Status == StoryRunStatus.Running) return "running";
            if (run.Status == StoryRunStatus.Waiting) return "waiting";
        }

        return _completed.Contains(eventId) ? "completed" : "idle";
    }

    public StoryRunSnapshot Capture() => new()
    {
        Sequence = Sequence,
        CompletedEvents = [.. _completed],
        Runs =
        [
            .. _runs.Select(run => new StoryRunEntry
            {
                Id = run.Id,
                Script = run.Script,
                EventId = run.EventId,
                Occurrence = run.Occurrence,
                Sequence = run.Sequence,
                CursorScript = run.CursorScript,
                Node = run.Node,
                Status = StatusName(run.Status),
            }),
        ],
    };

    /// <summary>按快照重建。序号必须是非负整数，每行的标识不能重复，否则整份拒读。</summary>
    public void Restore(StoryRunSnapshot? snapshot)
    {
        _runs.Clear();
        _completed.Clear();
        Sequence = 0;
        if (snapshot is null) return;
        if (snapshot.Sequence < 0) throw new InvalidOperationException("剧情运行序号无效");

        var ids = new HashSet<string>();
        foreach (var entry in snapshot.Runs)
        {
            if (entry.Id.Length == 0 || !ids.Add(entry.Id))
                throw new InvalidOperationException("剧情运行标识无效");
            var status = ParseStatus(entry.Status);
            _runs.Add(new StoryRun(entry.Id, entry.Script, entry.EventId, entry.Occurrence,
                entry.Sequence, entry.Node)
            {
                Status = status,
            });
        }

        foreach (var id in snapshot.CompletedEvents)
            if (id.Length > 0 && !_completed.Contains(id)) _completed.Add(id);

        Sequence = snapshot.Sequence;
    }

    public static string StatusName(StoryRunStatus status) => status switch
    {
        StoryRunStatus.Running => "running",
        StoryRunStatus.Waiting => "waiting",
        StoryRunStatus.Deferred => "deferred",
        StoryRunStatus.Completed => "completed",
        _ => "idle",
    };

    private static StoryRunStatus ParseStatus(string text) => text switch
    {
        "running" => StoryRunStatus.Running,
        "waiting" => StoryRunStatus.Waiting,
        "deferred" => StoryRunStatus.Deferred,
        "completed" => StoryRunStatus.Completed,
        _ => throw new InvalidOperationException($"剧情运行状态无效：{text}"),
    };
}

public sealed class StoryCursorState
{
    public string Script { get; set; } = string.Empty;

    public string Node { get; set; } = string.Empty;

    public string? EventId { get; set; }

    public string? EncounterId { get; set; }
}
