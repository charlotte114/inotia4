using System;
using System.Collections.Generic;
using System.Linq;
using Inotia4.Domain.Content;
using Inotia4.Domain.World;

namespace Inotia4.Domain.Combat;

public enum CombatThreatRole
{
    Player,
    Member,
    Summon,
    Enemy,
}

public sealed class CombatThreatActor
{
    public required string Id { get; init; }

    public required string Team { get; init; }

    public required Vec2 Position { get; init; }

    public required CollisionBox Bounds { get; init; }

    public IReadOnlyList<string>? States { get; init; }

    public required bool Active { get; init; }

    public required int Level { get; init; }

    public required int SightRangeUnits { get; init; }

    public required bool TracksThreat { get; init; }

    public CombatThreatRole? Role { get; init; }
}

public interface ICombatThreatPort
{
    CombatThreatActor? Actor(string id);

    IReadOnlyList<CombatThreatActor> Actors();

    string? CurrentTarget(string ownerId);

    long LogicTick();

    int GenerationRate(string sourceId);

    int ReductionRate(string sourceId);

    (int Physical, int Magic) NormalDamage(string sourceId);

    string? TauntSource(string ownerId);

    void ApplyTaunt(string sourceId, string targetId);

    void CombatStarted(string ownerId);
}

public sealed class CombatThreatSystem
{
    private static readonly Dictionary<CombatThreatRole, int> PresenceWeight = new()
    {
        [CombatThreatRole.Player] = 10,
        [CombatThreatRole.Member] = 6,
        [CombatThreatRole.Summon] = 15,
        [CombatThreatRole.Enemy] = 10,
    };

    private const int SummonArrivalRate = 120;

    private const int MaxThreatScore = int.MaxValue;

    private readonly ThreatLedger _ledger = new();
    private readonly ICombatThreatPort _port;

    public CombatThreatSystem(ICombatThreatPort port) => _port = port;

    public void Damage(string sourceId, string targetId, int damage)
    {
        if (damage < 1) return;

        var source = _port.Actor(sourceId);
        var target = _port.Actor(targetId);
        if (source is null || target is null || !CombatStateRules.Capabilities(target.States).CanReceiveDamage) return;

        Add(target, source, ThreatLedger.CalculateDamageThreat(
            damage,
            BasicAttackRules.AttackDistance(source.Position, source.Bounds, target.Position, target.Bounds) - 2,
            _port.GenerationRate(sourceId)));
    }

    public void Heal(string sourceId, string targetId, int actualHealing)
    {
        var source = _port.Actor(sourceId);
        var target = _port.Actor(targetId);
        if (source is null || target is null) return;

        var generationRate = _port.GenerationRate(sourceId);
        foreach (var owner in _port.Actors())
        {
            if (!owner.Active || owner.Team == target.Team || _port.CurrentTarget(owner.Id) != targetId) continue;

            Add(owner, source, ThreatLedger.CalculateHealingThreat(
                actualHealing,
                BasicAttackRules.AttackDistance(source.Position, source.Bounds, owner.Position, owner.Bounds) - 2,
                generationRate));
        }
    }

    public void State(string sourceId, string targetId)
    {
        var source = _port.Actor(sourceId);
        var target = _port.Actor(targetId);
        if (source is null || target is null || source.Team == target.Team) return;

        Add(target, source, ThreatLedger.CalculateStateThreat(source.Level, _port.GenerationRate(sourceId)));
    }

    public void RedistributeAfterPhysicalHit(string attackerId, string targetId, int damage)
    {
        var attacker = _port.Actor(attackerId);
        var owner = _port.Actor(targetId);
        if (attacker is null || owner is null) return;

        foreach (var entry in _ledger.Entries(targetId))
        {
            var source = _port.Actor(entry.SourceId);
            if (source is null || source.Team != attacker.Team) continue;

            var amount = source.Id == attackerId
                ? unchecked(entry.Score + (damage * 3))
                : (int)Math.Truncate(entry.Score * -50 / 100.0);

            Add(owner, source, amount);
        }
    }

    public void Taunt(string sourceId, string targetId)
    {
        var source = _port.Actor(sourceId);
        var target = _port.Actor(targetId);
        if (source is null || target is null || source.Id == target.Id) return;

        if (!Has(targetId, sourceId))
        {
            var normal = _port.NormalDamage(sourceId);
            Add(target, source, unchecked(normal.Physical + normal.Magic));
        }

        if (!Has(targetId, sourceId)) return;

        _ledger.ClampCompetitors(targetId, sourceId, id => _port.Actor(id)?.Team == source.Team);
        _port.ApplyTaunt(sourceId, targetId);
    }

    public void Notice(string ownerId, string sourceId)
    {
        var owner = _port.Actor(ownerId);
        var source = _port.Actor(sourceId);
        if (owner is null || source is null || !owner.TracksThreat) return;
        if (Has(ownerId, sourceId)) return;

        var role = source.Role ?? (source.Team == "hostile" ? CombatThreatRole.Enemy : CombatThreatRole.Player);
        var presence = Presence(owner, role);
        Add(owner, source, presence);

        if (role == CombatThreatRole.Summon && Has(ownerId, sourceId))
        {
            var highest = _ledger.Highest(ownerId, id => id != sourceId && Eligible(owner, id));
            var hold = highest is null
                ? 0
                : (int)Math.Min(MaxThreatScore, Math.Ceiling(highest.Value * (double)SummonArrivalRate / 100));

            _ledger.RaiseTo(ownerId, sourceId, Math.Max(presence, hold), _port.LogicTick());
        }
    }

    public string? Select(string ownerId, string? currentSourceId)
    {
        var owner = _port.Actor(ownerId);
        if (owner is null) return null;
        if (!owner.TracksThreat) return currentSourceId;

        return _ledger.Select(ownerId, currentSourceId, id => Eligible(owner, id));
    }

    public bool Has(string ownerId, string sourceId) => _ledger.Score(ownerId, sourceId) is not null;

    public void ClearOwner(string ownerId) => _ledger.ClearOwner(ownerId);

    public void RemoveActor(string actorId) => _ledger.RemoveActor(actorId);

    public void Clear() => _ledger.Clear();

    public void Decay()
    {
        _ledger.Decay((ownerId, sourceId) =>
        {
            // 当前目标不衰减：接战后要一直追，不能因为追出视距就把账目减没。
            if (_port.CurrentTarget(ownerId) == sourceId) return false;

            var owner = _port.Actor(ownerId);
            var source = _port.Actor(sourceId);
            return owner is not null && source is not null &&
                   BasicAttackRules.AttackDistance(owner.Position, owner.Bounds, source.Position, source.Bounds) >
                   owner.SightRangeUnits;
        });
    }

    public ThreatLedgerState CaptureState() => _ledger.CaptureState();

    public void RestoreState(ThreatLedgerState state) => _ledger.RestoreState(state);

    private int Presence(CombatThreatActor owner, CombatThreatRole role) =>
        (int)Math.Truncate(unchecked((owner.Level + 10) * PresenceWeight[role]) / 10.0);

    private bool Eligible(CombatThreatActor owner, string sourceId)
    {
        var source = _port.Actor(sourceId);
        return source is { Active: true } &&
               source.Team != owner.Team &&
               CombatStateRules.Capabilities(source.States).CanTarget;
    }

    private void Add(CombatThreatActor owner, CombatThreatActor source, int amount)
    {
        if (!owner.TracksThreat) return;

        var allowedSourceId = _port.TauntSource(owner.Id);
        if (allowedSourceId is not null && allowedSourceId != source.Id) return;

        var sourceReductionRate = _port.ReductionRate(source.Id);
        if (_ledger.Add(owner.Id, source.Id, amount, _port.LogicTick(),
                new ThreatAdditionRules(sourceReductionRate, allowedSourceId)))
            _port.CombatStarted(owner.Id);
    }
}
