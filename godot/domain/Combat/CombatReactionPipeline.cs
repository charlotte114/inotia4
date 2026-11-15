using System;
using System.Collections.Generic;
using Inotia4.Domain.World;

namespace Inotia4.Domain.Combat;

public static class HitKindNames
{
    public static string Of(HitKind kind) => kind switch
    {
        HitKind.Miss => "miss",
        HitKind.ShieldBlock => "shield-block",
        HitKind.WeaponBlock => "weapon-block",
        HitKind.Critical => "critical",
        _ => "hit",
    };
}

public interface IHitReactionPort
{
    CombatConditions Conditions { get; }

    int ActorAttribute(string id, string key);

    void React(SkillReactionEvent action);

    void ReactToBuff(BuffReactionEvent action);

    void LightningOfHellReaction(string casterId, string targetId);

    void GodOfJusticeReaction(string casterId, string targetId);

    void SummonOwnerReactions(AttackContact contact, HitResult hit, string damageKind);

    void DisturbanceReaction(string casterId, string targetId);

    bool PartySide(string id);

    IReadOnlyList<string> ActivePartySlots();

    void PartyRevengeReactions(string casterId, string damageKind, string outcome);

    int EquipmentLifeDrainPerThousand(string id);

    bool IsTwoHanded(string id);

    void MoltenReflection(AttackContact contact, int rate);

    void HolySacrificeTransfer(AttackContact contact, HitResult hit, string damageKind);

    void AttackProc(AttackContact contact, HitResult hit, Vec2? position = null);
}

/// <summary>
/// 一次命中之后的有序反应链。
///
/// 每一步的先后都不能挪：骰子是按次序消耗的，挪一步后面的判定就会拿到不同的随机数。
/// 外层命中事务要等整条链跑完才提交死亡检查。
/// </summary>
public static class CombatReactionPipeline
{
    public static void Resolve(
        AttackContact contact,
        HitResult hit,
        string damageKind,
        string actionId,
        IHitReactionPort port,
        Vec2? targetPosition = null)
    {
        var outcome = HitKindNames.Of(hit.Kind);

        port.ReactToBuff(new PhysicalAvoidanceEvent
        {
            OwnerId = contact.TargetId,
            DamageKind = damageKind,
            Outcome = outcome,
        });

        if (damageKind == "physical" && (hit.Kind == HitKind.Miss || hit.Kind == HitKind.WeaponBlock))
        {
            var fortitudeChance = port.ActorAttribute(contact.TargetId, "defenseCooldownResetChance");
            if (fortitudeChance > 0)
            {
                port.React(new PhysicalDefenseInstinctEvent
                {
                    OwnerId = contact.TargetId,
                    OpponentId = contact.CasterId,
                    DamageKind = damageKind,
                    Outcome = outcome,
                    ChanceThreshold = fortitudeChance,
                });
            }
        }

        if (hit.Kind == HitKind.Miss || (damageKind == "magic" && hit.Kind == HitKind.ShieldBlock)) return;

        if (damageKind == "magic" && hit.Kind == HitKind.Critical)
            port.LightningOfHellReaction(contact.CasterId, contact.TargetId);

        if (hit.Kind == HitKind.Critical && (actionId == "divine-strike" || actionId == "hammer-of-wrath"))
            port.GodOfJusticeReaction(contact.CasterId, contact.TargetId);

        port.SummonOwnerReactions(contact, hit, damageKind);

        port.DisturbanceReaction(contact.CasterId, contact.TargetId);

        if (port.PartySide(contact.TargetId))
            port.PartyRevengeReactions(contact.CasterId, damageKind, outcome);

        if (damageKind == "physical" && hit.Kind == HitKind.WeaponBlock)
        {
            port.ReactToBuff(new WeaponBlockStunEvent
            {
                OwnerId = contact.TargetId,
                OpponentId = contact.CasterId,
                DamageKind = damageKind,
                Outcome = outcome,
            });
            port.ReactToBuff(new WeaponBlockCounterEvent
            {
                OwnerId = contact.TargetId,
                OpponentId = contact.CasterId,
                DamageKind = damageKind,
                Outcome = outcome,
            });
        }

        if (damageKind == "physical" && (hit.Kind == HitKind.WeaponBlock || hit.Kind == HitKind.ShieldBlock))
            port.React(new PhysicalBlockEvent { OwnerId = contact.TargetId });

        if (actionId != "basic-attack")
        {
            port.React(new SkillAttackSuccessEvent
            {
                OwnerId = contact.CasterId,
                OpponentId = contact.TargetId,
                DamageKind = damageKind,
                Outcome = outcome,
            });
        }

        // 剑之灵魂与吸血之剑的恢复排在残影之前，所以残影的骰子在这里才消耗。
        port.React(new AttackSuccessEvent
        {
            OwnerId = contact.CasterId,
            OpponentId = contact.TargetId,
            DamageKind = damageKind,
            Outcome = outcome,
            HitDamage = hit.Damage,
            OtherLifeDrainPerThousand = port.EquipmentLifeDrainPerThousand(contact.CasterId),
        });

        if (damageKind == "physical")
        {
            port.React(new PhysicalFlareEvent
            {
                OwnerId = contact.CasterId,
                OpponentId = contact.TargetId,
                DamageKind = damageKind,
                Outcome = outcome,
                Hand = contact.Hand,
            });
        }

        if (damageKind == "physical")
        {
            var armorBreakChance = port.ActorAttribute(contact.CasterId, "armorBreakChance");
            if (armorBreakChance > 0)
            {
                port.React(new PhysicalAttackEffectsEvent
                {
                    OwnerId = contact.CasterId,
                    OpponentId = contact.TargetId,
                    DamageKind = damageKind,
                    Outcome = outcome,
                    ArmorBreakChance = armorBreakChance,
                });
            }

            var bloodThirstChance = port.ActorAttribute(contact.CasterId, "bloodThirstChance");
            if (bloodThirstChance >= 200)
            {
                port.React(new BloodThirstEvent
                {
                    OwnerId = contact.CasterId,
                    OpponentId = contact.TargetId,
                    DamageKind = damageKind,
                    Outcome = outcome,
                    HitDamage = hit.Damage,
                    ChanceThreshold = bloodThirstChance,
                });
            }

            foreach (var reaction in port.Conditions.BuffReactions(contact.CasterId))
            {
                if (reaction.Kind != "physical-success-party") continue;
                port.ReactToBuff(new PhysicalSuccessPartyEvent
                {
                    OwnerId = contact.CasterId,
                    DamageKind = damageKind,
                    Outcome = outcome,
                    PartyIds = port.ActivePartySlots(),
                });
                break;
            }

            var revengeChance = port.ActorAttribute(contact.TargetId, "revengeChance");
            if (revengeChance > 0)
            {
                port.React(new PhysicalRevengeEvent
                {
                    OwnerId = contact.TargetId,
                    OpponentId = contact.CasterId,
                    DamageKind = damageKind,
                    Outcome = outcome,
                    ChanceThreshold = revengeChance,
                });
            }

            var cooldownResetChance = port.ActorAttribute(contact.CasterId, "cooldownResetChance");
            if (cooldownResetChance > 0)
            {
                port.React(new PhysicalInstinctEvent
                {
                    OwnerId = contact.CasterId,
                    OpponentId = contact.TargetId,
                    DamageKind = damageKind,
                    Outcome = outcome,
                    ChanceThreshold = cooldownResetChance,
                    MainHandTwoHanded = port.IsTwoHanded(contact.CasterId),
                });
            }

            var reflectionRate = port.ActorAttribute(contact.TargetId, "moltenReflectionPerThousand");
            if (reflectionRate > 0) port.MoltenReflection(contact, reflectionRate);

            port.ReactToBuff(new PhysicalSuccessThreatEvent
            {
                OwnerId = contact.CasterId,
                OpponentId = contact.TargetId,
                DamageKind = damageKind,
                Outcome = outcome,
                HitDamage = hit.Damage,
            });
        }

        port.React(new AttackEffectsEvent
        {
            OwnerId = contact.CasterId,
            OpponentId = contact.TargetId,
            DamageKind = damageKind,
            Outcome = outcome,
            ActionId = actionId,
            HasMoltenArmor = port.ActorAttribute(contact.CasterId, "moltenReflectionPerThousand") > 0,
        });

        port.HolySacrificeTransfer(contact, hit, damageKind);

        if (damageKind == "physical")
            port.React(new PhysicalHitReceivedEvent { OwnerId = contact.TargetId });

        port.ReactToBuff(new PhysicalSuccessEvent
        {
            OwnerId = contact.CasterId,
            DamageKind = damageKind,
            Outcome = outcome,
        });

        if (hit.Damage > 0) port.Conditions.RemoveState(contact.TargetId, "sleep");

        port.AttackProc(contact, hit, targetPosition);
    }
}
