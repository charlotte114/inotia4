using System;
using System.Collections.Generic;
using System.Linq;
using Inotia4.Domain.Content;
using Inotia4.Domain.World;

namespace Inotia4.Domain.Combat;

public sealed record Combatant(
    string Id,
    string Team,
    Vec2 Position,
    CollisionBox Bounds,
    string? Facing = null,
    IReadOnlyList<string>? States = null) : IPlacedRect;

public sealed record CombatSnapshot
{
    public required string State { get; init; }

    public string? TargetId { get; init; }

    public IReadOnlyDictionary<string, Cooldown> Cooldowns { get; init; } =
        new Dictionary<string, Cooldown>();
}

public sealed record ActiveCombatAction(string State, string? TargetId, SkillPlayback? Skill);

public sealed record SkillPlayback(
    string Id,
    SkillAction Action,
    int? Level = null,
    int? ManaCost = null,
    IReadOnlyDictionary<string, string>? Clips = null);

public sealed record CapturedTarget(string Id, Vec2 Position);

public sealed class CombatAction
{
    public SkillActionRules.SkillRepeatState? RepeatState { get; set; }

    public List<CapturedTarget>? CapturedTargets { get; set; }

    public ChargePlan? Charge { get; set; }

    public required Combatant Caster { get; set; }

    public Combatant? Target { get; set; }

    public required string Facing { get; set; }

    public SkillPlayback? Skill { get; set; }

    public ResolvedBasicAttack? BasicAttack { get; init; }

    public int? BasicAttackWait { get; init; }

    public Func<int>? GetAttackSpeedRate { get; set; }
}

public sealed class BasicAttackSelection
{
    private BasicAttackSelection(ResolvedBasicAttack? attack, int? waitFrames)
    {
        Attack = attack;
        WaitFrames = waitFrames;
    }

    public ResolvedBasicAttack? Attack { get; }

    public int? WaitFrames { get; }

    public static BasicAttackSelection Of(ResolvedBasicAttack attack) => new(attack, null);

    public static BasicAttackSelection Wait(int durationFrames) => new(null, durationFrames);
}

public sealed record AttackContact(
    string CasterId,
    string TargetId,
    string? SkillId = null,
    string? Hand = null,
    ResolvedBasicAttack? BasicAttack = null);

public sealed record BasicAreaContact(string CasterId, ResolvedBasicAttack BasicAttack);

/// <summary>
/// 一个「执行单元」事件在表现层的落地对象。同一个对象被问两次时必须给同一份结果，
/// 所以第一次算出来的结果直接缓存在对象上，后续取用不再重算。
/// </summary>
public sealed class SkillUnitsArrival
{
    public required UnitsEvent Event { get; init; }

    public string? SelectedTargetId { get; init; }

    public Vec2? ImpactCenter { get; set; }

    internal List<ResolvedSkillUnit>? Results { get; set; }
}

public delegate IReadOnlyList<ResolvedSkillUnit> SkillUnitsReady(SkillUnitsArrival arrival);

public interface IApproachProfile
{
    int ApproachDistanceUnits { get; }

    float SearchRadiusPx { get; }
}

public interface ICombatPort
{
    IActorCooldownPort Cooldowns => AlwaysReadyCooldownPort.Instance;

    void SkillSounded(SkillPlayback skill) { }

    bool IsPaused() => false;

    bool CanSelectSkill(SkillPlayback skill) => string.IsNullOrEmpty(skill.Action.RequiredOffhand);

    Combatant? Actor(string id);

    IReadOnlyList<Combatant> Candidates();

    bool IsAlive(string id) => true;

    IReadOnlyList<PartyBuffSlot?>? PartySlots() => null;

    int RangeBonusUnits(string id) => 0;

    int? HealScore(string id) => null;

    IReadOnlyList<HealSlot>? HealCandidates() => null;

    string Facing();

    void MoveToward(Vec2 position);

    bool? ApproachTarget(Combatant target, int range) => null;

    bool CanFinishApproach(string targetId) => true;

    BasicAttackSelection SelectBasicAttack(ResolvedBasicAttack normal) => BasicAttackSelection.Of(normal);

    void Stop();

    bool CanStand(Vec2 position, string? clearanceActorId = null);

    bool? CanPlaceBody(Vec2 position, string? clearanceActorId = null) => null;

    bool? CanReach(Vec2 position) => null;

    Rect? NavigationBounds() => null;

    void Relocate(Vec2 position, string? clearanceActorId = null)
    {
    }

    void Face(string direction)
    {
    }

    Action Play(CombatAction action, Action<int> frame, Action complete);

    /// <summary>
    /// 派发某一逻辑帧的表现与结算：普攻的挥击／命中、技能的事件与单元。
    /// 与 <see cref="Play"/> 分开，是因为前者管推进时间、后者管这一帧派发什么。
    /// </summary>
    void Frame(
        CombatAction action,
        int index,
        Action<string, string?> contact,
        SkillUnitsReady unitsReady,
        Action? areaContact = null);

    void Contact(AttackContact contact);

    void AreaContact(BasicAreaContact contact)
    {
    }

    SkillUnitOutcome Unit(SkillUnitHit hit) => SkillUnitOutcome.Skipped;

    PreparedDamage? PrepareUnit(SkillUnitStart start) => null;

    void RunUnitSequence(Action work) => work();

    void UnitFeedback(SkillUnitHit hit, SkillUnitOutcome outcome, SkillEffectCue cue)
    {
    }

    int? ManaCost(SkillPlayback skill) => skill.ManaCost;

    bool CanSpendMana(int cost) => true;

    bool SpendMana(int cost) => true;

    void ClearEffects()
    {
    }
}

public enum CombatState
{
    Idle,
    Approaching,
    Attacking,
}

public sealed class CombatController
{
    private readonly ICombatPort _port;
    private readonly string _casterId;

    private ResolvedBasicAttack? _basic;
    private CombatState _state = CombatState.Idle;
    private Active? _active;
    private string? _selectedId;
    private Action? _cancelPlayback;
    private int _generation;
    private readonly List<Action<CombatSnapshot>> _listeners = [];

    private sealed class Active
    {
        public SkillPlayback? Skill;
        public string? TargetId;
        public bool TurnToTarget;
        public bool Approached;
        public ResolvedBasicAttack? BasicAttack;

        public int? BasicAttackWait;
    }

    public CombatController(string casterId, ResolvedBasicAttack? basic, ICombatPort port)
    {
        _casterId = casterId;
        _basic = basic;
        _port = port;
    }

    public void SetBasicAttack(ResolvedBasicAttack basic)
    {
        Cancel();
        _basic = basic;
    }

    public CombatState State => _state;

    public CombatSnapshot Snapshot() => new()
    {
        State = _state switch
        {
            CombatState.Approaching => "approaching",
            CombatState.Attacking => "attacking",
            _ => "idle",
        },
        TargetId = _selectedId,
        Cooldowns = _port.Cooldowns.Snapshot(),
    };

    public CombatState ActionState() => _state;

    public string? SelectedTargetId => _selectedId;

    public bool IsCastingSkill => _active?.Skill is not null && _state != CombatState.Idle;

    public string? ActiveTargetId => _state == CombatState.Idle ? null : _active?.TargetId;

    public ActiveCombatAction? ActiveAction() => _active is null || _state == CombatState.Idle
        ? null
        : new ActiveCombatAction(_state == CombatState.Approaching ? "approaching" : "attacking",
            _active.TargetId, _active.Skill);

    public void RestoreState(CombatSnapshot snapshot)
    {
        Cancel(true);
        _selectedId = snapshot.TargetId;
        Publish();
    }

    public Action Subscribe(Action<CombatSnapshot> listener)
    {
        _listeners.Add(listener);
        listener(Snapshot());
        return () => _listeners.Remove(listener);
    }

    private void Publish()
    {
        if (_listeners.Count == 0) return;
        var value = Snapshot();
        foreach (var listener in _listeners.ToList()) listener(value);
    }

    public bool SelectTarget(string targetId)
    {
        var caster = _port.Actor(_casterId);
        var target = _port.Actor(targetId);
        if (caster is null || target is null || caster.Team == target.Team || !CanTargetCombatant(caster, target))
            return false;

        Cancel();
        _selectedId = targetId;
        Publish();
        return true;
    }

    public void SetReactionTarget(string targetId)
    {
        _selectedId = targetId;
        Publish();
    }

    public void ClearSelection()
    {
        if (_selectedId is null) return;
        _selectedId = null;
        Publish();
    }

    public string RequestTarget(string? targetId = null, bool turnToTarget = true) =>
        RequestAction(null, targetId, turnToTarget);

    public string Request(SkillPlayback? skill = null, string? targetId = null, bool turnToTarget = true) =>
        RequestAction(skill, targetId, turnToTarget);

    public bool CanSelectSkill(SkillPlayback skill) => _port.CanSelectSkill(skill);

    public string RequestManualSkill(SkillPlayback skill, string? targetId = null, bool turnToTarget = true) =>
        RequestAction(skill, targetId, turnToTarget, true);

    public string RequestTriggeredAction(SkillPlayback skill, string? targetId = null, bool turnToTarget = true) =>
        RequestAction(skill, targetId, turnToTarget, false, true);

    private string RequestAction(
        SkillPlayback? skill, string? targetId, bool turnToTarget, bool interruptBasic = false,
        bool triggered = false)
    {
        if (_port.IsPaused()) return "busy";
        if (skill is not null && !triggered && !CanSelectSkill(skill)) return "unavailable";

        var replaceBasic = interruptBasic && _active is not null && _active.Skill is null;
        if (_state != CombatState.Idle && !replaceBasic) return "busy";

        var manaCost = skill is null ? null : _port.ManaCost(skill) ?? skill.ManaCost;
        if (manaCost is not null and not 0 && !_port.CanSpendMana(manaCost.Value)) return "no-mana";
        if (skill is not null && !_port.Cooldowns.IsReady(skill.Id)) return "cooldown";

        var caster = _port.Actor(_casterId);
        if (caster is null || !CombatStateRules.Capabilities(caster.States).CanAct) return "busy";

        IApproachProfile? profile = skill is not null ? skill.Action : _basic;
        if (profile is null) return "no-target";

        var wanted = skill is null || SkillActionRules.NeedsTarget(skill.Action);
        var previousId = targetId ?? _selectedId;
        var previous = previousId is null ? null : _port.Actor(previousId);
        var enemies = _port.Candidates()
            .Where(actor => actor.Team != caster.Team && CanTargetCombatant(caster, actor))
            .ToList();

        var rangeBonus = _port.RangeBonusUnits(_casterId);
        Combatant? target = null;
        if (wanted)
        {
            if (previous is not null && previous.Team != caster.Team && CanTargetCombatant(caster, previous))
                target = previous;
            else if (targetId is null)
                target = enemies
                    .Select(actor => (Actor: actor,
                        Score: BasicAttackRules.TargetScore(caster.Position, actor.Position, _port.Facing(),
                            profile.SearchRadiusPx + rangeBonus * 8)))
                    .Where(item => item.Score > 0f)
                    .OrderByDescending(item => item.Score)
                    .Select(item => item.Actor)
                    .FirstOrDefault();
        }

        if (target is not null &&
            BasicAttackRules.AttackDistance(caster.Position, caster.Bounds, target.Position, target.Bounds) >
            profile.ApproachDistanceUnits + rangeBonus &&
            !CombatStateRules.Capabilities(caster.States).CanAutoApproach)
            return "busy";

        if (replaceBasic) Cancel(false, false);
        if (wanted && target is null) return "no-target";

        _active = new Active { Skill = skill, TargetId = target?.Id, TurnToTarget = turnToTarget };
        if (target is not null) _selectedId = target.Id;
        _state = CombatState.Approaching;
        Publish();
        return Approach() ?? "started";
    }

    public void TargetRemoved(string targetId)
    {
        var selected = _selectedId == targetId;
        var active = _active?.TargetId == targetId;
        if (!selected && !active) return;

        if (active && _state == CombatState.Attacking &&
            (_active?.Skill is not null || _active?.BasicAttack?.Definition.Targeting is not null ||
             _active?.BasicAttackWait is not null))
        {
            _active!.TargetId = null;
            if (selected) _selectedId = null;
            Publish();
            return;
        }

        if (active)
        {
            Cancel(true);
            return;
        }

        _selectedId = null;
        Publish();
    }

    public void Update()
    {
        if (_port.IsPaused()) return;

        var caster = _port.Actor(_casterId);
        var selected = _selectedId is null ? null : _port.Actor(_selectedId);
        if (_selectedId is not null &&
            (selected is null || caster is null || !CanTargetCombatant(caster, selected)))
            TargetRemoved(_selectedId);

        if (caster is not null && !CombatStateRules.Capabilities(caster.States).CanAct)
        {
            Cancel(false, false);
            return;
        }

        if (_state == CombatState.Attacking && _active?.Skill is not null && _active.TargetId is not null &&
            _port.Actor(_active.TargetId) is null)
            TargetRemoved(_active.TargetId);

        Approach();
    }

    private bool CanRelocateTo(Vec2 position, string? clearanceActorId = null) =>
        _port.CanPlaceBody(position, clearanceActorId) ?? false;

    private string? Approach()
    {
        if (_state != CombatState.Approaching || _active is null) return null;

        var caster = _port.Actor(_casterId);
        var target = _active.TargetId is null ? null : _port.Actor(_active.TargetId);
        if (caster is null || !CombatStateRules.Capabilities(caster.States).CanAct ||
            (_active.TargetId is not null && (target is null || !CanTargetCombatant(caster, target))))
        {
            Cancel();
            return null;
        }

        var skill = _active.Skill;
        IApproachProfile? profile = skill is not null ? skill.Action : _basic;
        if (profile is null)
        {
            Cancel();
            return null;
        }

        if (target is not null &&
            BasicAttackRules.AttackDistance(caster.Position, caster.Bounds, target.Position, target.Bounds) >
            profile.ApproachDistanceUnits + _port.RangeBonusUnits(_casterId))
        {
            if (!CombatStateRules.Capabilities(caster.States).CanAutoApproach)
            {
                Cancel(false, false);
                return null;
            }

            _active.Approached = true;
            var handled = _port.ApproachTarget(target, profile.ApproachDistanceUnits);
            if (handled == false) Cancel();
            else if (handled is null) _port.MoveToward(target.Position);
            return null;
        }

        if (target is not null && _active.Approached && !_port.CanFinishApproach(target.Id))
        {
            Cancel();
            return null;
        }

        var charge = skill?.Action.Charge is { } chargeDefinition && target is not null
            ? TargetCharge.Plan(caster, target, chargeDefinition, point => CanRelocateTo(point))
            : (ChargePlan?)null;
        if (skill?.Action.Charge is not null && charge is null)
        {
            Cancel();
            return "no-target";
        }

        var manaCost = skill is null ? null : _port.ManaCost(skill) ?? skill.ManaCost;
        if (manaCost is not null and not 0 && !_port.SpendMana(manaCost.Value))
        {
            Cancel();
            return "no-mana";
        }

        _port.Stop();
        _state = CombatState.Attacking;
        if (skill is not null) _port.Cooldowns.Start(skill.Id, skill.Action.CooldownFrames);
        Publish();

        var selection = skill is null && _basic is not null
            ? _port.SelectBasicAttack(_basic)
            : _basic is null ? null : BasicAttackSelection.Of(_basic);
        var basicAttack = selection?.Attack;
        _active.BasicAttack = basicAttack;
        _active.BasicAttackWait = selection?.WaitFrames;

        var action = new CombatAction
        {
            Caster = caster,
            Target = target,
            Skill = skill,
            Charge = charge,
            BasicAttack = basicAttack,
            BasicAttackWait = selection?.WaitFrames,
            Facing = target is not null && _active.TurnToTarget
                ? BasicAttackRules.FaceTarget(charge?.Destination ?? caster.Position, target.Position)
                : _port.Facing(),
        };
        if (skill?.Action.Repeat?.RequiresLivingTarget == true)
            action.RepeatState = new SkillActionRules.SkillRepeatState();

        BeginPlayback(action, skill);
        return null;
    }

    private void BeginPlayback(CombatAction action, SkillPlayback? skill)
    {
        var generation = ++_generation;
        var lastFrame = -1;
        var areaContacted = false;

        void Frame(int index)
        {
            if (generation != _generation || _state != CombatState.Attacking || _port.IsPaused()) return;

            for (var next = lastFrame + 1; next <= index; next += 1)
            {
                if (generation != _generation) return;

                var live = _port.Actor(_casterId);
                if (live is null || !CombatStateRules.Capabilities(live.States).CanAct)
                {
                    Cancel(false, false);
                    return;
                }

                if (action.Charge is { } plan && skill?.Action.Charge is { } chargeDefinition)
                {
                    if (next == 0 && plan.Close)
                    {
                        var distance = Math.Max(0, (int)Math.Round(chargeDefinition.LungeDistancePx));
                        var dx = action.Facing == "east" ? 1 : action.Facing == "west" ? -1 : 0;
                        var dy = action.Facing == "south" ? 1 : action.Facing == "north" ? -1 : 0;
                        for (var step = distance; step >= 1; step -= 1)
                        {
                            var lunge = new Vec2(plan.Destination.X + dx * step, plan.Destination.Y + dy * step);
                            if (!CanRelocateTo(lunge)) continue;
                            _port.Relocate(lunge);
                            break;
                        }
                    }
                    else if (next == chargeDefinition.ArrivalFrame)
                    {
                        _port.Relocate(plan.Destination);
                    }

                    live = _port.Actor(_casterId) ?? live;
                }

                action.Caster = live;
                action.Target = _active?.TargetId is null ? null : _port.Actor(_active.TargetId);
                if (action.Target is not null && !CanTargetCombatant(live, action.Target))
                {
                    TargetRemoved(action.Target.Id);
                    action.Target = null;
                    if (generation != _generation) return;
                }

                if (skill is not null && _active?.TargetId is not null && action.Target is null)
                    TargetRemoved(_active.TargetId);

                if (skill?.Action.Repeat is { } repeat &&
                    SkillActionRules.SkillPlaybackEvents(skill.Action, next, skill.Level ?? 1, action.RepeatState)
                        .Any(item => item is UnitsEvent))
                {
                    if (repeat.EndsWithoutTarget == true && action.Target is null)
                    {
                        Cancel(false, false);
                        return;
                    }

                    if (repeat.FacesTargetOnRelease == true && action.Target is not null)
                    {
                        action.Facing = BasicAttackRules.FaceTarget(action.Caster.Position, action.Target.Position);
                        _port.Face(action.Facing);
                    }
                }

                if (skill?.Action.RearTeleport?.Frame == next)
                {
                    var teleportTarget = action.Target;
                    var destination = teleportTarget is null
                        ? null
                        : RearTeleportRules.Plan(action.Caster, teleportTarget,
                            point => CanRelocateTo(point, teleportTarget.Id),
                            point => _port.CanReach(point) ?? true,
                            _port.NavigationBounds());
                    if (destination is { } spot)
                    {
                        _port.Relocate(spot, teleportTarget!.Id);
                        action.Caster = _port.Actor(_casterId)!;
                        action.Facing = BasicAttackRules.FaceTarget(spot, teleportTarget.Position);
                        _port.Face(action.Facing);
                    }
                    else
                    {
                        action.Skill = new SkillPlayback(skill.Id, WithoutUnitsEvents(skill.Action),
                            skill.Level, skill.ManaCost, skill.Clips);
                    }
                }

                if (skill is not null)
                    foreach (var captured in SkillActionRules.SkillPlaybackEvents(
                                 skill.Action, next, skill.Level ?? 1, action.RepeatState))
                        if (captured is CaptureTargetsEvent capture)
                            action.CapturedTargets = CueTargets.Resolve(capture.Target, capture.TargetRangePx, null,
                                    action.Caster, action.Target, _port.Candidates(),
                                    capture.Target is "party" or "party-ally" ? _port.PartySlots() : null,
                                    _port.RangeBonusUnits(_casterId), _port.HealScore)
                                .Take(capture.Limit)
                                .Select(target2 => new CapturedTarget(target2.Id, target2.Position))
                                .ToList();

                var contacted = new HashSet<string>();

                // 这次动作有没有为技能音效出过声（一次性效果）。
                // 标记挂在动作帧回调上，所以每一次施放都是干净的一次；同一次动作里的多段单元只出一次声。
                var skillSounded = false;

                void Contact(string targetId, string? hand)
                {
                    var contactKey = $"{targetId}|{hand ?? "main-hand"}";
                    if ((action.BasicAttack?.Strikes is { Count: > 0 } && generation != _generation) ||
                        skill is not null || action.BasicAttackWait is not null ||
                        action.BasicAttack?.Definition.Targeting is not null ||
                        contacted.Contains(contactKey))
                        return;

                    var source = _port.Actor(_casterId);
                    var recipient = _port.Actor(targetId);
                    if (source is null || recipient is null || !CanTargetCombatant(source, recipient)) return;
                    if (!contacted.Add(contactKey)) return;
                    if (source.Id == recipient.Id || source.Team == recipient.Team) return;

                    _port.Contact(action.BasicAttack?.Strikes is { Count: > 0 }
                        ? new AttackContact(source.Id, recipient.Id, Hand: hand ?? "main-hand",
                            BasicAttack: action.BasicAttack)
                        : new AttackContact(source.Id, recipient.Id));
                }

                void AreaContact()
                {
                    if (generation != _generation || skill is not null || areaContacted) return;
                    if (action.BasicAttack?.Definition.Targeting is null || _port.Actor(_casterId) is null) return;
                    areaContacted = true;
                    _port.AreaContact(new BasicAreaContact(_casterId, action.BasicAttack));
                }

                void SettleRepeat()
                {
                    if (skill is null || action.RepeatState is null || generation != _generation) return;
                    var current = _active?.TargetId is null ? null : _port.Actor(_active.TargetId);
                    SkillActionRules.SettleSkillRepeat(skill.Action, next, skill.Level ?? 1,
                        current is not null && _port.IsAlive(current.Id), action.RepeatState);
                }

                IReadOnlyList<ResolvedSkillUnit> UnitsReady(SkillUnitsArrival arrival)
                {
                    if (skill is null) return [];
                    if (arrival.Results is not null) return arrival.Results;

                    if (!skillSounded && !SkillSoundRules.PlaysOwnSound(skill.Action))
                    {
                        skillSounded = true;
                        _port.SkillSounded(skill);
                    }

                    arrival.Results = [];
                    arrival.Results = CombatUnitExecution.ExecuteSkillUnits(new SkillExecution
                    {
                        CasterId = _casterId,
                        Skill = skill,
                        Cues = arrival.Event.Cues,
                        Origin = arrival.ImpactCenter,
                        SelectedTargetId = arrival.SelectedTargetId,
                    }, new SkillExecutionPort
                    {
                        Port = _port,
                        CurrentTargetId = () => _selectedId,
                        SelectTarget = id =>
                        {
                            if (id != _selectedId) SetReactionTarget(id);
                        },
                        Resolved = hit =>
                        {
                            var source = _port.Actor(_casterId);
                            var recipient = _port.Actor(hit.TargetId);
                            if (source is not null && recipient is not null && source.Team != recipient.Team)
                                _port.Contact(new AttackContact(source.Id, recipient.Id, skill.Id));
                        },
                    });
                    SettleRepeat();
                    return arrival.Results;
                }

                _port.Frame(action, next, Contact, UnitsReady, AreaContact);
                SettleRepeat();
            }

            lastFrame = Math.Max(lastFrame, index);
        }

        _cancelPlayback = _port.Play(action, Frame, () =>
        {
            if (generation != _generation) return;
            _cancelPlayback?.Invoke();
            _cancelPlayback = null;
            _state = CombatState.Idle;
            _active = null;
            _port.Stop();
            Publish();
        });

        Frame(0);
    }

    private static SkillAction WithoutUnitsEvents(SkillAction action) => new()
    {
        RequiredOffhand = action.RequiredOffhand,
        HitEffectStyle = action.HitEffectStyle,
        ManaGemDrop = action.ManaGemDrop,
        CombatUnits = action.CombatUnits,
        UsesSkillDamageBuff = action.UsesSkillDamageBuff,
        Repeat = action.Repeat,
        Charge = action.Charge,
        RequiresActiveSummon = action.RequiresActiveSummon,
        RearTeleport = action.RearTeleport,
        Afterimages = action.Afterimages,
        RequiresTarget = action.RequiresTarget,
        MinHealthRatePerThousand = action.MinHealthRatePerThousand,
        Events = action.Events.Where(item => item is not UnitsEvent).ToList(),
        CooldownFrames = action.CooldownFrames,
        ApproachDistanceUnits = action.ApproachDistanceUnits,
        SearchRadiusPx = action.SearchRadiusPx,
        Transition = action.Transition,
        EffectNames = action.EffectNames,
        SoundNames = action.SoundNames,
        SummonNames = action.SummonNames,
        EventFrames = action.EventFrames,
    };

    public void Cancel(bool clearEffects = false, bool clearTarget = true)
    {
        if (_state == CombatState.Idle && (!clearTarget || _selectedId is null) && !clearEffects) return;

        _generation += 1;
        _cancelPlayback?.Invoke();
        _cancelPlayback = null;
        _state = CombatState.Idle;
        _active = null;
        if (clearTarget) _selectedId = null;
        _port.Stop();
        if (clearEffects) _port.ClearEffects();
        Publish();
    }

    public void Reset()
    {
        Cancel(true);
        _port.Cooldowns.Reset();
        Publish();
    }

    public void ResetSkillCooldowns()
    {
        _port.Cooldowns.Reset();
        Publish();
    }

    public void Dispose()
    {
        Cancel(true);
        _listeners.Clear();
    }

    public static bool CanTargetCombatant(Combatant source, Combatant target) =>
        CombatStateRules.Capabilities(target.States, source.Team != target.Team).CanTarget;
}
