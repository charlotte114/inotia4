using System;
using System.Collections.Generic;
using System.Linq;
using Inotia4.Domain.World;

namespace Inotia4.Domain.Combat;

public abstract class CombatRequestResult
{
    public abstract bool Accepted { get; }

    public string? Reason { get; init; }
}

public sealed class CombatRequestStarted : CombatRequestResult
{
    public override bool Accepted => true;

    public bool Approaching { get; init; }
}

public sealed class CombatRequestRejected : CombatRequestResult
{
    public override bool Accepted => false;
}

public readonly record struct ActionTiming(int SourceFrames, float FrameDurationMs);

public abstract class FlightEvent
{
    public required int Id { get; init; }
}

public sealed class FlightStartEvent : FlightEvent
{
    public required string Effect { get; init; }

    public required Vec2 Position { get; init; }

    public required int Heading { get; init; }

    public bool Directional { get; init; }

    public bool AboveCharacters { get; init; }
}

public sealed class FlightStepEvent : FlightEvent
{
    public required Vec2 Position { get; init; }

    public required int Heading { get; init; }

    public required int DurationMs { get; init; }
}

public sealed class FlightEndEvent : FlightEvent
{
}

public interface IFlightPort
{
    Combatant? Actor(string id);

    void Emit(FlightEvent action);
}

public interface ITransitionPort : IFlightPort
{
    void Effect(string name, Vec2 position);
}

public sealed class FlightRequest
{
    public required string OwnerId { get; init; }

    public required string Effect { get; init; }

    public required Vec2 Position { get; init; }

    public string? TargetId { get; init; }

    public Vec2? TargetPosition { get; init; }

    public required string Kind { get; init; }

    public required int StepDistancePx { get; init; }

    public required int StepDurationMs { get; init; }

    public int? SwitchFrame { get; init; }

    public bool Directional { get; init; } = true;

    public bool AboveCharacters { get; init; }
}

public sealed class CombatRuntimeState
{
    public required IReadOnlyList<KeyValuePair<string, CombatSnapshot>> Actors { get; init; }
}

public sealed class CombatRuntime
{
    private sealed class ScheduledWork
    {
        public required int Id { get; init; }

        public required string OwnerId { get; init; }

        public required long DueFrame { get; init; }

        public required Action Work { get; init; }
    }

    private sealed class RunningAction
    {
        public required int Id { get; init; }

        public required string OwnerId { get; init; }

        public required long StartedFrame { get; init; }

        public required CombatAction Action { get; init; }

        public required ActionTiming Timing { get; init; }

        public float ElapsedMs { get; set; }

        public int Frame { get; set; }

        public required int Frames { get; init; }

        public required Action<int> FrameWork { get; init; }

        public required Action Complete { get; init; }
    }

    private sealed class InFlight
    {
        public required string OwnerId { get; init; }

        public required Action Cancel { get; init; }
    }

    private readonly Func<long> _now;
    private int _serial;
    private long _lastFrame;
    private bool _closed;
    private readonly HashSet<string> _pauses = [];
    private readonly Dictionary<string, CombatController> _controllers = [];
    private readonly Dictionary<int, ScheduledWork> _tasks = [];
    private readonly Dictionary<int, RunningAction> _actions = [];
    private readonly Dictionary<int, InFlight> _flights = [];

    public CombatRuntime(Func<long> now)
    {
        _now = now;
        _lastFrame = now();
    }

    public bool Paused => _pauses.Count > 0;

    public IReadOnlyCollection<string> PauseOwners => _pauses;

    public bool Idle => _actions.Count == 0 && _tasks.Count == 0 && _flights.Count == 0;

    public int PendingCount => _tasks.Count;

    public bool HasPendingBasicStrike(string ownerId)
    {
        foreach (var track in _actions.Values)
        {
            if (track.OwnerId != ownerId || track.Action.Skill is not null ||
                track.Action.BasicAttack is null) continue;

            var attack = track.Action.BasicAttack;
            var strikeFrame = attack.Strikes is { Count: > 0 } strikes
                ? strikes.Max(strike => strike.Frame)
                : attack.StrikeFrame;
            if (track.Frame < strikeFrame) return true;
        }

        return false;
    }

    public void SetPaused(string owner, bool paused)
    {
        if (paused) _pauses.Add(owner);
        else _pauses.Remove(owner);
    }

    public void RegisterActor(string id, CombatController controller)
    {
        if (_controllers.TryGetValue(id, out var previous) && !ReferenceEquals(previous, controller))
        {
            previous.Cancel(true);
            Cancel(id);
        }

        _controllers[id] = controller;
    }

    public void ClearSelection(string actorId)
    {
        if (_controllers.TryGetValue(actorId, out var controller)) controller.ClearSelection();
    }

    public string? SelectedTargetId(string actorId) =>
        _controllers.TryGetValue(actorId, out var controller) ? controller.SelectedTargetId : null;

    public void CancelAttack(string actorId)
    {
        if (_controllers.TryGetValue(actorId, out var controller)) controller.Cancel(false, false);
    }

    public void CancelAndClearTarget(string actorId)
    {
        if (_controllers.TryGetValue(actorId, out var controller)) controller.Cancel(false, true);
    }

    public CombatState ActionState(string actorId) =>
        _controllers.TryGetValue(actorId, out var controller) ? controller.ActionState() : CombatState.Idle;

    public bool IsCastingSkill(string actorId) =>
        _controllers.TryGetValue(actorId, out var controller) && controller.IsCastingSkill;

    public void RemoveActor(string id)
    {
        if (_controllers.TryGetValue(id, out var controller)) controller.Cancel(true);
        Cancel(id);
        _controllers.Remove(id);
    }

    public CombatRequestResult Request(string actorId, SkillPlayback? skill = null, string? targetId = null,
        string? mode = null)
    {
        if (Paused || _closed) return new CombatRequestRejected { Reason = "busy" };
        if (!_controllers.TryGetValue(actorId, out var actor)) return new CombatRequestRejected { Reason = "no-target" };

        var result = skill is not null && mode == "manual"
            ? actor.RequestManualSkill(skill, targetId)
            : skill is not null && mode == "triggered"
                ? actor.RequestTriggeredAction(skill, targetId)
                : actor.Request(skill, targetId);

        if (result != "started") return new CombatRequestRejected { Reason = result };
        return new CombatRequestStarted { Approaching = actor.ActionState() == CombatState.Approaching };
    }

    public CombatRuntimeState CaptureState() => new()
    {
        Actors = _controllers.Select(pair => new KeyValuePair<string, CombatSnapshot>(pair.Key, pair.Value.Snapshot()))
            .ToList(),
    };

    public void RestoreState(CombatRuntimeState state)
    {
        var ids = new HashSet<string>();
        foreach (var pair in state.Actors)
        {
            if (!ids.Add(pair.Key)) throw new InvalidOperationException("战斗角色快照无效");
            if (!_controllers.ContainsKey(pair.Key)) throw new InvalidOperationException("战斗角色快照无效");
        }

        Reset();
        foreach (var pair in state.Actors) _controllers[pair.Key].RestoreState(pair.Value);
    }

    public Action Schedule(string ownerId, int delayFrames, Action work)
    {
        if (delayFrames < 0) throw new ArgumentException("战斗延迟帧无效", nameof(delayFrames));
        if (_closed) return () => { };

        var id = ++_serial;
        _tasks[id] = new ScheduledWork { Id = id, OwnerId = ownerId, DueFrame = _now() + delayFrames, Work = work };
        return () => _tasks.Remove(id);
    }

    public Action Play(CombatAction action, ActionTiming timing, Action<int> frame, Action complete)
    {
        if (_closed) throw new InvalidOperationException("战斗运行实例已关闭");
        if (timing.SourceFrames < 1 || !float.IsFinite(timing.FrameDurationMs) || timing.FrameDurationMs <= 0)
            throw new ArgumentException("战斗动作周期无效", nameof(timing));

        var frames = action.BasicAttackWait ?? (action.Skill is { } skill
            ? SkillActionRules.SkillSequence(skill.Action, skill.Level ?? 1, timing.SourceFrames).Length
            : action.BasicAttack!.EffectiveCycleFrames);

        var id = ++_serial;
        _actions[id] = new RunningAction
        {
            Id = id,
            OwnerId = action.Caster.Id,
            StartedFrame = _now(),
            Action = action,
            Timing = timing,
            Frames = frames,
            FrameWork = frame,
            Complete = complete,
        };
        return () => _actions.Remove(id);
    }

    public void Step(long frame)
    {
        if (_closed || Paused) return;
        if (frame < _lastFrame) throw new ArgumentException("战斗推进帧无效", nameof(frame));
        if (frame == _lastFrame) return;
        if (frame != _lastFrame + 1) throw new InvalidOperationException("战斗运行实例必须逐逻辑帧推进");
        _lastFrame = frame;

        foreach (var track in _actions.Values.ToList())
        {
            if (track.StartedFrame >= frame) continue;
            track.ElapsedMs += CombatClock.LogicFrameMs;
            while (_actions.ContainsKey(track.Id) && track.ElapsedMs >= track.Timing.FrameDurationMs)
            {
                track.ElapsedMs -= track.Timing.FrameDurationMs;
                var next = track.Frame + 1;
                var attack = track.Action.BasicAttack;
                var duration = attack is not null && !attack.FixedAnimation &&
                               track.Action.Skill is null && track.Action.BasicAttackWait is null
                    ? CombatTempo.AttackCycleFrames(attack.Definition.CycleFrames,
                        track.Action.GetAttackSpeedRate?.Invoke() ?? 1000, attack.StrikeFrame)
                    : track.Frames -
                      (track.Action.Skill is { } playing
                          ? SkillActionRules.SkillRepeatSkippedFrames(playing.Action, playing.Level ?? 1,
                              track.Action.RepeatState)
                          : 0);

                if (next >= duration)
                {
                    _actions.Remove(track.Id);
                    track.Complete();
                    break;
                }

                track.Frame = next;
                if (track.Action.BasicAttackWait is null) track.FrameWork(next);
                if (Paused || _closed) break;
            }

            if (Paused || _closed) break;
        }

        var budget = 4096;
        while (!Paused && !_closed)
        {
            var due = _tasks.Values
                .Where(task => task.DueFrame <= frame)
                .OrderBy(task => task.DueFrame)
                .ThenBy(task => task.Id)
                .ToList();
            if (due.Count == 0) break;

            foreach (var task in due)
            {
                if (!_tasks.Remove(task.Id)) continue;
                budget -= 1;
                if (budget < 0) throw new InvalidOperationException("同帧战斗任务超过执行上限");
                task.Work();
                if (Paused || _closed) break;
            }
        }
    }

    public void Fly(FlightRequest request, IFlightPort port, Action<Vec2> arrive)
    {
        if (request.StepDistancePx <= 0 || request.StepDurationMs <= 0 ||
            request.SwitchFrame is < 0)
            throw new ArgumentException("战斗弹道参数无效");

        var target = request.TargetPosition ??
                     (request.TargetId is { } id ? port.Actor(id)?.Position : null);
        if (target is null || _closed) return;

        var flightId = ++_serial;
        var spot = target.Value;
        var position = request.Position;
        var steps = 0;
        Action cancelStep = () => { };

        void Cancel()
        {
            cancelStep();
            _flights.Remove(flightId);
            port.Emit(new FlightEndEvent { Id = flightId });
        }

        _flights[flightId] = new InFlight { OwnerId = request.OwnerId, Cancel = Cancel };
        port.Emit(new FlightStartEvent
        {
            Id = flightId,
            Effect = request.Effect,
            Position = position,
            Heading = BasicAttackRules.ProjectileHeading(position, spot),
            Directional = request.Directional,
            AboveCharacters = request.AboveCharacters,
        });

        void Advance()
        {
            if (!_flights.ContainsKey(flightId)) return;

            var live = request.Kind == "trace" && request.TargetId is { } targetId
                ? port.Actor(targetId)
                : null;
            if (request.Kind == "trace" && live is null)
            {
                Cancel();
                return;
            }

            var destination = live?.Position ?? spot;
            var next = request.Kind == "position"
                ? SkillGeometry.PositionTransitionStep(position, destination, request.StepDistancePx, steps++,
                    request.SwitchFrame ?? 0)
                : BasicAttackRules.ProjectileStep(position, destination, request.StepDistancePx);

            port.Emit(new FlightStepEvent
            {
                Id = flightId,
                Position = next.Position,
                Heading = BasicAttackRules.ProjectileHeading(position, destination),
                DurationMs = request.StepDurationMs,
            });

            cancelStep = Schedule(request.OwnerId,
                Math.Max(1, (int)Math.Ceiling(request.StepDurationMs / (double)CombatClock.LogicFrameMs)), () =>
                {
                    position = next.Position;
                    if (!next.Arrived)
                    {
                        Advance();
                        return;
                    }

                    var exists = request.Kind != "trace" ||
                                 (request.TargetId is { } targetId2 && port.Actor(targetId2) is not null);
                    Cancel();
                    if (exists) arrive(position);
                });
        }

        Advance();
    }

    public void Transition(string ownerId, SkillTransition definition, Combatant? target, Vec2 position,
        ITransitionPort port, Action<Vec2> arrive)
    {
        void Finish(Vec2 spot)
        {
            if (definition.ActionEffect is { } effect) port.Effect(effect, spot);
            arrive(spot);
        }

        if (definition.Kind == "stationary" && definition.Effect is { } stationaryEffect)
        {
            if (target is null) return;
            var spot = target.Position;
            port.Effect(stationaryEffect, spot);
            if (definition.SwitchFrame == 0) Finish(spot);
            else Schedule(ownerId, definition.SwitchFrame, () => Finish(spot));
            return;
        }

        if (definition.Effect is { } flyingEffect && target is not null &&
            (definition.Kind == "trace" || definition.Kind == "position"))
        {
            Fly(new FlightRequest
            {
                OwnerId = ownerId,
                Effect = flyingEffect,
                Position = new Vec2(position.X + definition.OffsetX, position.Y + definition.OffsetY),
                TargetId = target.Id,
                TargetPosition = definition.Kind == "position" ? target.Position : null,
                Kind = definition.Kind,
                StepDistancePx = (int)definition.StepDistancePx,
                StepDurationMs = CombatClock.LogicFrameMs,
                SwitchFrame = definition.SwitchFrame,
                Directional = definition.Kind == "trace" && definition.Directional != false,
                AboveCharacters = definition.Kind == "position",
            }, port, Finish);
            return;
        }

        void FinishLive()
        {
            var live = target is null ? null : port.Actor(target.Id);
            if (target is not null && live is null) return;
            Finish(live?.Position ?? position);
        }

        if (definition.Kind == "trace" || definition.SwitchFrame == 0) FinishLive();
        else Schedule(ownerId, definition.SwitchFrame, FinishLive);
    }

    public void Cancel(string ownerId)
    {
        foreach (var (id, track) in _actions.ToList())
            if (track.OwnerId == ownerId) _actions.Remove(id);
        foreach (var (id, task) in _tasks.ToList())
            if (task.OwnerId == ownerId) _tasks.Remove(id);
        foreach (var flight in _flights.Values.ToList())
            if (flight.OwnerId == ownerId) flight.Cancel();
    }

    public void Reset()
    {
        _actions.Clear();
        _tasks.Clear();
        foreach (var flight in _flights.Values.ToList()) flight.Cancel();
        _lastFrame = _now();
    }

    public void Dispose()
    {
        Reset();
        _controllers.Clear();
        _closed = true;
        _pauses.Clear();
    }
}
