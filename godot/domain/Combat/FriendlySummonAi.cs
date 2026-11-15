using System;
using System.Collections.Generic;
using System.Linq;
using Inotia4.Domain.Content;
using Inotia4.Domain.World;

namespace Inotia4.Domain.Combat;

public static class FriendlySummonAiState
{
    public const string Idle = "idle";
    public const string Following = "following";
    public const string Chasing = "chasing";
    public const string Attacking = "attacking";
    public const string Returning = "returning";
}

public static class FriendlySummonRole
{
    public const string HellSoldier = "hell-soldier";
    public const string Beast = "beast";
}

/// <summary>
/// 召唤物 AI 的世界端口（在敌人端口之上再加三件事：战术、召回、自主索敌许可）。
///
/// 控制器从头到尾只需要「可达范围」与「这一点能不能站」，C# 侧沿用
/// <see cref="IEnemyPort.NavigationSnapshot"/> 的 <see cref="IStandability"/> 即可，不多定义字段。
/// </summary>
public interface IFriendlySummonPort : IEnemyPort
{
    SummonTactics Tactics();

    bool TryRecall();

    /// <summary>自主索敌是否允许以该敌人为目标；展示型场景物件必须返回 false。</summary>
    bool AutonomousTarget(string id);
}

public static class FriendlySummonAiConstants
{
    public const int DecisionMs = 180;
    public const int HoldTargetMs = 600;
    public const int PathImprovementPx = 32;
    public const int FailedPathMs = 900;
    public const int FollowStartPx = 32;
    public const int FollowStopPx = 16;
    public const int JoinCombatPx = 128;
    public const int LeashPx = 192;
    public const int ReturnStopPx = 64;
    public const int FarRecallPx = 320;
    public const int RecallDelayMs = 5040;
    public const int ProgressPx = 4;
    public const int DestinationChangePx = 16;
    public const int SearchNodesPerFrame = 64;
}

public sealed class FriendlySummonState
{
    public required string OwnerId { get; init; }

    public required string Role { get; init; }

    public required string State { get; init; }

    public string? TargetId { get; init; }

    public bool Returning { get; init; }

    public long Clock { get; init; }

    public double Accumulator { get; init; }

    public long NextDecisionAt { get; init; }

    public long NextSearchAt { get; init; }

    public long NextRecallAt { get; init; }

    public long NextFollowSearchAt { get; init; }

    public long TargetSince { get; init; }

    public List<Vec2> Path { get; init; } = [];

    public string? PathTargetId { get; init; }

    public Vec2? PathDestination { get; init; }

    public bool FollowActive { get; init; }

    public List<FriendlyRememberedTarget> FailedTargets { get; init; } = [];

    public List<FriendlyRememberedTarget> CheckedTargets { get; init; } = [];

    public List<FriendlyRememberedTarget> FallbackTargets { get; init; } = [];

    public long FarElapsed { get; init; }

    public long StuckElapsed { get; init; }

    public Vec2? ProgressOrigin { get; init; }

    public Vec2? FollowDestination { get; init; }

    public bool FollowUnreachable { get; init; }

    public int FollowSlot { get; init; }

    public CombatSnapshot? Combat { get; init; }
}

public sealed record FriendlyRememberedTarget(string Id, long Until, Vec2 Position);

public static class FriendlySummonAi
{
    public static FriendlySummonState StationaryState(FriendlySummonState saved) => new()
    {
        OwnerId = saved.OwnerId,
        Role = saved.Role,
        State = FriendlySummonAiState.Idle,
        Returning = false,
        Clock = 0,
        Accumulator = 0,
        NextDecisionAt = 0,
        NextSearchAt = 0,
        NextRecallAt = 0,
        NextFollowSearchAt = 0,
        TargetSince = 0,
        Path = [],
        FollowActive = false,
        FailedTargets = [],
        CheckedTargets = [],
        FallbackTargets = [],
        FarElapsed = 0,
        StuckElapsed = 0,
        FollowUnreachable = false,
        FollowSlot = 0,
        Combat = saved.Combat is null
            ? null
            : new CombatSnapshot
            {
                State = "idle",
                Cooldowns = new Dictionary<string, Cooldown>(saved.Combat.Cooldowns),
            },
    };
}

public sealed record FriendlySummonDebugSnapshot(
    string Kind,
    string State,
    string? TargetId,
    IReadOnlyDictionary<string, Cooldown> Cooldowns,
    bool SearchPending,
    int ExpandedNodes);

internal sealed record FriendlyCandidate(Combatant Actor, int Priority, float Distance);

internal sealed class FriendlySearchWork
{
    public required PathSearch Task;
    public required string TargetId;
    public required Vec2 Destination;
    public required float Range;
    public required string Kind;
    public Vec2? Landing;
}

internal sealed record FriendlyReadyChoice(string TargetId, Vec2 Destination, float Range, IReadOnlyList<Vec2> Path);

public sealed class FriendlySummonController
{
    public const string Kind = "friendly-summon";

    public readonly CombatController CombatController;

    private readonly string _id;
    private readonly string _ownerId;
    private readonly string _role;
    private readonly EnemyBehavior _behavior;
    private readonly ResolvedBasicAttack? _attack;
    private readonly IFriendlySummonPort _port;

    private string _state = FriendlySummonAiState.Idle;
    private string? _targetId;
    private bool _returning;
    private long _clock;
    private double _accumulator;
    private long _nextDecisionAt;
    private long _nextSearchAt;
    private long _nextRecallAt;
    private long _nextFollowSearchAt;
    private long _targetSince;
    private List<Vec2> _path = [];
    private string? _pathTargetId;
    private Vec2? _pathDestination;
    private bool _followActive;
    private readonly Dictionary<string, FriendlyRememberedTarget> _failedTargets = new();
    private readonly Dictionary<string, FriendlyRememberedTarget> _checkedTargets = new();
    private readonly Dictionary<string, FriendlyRememberedTarget> _fallbackTargets = new();
    private long _farElapsed;
    private long _stuckElapsed;
    private Vec2? _progressOrigin;
    private Vec2? _followDestination;
    private bool _followUnreachable;
    private int _followSlot;
    private FriendlySearchWork? _search;
    private FriendlyReadyChoice? _readyChoice;
    private bool _disposed;
    private bool _searchedThisFrame;
    private int _expandedNodes;

    public FriendlySummonController(string id, string ownerId, string role, EnemyBehavior behavior,
        ResolvedBasicAttack? attack, IFriendlySummonPort port, CombatController? sharedCombat = null)
    {
        _id = id;
        _ownerId = ownerId;
        _role = role;
        _behavior = behavior;
        _attack = attack;
        _port = port;
        // 普攻只在射程内起手；受命动作也不能绕过有预算的寻路走直线。
        CombatController = sharedCombat ?? new CombatController(id, attack, new NoAutoApproachPort(port));
    }

    public string ActionState() => _state;

    public string? CurrentTargetId() => _targetId;

    public FriendlySummonDebugSnapshot Snapshot() => new(
        Kind, _state, _targetId, CombatController.Snapshot().Cooldowns, _search is not null, _expandedNodes);

    public FriendlySummonState CaptureState() => new()
    {
        OwnerId = _ownerId,
        Role = _role,
        State = _state,
        TargetId = _targetId,
        Returning = _returning,
        Clock = _clock,
        Accumulator = _accumulator,
        NextDecisionAt = _nextDecisionAt,
        NextSearchAt = _nextSearchAt,
        NextRecallAt = _nextRecallAt,
        NextFollowSearchAt = _nextFollowSearchAt,
        TargetSince = _targetSince,
        Path = [.. _path],
        PathTargetId = _pathTargetId,
        PathDestination = _pathDestination,
        FollowActive = _followActive,
        FailedTargets = [.. _failedTargets.Values],
        CheckedTargets = [.. _checkedTargets.Values],
        FallbackTargets = [.. _fallbackTargets.Values],
        FarElapsed = _farElapsed,
        StuckElapsed = _stuckElapsed,
        ProgressOrigin = _progressOrigin,
        FollowDestination = _followDestination,
        FollowUnreachable = _followUnreachable,
        FollowSlot = _followSlot,
        Combat = CombatController.Snapshot(),
    };

    public void RestoreState(FriendlySummonState saved)
    {
        if (_disposed) throw new InvalidOperationException("无法恢复已销毁的召唤物行为");
        if (saved.OwnerId != _ownerId || saved.Role != _role) throw new InvalidOperationException("召唤物主人或行为类型不匹配");
        Cancel();
        _state = saved.State;
        _targetId = saved.TargetId;
        _returning = saved.Returning;
        _clock = saved.Clock;
        _accumulator = saved.Accumulator;
        _nextDecisionAt = Math.Min(saved.NextDecisionAt, saved.Clock);
        _nextSearchAt = Math.Min(saved.NextSearchAt, saved.Clock);
        _nextRecallAt = saved.NextRecallAt;
        _nextFollowSearchAt = saved.NextFollowSearchAt;
        _targetSince = saved.TargetSince;
        _path = [.. saved.Path];
        _pathTargetId = saved.PathTargetId;
        _pathDestination = saved.PathDestination;
        _followActive = saved.FollowActive;
        foreach (var entry in saved.FailedTargets) _failedTargets[entry.Id] = entry;
        foreach (var entry in saved.CheckedTargets) _checkedTargets[entry.Id] = entry;
        foreach (var entry in saved.FallbackTargets) _fallbackTargets[entry.Id] = entry;
        _farElapsed = saved.FarElapsed;
        _stuckElapsed = saved.StuckElapsed;
        _progressOrigin = saved.ProgressOrigin;
        _followDestination = saved.FollowDestination;
        _followUnreachable = saved.FollowUnreachable;
        _followSlot = saved.FollowSlot;
        if (saved.Combat is { } combat) CombatController.RestoreState(combat);
        if (_path.Count > 0) _port.FollowPath(NextWaypoint);
    }

    public void Update(double deltaMs)
    {
        if (_disposed || _port.IsPaused() || !double.IsFinite(deltaMs) || deltaMs <= 0) return;
        _accumulator += deltaMs;
        while (_accumulator >= CombatClock.LogicFrameMs)
        {
            _accumulator -= CombatClock.LogicFrameMs;
            Step();
        }
    }

    public void Step()
    {
        if (_disposed || _port.IsPaused()) return;
        _clock += CombatClock.LogicFrameMs;
        _searchedThisFrame = false;
        _expandedNodes = 0;
        var actor = _port.Actor(_id);
        var owner = _port.Actor(_ownerId);
        if (actor is null || owner is null || _port.IsAlive(_id) is false || _port.IsAlive(_ownerId) is false)
        {
            Cancel();
            return;
        }
        CombatController.Update();
        var capabilities = CombatStateRules.Capabilities(actor.States ?? []);
        if (!capabilities.CanAct)
        {
            Disable();
            return;
        }
        var ownerDistance = Distance(actor.Position, owner.Position);
        if (ownerDistance > FriendlySummonAiConstants.LeashPx && !_returning)
        {
            _returning = true;
            DropTarget();
            ClearSearch();
            ClearPath();
        }
        else if (_returning && ownerDistance <= FriendlySummonAiConstants.ReturnStopPx)
        {
            _returning = false;
            _followActive = false;
            ClearSearch();
            ClearPath();
            _nextDecisionAt = _clock;
        }

        if (_targetId is not null && ValidTarget(actor, owner, _targetId) is null) TargetRemoved(_targetId);
        AdvanceSearch(actor, owner);
        ApplyReadyChoice(actor, owner);
        if (_clock >= _nextDecisionAt)
        {
            _nextDecisionAt = _clock + FriendlySummonAiConstants.DecisionMs;
            Decide(actor, owner);
        }
        AdvanceSearch(actor, owner);
        ApplyReadyChoice(actor, owner);
        Act(actor, owner);
        TrackRecovery(actor, owner);
    }

    public void OnContact(AttackContact contact)
    {
        if (!_disposed && (contact.TargetId == _id || contact.TargetId == _ownerId || contact.CasterId == _ownerId))
            _nextDecisionAt = _clock;
    }

    public void OnThreatAdded()
    {
        if (!_disposed) _nextDecisionAt = _clock;
    }

    public bool Command(EnemyPlayableSkill skill, string? targetId, int level)
    {
        if (_disposed || skill.Action is null) return false;
        var result = CombatController.RequestTriggeredAction(
            new SkillPlayback(skill.Id, skill.Action, level, skill.ManaCost, skill.Clips),
            targetId, _behavior.CanTurn);
        if (result != "started") return false;
        ClearPath();
        _state = FriendlySummonAiState.Attacking;
        return true;
    }

    public void TargetRemoved(string targetId)
    {
        CombatController.TargetRemoved(targetId);
        _failedTargets.Remove(targetId);
        _checkedTargets.Remove(targetId);
        _fallbackTargets.Remove(targetId);
        if (_search?.TargetId == targetId) _search = null;
        if (_readyChoice?.TargetId == targetId) _readyChoice = null;
        if (_targetId == targetId)
        {
            _targetId = null;
            ClearPath();
            _nextDecisionAt = _clock;
        }
        if (_pathTargetId == targetId) ClearPath();
    }

    public void Disable()
    {
        CombatController.Cancel(true, false);
        ClearPath();
        ClearSearch();
        _port.Stop();
        _farElapsed = 0;
        _stuckElapsed = 0;
        _progressOrigin = null;
        _followUnreachable = false;
        _state = _returning ? FriendlySummonAiState.Returning : FriendlySummonAiState.Idle;
    }

    public void Cancel()
    {
        CombatController.Cancel(true);
        _targetId = null;
        _returning = false;
        _state = FriendlySummonAiState.Idle;
        ClearPath();
        ClearSearch();
        _failedTargets.Clear();
        _checkedTargets.Clear();
        _fallbackTargets.Clear();
        _followActive = false;
        _farElapsed = 0;
        _stuckElapsed = 0;
        _progressOrigin = null;
        _followDestination = null;
        _followUnreachable = false;
        _accumulator = 0;
        _nextDecisionAt = _clock;
        _nextSearchAt = _clock;
        _nextRecallAt = _clock;
        _nextFollowSearchAt = _clock;
        _port.Stop();
    }

    public void Reset()
    {
        Cancel();
        CombatController.Reset();
    }

    public void Dispose()
    {
        Cancel();
        _disposed = true;
        CombatController.Dispose();
    }

    private Combatant? ValidTarget(Combatant actor, Combatant owner, string id)
    {
        var target = _port.Actor(id);
        return target is not null && target.Team != actor.Team && _port.IsAlive(id) is not false
            && CombatController.CanTargetCombatant(actor, target)
            && Distance(owner.Position, target.Position) <= FriendlySummonAiConstants.LeashPx
                ? target
                : null;
    }

    private int Priority(string targetId, SummonTactics tactics)
    {
        var ownerHealth = _port.Health(_ownerId);
        bool Threatens(string id) => tactics.Threats.Any(threat => threat.EnemyId == targetId && threat.TargetId == id);
        var ownerThreat = Threatens(_ownerId);
        if (ownerThreat && ownerHealth is { } health && health.Maximum > 0 &&
            (float)health.Current / health.Maximum <= .3f) return 0;
        var lowAlly = tactics.PartyIds.Any(id =>
        {
            var ally = _port.Health(id);
            return ally is { Current: > 0, Maximum: > 0 } value &&
                   (float)value.Current / value.Maximum <= .35f && Threatens(id);
        });
        var ownerTarget = tactics.OwnerTargetId == targetId;
        var focus = tactics.FocusTargetId == targetId;
        if (_role == FriendlySummonRole.HellSoldier)
            return ownerTarget ? 1 : focus ? 2 : ownerThreat || lowAlly ? 3 : 4;
        return ownerThreat ? 1 : Threatens(_id) ? 2 : lowAlly ? 3 : ownerTarget ? 4 : focus ? 5 : 6;
    }

    private bool InAwareness(Combatant actor, Combatant target)
    {
        Rect? bounds = _behavior.Engagement == "map-wide" ? _port.NavigationBounds() : null;
        return (int)Math.Floor(BasicAttackRules.TargetDistance(actor.Position, target.Position) / 8.0) <=
               EnemyEngagement.AwarenessRangeUnits(_behavior, bounds);
    }

    private List<FriendlyCandidate> Candidates(Combatant actor, Combatant owner, SummonTactics tactics)
    {
        var admitted = new HashSet<string>(tactics.EngagedTargetIds);
        var active = _behavior.Aggression == "active";
        var candidates = new List<FriendlyCandidate>();
        foreach (var target in _port.Candidates())
        {
            var engaged = admitted.Contains(target.Id);
            // 主动型角色按自身警戒范围发现敌人，不要求战斗已经发生（<c>CHAR_LookAroundEnemy</c>）。
            // 展示型场景物仍由世界侧的「能不能自主索敌」一道拦掉。
            var noticed = active && !engaged && _port.AutonomousTarget(target.Id) && InAwareness(actor, target);
            if ((!engaged && !noticed) || ValidTarget(actor, owner, target.Id) is null) continue;
            var committed = target.Id == tactics.OwnerTargetId || target.Id == tactics.FocusTargetId ||
                            target.Id == _targetId;
            if (Distance(owner.Position, target.Position) >
                (committed ? FriendlySummonAiConstants.LeashPx : FriendlySummonAiConstants.JoinCombatPx)) continue;
            if (Remembered(_failedTargets, target)) continue;
            candidates.Add(new FriendlyCandidate(target, Priority(target.Id, tactics),
                Distance(actor.Position, target.Position)));
        }

        return candidates
            .OrderBy(candidate => candidate.Priority)
            .ThenBy(candidate => candidate.Distance)
            .ThenBy(candidate => candidate.Actor.Id, StringComparer.Ordinal)
            .ToList();
    }

    private bool Remembered(Dictionary<string, FriendlyRememberedTarget> memory, Combatant target)
    {
        if (!memory.TryGetValue(target.Id, out var entry)) return false;
        if (entry.Until <= _clock ||
            Distance(entry.Position, target.Position) >= FriendlySummonAiConstants.DestinationChangePx)
        {
            memory.Remove(target.Id);
            return false;
        }

        return true;
    }

    private void Decide(Combatant actor, Combatant owner)
    {
        if (_returning)
        {
            FollowOwner(actor, owner);
            return;
        }
        var tactics = _port.Tactics();
        var candidates = Candidates(actor, owner, tactics);
        var current = candidates.FirstOrDefault(candidate => candidate.Actor.Id == _targetId);
        if (_targetId is not null && current is null) DropTarget();
        var best = candidates.FirstOrDefault();
        if (best is null)
        {
            FollowOwner(actor, owner);
            return;
        }
        if (current is null)
        {
            PlanTarget(actor, best.Actor);
            return;
        }
        var better = candidates.FirstOrDefault(candidate => candidate.Priority < current.Priority);
        if (better is not null)
        {
            PlanTarget(actor, better.Actor);
            return;
        }
        if (InRange(actor, current.Actor)) return;
        var routeValid = _pathTargetId == current.Actor.Id && _path.Count > 0 && NextWaypoint() is not null;
        if (!routeValid || _pathDestination is not { } pathDestination ||
            Distance(pathDestination, current.Actor.Position) >= FriendlySummonAiConstants.DestinationChangePx)
        {
            PlanTarget(actor, current.Actor);
            return;
        }
        if (_clock - _targetSince < FriendlySummonAiConstants.HoldTargetMs) return;
        var alternative = candidates.FirstOrDefault(candidate =>
            candidate.Actor.Id != current.Actor.Id && candidate.Priority == current.Priority &&
            !Remembered(_checkedTargets, candidate.Actor));
        if (alternative is not null) PlanTarget(actor, alternative.Actor);
    }

    private void FollowOwner(Combatant actor, Combatant owner)
    {
        var gap = EdgeGap(actor.Position, actor.Bounds, owner.Position, owner.Bounds);
        if (!_returning)
        {
            if (gap <= FriendlySummonAiConstants.FollowStopPx) _followActive = false;
            else if (gap > FriendlySummonAiConstants.FollowStartPx) _followActive = true;
        }
        if (!_returning && !_followActive)
        {
            ClearPath();
            _port.Stop();
            ResetStuck();
            return;
        }
        if (CombatController.ActionState() == CombatState.Attacking) return;
        if (!CanMove(actor))
        {
            ClearPath();
            return;
        }
        if (_followDestination is { } followDestination &&
            Distance(followDestination, owner.Position) >= FriendlySummonAiConstants.DestinationChangePx)
        {
            _followUnreachable = false;
            _stuckElapsed = 0;
            _followDestination = owner.Position;
            _nextFollowSearchAt = _clock;
        }
        if (_followUnreachable && _clock < _nextFollowSearchAt) return;
        var range = _returning ? FriendlySummonAiConstants.ReturnStopPx : FriendlySummonAiConstants.FollowStopPx;
        if (_pathTargetId != owner.Id || _path.Count == 0 ||
            _pathDestination is not { } pathDestination ||
            Distance(pathDestination, owner.Position) >= FriendlySummonAiConstants.DestinationChangePx ||
            NextWaypoint() is null)
        {
            BeginSearch(actor, owner, range, "follow");
        }
    }

    private void PlanTarget(Combatant actor, Combatant target)
    {
        if (_readyChoice?.TargetId == target.Id) return;
        if (InRange(actor, target))
        {
            _readyChoice = new FriendlyReadyChoice(target.Id, target.Position, AttackRange(), []);
            return;
        }
        if (CanMove(actor)) BeginSearch(actor, target, AttackRange(), "target");
    }

    private void BeginSearch(Combatant actor, Combatant target, float range, string kind)
    {
        if (_search is { } search && search.TargetId == target.Id && search.Kind == kind) return;
        if (_clock < _nextSearchAt) return;
        var frozen = _port.NavigationSnapshot() ??
            throw new InvalidOperationException("召唤物端口必须提供冻结通行快照");
        var destination = target.Position;
        var bounds = CopyBounds(target.Bounds);
        var actorBounds = CopyBounds(actor.Bounds);
        var returning = _returning;
        Vec2? landing = null;
        if (kind == "follow" && !returning || kind == "target" && !Remembered(_fallbackTargets, target))
        {
            var hash = StableSeed.Of(_id);
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var angle = (int)((hash + (kind == "follow" ? _followSlot : 0) + attempt) % 8) * MathF.PI / 4;
                var gap = kind == "follow"
                    ? FriendlySummonAiConstants.FollowStopPx
                    : Math.Max(0, range - 2) * PathGrid.Size;
                var point = new Vec2(
                    destination.X + bounds.X + bounds.Width / 2 - actorBounds.X - actorBounds.Width / 2 +
                    MathF.Cos(angle) * ((bounds.Width + actorBounds.Width) / 2 + gap),
                    destination.Y + bounds.Y + bounds.Height / 2 - actorBounds.Y - actorBounds.Height / 2 +
                    MathF.Sin(angle) * ((bounds.Height + actorBounds.Height) / 2 + gap));
                if (frozen.CanStand(point) && (kind == "follow" ||
                        BasicAttackRules.AttackDistance(point, actorBounds, destination, bounds) <= range))
                {
                    landing = point;
                    break;
                }
            }
        }

        var standability = frozen;
        bool IsGoal(Vec2 point) => landing is { } goal
            ? standability.CanStand(point) && Distance(point, goal) <= PathGrid.Size &&
              Pathfinding.CanTraverse(point, goal, standability.CanStand)
            : kind == "target"
                ? standability.CanStand(point) &&
                  BasicAttackRules.AttackDistance(point, actorBounds, destination, bounds) <= range
                : returning
                    ? standability.CanStand(point) && Distance(point, destination) <= range
                    : standability.CanStand(point) &&
                      EdgeGap(point, actorBounds, destination, bounds) <= range;
        var radius = kind == "target"
            ? MathF.Sqrt((actorBounds.Width + bounds.Width) * (actorBounds.Width + bounds.Width) +
                         (actorBounds.Height + bounds.Height) * (actorBounds.Height + bounds.Height)) +
              range * PathGrid.Size
            : MathF.Sqrt((actorBounds.Width + bounds.Width) * (actorBounds.Width + bounds.Width) +
                         (actorBounds.Height + bounds.Height) * (actorBounds.Height + bounds.Height)) + range;
        _search = new FriendlySearchWork
        {
            Task = new PathSearch(actor.Position, new SummonPathQuery(standability, IsGoal, point =>
                Math.Max(0, Distance(point, landing ?? destination) - (landing is not null ? PathGrid.Size : radius)))),
            TargetId = target.Id,
            Destination = destination,
            Range = range,
            Kind = kind,
            Landing = landing,
        };
        _nextSearchAt = _clock + FriendlySummonAiConstants.DecisionMs;
        if (kind == "follow") _followDestination = destination;
    }

    private void AdvanceSearch(Combatant actor, Combatant owner)
    {
        if (_search is null || _searchedThisFrame || !CanMove(actor)) return;
        var work = _search;
        _searchedThisFrame = true;
        var result = work.Task.Step(FriendlySummonAiConstants.SearchNodesPerFrame);
        _expandedNodes = result.Expanded;
        if (result.Status == PathStatus.Pending) return;
        _search = null;
        var target = _port.Actor(work.TargetId);
        if (target is null)
        {
            _nextDecisionAt = _clock;
            return;
        }
        var moved = Distance(work.Destination, target.Position) >= FriendlySummonAiConstants.DestinationChangePx;
        if (result.Status == PathStatus.Unreachable)
        {
            if (moved)
            {
                _nextDecisionAt = _clock;
                return;
            }
            if (work.Kind == "target")
            {
                var entry = new FriendlyRememberedTarget(work.TargetId,
                    _clock + FriendlySummonAiConstants.FailedPathMs, target.Position);
                if (work.Landing is not null) _fallbackTargets[work.TargetId] = entry;
                else
                {
                    _failedTargets[work.TargetId] = entry;
                    if (_targetId == work.TargetId) DropTarget();
                }
            }
            else
            {
                _followUnreachable = true;
                _followSlot = (_followSlot + 1) % 8;
                _nextFollowSearchAt = _clock + FriendlySummonAiConstants.FailedPathMs;
            }
            return;
        }

        var planned = work.Landing is { } landingPoint
            ? result.Path.Append(landingPoint).ToList()
            : result.Path;
        var joined = JoinPath(actor, planned);
        if (joined is null)
        {
            _nextDecisionAt = _clock;
            return;
        }
        if (work.Kind == "follow")
        {
            if (_targetId is not null && !_returning) return;
            _path = joined;
            _pathTargetId = owner.Id;
            _pathDestination = work.Destination;
            if (_followUnreachable)
            {
                _stuckElapsed = 0;
                _progressOrigin = actor.Position;
            }
            _followUnreachable = false;
            return;
        }
        _readyChoice = new FriendlyReadyChoice(work.TargetId, work.Destination, work.Range, joined);
    }

    private List<Vec2>? JoinPath(Combatant actor, IReadOnlyList<Vec2> path)
    {
        if (path.Count == 0) return [];
        var closest = 0;
        for (var index = 1; index < path.Count; index++)
            if (Distance(actor.Position, path[index]) < Distance(actor.Position, path[closest]))
                closest = index;
        if (!Pathfinding.CanTraverse(actor.Position, path[closest], point => _port.CanStand(point))) return null;
        return path.Skip(closest).ToList();
    }

    private void ApplyReadyChoice(Combatant actor, Combatant owner)
    {
        if (_readyChoice is null || _returning || CombatController.ActionState() == CombatState.Attacking) return;
        var choice = _readyChoice;
        _readyChoice = null;
        var candidates = Candidates(actor, owner, _port.Tactics());
        var candidate = candidates.FirstOrDefault(entry => entry.Actor.Id == choice.TargetId);
        if (candidate is null) return;
        var current = candidates.FirstOrDefault(entry => entry.Actor.Id == _targetId);
        var joined = JoinPath(actor, choice.Path);
        if (joined is null) return;
        if (current is { } currentCandidate && currentCandidate.Actor.Id != candidate.Actor.Id)
        {
            if (candidate.Priority > currentCandidate.Priority) return;
            if (candidate.Priority == currentCandidate.Priority)
            {
                var end = joined.Count > 0 ? joined[^1] : actor.Position;
                var reachesCurrentTarget =
                    BasicAttackRules.AttackDistance(end, actor.Bounds, candidate.Actor.Position,
                        candidate.Actor.Bounds) <= choice.Range;
                var inRange = InRange(actor, currentCandidate.Actor);
                var hasRoute = _pathTargetId == currentCandidate.Actor.Id && NextWaypoint() is not null;
                float? currentCost = inRange ? 0f
                    : hasRoute ? RouteLength(actor.Position, _path)
                    : null;
                if (!reachesCurrentTarget || _clock - _targetSince < FriendlySummonAiConstants.HoldTargetMs ||
                    currentCost is null ||
                    currentCost.Value - RouteLength(actor.Position, joined) < FriendlySummonAiConstants.PathImprovementPx)
                {
                    _checkedTargets[candidate.Actor.Id] = new FriendlyRememberedTarget(candidate.Actor.Id,
                        _clock + FriendlySummonAiConstants.FailedPathMs, candidate.Actor.Position);
                    return;
                }
            }
        }
        if (_targetId != candidate.Actor.Id)
        {
            _targetId = candidate.Actor.Id;
            _targetSince = _clock;
            CombatController.ClearSelection();
        }
        _path = joined;
        _pathTargetId = candidate.Actor.Id;
        _pathDestination = choice.Destination;
        _followActive = false;
        ResetStuck();
    }

    private void Act(Combatant actor, Combatant owner)
    {
        if (CombatController.ActionState() == CombatState.Attacking)
        {
            _state = FriendlySummonAiState.Attacking;
            return;
        }
        if (_returning)
        {
            _state = FriendlySummonAiState.Returning;
            Walk(actor);
            return;
        }
        var target = _targetId is not null ? ValidTarget(actor, owner, _targetId) : null;
        if (target is not null)
        {
            if (_attack is not null && InRange(actor, target))
            {
                ClearPath();
                CombatController.RequestTarget(target.Id, _behavior.CanTurn);
                _state = CombatController.ActionState() == CombatState.Attacking
                    ? FriendlySummonAiState.Attacking
                    : FriendlySummonAiState.Chasing;
            }
            else
            {
                _state = FriendlySummonAiState.Chasing;
                Walk(actor);
            }
        }
        else if (_followActive)
        {
            _state = FriendlySummonAiState.Following;
            Walk(actor);
        }
        else
        {
            _state = FriendlySummonAiState.Idle;
            _port.Stop();
        }
    }

    private bool CanMove(Combatant actor)
    {
        var state = CombatStateRules.Capabilities(actor.States ?? []);
        return _behavior.CanMove && _behavior.MoveSpeedPxPerSecond > 0 && state.CanMove && state.CanAutoApproach;
    }

    private float AttackRange() =>
        (_attack?.ApproachDistanceUnits ?? 0) + _port.RangeBonusUnits(_id);

    private bool InRange(Combatant actor, Combatant target) =>
        _attack is not null &&
        BasicAttackRules.AttackDistance(actor.Position, actor.Bounds, target.Position, target.Bounds) <=
        AttackRange();

    private void Walk(Combatant actor)
    {
        if (CanMove(actor) && NextWaypoint() is not null) _port.FollowPath(NextWaypoint);
        else _port.Stop();
    }

    private Vec2? NextWaypoint()
    {
        var actor = _port.Actor(_id);
        if (actor is null || _disposed || !CanMove(actor)) return null;
        if (_pathTargetId is { } pathTargetId && pathTargetId != _ownerId)
        {
            var target = _port.Actor(pathTargetId);
            if (target is null || !CombatController.CanTargetCombatant(actor, target)) return null;
        }
        while (_path.Count > 0 && Distance(actor.Position, _path[0]) <= .5f) _path.RemoveAt(0);
        if (_path.Count == 0) return null;
        var next = _path[0];
        if (!Pathfinding.CanTraverse(actor.Position, next, point => _port.CanStand(point)))
        {
            _path = [];
            _nextDecisionAt = _clock;
            return null;
        }
        return next;
    }

    private void TrackRecovery(Combatant actor, Combatant owner)
    {
        if (!CanMove(actor))
        {
            _farElapsed = 0;
            ResetStuck();
            return;
        }
        var ownerDistance = Distance(actor.Position, owner.Position);
        _farElapsed = ownerDistance > FriendlySummonAiConstants.FarRecallPx
            ? _farElapsed + CombatClock.LogicFrameMs
            : 0;
        var following = _returning || (_targetId is null && _followActive);
        // 贴身的正常待机不该反复清零计时；但主人就在 64px 内、召唤物却被地形隔开时，
        // 它同样需要走一次召回，否则会一直停在墙的另一侧。
        var blockedPath = _followUnreachable || (_pathTargetId == owner.Id && _path.Count > 0);
        if (!following || CombatController.ActionState() != CombatState.Idle ||
            (ownerDistance <= FriendlySummonAiConstants.ReturnStopPx && !blockedPath))
        {
            ResetStuck();
            return;
        }
        _progressOrigin ??= actor.Position;
        if (_followDestination is { } followDestination &&
            Distance(followDestination, owner.Position) >= FriendlySummonAiConstants.DestinationChangePx)
        {
            ResetStuck();
            _followDestination = owner.Position;
            _nextFollowSearchAt = _clock;
        }
        if (_progressOrigin is { } progressOrigin &&
            Distance(progressOrigin, actor.Position) >= FriendlySummonAiConstants.ProgressPx)
        {
            _stuckElapsed = 0;
            _progressOrigin = actor.Position;
            _followUnreachable = false;
            _nextFollowSearchAt = _clock;
        }
        else if (_followUnreachable || _pathTargetId == owner.Id && _path.Count > 0)
        {
            _stuckElapsed += CombatClock.LogicFrameMs;
        }
        if (_search is not null && _path.Count == 0) return;
        if (_farElapsed < FriendlySummonAiConstants.RecallDelayMs &&
            _stuckElapsed < FriendlySummonAiConstants.RecallDelayMs) return;
        if (_clock < _nextRecallAt) return;
        _nextRecallAt = _clock + FriendlySummonAiConstants.DecisionMs;
        if (!_port.TryRecall()) return;
        DropTarget();
        ClearSearch();
        ClearPath();
        _returning = false;
        _followActive = false;
        _state = FriendlySummonAiState.Idle;
        _farElapsed = 0;
        ResetStuck();
        _nextDecisionAt = _clock;
    }

    private void ResetStuck()
    {
        _stuckElapsed = 0;
        _progressOrigin = null;
        _followUnreachable = false;
    }

    private void DropTarget()
    {
        _targetId = null;
        ClearPath();
        if (CombatController.ActionState() == CombatState.Approaching) CombatController.Cancel(false);
        else CombatController.ClearSelection();
    }

    private void ClearPath()
    {
        _path = [];
        _pathTargetId = null;
        _pathDestination = null;
        _port.Stop();
    }

    private void ClearSearch()
    {
        _search = null;
        _readyChoice = null;
    }

    private static float Distance(Vec2 a, Vec2 b) => MathF.Sqrt(
        (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static float EdgeGap(Vec2 a, CollisionBox actor, Vec2 b, CollisionBox target)
    {
        var dx = MathF.Max(0f, MathF.Max(
            a.X + actor.X - b.X - target.X - target.Width,
            b.X + target.X - a.X - actor.X - actor.Width));
        var dy = MathF.Max(0f, MathF.Max(
            a.Y + actor.Y - b.Y - target.Y - target.Height,
            b.Y + target.Y - a.Y - actor.Y - actor.Height));
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private static float RouteLength(Vec2 start, IReadOnlyList<Vec2> path)
    {
        float total = 0;
        var previous = start;
        foreach (var point in path)
        {
            total += Distance(previous, point);
            previous = point;
        }

        return total;
    }

    private static CollisionBox CopyBounds(CollisionBox bounds) => new()
    {
        X = bounds.X,
        Y = bounds.Y,
        Width = bounds.Width,
        Height = bounds.Height,
    };

    private sealed class SummonPathQuery(
        IStandability standability,
        Func<Vec2, bool> isGoal,
        Func<Vec2, float> heuristic) : IPathQuery
    {
        public Rect Bounds => standability.Bounds;

        public bool CanStand(Vec2 point) => standability.CanStand(point);

        public bool IsGoal(Vec2 point) => isGoal(point);

        public float Heuristic(Vec2 point) => heuristic(point);
    }

    /// <summary>
    /// 共用战斗控制器时的包装：除了接近一项恒为「这一帧走不到」，其余逐成员转发。
    /// （召唤物自己算路径，不能让战斗控制器再自作主张往目标身上贴。）
    /// </summary>
    private sealed class NoAutoApproachPort(ICombatPort inner) : ICombatPort
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

        public bool? ApproachTarget(Combatant target, int range) => false;

        public bool CanFinishApproach(string targetId) => inner.CanFinishApproach(targetId);

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
