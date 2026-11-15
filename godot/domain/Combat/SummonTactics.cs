using System;
using System.Collections.Generic;
using System.Linq;

namespace Inotia4.Domain.Combat;

public sealed class SummonCombatAwareness
{
    private const int RecentContactMs = 1800;

    private long _lastCombatAt;
    private readonly Dictionary<string, long> _engaged = new();
    private readonly Dictionary<(string EnemyId, string TargetId), (string EnemyId, string TargetId, long At)> _attacks = new();
    private readonly Dictionary<string, (string Id, long At)> _friendlyTargets = new();

    public SummonCombatAwareness(long nowMs) => _lastCombatAt = nowMs;

    public void Contact(Combatant source, Combatant target, long nowMs)
    {
        if (source.Team == target.Team || (source.Team != "party" && target.Team != "party")) return;
        _lastCombatAt = nowMs;
        var enemy = source.Team == "party" ? target : source;
        _engaged[enemy.Id] = nowMs;
        if (source.Team == "party") _friendlyTargets[source.Id] = (target.Id, nowMs);
        else _attacks[(source.Id, target.Id)] = (source.Id, target.Id, nowMs);
    }

    public void Action(Combatant source, ActiveCombatAction action, Combatant? target, long nowMs)
    {
        if (action.State != "attacking" || !SummonTacticsRules.IsHostileAction(action)) return;
        if (source.Team == "party") _lastCombatAt = nowMs;
        if (target is not null) Contact(source, target, nowMs);
    }

    public void Observe(long nowMs, IReadOnlyList<SummonCombatObservation> observations)
    {
        var actors = observations.ToDictionary(entry => entry.Actor.Id, entry => entry.Actor);
        foreach (var entry in observations)
        {
            if (entry.Action is { } action)
                Action(entry.Actor, action,
                    action.TargetId is { } id && actors.TryGetValue(id, out var target) ? target : null, nowMs);
            if (entry.Actor.Team == "party" || entry.ChasingTargetId is not { } chasing) continue;
            if (actors.TryGetValue(chasing, out var chased) && chased.Team == "party")
                Contact(entry.Actor, chased, nowMs);
        }

        foreach (var (id, at) in _engaged.Where(pair => nowMs - pair.Value > RecentContactMs || !actors.ContainsKey(pair.Key)))
            _engaged.Remove(id);
        foreach (var (key, entry) in _attacks.Where(pair =>
                     nowMs - pair.Value.At > RecentContactMs ||
                     !actors.ContainsKey(pair.Value.EnemyId) || !actors.ContainsKey(pair.Value.TargetId)))
            _attacks.Remove(key);
        foreach (var (id, entry) in _friendlyTargets.Where(pair =>
                     nowMs - pair.Value.At > RecentContactMs ||
                     !actors.ContainsKey(pair.Key) || !actors.ContainsKey(pair.Value.Id)))
            _friendlyTargets.Remove(id);
    }

    public SummonTactics Tactics(string ownerId, IReadOnlyList<string> partyIds, string? focusTargetId = null) => new(
        [.. _engaged.Keys],
        _friendlyTargets.TryGetValue(ownerId, out var ownerTarget) ? ownerTarget.Id : null,
        focusTargetId,
        [.. _attacks.Values.Select(entry => new SummonThreat(entry.EnemyId, entry.TargetId))],
        partyIds);

    public long QuietFor(long nowMs) => Math.Max(0, nowMs - _lastCombatAt);

    public bool InCombat(long nowMs) => _engaged.Values.Any(at => nowMs - at <= RecentContactMs);
}

public sealed record SummonCombatObservation(
    Combatant Actor,
    ActiveCombatAction? Action = null,
    string? ChasingTargetId = null);

public sealed record SummonTactics(
    IReadOnlyList<string> EngagedTargetIds,
    string? OwnerTargetId,
    string? FocusTargetId,
    IReadOnlyList<SummonThreat> Threats,
    IReadOnlyList<string> PartyIds);

public sealed record SummonThreat(string EnemyId, string TargetId);

public static class SummonTacticsRules
{
    /// <summary>无需目标的范围攻击也是敌对动作；施放条件不能用来判断动作性质。</summary>
    public static bool IsHostileAction(ActiveCombatAction action) =>
        action.Skill is null || SkillActionRules.ActionCues(action.Skill.Action).Any(cue =>
            cue.Target is "current" or "enemy" or "area" or "target-area");
}
