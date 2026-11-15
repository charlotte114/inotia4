using System;
using System.Collections.Generic;
using Inotia4.Domain.Combat;
using Inotia4.Domain.World;

namespace Inotia4.Scenes;
/// <summary>
/// 主角看到的战斗世界。控制器只管节奏与目标选取，凡是要问「这一步能不能走、
/// 这个人还活着吗、这一下打中谁」的事都从这里转成场景里的走位、地形与表现。
/// 翻译成场景里的走位、地形与表现，控制器本身只管节奏与目标选取。
/// </summary>
public sealed class PlayerCombatPort(WorldScene scene) : ICombatPort
{
    public Combatant? Actor(string id) => scene.FindCombatant(id);

    public IReadOnlyList<Combatant> Candidates() => scene.AllCombatants();

    public bool IsPaused() => scene.CombatRuntime.Paused;

    public bool IsAlive(string id) => scene.IsCombatantAlive(id);

    public BasicAttackSelection SelectBasicAttack(ResolvedBasicAttack normal) =>
        scene.SelectPlayerBasicAttack(normal);

    public IActorCooldownPort Cooldowns => scene.CooldownsFor(WorldScene.PlayerCombatId);

    public void SkillSounded(SkillPlayback skill) => scene.NoticeSkillSound(skill);

    public IReadOnlyList<HealSlot> HealCandidates() => scene.HealCandidatesFor();

    public IReadOnlyList<PartyBuffSlot?>? PartySlots() => scene.PartyBuffSlots();

    public int? HealScore(string id) => scene.HealScoreFor(id);

    /// <summary>
/// 手动技能的起手资格：生命门槛严格大于、需要活动召唤的必须有召唤、
    /// 需要活动召唤的必须有召唤、要求副手盾的要真装了盾。
    /// </summary>
    public bool CanSelectSkill(SkillPlayback skill)
    {
        var action = skill.Action;
        var maxHealth = scene.ResourceCapacityOf(WorldScene.PlayerCombatId, "maxHealth");
        var health = scene.PlayerHealth;
        // 严格大于：等于门槛不可起手。也要求自己的生命上限缓存有效。
        if (action.MinHealthRatePerThousand is { } gate && !(maxHealth > 0) ||
            action.MinHealthRatePerThousand is { } gateValue &&
            health <= (int)Math.Truncate(maxHealth * (double)gateValue / 1000))
            return false;

        // 没有自己的活动召唤就不能起手；这条判定在扣蓝与冷却之前，所以不会白扣蓝。
        if (action.RequiresActiveSummon && !scene.ActiveSummonsOf(WorldScene.PlayerCombatId)) return false;

        if (action.RequiredOffhand != "shield") return true;
        return scene.PlayerHasOffhandShield;
    }

    public int? ManaCost(SkillPlayback skill)
    {
        var ranks = scene.SkillCatalog.Skills.GetValueOrDefault(skill.Id)?.Ranks;
        if (ranks is null) return skill.ManaCost;
        var level = skill.Level ?? 1;
        foreach (var rank in ranks)
        {
            if (rank.Level != level) continue;
            return CombatResources.CalculateManaCost(rank.ManaCost,
                scene.AttributeOf(WorldScene.PlayerCombatId, "manaCostRate"),
                scene.AttributeOf(WorldScene.PlayerCombatId, "manaCostReductionRate"));
        }

        return null;
    }

    public bool CanSpendMana(int cost) => scene.IsInvincible(WorldScene.PlayerCombatId) || scene.PlayerMana >= cost;

    public bool SpendMana(int cost) => scene.IsInvincible(WorldScene.PlayerCombatId) || scene.SpendPlayerMana(cost);

    public int RangeBonusUnits(string id) => scene.AttributeOf(id, "attackRangeBonusUnits");

    public string Facing() => scene.PlayerFacing;

    public void MoveToward(Vec2 position) => scene.ApproachCombatTarget(position);

    public void Stop() => scene.StopCombatApproach();

    public bool CanStand(Vec2 position, string? clearanceActorId = null) =>
        scene.CanPlayerStandAt(position);

    public bool? CanReach(Vec2 position) => scene.CanPlayerReach(position);

    public Rect? NavigationBounds() => scene.MapBounds;

    public void Face(string direction) => scene.FacePlayer(direction);

    public void Relocate(Vec2 position, string? clearanceActorId = null) => scene.PlacePlayer(position);

    public Action Play(CombatAction action, Action<int> frame, Action complete) =>
        scene.PlayCombatAction(action, frame, complete);

    public void Frame(
        CombatAction action,
        int index,
        Action<string, string?> contact,
        SkillUnitsReady unitsReady,
        Action? areaContact = null) =>
        scene.DispatchCombatFrame(action, index, contact, unitsReady, areaContact);

    public void Contact(AttackContact contact)
    {
        if (contact.SkillId is not null)
        {
            scene.NotifySkillContact(contact);
            return;
        }

        scene.SettlePlayerContact(contact.TargetId, contact.Hand);
    }

    public void AreaContact(BasicAreaContact contact) => scene.SettleAreaContact(contact);

    public SkillUnitOutcome Unit(SkillUnitHit hit) => scene.SkillUnit(hit);

    public PreparedDamage? PrepareUnit(SkillUnitStart start) => scene.PrepareSkillUnit(start);

    public void RunUnitSequence(Action work) => scene.RunInHitResolution(work);

    public void UnitFeedback(SkillUnitHit hit, SkillUnitOutcome outcome, SkillEffectCue cue) =>
        scene.ShowUnitFeedback(hit, outcome, cue);

    public void ClearEffects() => scene.ClearPlayerActionVisuals();
}
