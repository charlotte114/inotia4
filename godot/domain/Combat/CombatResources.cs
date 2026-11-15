using System;
using System.Collections.Generic;

namespace Inotia4.Domain.Combat;

public readonly record struct ResourceAttributes(
    int HealthRecoveryPerPulse,
    int ManaRecoveryPerPulse,
    int ManaCostRate,
    int ManaCostReductionRate);

public static class CombatResources
{
    public const int RecoveryIntervalFrames = 31;

    public const long RecoveryIntervalMs = RecoveryIntervalFrames * CombatClock.LogicFrameMs;

    public static ResourceAttributes DefaultResourceAttributes(string kind, int level) => new(
        HealthRecoveryPerPulse: kind == "member" ? (int)Math.Truncate((960 + 36 * level) / 10.0) : 0,
        ManaRecoveryPerPulse: 8,
        ManaCostRate: kind == "member" ? 1000 : 0,
        ManaCostReductionRate: 0);

    public static int CalculateManaCost(int baseManaCost, int manaCostRate, int manaCostReductionRate)
    {
        var cost = (int)Math.Truncate(baseManaCost * (manaCostRate - manaCostReductionRate) / 1000.0);
        return cost == 0 ? 0 : cost;
    }
}

public sealed record ResourceRecoveryActor(
    string ActorId,
    bool Alive,
    int Level,
    int HealthRecoveryPerPulse,
    int ManaRecoveryPerPulse,
    bool EmergencyRecovery = false);

public sealed record ResourceRecoveryAmount(string ActorId, int HealthAmount, int ManaAmount);

public sealed class ResourceRecoveryPulse
{
    public required long AtMs { get; init; }

    public required IReadOnlyList<ResourceRecoveryAmount> Amounts { get; init; }
}

public sealed class CombatResourceRecoveryState
{
    public required long ElapsedMs { get; init; }

    public required long DeadlineTick { get; init; }
}

public sealed class CombatResourceRecovery
{
    private long _elapsedMs;
    private long _deadlineTick = CombatResources.RecoveryIntervalFrames - 1;

    public long CurrentTick => _elapsedMs / CombatClock.LogicFrameMs;

    public long MillisecondsUntilNextPulse =>
        (_deadlineTick + 1) * CombatClock.LogicFrameMs - _elapsedMs;

    public CombatResourceRecoveryState CaptureState() =>
        new() { ElapsedMs = _elapsedMs, DeadlineTick = _deadlineTick };

    public void RestoreState(CombatResourceRecoveryState state)
    {
        _elapsedMs = state.ElapsedMs;
        _deadlineTick = state.DeadlineTick;
    }

    public IReadOnlyList<ResourceRecoveryPulse> AdvanceToFrame(
        long frame, IReadOnlyList<ResourceRecoveryActor> actors)
    {
        if (frame < CurrentTick) throw new InvalidOperationException("资源恢复帧无效");
        return Advance(frame * CombatClock.LogicFrameMs - _elapsedMs, actors);
    }

    public bool PulseDueAtFrame(long frame)
    {
        if (frame < 0) throw new InvalidOperationException("资源恢复帧无效");
        return _deadlineTick < frame;
    }

    public void ResetDeadline(long frame)
    {
        if (frame < 0) throw new InvalidOperationException("资源恢复帧无效");
        _deadlineTick = frame + CombatResources.RecoveryIntervalFrames - 1;
    }

    public IReadOnlyList<ResourceRecoveryPulse> Advance(
        long deltaMs, IReadOnlyList<ResourceRecoveryActor> actors)
    {
        if (deltaMs <= 0) return [];

        _elapsedMs += deltaMs;
        var currentTick = _elapsedMs / CombatClock.LogicFrameMs;
        var pulses = new List<ResourceRecoveryPulse>();

        while (_deadlineTick < currentTick)
        {
            var amounts = new List<ResourceRecoveryAmount>();
            foreach (var actor in actors)
            {
                if (!actor.Alive) continue;

                amounts.Add(new ResourceRecoveryAmount(
                    actor.ActorId,
                    actor.HealthRecoveryPerPulse + (actor.EmergencyRecovery ? 3 * actor.Level : 0),
                    actor.ManaRecoveryPerPulse));
            }

            pulses.Add(new ResourceRecoveryPulse
            {
                AtMs = (_deadlineTick + 1) * CombatClock.LogicFrameMs,
                Amounts = amounts,
            });

            _deadlineTick += CombatResources.RecoveryIntervalFrames;
        }

        return pulses;
    }
}
