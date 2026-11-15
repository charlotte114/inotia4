using System;
using System.Collections.Generic;

namespace Inotia4.Domain.Combat;

public abstract class BuffReactionEvent
{
    public abstract string Kind { get; }

    public required string OwnerId { get; init; }

    public string DamageKind { get; init; } = "physical";

    public string Outcome { get; init; } = "hit";
}

public sealed class PhysicalAvoidanceEvent : BuffReactionEvent
{
    public override string Kind => "physical-avoidance";
}

public sealed class PhysicalSuccessEvent : BuffReactionEvent
{
    public override string Kind => "physical-success";
}

public sealed class WeaponBlockCounterEvent : BuffReactionEvent
{
    public override string Kind => "weapon-block-counter";

    public required string OpponentId { get; init; }
}

public sealed class WeaponBlockStunEvent : BuffReactionEvent
{
    public override string Kind => "weapon-block-stun";

    public required string OpponentId { get; init; }
}

public sealed class PhysicalSuccessPartyEvent : BuffReactionEvent
{
    public override string Kind => "physical-success-party";

    public required IReadOnlyList<string> PartyIds { get; init; }
}

public sealed class PhysicalSuccessThreatEvent : BuffReactionEvent
{
    public override string Kind => "physical-success-threat";

    public required string OpponentId { get; init; }

    public int HitDamage { get; init; }
}

public abstract class BuffReactionCommand
{
    public abstract string Kind { get; }
}

public sealed class ApplyBuffReactionCommand : BuffReactionCommand
{
    public override string Kind => "apply-buff";

    public required string SourceId { get; init; }

    public required string TargetId { get; init; }

    public required BuffReactionBuff Buff { get; init; }
}

public sealed class RestoreBuffManaCommand : BuffReactionCommand
{
    public override string Kind => "restore-mana";

    public required string TargetId { get; init; }

    public required int Amount { get; init; }
}

public sealed class PlayBuffReactionEffectCommand : BuffReactionCommand
{
    public override string Kind => "play-effect";

    public required string TargetId { get; init; }

    public required string Effect { get; init; }
}

public sealed class RedistributeThreatCommand : BuffReactionCommand
{
    public override string Kind => "redistribute-threat";

    public required string SourceId { get; init; }

    public required string TargetId { get; init; }

    public required int Damage { get; init; }
}

public sealed class ApplyBuffStateCommand : BuffReactionCommand
{
    public override string Kind => "apply-state";

    public required string AttributeOwner { get; init; }

    public required string TargetId { get; init; }

    public required string State { get; init; }

    public required string DurationAttribute { get; init; }
}

public static class BuffReactions
{
    public const int ActivePartySlotCount = 3;

    public static IReadOnlyList<BuffReactionCommand> Resolve(
        IReadOnlyList<BuffReaction> reactions, BuffReactionEvent action)
    {
        var commands = new List<BuffReactionCommand>();
        if (action.DamageKind != "physical") return commands;

        switch (action)
        {
            case WeaponBlockStunEvent stun:
            {
                if (stun.Outcome != "weapon-block") return commands;
                foreach (var reaction in reactions)
                {
                    if (reaction is not WeaponBlockStunReaction rule) continue;
                    commands.Add(new ApplyBuffStateCommand
                    {
                        AttributeOwner = stun.OwnerId,
                        TargetId = stun.OpponentId,
                        State = rule.State,
                        DurationAttribute = rule.DurationAttribute,
                    });
                }

                return commands;
            }

            case WeaponBlockCounterEvent counter:
            {
                if (counter.Outcome != "weapon-block") return commands;
                foreach (var reaction in reactions)
                {
                    if (reaction is not WeaponBlockCounterReaction rule) continue;
                    commands.Add(new ApplyBuffReactionCommand
                    {
                        SourceId = counter.OwnerId,
                        TargetId = counter.OpponentId,
                        Buff = rule.Buff,
                    });
                }

                return commands;
            }

            case PhysicalSuccessEvent success:
            {
                if (success.Outcome == "miss") return commands;
                foreach (var reaction in reactions)
                {
                    if (reaction is not ManaOnPhysicalSuccessReaction rule) continue;
                    commands.Add(new RestoreBuffManaCommand
                    {
                        TargetId = success.OwnerId,
                        Amount = rule.Amount,
                    });
                    commands.Add(new PlayBuffReactionEffectCommand
                    {
                        TargetId = success.OwnerId,
                        Effect = rule.Effect,
                    });
                }

                return commands;
            }

            case PhysicalSuccessThreatEvent threat:
            {
                if (threat.Outcome == "miss") return commands;
                foreach (var reaction in reactions)
                {
                    if (reaction is not PhysicalSuccessThreatReaction) continue;
                    commands.Add(new RedistributeThreatCommand
                    {
                        SourceId = threat.OwnerId,
                        TargetId = threat.OpponentId,
                        Damage = threat.HitDamage,
                    });
                }

                return commands;
            }
        }

        List<string> targets;
        if (action is PhysicalAvoidanceEvent)
        {
            var avoided = action.Outcome is "miss" or "weapon-block" or "shield-block";
            targets = avoided ? [action.OwnerId] : [];
        }
        else
        {
            targets = [];
            if (action is PhysicalSuccessPartyEvent party && action.Outcome != "miss")
            {
                var count = Math.Min(ActivePartySlotCount, party.PartyIds.Count);
                for (var index = 0; index < count; index += 1) targets.Add(party.PartyIds[index]);
            }
        }

        foreach (var reaction in reactions)
        {
            if (reaction is not IBuffReactionWithBuff holder || reaction.Kind != action.Kind) continue;
            foreach (var targetId in targets)
            {
                commands.Add(new ApplyBuffReactionCommand
                {
                    SourceId = action.OwnerId,
                    TargetId = targetId,
                    Buff = holder.Buff,
                });
            }
        }

        return commands;
    }
}
