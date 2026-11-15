using Inotia4.Domain.World;

namespace Inotia4.Domain.Combat;

public readonly record struct Vitals(int Health, int Mana, int MaxMana);

public readonly record struct HitPresentation(string Style, bool Sound);

public sealed record SkillReactionBuff(
    [property: System.Text.Json.Serialization.JsonPropertyName("key")] string Key,
    [property: System.Text.Json.Serialization.JsonPropertyName("rankPriority")] int RankPriority,
    [property: System.Text.Json.Serialization.JsonPropertyName("effect")] BattleBuffEffect Effect);

public interface ICombatEffectPort
{
    CombatConditions Conditions { get; }

    CombatThreatSystem? Threat { get; }

    double Random();

    T Run<T>(Func<T> work);

    bool Available(string id);

    bool PartySide(string id);

    Fighter? Fighter(string id);

    Vec2? Position(string id);

    Vitals? Vitals(string id);

    int Attribute(string id, string key);

    string ValueSourceId(string id, ICombatUnit unit);

    PreparedDamage? Prepare(SkillUnitHit hit);

    bool ApplyBuff(string id, string buffId, int strength, BattleBuffEffect effect, string sourceId);

    StateApplicationRequest StateRequest(string sourceId, string targetId, string state);

    void StateAccepted(string targetId, string state);

    SkillUnitOutcome CommandSummons(SkillUnitHit hit);

    SkillUnitOutcome Summon(SkillUnitHit hit, SummonCombatUnit unit, int rank);

    void RestoreHealth(string id, int amount);

    void SetMana(string id, int value);

    void HealAccepted(string sourceId, string targetId);

    SkillReactionBuff? LearnedBuff(string id, string key);

    int RearBonus(string sourceId, string targetId);

    void Reactions(SkillUnitHit hit, HitResult result, string kind, Vec2? position);

    void Hit(SkillUnitHit hit, HitResult result, HitPresentation presentation);

    void DropMana(string id, int damage, Vec2? position, string? kind);

    void ShowHit(SkillUnitHit hit, HitResult result, HitPresentation presentation);
}

public sealed class CombatEffectExecutor(ICombatEffectPort port)
{
    public SkillUnitOutcome Execute(SkillUnitHit hit)
    {
        var p = port;
        return p.Run(() =>
        {
            if (!p.Available(hit.TargetId)) return SkillUnitOutcome.Skipped;
            if (!p.Available(hit.CasterId)) return SkillUnitOutcome.Failure;

            var units = hit.Skill.Action.CombatUnits;
            if (units is null || hit.CueIndex >= units.Count) return SkillUnitOutcome.Skipped;
            var unit = units[hit.CueIndex];

            var source = p.Fighter(hit.CasterId);
            if (source is null) return SkillUnitOutcome.Failure;

            var presentation = new HitPresentation(hit.Skill.Action.HitEffectStyle ?? "weapon", true);

            var hostile = p.PartySide(hit.CasterId) != p.PartySide(hit.TargetId);
            var damaging = unit.Effect is "physical" or "magic";
            if (hostile && !p.Conditions.CanReceiveDamage(hit.TargetId))
            {
                if (damaging) p.ShowHit(hit, new HitResult(HitKind.Hit, 0), presentation);
                return SkillUnitOutcome.Failure;
            }

            // GM 调试无敌只挡 HP 伤害；吸蓝、状态、增益等非伤害效果照常结算，
            // 否则「无敌」会被误解成「所有效果免疫」，连敌人的 MP 消耗也一并免掉。
            if (damaging && hostile && !p.Conditions.CanLoseHealth(hit.TargetId))
            {
                p.ShowHit(hit, new HitResult(HitKind.Hit, 0), presentation);
                return SkillUnitOutcome.Failure;
            }

            var level = hit.Skill.Level ?? 1;
            var rank = RankOf(unit, level);
            var amount = 0;
            if (unit is NumericUnit)
            {
                var prepared = hit.PreparedDamage ?? p.Prepare(hit);
                if (prepared is null) return SkillUnitOutcome.Failure;
                amount = DamageFormula.Resolve(prepared,
                    prepared.Source == "none" ? 0 : p.Attribute(p.ValueSourceId(hit.CasterId, unit), prepared.Source));
            }

            switch (unit)
            {
                case SimpleCombatUnit { Effect: "action" }:
                    return p.CommandSummons(hit);

                case SetAttributeUnit set:
                    p.Conditions.SetTemporaryAttribute(hit.CasterId, set.Attribute,
                        CombatOperations.Apply(p.Attribute(hit.CasterId, set.Attribute),
                            set.Ranks.Count > 0 ? set.Ranks[rank] : CombatOperation.Keep()));
                    return SkillUnitOutcome.Success;

                case ClearAttributeUnit clear:
                    p.Conditions.ClearTemporaryAttribute(hit.CasterId, clear.Attribute);
                    return SkillUnitOutcome.Success;

                case SimpleCombatUnit { Effect: "taunt" }:
                    p.Threat?.Taunt(hit.CasterId, hit.TargetId);
                    return SkillUnitOutcome.Success;

                case SummonCombatUnit summon:
                    return p.Summon(hit, summon, rank);

                case BuffCombatUnit buff:
                {
                    var formula = buff.Ranks[rank];
                    if (!ActionStateRules.PassesChanceThreshold(formula.ChanceThreshold, p.Random))
                        return SkillUnitOutcome.Failure;
                    p.ApplyBuff(hit.TargetId, buff.Buff, formula.Strength, formula.Effect, hit.CasterId);
                    return SkillUnitOutcome.Success;
                }

                case StateCombatUnit state:
                {
                    var values = state.Ranks[rank];
                    var request = p.StateRequest(hit.CasterId, hit.TargetId, state.State);
                    var decision = StateApplication.Resolve(new StateApplicationRequest
                    {
                        State = request.State,
                        SourceRelation = request.SourceRelation,
                        Kind = "action",
                        Rules = request.Rules,
                        SpecificImmunityValue = request.SpecificImmunityValue,
                        ResistanceThreshold = request.ResistanceThreshold,
                        ChanceThreshold = values.ChanceThreshold,
                    }, p.Random);
                    if (!decision.Allowed) return SkillUnitOutcome.Failure;

                    var applied = values.StateDurationProfile is { } profile
                        ? p.Conditions.ApplyStateFrames(hit.TargetId, state.State,
                            StateApplication.SelectStateDurationProfile(profile, decision).LastActiveFrame,
                            hit.CasterId)
                        : p.Conditions.ApplyState(hit.TargetId, state.State, values.DurationMs, hit.CasterId);
                    if (!applied) return SkillUnitOutcome.Failure;

                    p.StateAccepted(hit.TargetId, state.State);
                    p.Threat?.State(hit.CasterId, hit.TargetId);
                    return SkillUnitOutcome.Success;
                }

                case NumericUnit { Effect: "heal" }:
                {
                    var before = p.Vitals(hit.TargetId);
                    if (before is null || before.Value.Health < 1) return SkillUnitOutcome.Success;
                    var health = before.Value.Health;
                    p.RestoreHealth(hit.TargetId,
                        ActionHealing.Apply(amount, p.Attribute(hit.CasterId, "healingPowerRate")));
                    p.Threat?.Heal(hit.CasterId, hit.TargetId, (p.Vitals(hit.TargetId)?.Health ?? health) - health);
                    p.HealAccepted(hit.CasterId, hit.TargetId);
                    return SkillUnitOutcome.Success;
                }

                case NumericUnit { Effect: "drain" }:
                {
                    if (!p.Conditions.LosesMana(hit.TargetId)) return SkillUnitOutcome.Success;
                    var caster = p.Vitals(hit.CasterId);
                    if (caster is { } own)
                    {
                        var result = ManaDrain.Drain(amount, p.Vitals(hit.TargetId)?.Mana ?? 0,
                            own.Mana, own.MaxMana);
                        p.SetMana(hit.TargetId, result.TargetMana);
                        p.SetMana(hit.CasterId, result.CasterMana);
                    }

                    return SkillUnitOutcome.Success;
                }

                case NumericUnit { Effect: "self-damage" } self:
                {
                    if (amount > 0)
                    {
                        var reflected = new SkillUnitHit
                        {
                            CasterId = hit.CasterId,
                            Skill = hit.Skill,
                            CueIndex = hit.CueIndex,
                            TargetId = hit.CasterId,
                            PreparedDamage = hit.PreparedDamage,
                        };
                        p.Hit(reflected, new HitResult(HitKind.Hit, amount), new HitPresentation("weapon", true));
                    }

                    if (self.AlsoAppliesLearnedBuff is { } learned)
                    {
                        var buff = p.LearnedBuff(hit.CasterId, learned);
                        if (buff is not null)
                            p.ApplyBuff(hit.CasterId, buff.Key, buff.RankPriority, buff.Effect, hit.CasterId);
                    }

                    return SkillUnitOutcome.Success;
                }

                case NumericUnit { Effect: "physical" or "magic" } numeric:
                {
                    var target = p.Fighter(hit.TargetId);
                    if (target is null) return SkillUnitOutcome.Skipped;
                    var position = p.Position(hit.TargetId);
                    var usesBuff = hit.Skill.Action.UsesSkillDamageBuff;
                    var bonus = usesBuff ? p.Conditions.Attribute(hit.CasterId, "skillDamageBonus", 0) : 0;

                    var result = numeric.Effect == "magic"
                        ? HitResolver.RollMagic(source, target, p.Random, amount,
                            bonus + (usesBuff ? source.ConditionalMagicDamageRate : 0))
                        : HitResolver.RollPhysical(source, target, p.Random, amount,
                            bonus + p.RearBonus(hit.CasterId, hit.TargetId),
                            hit.Skill.Id == "assassin-s-sword"
                                ? p.Attribute(hit.CasterId, "assassinSwordHitAdjustment")
                                : 0,
                            usesBuff ? () => p.Attribute(hit.CasterId, "physicalAttack") : null);

                    if (bonus > 0 && result.Kind != HitKind.Miss)
                        p.Conditions.RemoveBuff(hit.CasterId, "dark-strike");

                    p.Reactions(hit, result, numeric.Effect, position);
                    p.Hit(hit, result, presentation);
                    p.DropMana(hit.CasterId, result.Damage, position, hit.Skill.Action.ManaGemDrop);
                    return result.Damage > 0 ? SkillUnitOutcome.Success : SkillUnitOutcome.Failure;
                }

                default:
                    return SkillUnitOutcome.Success;
            }
        });
    }

    private static int RankOf(ICombatUnit unit, int level)
    {
        var count = unit switch
        {
            NumericUnit numeric => numeric.Ranks.Length,
            StateCombatUnit state => state.Ranks.Count,
            BuffCombatUnit buff => buff.Ranks.Count,
            SummonCombatUnit summon => summon.Ranks.Count,
            SetAttributeUnit set => set.Ranks.Count,
            ClearAttributeUnit => 0,
            _ => 0,
        };

        return count == 0 ? 0 : Math.Min(count - 1, Math.Max(0, level - 1));
    }
}
