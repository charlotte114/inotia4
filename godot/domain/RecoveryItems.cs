using System;
using System.Collections.Generic;
using System.Linq;
using Inotia4.Domain.Character;
using Inotia4.Domain.Combat;

namespace Inotia4.Domain;

public interface IRecoveryMember
{
    string Id { get; }

    int Health { get; }

    int MaxHealth { get; }

    int Mana { get; }

    int MaxMana { get; }

    void SetHealth(int value);

    void SetMana(int value);
}

public readonly record struct RecoveryModifiers(
    bool Sleeping = false,
    bool PotionSickness = false,
    bool Weakened = false,
    bool RecoveryBonus = false,
    int CooldownReductionMs = 0);

public sealed record RecoveryResult(
    bool Used, string? Reason, int Amount, IReadOnlyList<string> Healed, IReadOnlyList<string> Effects)
{
    public static RecoveryResult Reject(string reason) => new(false, reason, 0, [], []);
}

public sealed record RegenerationState(long RemainingMs, int HealthRecoveryPerPulse, int ManaRecoveryPerPulse);

public sealed record RecoveryItemsState(
    long ElapsedMs, long NextTick, long DurationMs, RegenerationState? Regeneration);

public sealed class RecoveryItems
{
    private const long LogicFrameMs = CombatClock.LogicFrameMs;

    /// <summary>夹随机数用的「1 减一个极小的正数」；C# 的 <c>double.Epsilon</c> 是次正规数，含义与它不同，不能直接用。</summary>
    private const double OneMinusEpsilon = 1 - 2.220446049250313080847e-16;

    private long _elapsedMs;
    private long _nextTick = -1;
    private long _durationMs = 9900;
    private RegenerationState? _regeneration;

    public RegenerationState? ActiveRegeneration =>
        _regeneration is { RemainingMs: > 0 } regeneration ? regeneration : null;

    public RecoveryItemsState CaptureState() => new(_elapsedMs, _nextTick, _durationMs, _regeneration);

    public void RestoreState(RecoveryItemsState state)
    {
        _elapsedMs = state.ElapsedMs;
        _nextTick = state.NextTick;
        _durationMs = state.DurationMs;
        _regeneration = state.Regeneration;
    }

    public void Advance(long deltaMs)
    {
        if (deltaMs <= 0) return;
        _elapsedMs += deltaMs;
        if (_regeneration is { } regeneration)
            _regeneration = regeneration with { RemainingMs = Math.Max(0, regeneration.RemainingMs - deltaMs) };
    }

    private long Tick => _elapsedMs / LogicFrameMs;

    public (long RemainingMs, long DurationMs, bool Cooling) Cooldown() =>
        (Math.Max(0, (_nextTick - Tick) * LogicFrameMs), _durationMs, Tick <= _nextTick);

    public RecoveryResult Use(
        Bag bag, string entryId, IReadOnlyList<IRecoveryMember> members, string userId,
        RecoveryModifiers modifiers = default, Func<double>? random = null)
    {
        random ??= Random.Shared.NextDouble;
        var entry = bag.Get(entryId);
        var use = entry is null ? null : bag.Catalog.Get(entry.ItemId).Use;
        var user = members.FirstOrDefault(member => member.Id == userId);
        var kind = use?.Kind ?? string.Empty;

        var usable = (kind == ItemUseRules.RecoverHealth && use?.Recovery is not null) ||
                     (kind == ItemUseRules.Regenerate && use?.Regenerate is not null);
        if (entry is null || !bag.Entries.Any(item => item.Id == entryId) || !usable ||
            user is null || user.Health <= 0)
            return RecoveryResult.Reject("unavailable");

        if (modifiers.Sleeping || modifiers.PotionSickness) return RecoveryResult.Reject("blocked");

        if (kind == ItemUseRules.Regenerate)
        {
            var regeneration = use!.Regenerate!;
            _regeneration = new RegenerationState(regeneration.DurationMs,
                regeneration.HealthRecoveryPerPulse, regeneration.ManaRecoveryPerPulse);
            bag.Take(entryId, 1);
            return new RecoveryResult(true, null, 0, [],
                members.Where(member => member.Health > 0).Select(member => member.Id).ToList());
        }

        if (Tick <= _nextTick) return RecoveryResult.Reject("cooldown");

        var recovery = use!.Recovery!;
        // amount：在区间内随机**一次**，全队同一数值。rate：各按自己的上限算，所以逐人计算。
        var rolled = recovery.Mode == "amount"
            ? recovery.MinHealth + (int)Math.Floor(
                Math.Max(0, Math.Min(OneMinusEpsilon, random())) *
                (recovery.MaxHealth - recovery.MinHealth + 1))
            : 0;

        int Scaled(int amount)
        {
            var value = amount;
            if (modifiers.Weakened) value = (int)Math.Floor(value / 2.0);
            if (modifiers.RecoveryBonus) value += (int)Math.Floor(value / 10.0);
            return value;
        }

        var healed = new List<string>();
        var effects = new List<string>();
        var changes = new List<(IRecoveryMember Member, int Health, int Mana, int ManaBefore)>();

        foreach (var member in members)
        {
            var health = member.Health;
            var maxHealth = member.MaxHealth;
            var mana = member.Mana;
            var maxMana = member.MaxMana;
            if (health <= 0) continue;

            var healthAmount = Scaled(recovery.Mode == "amount"
                ? rolled
                : (int)Math.Truncate(maxHealth * (double)recovery.RatePercent / 100));
            var manaAmount = recovery.RestoresMana
                ? Scaled(recovery.Mode == "amount"
                    ? rolled
                    : (int)Math.Truncate(maxMana * (double)recovery.RatePercent / 100))
                : 0;

            var nextHealth = Math.Min(maxHealth, health + healthAmount);
            var nextMana = manaAmount > 0 ? Math.Min(maxMana, mana + manaAmount) : mana;

            if ((healthAmount > 0 && nextHealth > health) || nextMana > mana)
            {
                healed.Add(member.Id);
                changes.Add((member, nextHealth, nextMana, mana));
            }

            if (healed.Count > 0) effects.Add(member.Id);
        }

        if (healed.Count == 0) return RecoveryResult.Reject("full-health");

        _durationMs = recovery.CooldownMs;
        _nextTick = Tick + Math.Max(0, recovery.CooldownMs - modifiers.CooldownReductionMs) / LogicFrameMs;
        bag.Take(entryId, 1);
        foreach (var change in changes)
        {
            change.Member.SetHealth(change.Health);
            if (change.Mana != change.ManaBefore) change.Member.SetMana(change.Mana);
        }

        return new RecoveryResult(true, null, recovery.Mode == "amount" ? Scaled(rolled) : 0, healed, effects);
    }
}
