using System;
using System.Collections.Generic;
using System.Linq;
using Inotia4.Domain.Content;
using Inotia4.Domain.World;

namespace Inotia4.Domain.Combat;

public static class EnemyAiState
{
    public const string Idle = "idle";
    public const string Wandering = "wandering";
    public const string Chasing = "chasing";
    public const string Attacking = "attacking";
    public const string Returning = "returning";
}

public interface IEnemyThreatPort
{
    string? Select(string? current);

    void Notice(string source);

    bool Has(string source);

    void Clear();
}

public interface IEnemyPort : ICombatPort
{
    void FollowPath(Func<Vec2?> nextWaypoint);

    Vec2 Home();

    float? FollowDistancePx => null;

    string? FocusTarget() => null;

    bool? Counterattack() => null;

    (int Current, int Maximum)? Health(string id) => null;

    /// <summary>
    /// 地图边界。基接口给的是可空的 <c>Rect?</c>（不提供就当没有边界），敌人寻路必须有边界，
    /// 所以这里把返回类型收紧成非空——同名成员靠 <c>new</c> 明说是有意收窄，不是漏写。
    /// </summary>
    new Rect NavigationBounds();

    PathBudget? PathBudget => null;

    IStandability? NavigationSnapshot() => null;

    IEnemyThreatPort? Threat => null;

}

/// <summary>
/// 敌人一次行为快照，随存档往返：阶段、目标与冷却。
///
/// 名册里另有一份同名的敌人数值快照（等级、血魔那一份），
/// C# 同一命名空间里不能重名；那一份已先占用该名字，
/// 这里按来源文件加 <c>Ai</c> 后缀区分。
/// </summary>
public sealed class EnemyAiSnapshot
{
    public required string State { get; init; }

    public string? TargetId { get; init; }

    public IReadOnlyDictionary<string, Cooldown> Cooldowns { get; init; } = new Dictionary<string, Cooldown>();
}

public sealed class EnemyControllerState
{
    public required string State { get; init; }

    public string? TargetId { get; init; }

    public bool PendingReturn { get; init; }

    public bool? ThreatCombatActive { get; init; }

    public double Accumulator { get; init; }

    public long Clock { get; init; }

    public long NextPathAt { get; init; }

    public List<Vec2> Path { get; init; } = [];

    public Vec2? Destination { get; init; }

    public float? DestinationRange { get; init; }

    public Vec2? ProgressPosition { get; init; }

    public long LastProgressAt { get; init; }

    public long IdleElapsed { get; init; }

    public Vec2? WanderDestination { get; init; }

    public CombatSnapshot? Combat { get; init; }

    public List<string>? SpentOnce { get; init; }

    public EnemyControllerState Stationary() => new()
    {
        State = "idle",
        TargetId = null,
        PendingReturn = false,
        ThreatCombatActive = false,
        Accumulator = 0,
        Clock = Clock,
        NextPathAt = NextPathAt,
        Path = [],
        Destination = null,
        DestinationRange = null,
        ProgressPosition = null,
        LastProgressAt = LastProgressAt,
        IdleElapsed = IdleElapsed,
        WanderDestination = null,
        Combat = Combat is null ? null : Combat with { State = "idle", TargetId = null },
        SpentOnce = SpentOnce,
    };
}

public sealed class EnemyController
{
    private const int LogicFrameMs = CombatClock.LogicFrameMs;

    private const long RepathIntervalMs = 180;

    private const int SearchSlice = 128;

    /// <summary>施法前的同步可达性检查；不能沿用跨帧搜索，保持原判定的即时性。</summary>
    private const int HoldSearchCap = 1024;

    private const int WanderSearchCap = 512;

    /// <summary>目标不可达后的重试间隔（常见 RPG 做法：0.5–1 秒）。</summary>
    private const long FailedPathMs = 600;

    private const float ArrivalDistancePx = .5f;

    private static readonly Dictionary<string, int?> ReleaseHealthRate = new()
    {
        ["hp-below-30"] = 30,
        ["hp-below-50"] = 50,
        ["hp-below-70"] = 70,
        ["hp-below-95"] = 95,
        ["always"] = null,
    };

    private static readonly string[] Directions = ["south", "west", "north", "east"];

    private readonly string _id;
    private readonly EnemyBehavior _behavior;
    private readonly ResolvedBasicAttack? _attack;
    private readonly IReadOnlyList<EnemyPlayableSkill> _skills;
    private readonly IEnemyPort _port;
    private readonly CombatController? _combat;
    private readonly Func<double> _random;

    private string _state = EnemyAiState.Idle;
    private string? _targetId;
    private bool _pendingReturn;
    private bool _threatCombatActive;
    private double _accumulator;
    private long _clock;
    private long _nextPathAt;
    private List<Vec2> _path = [];
    private Vec2? _destination;
    private float? _destinationRange;
    private Vec2? _progressPosition;
    private long _lastProgressAt;
    private bool _disposed;
    private long _idleElapsed;
    private Vec2? _wanderDestination;
    private readonly HashSet<string> _spentOnce = [];

    private Search? _search;

    private sealed class Search
    {
        public required PathSearch Task { get; init; }

        public Combatant? Target { get; init; }

        public string? TargetId { get; init; }

        public required Vec2 Destination { get; init; }

        public required float Range { get; init; }

        public required Func<Vec2, bool> IsGoal { get; init; }
    }

    public EnemyController(
        string id,
        EnemyBehavior behavior,
        ResolvedBasicAttack? basicAttack,
        IEnemyPort port,
        Func<double>? random = null,
        IReadOnlyList<EnemyPlayableSkill>? skills = null,
        CombatController? sharedCombat = null)
    {
        _id = id;
        _behavior = behavior;
        _attack = basicAttack;
        _skills = skills?.Where(skill => skill.Action is not null).ToList() ?? [];
        _port = port;
        _random = random ?? (() => Random.Shared.NextDouble());

        if (basicAttack is not null || _skills.Count > 0 || sharedCombat is not null)
            _combat = sharedCombat ?? new CombatController(id, basicAttack,
                port.Threat is null ? port : new EnemyCombatPort(port, this));
    }

    public CombatController? CombatController => _combat;

    private bool MapWide => _behavior.Engagement == "map-wide";

    public EnemyAiSnapshot Snapshot() => new()
    {
        State = _state,
        TargetId = _targetId,
        Cooldowns = _combat?.Snapshot().Cooldowns ?? new Dictionary<string, Cooldown>(),
    };

    public string ActionState() => _state;

    public string? CurrentTargetId() => _targetId;

    public EnemyControllerState CaptureState()
    {
        var combat = _combat?.Snapshot();
        return new EnemyControllerState
        {
            State = _state,
            TargetId = _targetId,
            PendingReturn = _pendingReturn,
            ThreatCombatActive = _threatCombatActive,
            Accumulator = _accumulator,
            Clock = _clock,
            NextPathAt = _nextPathAt,
            Path = [.. _path],
            Destination = _destination,
            DestinationRange = _destinationRange,
            ProgressPosition = _progressPosition,
            LastProgressAt = _lastProgressAt,
            IdleElapsed = _idleElapsed,
            WanderDestination = _wanderDestination,
            Combat = combat,
            SpentOnce = [.. _spentOnce],
        };
    }

    public void RestoreState(EnemyControllerState state)
    {
        if (_disposed) throw new InvalidOperationException("无法恢复已销毁的敌人行为");

        Cancel();
        _search = null;
        if (state.Combat is not null) _combat?.RestoreState(state.Combat);

        _state = state.State;
        _targetId = state.TargetId;
        _pendingReturn = state.PendingReturn;
        _threatCombatActive = state.ThreatCombatActive ?? state.TargetId is not null;
        _accumulator = state.Accumulator;
        _clock = state.Clock;
        _nextPathAt = MapWide && state.Path.Count == 0 ? state.Clock : state.NextPathAt;
        _path = [.. state.Path];
        _destination = state.Destination;
        _destinationRange = state.DestinationRange;
        _progressPosition = state.ProgressPosition;
        _lastProgressAt = state.LastProgressAt;
        _idleElapsed = state.IdleElapsed;
        _wanderDestination = state.WanderDestination;

        _spentOnce.Clear();
        foreach (var skillId in state.SpentOnce ?? []) _spentOnce.Add(skillId);

        if (_path.Count > 0) _port.FollowPath(NextWaypoint);
    }

    public void Update(double deltaMs)
    {
        if (_disposed || _port.IsPaused() || !double.IsFinite(deltaMs) || deltaMs <= 0) return;

        _accumulator += deltaMs;
        while (_accumulator >= LogicFrameMs)
        {
            _accumulator -= LogicFrameMs;
            Step();
        }
    }

    public void Step()
    {
        if (_disposed || _port.IsPaused()) return;

        _clock += LogicFrameMs;
        Tick();
    }

    public void OnContact(string casterId, string targetId)
    {
        if (_port.Threat is { } threat)
        {
            if (!_disposed && targetId == _id && threat.Has(casterId)) OnThreatAdded();
            return;
        }

        if (_disposed || targetId != _id || _state == EnemyAiState.Returning || _pendingReturn ||
            _behavior.Aggression == "none" || _combat is null || _port.Counterattack() == false) return;

        var actor = _port.Actor(_id);
        var source = _port.Actor(casterId);
        if (actor is null || source is null || source.Id == actor.Id || source.Team == actor.Team ||
            _targetId is not null || !CombatController.CanTargetCombatant(actor, source)) return;

        _targetId = source.Id;
        _state = EnemyAiState.Chasing;
    }

    public void OnThreatAdded()
    {
        if (!_disposed) _threatCombatActive = true;
    }

    public bool Command(EnemyPlayableSkill skill, string? targetId, int level)
    {
        if (_disposed || _combat is null || skill.Action is null ||
            _state == EnemyAiState.Returning || _behavior.Aggression == "none") return false;
        var started = _combat.RequestTriggeredAction(Playback(skill, level), targetId, _behavior.CanTurn) == "started";
        if (started)
        {
            if (targetId is not null) _targetId = targetId;
            _pendingReturn = false;
            _state = _combat.State == CombatState.Attacking ? EnemyAiState.Attacking : EnemyAiState.Chasing;
        }
        return started;
    }

    private static SkillPlayback Playback(EnemyPlayableSkill skill, int? level = null) =>
        new(skill.Id, skill.Action!, level ?? skill.Level, skill.ManaCost, skill.Clips);

    public void TargetRemoved(string targetId, string reason = "hidden")
    {
        var current = _port.Threat is not null ? _combat?.SelectedTargetId : _targetId;
        _combat?.TargetRemoved(targetId);
        if (current != targetId) return;

        if (reason == "inactive" && !MapWide)
        {
            _port.Threat?.Clear();
            _threatCombatActive = false;
        }

        _targetId = null;
        _pendingReturn = false;
        _path = [];
        _destination = null;
        _search = null;
        _progressPosition = null;
        _nextPathAt = 0;
        _port.Stop();
        _state = _combat?.State == CombatState.Attacking ? EnemyAiState.Attacking : EnemyAiState.Idle;
    }

    public void Disable()
    {
        _combat?.Cancel(true, _port.Threat is null);
        _path = [];
        _destination = null;
        _search = null;
        _progressPosition = null;
        _wanderDestination = null;
        _idleElapsed = 0;
        _port.Stop();
        if (_state != EnemyAiState.Returning)
            _state = _targetId is not null ? EnemyAiState.Chasing : EnemyAiState.Idle;
    }

    public void Cancel()
    {
        _combat?.Cancel(true);
        _state = EnemyAiState.Idle;
        _targetId = null;
        _pendingReturn = false;
        _threatCombatActive = false;
        _path = [];
        _destination = null;
        _search = null;
        _progressPosition = null;
        _accumulator = 0;
        _wanderDestination = null;
        _idleElapsed = 0;
        _port.Stop();
    }

    public void Reset()
    {
        Cancel();
        _combat?.Reset();
        _spentOnce.Clear();
    }

    public void Dispose()
    {
        Cancel();
        _disposed = true;
        _combat?.Dispose();
    }

    /// <summary>
    /// 这门技能现在能不能放：先看「每场只放一次」这笔账，再按血量门槛判定——
    /// 门槛来自怪物 AI 表登记的那一档生命千分比。
    /// </summary>
    public bool ReleaseAllowed(string? releaseCondition, bool once, string skillId)
    {
        if (once && _spentOnce.Contains(skillId)) return false;

        if (!ReleaseHealthRate.TryGetValue(releaseCondition ?? "always", out var rate) || rate is null) return true;

        var health = _port.Health(_id);
        if (health is null || health.Value.Maximum <= 0) return false;

        return health.Value.Current > 0 && health.Value.Current * 100.0 <= health.Value.Maximum * (double)rate.Value;
    }

    private bool InAwareness(Combatant actor, Combatant target) =>
        (int)Math.Floor(BasicAttackRules.TargetDistance(actor.Position, target.Position) / 8.0) <=
        EnemyEngagement.AwarenessRangeUnits(_behavior, MapWide ? _port.NavigationBounds() : null);

    private void AwaitTarget()
    {
        if (_combat?.State == CombatState.Approaching) _combat.Cancel();
        else _combat?.ClearSelection();

        _state = EnemyAiState.Idle;
        _targetId = null;
        _pendingReturn = false;
        _path = [];
        _destination = null;
        _search = null;
        _progressPosition = null;
        _wanderDestination = null;
        _idleElapsed = 0;
        _port.Stop();
    }

    private void ReturnHome()
    {
        if (MapWide)
        {
            AwaitTarget();
            return;
        }

        _port.Threat?.Clear();
        _threatCombatActive = false;
        _combat?.Cancel(true);
        _state = EnemyAiState.Returning;
        _targetId = null;
        _pendingReturn = false;
        _path = [];
        _destination = null;
        _search = null;
        _progressPosition = null;
        _wanderDestination = null;
        _idleElapsed = 0;
        _port.Stop();
    }

    private void Tick()
    {
        var actor = _port.Actor(_id);
        if (actor is null || _combat is null || _behavior.Aggression == "none")
        {
            _port.Stop();
            return;
        }

        var current = _port.Threat is not null ? _combat.SelectedTargetId : _targetId;
        if (current is not null && _port.Actor(current) is null) TargetRemoved(current, "inactive");

        var previous = current is null ? null : _port.Actor(current);
        if (previous is not null && !CombatController.CanTargetCombatant(actor, previous)) TargetRemoved(previous.Id);

        var previousAction = _combat.State;
        _combat.Update();

        var capabilities = CombatStateRules.Capabilities(actor.States);
        if (!capabilities.CanAct)
        {
            Disable();
            return;
        }

        if (_port.Threat is not null)
        {
            var action = _combat.State;
            if (action is CombatState.Approaching or CombatState.Attacking)
            {
                _state = action == CombatState.Attacking ? EnemyAiState.Attacking : EnemyAiState.Chasing;
                return;
            }

            if (previousAction == CombatState.Approaching)
            {
                if (_pendingReturn) ReturnHome();
                else
                {
                    _targetId = null;
                    _state = EnemyAiState.Idle;
                }

                return;
            }

            if (_threatCombatActive)
            {
                _pendingReturn = false;
                if (_state == EnemyAiState.Returning)
                {
                    _state = EnemyAiState.Idle;
                    _path = [];
                    _destination = null;
                    _search = null;
                    _nextPathAt = 0;
                    _port.Stop();
                }
            }
        }

        if (_state == EnemyAiState.Returning)
        {
            var home = _port.Home();
            if (!_behavior.CanMove ||
                MathF.Sqrt(Square(actor.Position.X - home.X) + Square(actor.Position.Y - home.Y)) <=
                (_port.FollowDistancePx ?? ArrivalDistancePx))
            {
                _state = EnemyAiState.Idle;
                _path = [];
                _destination = null;
                _search = null;
                _idleElapsed = 0;
                _port.Stop();
                return;
            }

            Navigate(actor, home, null, _port.FollowDistancePx ?? 0f);
            return;
        }

        if (_port.Threat is { } threat)
        {
            if (_behavior.Aggression == "active" ||
                (_threatCombatActive && _combat.SelectedTargetId is not null))
            {
                foreach (var candidate in _port.Candidates())
                {
                    if (candidate.Id == _id || candidate.Team == actor.Team) continue;
                    if (MapWide && !_port.IsAlive(candidate.Id)) continue;
                    if (!InAwareness(actor, candidate) || !CombatController.CanTargetCombatant(actor, candidate)) continue;
                    if (threat.Has(candidate.Id)) continue;

                    threat.Notice(candidate.Id);
                }
            }

            _targetId = threat.Select(_combat.SelectedTargetId);
            if (_targetId is not null) _threatCombatActive = true;
            else if (_threatCombatActive)
            {
                ReturnHome();
                return;
            }
        }

        if (_port.Threat is null && _targetId is null && _port.FocusTarget() is { } focusId)
        {
            var focused = _port.Actor(focusId);
            if (focused is not null && focused.Team != actor.Team && InAwareness(actor, focused) &&
                CombatController.CanTargetCombatant(actor, focused))
                _targetId = focused.Id;
        }

        if (_port.Threat is null && _targetId is null && _behavior.Aggression == "active")
        {
            _targetId = _port.Candidates()
                .Where(target => target.Id != _id && target.Team != actor.Team &&
                                 InAwareness(actor, target) && CombatController.CanTargetCombatant(actor, target))
                .OrderBy(target => BasicAttackRules.TargetDistance(actor.Position, target.Position))
                .ThenBy(target => target.Id, StringComparer.Ordinal)
                .Select(target => target.Id)
                .FirstOrDefault();
        }

        var target = _targetId is null ? null : _port.Actor(_targetId);
        if (target is not null && _wanderDestination is not null)
        {
            _wanderDestination = null;
            _path = [];
            _destination = null;
            _search = null;
            _nextPathAt = 0;
            _port.Stop();
        }

        var lost = _targetId is not null &&
                   (target is null || target.Team == actor.Team ||
                    (_port.Threat is null && !InAwareness(actor, target)));

        if (_combat.State == CombatState.Approaching)
        {
            _state = EnemyAiState.Chasing;
            return;
        }

        if (_combat.State == CombatState.Attacking)
        {
            _state = EnemyAiState.Attacking;
            if (lost) _pendingReturn = true;
            return;
        }

        if (lost || _pendingReturn)
        {
            ReturnHome();
            return;
        }

        if (target is null)
        {
            if (MapWide)
            {
                AwaitTarget();
                return;
            }

            if (_port.FollowDistancePx is { } follow)
            {
                _state = EnemyAiState.Idle;
                var home = _port.Home();
                if (MathF.Sqrt(Square(actor.Position.X - home.X) + Square(actor.Position.Y - home.Y)) > follow)
                    Navigate(actor, home, null, follow);
                else _port.Stop();
            }
            else Wander(actor);

            return;
        }

        _idleElapsed = 0;
        var distance = BasicAttackRules.AttackDistance(actor.Position, actor.Bounds, target.Position, target.Bounds);
        var cooldowns = _combat.Snapshot().Cooldowns;
        var ready = _skills.Where(skill => !cooldowns.ContainsKey(skill.Id) &&
            _combat.CanSelectSkill(Playback(skill)) &&
            ReleaseAllowed(skill.ReleaseCondition, skill.Once == true, skill.Id)).ToList();
        var available = _port.Threat is not null ? ready : ready.Where(skill =>
            !SkillActionRules.NeedsTarget(skill.Action!) || distance <= skill.Action!.ApproachDistanceUnits).ToList();
        if (available.Count > 0)
        {
            var skill = available[^1];
            if (SkillActionRules.NeedsTarget(skill.Action!) &&
                HoldImmobileAction(actor, target, skill.Action!.ApproachDistanceUnits, skill)) return;
            _path = [];
            _destination = null;
            _search = null;
            if (_port.Threat is not null && !MapWide) _nextPathAt = 0;
            var result = _combat.Request(Playback(skill), target.Id, _behavior.CanTurn);
            if (result == "started" && skill.Once == true) _spentOnce.Add(skill.Id);
            if (_port.Threat is not null && (result != "started" || _combat.State == CombatState.Idle))
            {
                ReturnHome();
                return;
            }
            _state = _combat.State == CombatState.Attacking ? EnemyAiState.Attacking : EnemyAiState.Chasing;
            return;
        }

        if (_attack is not null && (_port.Threat is not null || distance <= _attack.ApproachDistanceUnits))
        {
            if (HoldImmobileAction(actor, target, _attack.ApproachDistanceUnits)) return;

            _path = [];
            _destination = null;
            _search = null;
            if (_port.Threat is not null && !MapWide) _nextPathAt = 0;

            var result = _combat.RequestTarget(target.Id, _behavior.CanTurn);
            if (_port.Threat is not null && (result != "started" || _combat.State == CombatState.Idle))
            {
                ReturnHome();
                return;
            }

            _state = _combat.State == CombatState.Attacking ? EnemyAiState.Attacking : EnemyAiState.Chasing;
            return;
        }

        _state = EnemyAiState.Chasing;
        var ranges = new List<float>();
        if (_attack is not null) ranges.Add(_attack.ApproachDistanceUnits);
        ranges.AddRange(ready.Select(skill => (float)skill.Action!.ApproachDistanceUnits));
        if (_behavior.CanMove && _behavior.MoveSpeedPxPerSecond > 0 && ranges.Count > 0)
            Navigate(actor, target.Position, target, ranges.Max());
        else _port.Stop();
    }

    private bool HoldImmobileAction(Combatant actor, Combatant target, float range, EnemyPlayableSkill? skill = null)
    {
        if (_port.Threat is null ||
            BasicAttackRules.AttackDistance(actor.Position, actor.Bounds, target.Position, target.Bounds) <= range ||
            actor.States?.Contains("ambush") == true) return false;

        if (_behavior.CanMove && _behavior.MoveSpeedPxPerSecond > 0 &&
            CombatStateRules.Capabilities(actor.States).CanMove) return false;
        var manaCost = skill is null ? null : _port.ManaCost(Playback(skill)) ?? skill.ManaCost;
        if (manaCost is not null and not 0 && !_port.CanSpendMana(manaCost.Value)) return false;

        _path = [];
        _destination = null;
        _search = null;
        if (!MapWide) _nextPathAt = 0;

        var reachable = ReachableNow(actor, target.Position, target, range);
        _path = [];
        _destination = null;
        _search = null;
        _progressPosition = null;
        _port.Stop();

        if (reachable)
        {
            _state = EnemyAiState.Idle;
            _combat?.SetReactionTarget(target.Id);
        }
        else ReturnHome();

        return true;
    }

    private bool Navigate(Combatant actor, Vec2 destination, Combatant? target, float range = 0f, bool follow = true)
    {
        var capabilities = CombatStateRules.Capabilities(actor.States);
        if (follow && (!capabilities.CanMove || (target is not null && !capabilities.CanAutoApproach)))
        {
            _port.Stop();
            return false;
        }

        while (_path.Count > 0 &&
               MathF.Sqrt(Square(actor.Position.X - _path[0].X) + Square(actor.Position.Y - _path[0].Y)) <=
               ArrivalDistancePx)
            _path.RemoveAt(0);

        if (_progressPosition is not { } progress ||
            MathF.Sqrt(Square(actor.Position.X - progress.X) + Square(actor.Position.Y - progress.Y)) >=
            ArrivalDistancePx)
        {
            _progressPosition = actor.Position;
            _lastProgressAt = _clock;
        }

        var stalled = _path.Count > 0 && _clock - _lastProgressAt >= RepathIntervalMs;
        var moved = _destination is not { } previousDestination ||
                    BasicAttackRules.TargetDistance(previousDestination, destination) >= PathGrid.Size ||
                    range != _destinationRange;
        var blocked = _path.Count > 0 &&
                      !Pathfinding.CanTraverse(actor.Position, _path[0], point => _port.CanStand(point));

        if (_search is not null && _search.TargetId != target?.Id) _search = null;

        if (_search is not null) AdvanceSearch(actor);
        else if (_clock >= _nextPathAt && (moved || blocked || stalled || _path.Count == 0))
            BeginSearch(actor, destination, target, range, stalled);

        if (follow && _search is not null && _path.Count == 0)
        {
            _port.MoveToward(destination);
            return true;
        }

        if (!follow) return _path.Count > 0;

        if (NextWaypoint() is not null)
        {
            _port.FollowPath(NextWaypoint);
            return true;
        }

        _port.Stop();
        return _path.Count > 0;
    }

    private IPathQuery SearchQuery(Combatant actor, Vec2 destination, Combatant? target, float range)
    {
        var snapshot = _port.NavigationSnapshot();
        Func<Vec2, bool> canStand = snapshot is not null
            ? point => snapshot.CanStand(point)
            : point => _port.CanStand(point);

        var isGoal = target is not null
            ? (Func<Vec2, bool>)(point =>
                canStand(point) &&
                BasicAttackRules.AttackDistance(point, actor.Bounds, target.Position, target.Bounds) <= range)
            : point =>
                MathF.Sqrt(Square(point.X - destination.X) + Square(point.Y - destination.Y)) <=
                MathF.Max(PathGrid.Size, range) && canStand(point);

        var radius = target is not null
            ? MathF.Sqrt(Square(actor.Bounds.Width + target.Bounds.Width) +
                         Square(actor.Bounds.Height + target.Bounds.Height)) + range * 8
            : PathGrid.Size;

        return new EnemyPathQuery(
            snapshot?.Bounds ?? _port.NavigationBounds(),
            canStand,
            isGoal,
            point => MathF.Max(0f,
                MathF.Sqrt(Square(point.X - destination.X) + Square(point.Y - destination.Y)) - radius));
    }

    private void BeginSearch(Combatant actor, Vec2 destination, Combatant? target, float range, bool stalled)
    {
        _nextPathAt = _clock + RepathIntervalMs;
        _destination = destination;
        _destinationRange = range;
        if (stalled) _lastProgressAt = _clock;

        var query = SearchQuery(actor, destination, target, range);
        _search = new Search
        {
            Task = new PathSearch(actor.Position, query),
            Target = target,
            TargetId = target?.Id,
            Destination = destination,
            Range = range,
            IsGoal = query.IsGoal,
        };

        AdvanceSearch(actor);
    }

    private void AdvanceSearch(Combatant actor)
    {
        var search = _search;
        if (search is null) return;

        const int requested = SearchSlice;
        var granted = _port.PathBudget?.Take(requested) ?? requested;
        if (granted <= 0) return;

        var result = search.Task.Step(granted);
        if (result.Status == PathStatus.Pending) return;

        _search = null;
        if (result.Status == PathStatus.Unreachable)
        {
            _path = [];
            _nextPathAt = _clock + FailedPathMs;
            return;
        }

        var start = 0;
        var nearest = float.PositiveInfinity;
        for (var index = 0; index < result.Path.Count; index += 1)
        {
            var point = result.Path[index];
            var distance = MathF.Sqrt(Square(point.X - actor.Position.X) + Square(point.Y - actor.Position.Y));
            if (distance >= nearest) break;

            start = index;
            nearest = distance;
        }

        _path = result.Path.Skip(start).ToList();
        if (search.Target is null && search.Range == 0 && (_path.Count > 0 || search.IsGoal(actor.Position)))
            _path.Add(search.Destination);
    }

    private bool ReachableNow(Combatant actor, Vec2 destination, Combatant target, float range)
    {
        var result = new PathSearch(actor.Position, SearchQuery(actor, destination, target, range)).Step(HoldSearchCap);
        return result.Status == PathStatus.Found && result.Path.Count > 0;
    }

    private Vec2? NextWaypoint()
    {
        var actor = _port.Actor(_id);
        if (actor is null || _disposed) return null;

        var capabilities = CombatStateRules.Capabilities(actor.States);
        if (!capabilities.CanMove || (_targetId is not null && !capabilities.CanAutoApproach)) return null;

        var target = _targetId is null ? null : _port.Actor(_targetId);
        if (target is not null && !CombatController.CanTargetCombatant(actor, target)) return null;

        while (_path.Count > 0 &&
               MathF.Sqrt(Square(actor.Position.X - _path[0].X) + Square(actor.Position.Y - _path[0].Y)) <=
               ArrivalDistancePx)
            _path.RemoveAt(0);

        if (_path.Count == 0) return null;

        var next = _path[0];
        return Pathfinding.CanTraverse(actor.Position, next, point => _port.CanStand(point)) ? next : null;
    }

    private void Wander(Combatant actor)
    {
        var config = _behavior.Wander;
        if (config is null || !_behavior.CanMove || !_behavior.CanTurn ||
            _behavior.MoveSpeedPxPerSecond <= 0 || !CombatStateRules.Capabilities(actor.States).CanMove)
        {
            _state = EnemyAiState.Idle;
            _port.Stop();
            return;
        }

        if (_wanderDestination is not null)
        {
            if (_progressPosition is not { } progress ||
                MathF.Sqrt(Square(actor.Position.X - progress.X) + Square(actor.Position.Y - progress.Y)) >=
                ArrivalDistancePx)
            {
                _progressPosition = actor.Position;
                _lastProgressAt = _clock;
            }

            if (NextWaypoint() is not null && _clock - _lastProgressAt < RepathIntervalMs)
            {
                _port.FollowPath(NextWaypoint);
                return;
            }

            _wanderDestination = null;
            _path = [];
            _state = EnemyAiState.Idle;
            _idleElapsed = 0;
            _port.Stop();
            return;
        }

        _state = EnemyAiState.Idle;
        _port.Stop();
        _idleElapsed += LogicFrameMs;
        if (_idleElapsed < config.IdleDurationMs) return;

        _idleElapsed = 0;
        var move = (int)Math.Floor(_random() * 100);
        var turn = (int)Math.Floor(_random() * 100);

        var facing = _port.Facing();
        if (turn < (move < 30 ? 50 : 30))
        {
            var index = Array.IndexOf(Directions, facing);
            facing = Directions[(index + 1) % Directions.Length];
            _port.Face(facing);
        }

        if (move >= 30) return;

        var distance = (int)Math.Floor(
            (1 + Math.Floor(_random() * 4) + 1 + Math.Floor(_random() * 4)) / 2) * PathGrid.Size;

        var destination = new Vec2(MathF.Truncate(actor.Position.X), MathF.Truncate(actor.Position.Y));
        if (facing is "west" or "east") destination = destination with { X = destination.X + (facing == "west" ? -1 : 1) * distance };
        else destination = destination with { Y = destination.Y + (facing == "north" ? -1 : 1) * distance };

        bool CanStand(Vec2 point) => _port.CanStand(point);
        if (!CanStand(destination)) return;

        var granted = _port.PathBudget?.Take(WanderSearchCap) ?? WanderSearchCap;
        if (granted <= 0) return;

        var query = new EnemyPathQuery(
            _port.NavigationBounds(),
            CanStand,
            point => MathF.Sqrt(Square(point.X - destination.X) + Square(point.Y - destination.Y)) <= PathGrid.Size &&
                     Pathfinding.CanTraverse(point, destination, CanStand),
            point => MathF.Max(0f,
                MathF.Sqrt(Square(point.X - destination.X) + Square(point.Y - destination.Y)) - PathGrid.Size));

        var result = new PathSearch(actor.Position, query).Step(granted);
        if (result.Status != PathStatus.Found) return;

        var path = result.Path.ToList();
        if (path.Count == 0 ||
            MathF.Sqrt(Square(path[^1].X - destination.X) + Square(path[^1].Y - destination.Y)) > ArrivalDistancePx)
            path.Add(destination);

        _path = path;
        _destination = null;
        _wanderDestination = destination;
        _state = EnemyAiState.Wandering;
        _progressPosition = actor.Position;
        _lastProgressAt = _clock;
        _port.FollowPath(NextWaypoint);
    }

    private static float Square(float value) => value * value;

    private sealed class EnemyPathQuery(
        Rect bounds,
        Func<Vec2, bool> canStand,
        Func<Vec2, bool> isGoal,
        Func<Vec2, float> heuristic) : IPathQuery
    {
        public Rect Bounds => bounds;

        public bool CanStand(Vec2 point) => canStand(point);

        public bool IsGoal(Vec2 point) => isGoal(point);

        public float Heuristic(Vec2 point) => heuristic(point);
    }
    private sealed class EnemyCombatPort(IEnemyPort inner, EnemyController owner) : ICombatPort
    {
        public IActorCooldownPort Cooldowns => inner.Cooldowns;

        public bool IsPaused() => inner.IsPaused();

        public bool CanSelectSkill(SkillPlayback skill) => inner.CanSelectSkill(skill);

        public Combatant? Actor(string id) => inner.Actor(id);

        public IReadOnlyList<Combatant> Candidates() => inner.Candidates();

        public bool IsAlive(string id) => inner.IsAlive(id);

        public IReadOnlyList<PartyBuffSlot?>? PartySlots() => inner.PartySlots();

        public int RangeBonusUnits(string id) => inner.RangeBonusUnits(id);

        public int? HealScore(string id) => inner.HealScore(id);

        public IReadOnlyList<HealSlot>? HealCandidates() => inner.HealCandidates();

        public string Facing() => inner.Facing();

        public void MoveToward(Vec2 position) => inner.MoveToward(position);

        public bool? ApproachTarget(Combatant target, int range) {
            var actor = inner.Actor(owner._id);
            if (actor is not null && owner._behavior.CanMove && owner._behavior.MoveSpeedPxPerSecond > 0) {
                if (owner.Navigate(actor, target.Position, target, range)) return true;
                owner._pendingReturn = true;
            } else inner.Stop();
            return false;
        }

        public bool CanFinishApproach(string targetId) => inner.Threat!.Has(targetId) && inner.CanFinishApproach(targetId);

        public BasicAttackSelection SelectBasicAttack(ResolvedBasicAttack normal) => inner.SelectBasicAttack(normal);

        public void Stop() => inner.Stop();

        public bool CanStand(Vec2 position, string? clearanceActorId = null) =>
            inner.CanStand(position, clearanceActorId);

        public bool? CanPlaceBody(Vec2 position, string? clearanceActorId = null) =>
            inner.CanPlaceBody(position, clearanceActorId);

        public bool? CanReach(Vec2 position) => inner.CanReach(position);

        public Rect? NavigationBounds() => inner.NavigationBounds();

        public void Relocate(Vec2 position, string? clearanceActorId = null) =>
            inner.Relocate(position, clearanceActorId);

        public void Face(string direction) => inner.Face(direction);

        public Action Play(CombatAction action, Action<int> frame, Action complete) =>
            inner.Play(action, frame, complete);

        public void Frame(CombatAction action, int index, Action<string, string?> contact,
            SkillUnitsReady unitsReady, Action? areaContact = null) =>
            inner.Frame(action, index, contact, unitsReady, areaContact);

        public void Contact(AttackContact contact) => inner.Contact(contact);

        public void AreaContact(BasicAreaContact contact) => inner.AreaContact(contact);

        public SkillUnitOutcome Unit(SkillUnitHit hit) => inner.Unit(hit);

        public PreparedDamage? PrepareUnit(SkillUnitStart start) => inner.PrepareUnit(start);

        public void RunUnitSequence(Action work) => inner.RunUnitSequence(work);

        public void UnitFeedback(SkillUnitHit hit, SkillUnitOutcome outcome, SkillEffectCue cue) =>
            inner.UnitFeedback(hit, outcome, cue);

        public int? ManaCost(SkillPlayback skill) => inner.ManaCost(skill);

        public bool CanSpendMana(int cost) => inner.CanSpendMana(cost);

        public bool SpendMana(int cost) => inner.SpendMana(cost);

        public void ClearEffects() => inner.ClearEffects();
    }
}
