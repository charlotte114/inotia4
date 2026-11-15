using System;
using System.Collections.Generic;
using Inotia4.Domain.Character;
using Inotia4.Domain.Content;

namespace Inotia4.Domain.Combat;

public abstract class SkillReaction
{
    public abstract string Kind { get; }
}

public interface ISkillReactionWithBuff
{
    SkillReactionBuff Buff { get; }
}

public sealed class SoulSwordReaction : SkillReaction
{
    public override string Kind => "soul-sword";

    [System.Text.Json.Serialization.JsonPropertyName("chanceThreshold")]
    public required int ChanceThreshold { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("buffFromLearnedSkill")]
    public required string BuffFromLearnedSkill { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("physicalEffects")]
    public required IReadOnlyList<string> PhysicalEffects { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("magicEffects")]
    public required IReadOnlyList<string> MagicEffects { get; init; }
}

public sealed class BloodSwordReaction : SkillReaction
{
    public override string Kind => "blood-sword";

    [System.Text.Json.Serialization.JsonPropertyName("lifeDrainPerThousand")]
    public required int LifeDrainPerThousand { get; init; }
}

public sealed class ShieldOfRageReaction : SkillReaction, ISkillReactionWithBuff
{
    public override string Kind => "shield-of-rage";

    [System.Text.Json.Serialization.JsonPropertyName("buff")]
    public required SkillReactionBuff Buff { get; init; }
}

public sealed class FlaresOfPainReaction : SkillReaction, ISkillReactionWithBuff
{
    public override string Kind => "flares-of-pain";

    [System.Text.Json.Serialization.JsonPropertyName("chanceThreshold")]
    public required int ChanceThreshold { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("buff")]
    public required SkillReactionBuff Buff { get; init; }
}

public sealed class WrathOfDarknessReaction : SkillReaction, ISkillReactionWithBuff
{
    public override string Kind => "wrath-of-darkness";

    [System.Text.Json.Serialization.JsonPropertyName("chanceThreshold")]
    public required int ChanceThreshold { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("buff")]
    public required SkillReactionBuff Buff { get; init; }
}

public sealed class DarkSenseReaction : SkillReaction, ISkillReactionWithBuff
{
    public override string Kind => "dark-sense";

    [System.Text.Json.Serialization.JsonPropertyName("buff")]
    public required SkillReactionBuff Buff { get; init; }
}

public sealed class ArmorPenetrationReaction : SkillReaction, ISkillReactionWithBuff
{
    public override string Kind => "armor-penetration";

    [System.Text.Json.Serialization.JsonPropertyName("buff")]
    public required SkillReactionBuff Buff { get; init; }
}

public sealed class RevengeIncarnateReaction : SkillReaction, ISkillReactionWithBuff
{
    public override string Kind => "revenge-incarnate";

    [System.Text.Json.Serialization.JsonPropertyName("buff")]
    public required SkillReactionBuff Buff { get; init; }
}

public sealed class BloodThirstReaction : SkillReaction
{
    public override string Kind => "blood-thirst";

    [System.Text.Json.Serialization.JsonPropertyName("lifeEffect")]
    public required string LifeEffect { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("manaEffect")]
    public required string ManaEffect { get; init; }
}

public sealed class FlareReaction : SkillReaction
{
    public override string Kind => "flare";

    [System.Text.Json.Serialization.JsonPropertyName("chanceThreshold")]
    public required int ChanceThreshold { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("offhandChanceThreshold")]
    public required int OffhandChanceThreshold { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("stealthFromLearnedSkill")]
    public required string StealthFromLearnedSkill { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("effect")]
    public required string Effect { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("unlearnedStealth")]
    public required SkillReactionBuff UnlearnedStealth { get; init; }
}

public sealed class CleverFellowReaction : SkillReaction
{
    public override string Kind => "clever-fellow";

    [System.Text.Json.Serialization.JsonPropertyName("chanceThreshold")]
    public required int ChanceThreshold { get; init; }
}

public sealed class OwnerManaRestoreReaction : SkillReaction
{
    public override string Kind => "owner-mana-restore";

    [System.Text.Json.Serialization.JsonPropertyName("amount")]
    public required int Amount { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("effect")]
    public required string Effect { get; init; }
}

public sealed class ShieldOfTrustReaction : SkillReaction, ISkillReactionWithBuff
{
    public override string Kind => "shield-of-trust";

    [System.Text.Json.Serialization.JsonPropertyName("buff")]
    public required SkillReactionBuff Buff { get; init; }
}

public sealed class BloodRevengeReaction : SkillReaction, ISkillReactionWithBuff
{
    public override string Kind => "blood-revenge";

    [System.Text.Json.Serialization.JsonPropertyName("buff")]
    public required SkillReactionBuff Buff { get; init; }
}

public sealed class BardOfDestructionReaction : SkillReaction, ISkillReactionWithBuff
{
    public override string Kind => "bard-of-destruction";

    [System.Text.Json.Serialization.JsonPropertyName("buff")]
    public required SkillReactionBuff Buff { get; init; }
}

public sealed class DeepBreathReaction : SkillReaction, ISkillReactionWithBuff
{
    public override string Kind => "deep-breath";

    [System.Text.Json.Serialization.JsonPropertyName("buff")]
    public required SkillReactionBuff Buff { get; init; }
}

public sealed class SnipingReaction : SkillReaction, ISkillReactionWithBuff
{
    public override string Kind => "sniping";

    [System.Text.Json.Serialization.JsonPropertyName("buff")]
    public required SkillReactionBuff Buff { get; init; }
}

public sealed class OrdinationPrayerReaction : SkillReaction, ISkillReactionWithBuff
{
    public override string Kind => "ordination-prayer";

    [System.Text.Json.Serialization.JsonPropertyName("chanceThreshold")]
    public required int ChanceThreshold { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("buff")]
    public required SkillReactionBuff Buff { get; init; }
}

public sealed class MagicInsightReaction : SkillReaction, ISkillReactionWithBuff
{
    public override string Kind => "magic-insight";

    [System.Text.Json.Serialization.JsonPropertyName("buff")]
    public required SkillReactionBuff Buff { get; init; }
}

public sealed class DisturbanceReaction : SkillReaction
{
    public override string Kind => "disturbance";

    [System.Text.Json.Serialization.JsonPropertyName("chanceThreshold")]
    public required int ChanceThreshold { get; init; }
}

public sealed class LightningOfHellReaction : SkillReaction
{
    public override string Kind => "lightning-of-hell";

    [System.Text.Json.Serialization.JsonPropertyName("chanceThreshold")]
    public required int ChanceThreshold { get; init; }
}

public sealed class GodOfJusticeReaction : SkillReaction
{
    public override string Kind => "god-of-justice";

    [System.Text.Json.Serialization.JsonPropertyName("chanceThreshold")]
    public required int ChanceThreshold { get; init; }
}

public abstract class SkillReactionEvent
{
    public abstract string Kind { get; }

    [System.Text.Json.Serialization.JsonPropertyName("ownerId")]
    public required string OwnerId { get; init; }
}

public abstract class AttackReactionEvent : SkillReactionEvent
{
    public string OpponentId { get; init; } = string.Empty;

    public string DamageKind { get; init; } = "physical";

    public string Outcome { get; init; } = "hit";
}

public sealed class AttackSuccessEvent : AttackReactionEvent
{
    public override string Kind => "attack-success";

    [System.Text.Json.Serialization.JsonPropertyName("hitDamage")]
    public int HitDamage { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("otherLifeDrainPerThousand")]
    public int OtherLifeDrainPerThousand { get; init; }
}

public sealed class PhysicalAttackEffectsEvent : AttackReactionEvent
{
    public override string Kind => "physical-attack-effects";

    [System.Text.Json.Serialization.JsonPropertyName("armorBreakChance")]
    public int ArmorBreakChance { get; init; }
}

public sealed class PhysicalRevengeEvent : AttackReactionEvent
{
    public override string Kind => "physical-revenge";

    [System.Text.Json.Serialization.JsonPropertyName("chanceThreshold")]
    public int ChanceThreshold { get; init; }
}

public sealed class PhysicalInstinctEvent : AttackReactionEvent
{
    public override string Kind => "physical-instinct";

    [System.Text.Json.Serialization.JsonPropertyName("chanceThreshold")]
    public int ChanceThreshold { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("mainHandTwoHanded")]
    public bool MainHandTwoHanded { get; init; }
}

public sealed class PhysicalDefenseInstinctEvent : AttackReactionEvent
{
    public override string Kind => "physical-defense-instinct";

    [System.Text.Json.Serialization.JsonPropertyName("chanceThreshold")]
    public int ChanceThreshold { get; init; }
}

public sealed class PhysicalFlareEvent : AttackReactionEvent
{
    public override string Kind => "physical-flare";

    [System.Text.Json.Serialization.JsonPropertyName("hand")]
    public string? Hand { get; init; }
}

public sealed class BloodThirstEvent : AttackReactionEvent
{
    public override string Kind => "blood-thirst";

    [System.Text.Json.Serialization.JsonPropertyName("hitDamage")]
    public int HitDamage { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("chanceThreshold")]
    public int ChanceThreshold { get; init; }
}

public sealed class AttackEffectsEvent : AttackReactionEvent
{
    public override string Kind => "attack-effects";

    [System.Text.Json.Serialization.JsonPropertyName("actionId")]
    public required string ActionId { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("hasMoltenArmor")]
    public bool HasMoltenArmor { get; init; }
}

public sealed class SummonAttackSuccessEvent : AttackReactionEvent
{
    public override string Kind => "summon-attack-success";
}

public sealed class SummonPhysicalSuccessEvent : AttackReactionEvent
{
    public override string Kind => "summon-physical-success";
}

public sealed class PhysicalBlockEvent : SkillReactionEvent
{
    public override string Kind => "physical-block";
}

public sealed class MoltenReflectionCompletedEvent : SkillReactionEvent
{
    public override string Kind => "molten-reflection-completed";

    public string OpponentId { get; init; } = string.Empty;
}

public sealed class PhysicalHitReceivedEvent : SkillReactionEvent
{
    public override string Kind => "physical-hit-received";
}

public sealed class BloodRevengeEvent : AttackReactionEvent
{
    public override string Kind => "blood-revenge";
}

public sealed class ActionStealthBreakEvent : SkillReactionEvent
{
    public override string Kind => "action-stealth-break";
}

public sealed class DirectHealAcceptedEvent : SkillReactionEvent
{
    public override string Kind => "direct-heal-accepted";

    [System.Text.Json.Serialization.JsonPropertyName("targetId")]
    public required string TargetId { get; init; }
}

public sealed class SkillAttackSuccessEvent : AttackReactionEvent
{
    public override string Kind => "skill-attack-success";
}

public abstract class SkillReactionCommand
{
    public abstract string Kind { get; }
}

public sealed class ApplyReactionBuffCommand : SkillReactionCommand
{
    public override string Kind => "apply-buff";

    [System.Text.Json.Serialization.JsonPropertyName("sourceId")]
    public required string SourceId { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("targetId")]
    public required string TargetId { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("buff")]
    public required SkillReactionBuff Buff { get; init; }
}

public sealed class ApplyPartyBuffCommand : SkillReactionCommand
{
    public override string Kind => "apply-party-buff";

    [System.Text.Json.Serialization.JsonPropertyName("sourceId")]
    public required string SourceId { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("buff")]
    public required SkillReactionBuff Buff { get; init; }
}

public sealed class RestoreReactionHealthCommand : SkillReactionCommand
{
    public override string Kind => "restore-health";

    [System.Text.Json.Serialization.JsonPropertyName("targetId")]
    public required string TargetId { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("amount")]
    public required int Amount { get; init; }
}

public sealed class RestoreReactionManaCommand : SkillReactionCommand
{
    public override string Kind => "restore-mana";

    [System.Text.Json.Serialization.JsonPropertyName("targetId")]
    public required string TargetId { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("amount")]
    public required int Amount { get; init; }
}

public sealed class ResetReactionCooldownsCommand : SkillReactionCommand
{
    public override string Kind => "reset-skill-cooldowns";

    [System.Text.Json.Serialization.JsonPropertyName("targetId")]
    public required string TargetId { get; init; }
}

public sealed class PlayReactionEffectCommand : SkillReactionCommand
{
    public override string Kind => "play-effect";

    [System.Text.Json.Serialization.JsonPropertyName("targetId")]
    public required string TargetId { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("effect")]
    public required string Effect { get; init; }
}

public interface ISkillReactionPorts
{
    double Random();

    SkillReactionBuff? LearnedBuff(string ownerId, string skill);
}

public static class SkillReactions
{
    public static IReadOnlyList<SkillReaction> LearnedReactions(
        SkillCatalog catalog, string classId, SkillLoadout loadout, bool companion = false)
    {
        var list = new List<SkillReaction>();
        foreach (var id in catalog.SkillsOf(classId))
        {
            var level = loadout.Levels.GetValueOrDefault(id);
            if (level <= 0) continue;
            if (!catalog.Skills.TryGetValue(id, out var skill)) continue;
            var ranks = catalog.SkillForGrowth(id, companion).ReactionsByRank;
            if (ranks is null) continue;
            var picked = ranks[Math.Min(level, ranks.Count) - 1];
            if (picked is null) continue;
            list.AddRange(picked);
        }

        return list;
    }

    /// <summary>
    /// 一门技能在任意等级上会用到的全部特效名。
    /// 资源清单要在建目录时就收齐，所以逐档遍历而不是只看当前等级。
    /// </summary>
    public static IReadOnlyList<string> Effects(SkillDefinition skill)
    {
        var names = new List<string>();
        void Add(string name)
        {
            if (!names.Contains(name)) names.Add(name);
        }

        foreach (var rank in skill.ReactionsByRank ?? [])
        {
            foreach (var reaction in rank ?? [])
            {
                switch (reaction)
                {
                    case SoulSwordReaction soul:
                        foreach (var name in soul.PhysicalEffects) Add(name);
                        foreach (var name in soul.MagicEffects) Add(name);
                        break;
                    case BloodSwordReaction:
                        Add("life-drain");
                        break;
                    case BloodThirstReaction thirst:
                        Add(thirst.LifeEffect);
                        Add(thirst.ManaEffect);
                        break;
                    case FlareReaction flare:
                        Add(flare.Effect);
                        foreach (var name in SkillActionRules.BuffEffectNames(flare.UnlearnedStealth.Effect)) Add(name);
                        break;
                    case CleverFellowReaction:
                    case DisturbanceReaction:
                    case LightningOfHellReaction:
                    case GodOfJusticeReaction:
                        break;
                    case OwnerManaRestoreReaction mana:
                        Add(mana.Effect);
                        break;
                    case ISkillReactionWithBuff holder:
                        foreach (var name in SkillActionRules.BuffEffectNames(holder.Buff.Effect)) Add(name);
                        break;
                }
            }
        }

        return names;
    }

    public static bool Succeeds(int threshold, Func<double> random) =>
        threshold > 0 && Math.Floor(random() * 999) + 1 < threshold;

    public static bool SuccessfulAttack(AttackReactionEvent attack) =>
        attack.Outcome != "miss" && !(attack.DamageKind == "magic" && attack.Outcome == "shield-block");

    /// <summary>
    /// 嗜血反应：先恢复生命并播特效，
    /// 再读当前的回魔属性决定要不要回魔——顺序不能反，属性可能被刚才那段改变。
    /// </summary>
    public static void ExecuteBloodThirst(
        BloodThirstEvent action,
        Func<double> random,
        Func<int> lifeDrainPerThousand,
        Func<int> manaRecovery,
        Action<SkillReactionCommand> apply)
    {
        if (action.DamageKind != "physical" || !SuccessfulAttack(action)) return;
        if (action.ChanceThreshold < 200 || !Succeeds(action.ChanceThreshold, random)) return;

        var rate = Math.Max(0, lifeDrainPerThousand());
        apply(new RestoreReactionHealthCommand
        {
            TargetId = action.OwnerId,
            Amount = (int)Math.Truncate(action.HitDamage * (long)rate / 1000.0),
        });
        apply(new PlayReactionEffectCommand { TargetId = action.OwnerId, Effect = "life-drain" });

        var mana = manaRecovery();
        if (mana <= 0) return;
        apply(new RestoreReactionManaCommand { TargetId = action.OwnerId, Amount = mana });
        apply(new PlayReactionEffectCommand { TargetId = action.OwnerId, Effect = "mana-recovery" });
    }

    public static IReadOnlyList<SkillReactionCommand> Resolve(
        IReadOnlyList<SkillReaction> reactions, SkillReactionEvent action, ISkillReactionPorts ports)
    {
        var commands = new List<SkillReactionCommand>();

        void Apply(SkillReactionBuff buff, string targetId) => commands.Add(new ApplyReactionBuffCommand
        {
            SourceId = action.OwnerId,
            TargetId = targetId,
            Buff = buff,
        });

        void Effect(string targetId, string effect) =>
            commands.Add(new PlayReactionEffectCommand { TargetId = targetId, Effect = effect });

        switch (action)
        {
            case AttackSuccessEvent success:
            {
                if (!SuccessfulAttack(success)) return commands;

                foreach (var reaction in reactions)
                {
                    if (reaction is not SoulSwordReaction soul) continue;
                    if (!Succeeds(soul.ChanceThreshold, ports.Random)) continue;

                    var buff = ports.LearnedBuff(success.OwnerId, soul.BuffFromLearnedSkill);
                    if (buff is not null) Apply(buff, success.OwnerId);

                    var effects = success.DamageKind == "physical" ? soul.PhysicalEffects : soul.MagicEffects;
                    foreach (var name in effects) Effect(success.OwnerId, name);
                }

                if (success.DamageKind != "physical") break;

                var rate = success.OtherLifeDrainPerThousand;
                foreach (var reaction in reactions)
                    if (reaction is BloodSwordReaction sword) rate += sword.LifeDrainPerThousand;

                if (rate <= 0) break;
                commands.Add(new RestoreReactionHealthCommand
                {
                    TargetId = success.OwnerId,
                    Amount = (int)Math.Truncate(success.HitDamage * (long)rate / 1000.0),
                });
                Effect(success.OwnerId, "life-drain");
                break;
            }

            case PhysicalAttackEffectsEvent effects:
            {
                if (effects.DamageKind != "physical" || !SuccessfulAttack(effects)) return commands;
                if (!Succeeds(effects.ArmorBreakChance, ports.Random)) return commands;
                var buff = ports.LearnedBuff(effects.OwnerId, "armor-penetration");
                if (buff is not null) Apply(buff, effects.OpponentId);
                break;
            }

            case PhysicalRevengeEvent revenge:
            {
                if (revenge.DamageKind != "physical" || !SuccessfulAttack(revenge)) return commands;
                if (!Succeeds(revenge.ChanceThreshold, ports.Random)) return commands;
                var buff = ports.LearnedBuff(revenge.OwnerId, "revenge-incarnate");
                if (buff is not null) Apply(buff, revenge.OwnerId);
                break;
            }

            case SummonAttackSuccessEvent summon:
            {
                if (!SuccessfulAttack(summon)) return commands;
                foreach (var reaction in reactions)
                {
                    if (reaction is not OwnerManaRestoreReaction mana || mana.Amount <= 0) continue;
                    commands.Add(new RestoreReactionManaCommand
                    {
                        TargetId = summon.OwnerId,
                        Amount = mana.Amount,
                    });
                    Effect(summon.OwnerId, mana.Effect);
                }

                break;
            }

            case SummonPhysicalSuccessEvent clever:
            {
                if (clever.DamageKind != "physical" || !SuccessfulAttack(clever)) return commands;
                foreach (var reaction in reactions)
                {
                    if (reaction is not CleverFellowReaction fellow || fellow.ChanceThreshold <= 0) continue;
                    if (!Succeeds(fellow.ChanceThreshold, ports.Random)) continue;
                    commands.Add(new ResetReactionCooldownsCommand { TargetId = clever.OwnerId });
                }

                break;
            }

            case PhysicalInstinctEvent instinct:
            {
                if (instinct.DamageKind != "physical" || !SuccessfulAttack(instinct)) break;
                if (instinct.ChanceThreshold <= 0) break;
                var threshold = instinct.MainHandTwoHanded
                    ? (int)Math.Truncate(instinct.ChanceThreshold * 15 / 10.0)
                    : instinct.ChanceThreshold;
                if (!Succeeds(threshold, ports.Random)) break;
                commands.Add(new ResetReactionCooldownsCommand { TargetId = instinct.OwnerId });
                break;
            }

            case PhysicalFlareEvent flare:
            {
                // 副手先抽额外阈值、失败再抽一般阈值；两者为 0 也照抽，所以不经过 Succeeds 的短路。
                if (flare.DamageKind != "physical" || !SuccessfulAttack(flare)) break;

                int Draw() => (int)Math.Floor(ports.Random() * 999) + 1;

                var flares = new List<FlareReaction>();
                foreach (var reaction in reactions)
                    if (reaction is FlareReaction item) flares.Add(item);

                if (flares.Count == 0)
                {
                    if (flare.Hand == "offhand") Draw();
                    Draw();
                    return commands;
                }

                foreach (var reaction in flares)
                {
                    var gained = flare.Hand == "offhand"
                        ? Draw() < reaction.OffhandChanceThreshold || Draw() < reaction.ChanceThreshold
                        : Draw() < reaction.ChanceThreshold;
                    if (!gained) continue;

                    var learned = ports.LearnedBuff(flare.OwnerId, reaction.StealthFromLearnedSkill);
                    commands.Add(new ApplyReactionBuffCommand
                    {
                        SourceId = flare.OwnerId,
                        TargetId = flare.OwnerId,
                        Buff = learned ?? reaction.UnlearnedStealth,
                    });
                    Effect(flare.OwnerId, reaction.Effect);
                }

                break;
            }

            case PhysicalDefenseInstinctEvent defense:
            {
                if (defense.DamageKind != "physical") break;
                if (defense.Outcome != "miss" && defense.Outcome != "weapon-block") break;
                if (!Succeeds(defense.ChanceThreshold, ports.Random)) break;
                commands.Add(new ResetReactionCooldownsCommand { TargetId = defense.OwnerId });
                break;
            }

            case PhysicalBlockEvent block:
            {
                foreach (var reaction in reactions)
                {
                    if (reaction is ShieldOfRageReaction rage) Apply(rage.Buff, block.OwnerId);
                    else if (reaction is ShieldOfTrustReaction trust)
                        commands.Add(new ApplyPartyBuffCommand
                        {
                            SourceId = block.OwnerId,
                            Buff = trust.Buff,
                        });
                }

                break;
            }

            case AttackEffectsEvent effects:
            {
                if (!SuccessfulAttack(effects)) return commands;
                foreach (var reaction in reactions)
                {
                    if (reaction is not FlaresOfPainReaction pain) continue;
                    if (!Succeeds(pain.ChanceThreshold, ports.Random)) continue;
                    if (effects.ActionId == "firestorm" || effects.HasMoltenArmor)
                        Apply(pain.Buff, effects.OpponentId);
                }

                break;
            }

            case MoltenReflectionCompletedEvent molten:
            {
                foreach (var reaction in reactions)
                    if (reaction is FlaresOfPainReaction pain &&
                        Succeeds(pain.ChanceThreshold, ports.Random))
                        Apply(pain.Buff, molten.OpponentId);
                break;
            }

            case ActionStealthBreakEvent stealth:
            {
                foreach (var reaction in reactions)
                    if (reaction is DarkSenseReaction sense) Apply(sense.Buff, stealth.OwnerId);
                break;
            }

            case SkillAttackSuccessEvent skill:
            {
                if (!SuccessfulAttack(skill)) return commands;
                foreach (var reaction in reactions)
                    if (reaction is MagicInsightReaction insight) Apply(insight.Buff, skill.OpponentId);
                break;
            }

            case DirectHealAcceptedEvent heal:
            {
                foreach (var reaction in reactions)
                    if (reaction is OrdinationPrayerReaction prayer &&
                        Succeeds(prayer.ChanceThreshold, ports.Random))
                        Apply(prayer.Buff, heal.TargetId);
                break;
            }

            case PhysicalHitReceivedEvent received:
            {
                foreach (var reaction in reactions)
                    if (reaction is WrathOfDarknessReaction wrath &&
                        Succeeds(wrath.ChanceThreshold, ports.Random))
                        Apply(wrath.Buff, received.OwnerId);
                break;
            }

            case BloodRevengeEvent revenge:
            {
                if (!SuccessfulAttack(revenge)) return commands;
                foreach (var reaction in reactions)
                    if (reaction is BloodRevengeReaction blood) Apply(blood.Buff, revenge.OwnerId);
                break;
            }
        }

        return commands;
    }
}
