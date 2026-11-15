using System;
using System.Collections.Generic;
using Inotia4.Domain.Content;
using Inotia4.Domain.World;

namespace Inotia4.Domain.Combat;

public static class PartySkillKind
{
    public const string Chance = "chance";
    public const string CombatChance = "combat-chance";
    public const string Heal = "heal";
    public const string AllyCombat = "ally-combat";
    public const string EmergencyHeal = "emergency-heal";
}

public interface IPartyPort : ICombatPort
{
    Combatant? Leader();

    string? FocusTarget();

    string? PartyTarget() => null;

    bool InCombat();

    CounterattackMode Counterattack();

    int DamageCount();

    /// <summary>地图边界：寻路不会把人带出这张图。基类把这一项写成可空，
    /// 伙伴 AI 每帧都要用它收边，所以这里收窄成一定有值。</summary>
    new Rect NavigationBounds();

    PathBudget? PathBudget => null;

    void FollowPath(Func<Vec2?> nextWaypoint);

    void Regroup()
    {
    }

    IReadOnlyList<EnemyPlayableSkill> Skills();

    string? SkillKind(string id);

    (int Health, int MaxHealth)? Vitals(string id);

    bool Attacking(string id);

    int AttackRange();
}

public sealed class PartyControllerState
{
    public string? Retaliation { get; set; }

    public double Accumulator { get; set; }

    public long Clock { get; set; }

    public long NextPathAt { get; set; }

    public List<Vec2> Path { get; set; } = [];

    public Vec2? Destination { get; set; }

    public string? DestinationTarget { get; set; }

    public float? DestinationRange { get; set; }
}

public static class PartyAiConstants
{
    public const int PartyFollowDistanceUnits = 4;

    public const int PartyRepathDistancePx = PathGrid.Size * 3;

    public const int PartyRepathIntervalMs = 180;

    public const int PartySearchCap = 384;

    /// <summary>不可达后的重试间隔。</summary>
    public const int PartyFailedPathMs = 600;

    public const int PartyFollowStuckMs = 1500;

    public const int PartyChaseRecoveryMs = 2400;

    public const int PartyFollowProgressPx = 4;

    public const int PartyRegroupDistancePx = 48;

    public static readonly int[] CounterattackModes = [0, 1, 2];

    public const int DelayedCounterattackHits = 3;
}

public enum CounterattackMode
{
    Off = 0,
    Delayed = 1,
    On = 2,
}

public static class PartyAi
{
    public static bool CounterattackReady(CounterattackMode mode, int damageCount) =>
        mode == CounterattackMode.On ||
        (mode == CounterattackMode.Delayed && damageCount >= PartyAiConstants.DelayedCounterattackHits);

    public static int SkillThreshold(string kind, int frequency, bool following)
    {
        var high = frequency == 2;
        if (kind == PartySkillKind.Chance) return high ? 100 : 30;
        if (kind == PartySkillKind.CombatChance) return following ? (high ? 100 : 50) : (high ? 50 : 30);
        if (kind == PartySkillKind.Heal) return high ? 70 : 50;
        if (kind == PartySkillKind.EmergencyHeal) return high ? 40 : 20;
        return high ? 80 : 60;
    }
}

public sealed class PartyController
{
    private readonly string _id;
    private readonly IPartyPort _port;
    private readonly Random _random;

    public readonly CombatController CombatController;

    private string? _retaliation;
    private double _accumulator;
    private long _clock;
    private long _nextPathAt;
    private List<Vec2> _path = [];
    /// <summary>跨帧推进的寻路搜索；每帧只领一段预算，不能在预算用完时丢掉重来。</summary>
    private PathSearch? _search;
    private long _pathRetryAt;
    private Vec2? _followProgress;
    private long _followProgressAt;
    private (string TargetId, Vec2 Position, long Since)? _chaseProgress;
    private long _chaseRecoveryUntil;
    private Vec2? _destination;
    private string? _destinationTarget;
    private float? _destinationRange;
    private bool _disposed;

    public PartyController(string id, IPartyPort port, CombatController combat, Random? random = null)
    {
        _id = id;
        _port = port;
        _random = random ?? new Random();
        CombatController = combat;
    }

    public CombatSnapshot Snapshot() => CombatController.Snapshot();

    public CombatState ActionState() => CombatController.ActionState();

    public string? SelectedTargetId() => CombatController.SelectedTargetId;

    public PartyControllerState CaptureState() => new()
    {
        Retaliation = _retaliation,
        Accumulator = _accumulator,
        Clock = _clock,
        NextPathAt = _nextPathAt,
        Path = [.. _path],
        Destination = _destination,
        DestinationTarget = _destinationTarget,
        DestinationRange = _destinationRange,
    };

    public void RestoreState(PartyControllerState state)
    {
        _retaliation = state.Retaliation;
        _accumulator = state.Accumulator;
        _clock = state.Clock;
        _nextPathAt = state.NextPathAt;
        _path = [.. state.Path];
        _destination = state.Destination;
        _destinationTarget = state.DestinationTarget;
        _destinationRange = state.DestinationRange;
        // 搜索持有的是旧场地的闭包，读档后必须重新开始。
        _search = null;
        _pathRetryAt = 0;
        _followProgress = null;
        _followProgressAt = 0;
        _chaseProgress = null;
        _chaseRecoveryUntil = 0;
    }

    public void OnContact(AttackContact contact)
    {
        if (_disposed || contact.TargetId != _id ||
            !PartyAi.CounterattackReady(_port.Counterattack(), _port.DamageCount())) return;
        var actor = _port.Actor(_id);
        var source = _port.Actor(contact.CasterId);
        if (actor is not null && source is not null && actor.Team != source.Team &&
            CombatController.CanTargetCombatant(actor, source))
            _retaliation = source.Id;
    }

    public void TargetRemoved(string targetId)
    {
        CombatController.TargetRemoved(targetId);
        if (_retaliation == targetId) _retaliation = null;
        if (_destinationTarget != targetId) return;
        _path = [];
        _search = null;
        _pathRetryAt = 0;
        _followProgress = null;
        _followProgressAt = 0;
        _destination = null;
        _destinationTarget = null;
        _destinationRange = null;
        _nextPathAt = 0;
        _port.Stop();
    }

    public void Cancel()
    {
        CombatController.Cancel(true);
        _retaliation = null;
        _path = [];
        _search = null;
        _pathRetryAt = 0;
        _followProgress = null;
        _followProgressAt = 0;
        _chaseProgress = null;
        _chaseRecoveryUntil = 0;
        _destination = null;
        _destinationTarget = null;
        _destinationRange = null;
        _nextPathAt = 0;
        _accumulator = 0;
        _port.Stop();
    }

    public void Disable() => Cancel();

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

    public void Update(double deltaMs)
    {
        if (_disposed || !double.IsFinite(deltaMs) || deltaMs <= 0) return;
        _accumulator += deltaMs;
        while (_accumulator >= CombatClock.LogicFrameMs)
        {
            _accumulator -= CombatClock.LogicFrameMs;
            Step();
        }
    }

    public void Step()
    {
        if (_disposed) return;
        _clock += CombatClock.LogicFrameMs;
        Tick();
    }

    private void Tick()
    {
        var actor = _port.Actor(_id);
        if (actor is null)
        {
            _port.Stop();
            return;
        }
        var retaliating = _retaliation is { } retaliationId ? _port.Actor(retaliationId) : null;
        if (retaliating is not null && !CombatController.CanTargetCombatant(actor, retaliating))
            _retaliation = null;
        if (_port.Counterattack() == CounterattackMode.Off && _retaliation is not null)
        {
            _retaliation = null;
            if (_port.FocusTarget() is null)
            {
                CombatController.Cancel(true);
                _path = [];
                _destination = null;
                _nextPathAt = 0;
                _port.Stop();
            }
        }
        CombatController.Update();
        if (!CombatStateRules.Capabilities(actor.States ?? []).CanAct)
        {
            _path = [];
            _port.Stop();
            _followProgress = null;
            _followProgressAt = 0;
            return;
        }
        var actionState = CombatController.ActionState();
        if (actionState == CombatState.Approaching)
        {
            TrackChaseProgress(actor, CombatController.SelectedTargetId ?? "approach");
            return;
        }
        if (actionState != CombatState.Idle) return;
        if (_port.Counterattack() == CounterattackMode.Off) _retaliation = null;
        var inCombat = _port.InCombat();
        var recovering = _clock < _chaseRecoveryUntil;
        var target = recovering
            ? null
            : HostileTarget(actor,
            [
                _port.FocusTarget(),
                _retaliation,
                inCombat ? _port.PartyTarget() : null,
                inCombat ? CombatController.SelectedTargetId : null,
            ]);
        if (target is null) _retaliation = null;
        // 重新选中目标时清掉旧的队长跟随计时；没有目标但处于战斗时仍走队长跟随，
        // 这条计时不能被 inCombat 每帧清零，否则掉队收拢在战斗中永远等不到触发。
        if (target is not null)
        {
            _followProgress = null;
            _followProgressAt = 0;
        }
        if (inCombat)
        {
            var cooldowns = CombatController.Snapshot().Cooldowns;
            foreach (var skill in _port.Skills())
            {
                var frequency = skill.Frequency ?? 1;
                var kind = _port.SkillKind(skill.Id);
                if (skill.Action is not { } action) continue;
                var playback = new SkillPlayback(skill.Id, action, skill.Level, skill.ManaCost, skill.Clips);
                if (frequency == 0 || kind is null || cooldowns.ContainsKey(skill.Id) ||
                    !CombatController.CanSelectSkill(playback) ||
                    !_port.CanSpendMana(skill.ManaCost ?? 0)) continue;
                if (SkillActionRules.NeedsTarget(action) &&
                    (target is null || BasicAttackRules.AttackDistance(
                        actor.Position, actor.Bounds, target.Position, target.Bounds) >
                        action.ApproachDistanceUnits))
                    continue;
                var threshold = PartyAi.SkillThreshold(kind, frequency, target is null);
                var ready = false;
                if (kind == PartySkillKind.Heal || kind == PartySkillKind.EmergencyHeal)
                {
                    ready = System.Linq.Enumerable.Any(_port.Candidates(), ally =>
                    {
                        var hp = _port.Vitals(ally.Id);
                        return ally.Team == actor.Team && hp is { } vitals && vitals.Health > 0 &&
                               vitals.Health < (int)(vitals.MaxHealth * threshold / 100.0) &&
                               BasicAttackRules.TargetDistance(actor.Position, ally.Position) <=
                               action.SearchRadiusPx;
                    });
                }
                else if (kind == PartySkillKind.AllyCombat)
                {
                    ready = System.Linq.Enumerable.Any(_port.Candidates(), ally =>
                        ally.Id != actor.Id && ally.Team == actor.Team && _port.Attacking(ally.Id) &&
                        Roll(threshold));
                }
                else ready = Roll(threshold);
                if (ready && CombatController.Request(playback, target?.Id) == "started")
                {
                    _path = [];
                    return;
                }
            }
        }
        // 队伍已开战但没有可攻击目标：走和玩家攻击键相同的默认普攻请求，
        // 由 CombatController 自行索敌并接近。附近没有敌人时不能停在这里等——
        // 仍要回到队长身边，否则战斗发生在别处时佣兵会一直留在原地。
        // 收拢后的短暂恢复期不再自动索敌，避免同一面墙前反复瞬移。
        if (target is null && inCombat && !recovering &&
            CombatController.Request() != "no-target") return;
        if (target is { } chosen)
        {
            var range = _port.AttackRange();
            if (BasicAttackRules.AttackDistance(actor.Position, actor.Bounds,
                    chosen.Position, chosen.Bounds) <= range)
            {
                _chaseProgress = null;
                _path = [];
                CombatController.RequestTarget(chosen.Id);
            }
            else
            {
                if (TrackChaseProgress(actor, chosen.Id)) return;
                Follow(actor, chosen, range);
            }
            return;
        }
        _chaseProgress = null;
        var leader = _port.Leader();
        if (leader is null || leader.Id == actor.Id ||
            BasicAttackRules.AttackDistance(actor.Position, actor.Bounds,
                leader.Position, leader.Bounds) < PartyAiConstants.PartyFollowDistanceUnits)
        {
            _path = [];
            _port.Stop();
            _followProgress = null;
            _followProgressAt = 0;
            return;
        }
        Follow(actor, leader, PartyAiConstants.PartyFollowDistanceUnits - 1, true);
    }

    private Combatant? HostileTarget(Combatant actor, IReadOnlyList<string?> ids)
    {
        foreach (var id in ids)
        {
            var candidate = id is null ? null : _port.Actor(id);
            if (candidate is not null && candidate.Team != actor.Team &&
                CombatController.CanTargetCombatant(actor, candidate)) return candidate;
        }

        return null;
    }

    private bool Roll(int threshold) => 1 + (int)(_random.NextDouble() * 99) < threshold;

    /// <summary>
    /// 追击敌人或自动接近时，连续一段时间没有真实位移就收拢一次。
    ///
    /// 目标追击和默认普攻的直线接近都不走“跟随队长”分支；如果没有这条兜底，
    /// 被剧情障碍封在入口的佣兵会一直重算同一条不可达路线。收拢后短暂停手，
    /// 避免目标仍然不可达时每 1.5 秒重复瞬移。
    /// </summary>
    private bool TrackChaseProgress(Combatant actor, string targetId)
    {
        if (_clock < _chaseRecoveryUntil) return false;
        var previous = _chaseProgress;
        if (previous is not { } progress || progress.TargetId != targetId)
        {
            _chaseProgress = (targetId, actor.Position, _clock);
            return false;
        }
        if (Distance(actor.Position, progress.Position) >= PartyAiConstants.PartyFollowProgressPx)
        {
            _chaseProgress = (progress.TargetId, actor.Position, _clock);
            return false;
        }
        if (_search is not null) return false;
        if (_clock - progress.Since < PartyAiConstants.PartyFollowStuckMs) return false;
        _chaseProgress = null;
        _retaliation = null;
        _path = [];
        _search = null;
        _pathRetryAt = 0;
        _destination = null;
        _destinationTarget = null;
        _destinationRange = null;
        _nextPathAt = 0;
        CombatController.Cancel(true);
        _port.Stop();
        _port.Regroup();
        // 宿主的收拢会调用本控制器的 cancel()；冷却必须放在收拢之后，否则会被清掉。
        _chaseRecoveryUntil = _clock + PartyAiConstants.PartyChaseRecoveryMs;
        return true;
    }

    private void Follow(Combatant actor, Combatant leader, float range, bool regroup = false)
    {
        var capabilities = CombatStateRules.Capabilities(actor.States ?? []);
        if (!capabilities.CanMove || (actor.Team != leader.Team && !capabilities.CanAutoApproach))
        {
            _port.Stop();
            _followProgress = null;
            _followProgressAt = 0;
            return;
        }
        if (_destinationTarget != leader.Id || _destinationRange != range)
        {
            _path = [];
            _search = null;
            _pathRetryAt = 0;
            _followProgress = null;
            _followProgressAt = 0;
            _destination = null;
            _nextPathAt = 0;
            _destinationTarget = leader.Id;
            _destinationRange = range;
        }
        if (regroup)
        {
            if (TrackFollowProgress(actor, leader)) return;
            if (_search is null && _path.Count > 0 && _clock >= _pathRetryAt && NextWaypoint() is null)
            {
                _path = [];
                _search = null;
                _nextPathAt = _clock;
                _pathRetryAt = _clock + PartyAiConstants.PartyRepathIntervalMs;
            }
        }
        else
        {
            _followProgress = null;
            _followProgressAt = 0;
        }
        if (_search is not null) AdvanceSearch(actor);
        else if (_clock >= _nextPathAt && (_destination is not { } destination ||
                 BasicAttackRules.TargetDistance(destination, leader.Position) >=
                 PartyAiConstants.PartyRepathDistancePx ||
                 _path.Count == 0 || NextWaypoint() is null))
        {
            // 先领预算再建查询：没有预算的帧连地图边界都不该读。
            var granted = _port.PathBudget?.Take(PartyAiConstants.PartySearchCap) ??
                          PartyAiConstants.PartySearchCap;
            if (granted > 0) BeginSearch(actor, leader, range, granted);
            else _nextPathAt = _clock + CombatClock.LogicFrameMs;
        }
        if (NextWaypoint() is not null) _port.FollowPath(NextWaypoint);
        else _port.Stop();
    }

    /// <summary>
    /// 跟随队长时的掉队保护：连续一段时间一步没挪，就不再继续等寻路结果，
    /// 让宿主把佣兵放到队长旁边的合法落点。走不通的路线可以重算，但人不能永远留在原地。
    /// </summary>
    private bool TrackFollowProgress(Combatant actor, Combatant leader)
    {
        var previous = _followProgress;
        if (previous is null || Distance(actor.Position, previous.Value) >=
            PartyAiConstants.PartyFollowProgressPx)
        {
            _followProgress = actor.Position;
            _followProgressAt = _clock;
            return false;
        }
        if (_clock - _followProgressAt < PartyAiConstants.PartyFollowStuckMs) return false;
        if (Distance(actor.Position, leader.Position) < PartyAiConstants.PartyRegroupDistancePx)
        {
            _followProgressAt = _clock;
            return false;
        }
        _followProgress = actor.Position;
        _followProgressAt = _clock;
        _path = [];
        _search = null;
        _pathRetryAt = 0;
        _destination = null;
        _nextPathAt = 0;
        _port.Stop();
        _port.Regroup();
        return true;
    }

    private void BeginSearch(Combatant actor, Combatant leader, float range, int granted)
    {
        _nextPathAt = _clock + PartyAiConstants.PartyRepathIntervalMs;
        _destination = leader.Position;
        bool CanStand(Vec2 point) => _port.CanStand(point);
        var radius = MathF.Sqrt(
            (actor.Bounds.Width + leader.Bounds.Width) * (actor.Bounds.Width + leader.Bounds.Width) +
            (actor.Bounds.Height + leader.Bounds.Height) * (actor.Bounds.Height + leader.Bounds.Height)) +
            range * PathGrid.Size;
        _search = new PathSearch(actor.Position, new PartyPathQuery(_port, leader, actor.Bounds, range,
            CanStand, radius));
        StepSearch(actor, granted);
    }

    private void AdvanceSearch(Combatant actor)
    {
        var granted = _port.PathBudget?.Take(PartyAiConstants.PartySearchCap) ??
                      PartyAiConstants.PartySearchCap;
        if (granted > 0) StepSearch(actor, granted);
    }

    private void StepSearch(Combatant actor, int granted)
    {
        var search = _search;
        if (search is null) return;
        var result = search.Step(granted);
        if (result.Status == PathStatus.Pending) return;
        _search = null;
        if (result.Status == PathStatus.Unreachable)
        {
            _path = [];
            _nextPathAt = _clock + PartyAiConstants.PartyFailedPathMs;
            return;
        }
        var start = 0;
        var nearest = float.PositiveInfinity;
        for (var index = 0; index < result.Path.Count; index++)
        {
            var point = result.Path[index];
            var distance = Distance(point, actor.Position);
            if (distance >= nearest) break;
            start = index;
            nearest = distance;
        }

        _path = [.. result.Path.Skip(start)];
    }

    private Vec2? NextWaypoint()
    {
        var actor = _port.Actor(_id);
        if (actor is null || _disposed) return null;
        var target = _destinationTarget is { } destinationTarget ? _port.Actor(destinationTarget) : null;
        var capabilities = CombatStateRules.Capabilities(actor.States ?? []);
        if (!capabilities.CanMove ||
            (target is not null && actor.Team != target.Team &&
             (!capabilities.CanAutoApproach || !CombatController.CanTargetCombatant(actor, target))))
            return null;
        while (_path.Count > 0)
        {
            var point = _path[0];
            if (Distance(actor.Position, point) > .5f) break;
            // 脚点离网格点很近，但还没真正对齐。若从当前位置走不到下一个点，
            // 这个点就是用来把身体拉回网格的，不能提前丢掉，否则会永远卡在障碍边缘。
            if (_path.Count > 1 &&
                !Pathfinding.CanTraverse(actor.Position, _path[1], probe => _port.CanStand(probe)))
                break;
            _path.RemoveAt(0);
        }
        if (_path.Count == 0) return null;
        var next = _path[0];
        return Pathfinding.CanTraverse(actor.Position, next, probe => _port.CanStand(probe))
            ? next
            : null;
    }

    private static float Distance(Vec2 a, Vec2 b) => MathF.Sqrt(
        (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private sealed class PartyPathQuery(
        IPartyPort port,
        Combatant leader,
        CollisionBox actorBounds,
        float range,
        Func<Vec2, bool> canStand,
        float radius) : IPathQuery
    {
        public Rect Bounds => port.NavigationBounds();

        public bool CanStand(Vec2 point) => canStand(point);

        public bool IsGoal(Vec2 point) => canStand(point) &&
            BasicAttackRules.AttackDistance(point, actorBounds, leader.Position, leader.Bounds) <= range;

        public float Heuristic(Vec2 point) => Math.Max(0f,
            MathF.Sqrt((point.X - leader.Position.X) * (point.X - leader.Position.X) +
                       (point.Y - leader.Position.Y) * (point.Y - leader.Position.Y)) - radius);
    }
}
