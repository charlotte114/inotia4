using Inotia4.Domain.Content;
using Inotia4.Domain.World;

namespace Inotia4.Domain.Combat;

public delegate void ActionDispatchRunner(ActionDispatchDefinition definition, Action work, Action? after = null);

public interface ICombatActionPort
{
    CombatRuntime Runtime { get; }

    BattleContent Catalog { get; }

    Combatant? Actor(string id);

    IReadOnlyList<Combatant> Candidates();

    Func<double> Random { get; }

    (int Width, int Height) Viewport();

    void Pose(CombatAction action, int index);

    void Effect(string name, string variant, Vec2 position, string? placement = null, string? recipientId = null);

    void Flight(FlightEvent flight);

    void Screen(float transparency);

    void Notify(string name, IReadOnlyDictionary<string, object?> data);

    /// <summary>这个落点能不能召唤（整盒可站）。</summary>
    bool CanSummonAt(Vec2 position, CollisionBox bounds);

    /// <summary>召唤落点：<paramref name="preferred"/> 可达就原样用，否则从主人位置找最近的整盒可站点。</summary>
    Vec2? NearestSummonSpot(Vec2 position, CollisionBox bounds, Vec2? preferred = null);

    void BeginSummons(string ownerId);

    string? Summon(string ownerId, string enemyId, Vec2 position);
}

/// <summary>
/// 在逻辑帧中派发动作：只决定「这一帧该放什么、该结算什么」。
///
/// 它只决定「这一帧该放什么、该结算什么」，具体画法与伤害由
/// <see cref="ICombatActionPort"/> 的实现接手；投射物抵达由
/// <see cref="CombatRuntime"/> 决定，所以派发器本身没有时钟。
/// </summary>
public sealed class CombatActionDispatcher(ICombatActionPort port)
{
    private void Visual(SkillVisualEffect effect, string variant, Combatant actor)
    {
        port.Effect(effect.Effect, variant, actor.Position, effect.Placement, actor.Id);
        port.Notify("skill-effect", new Dictionary<string, object?>
        {
            ["name"] = effect.Effect,
            ["variant"] = variant,
            ["recipientId"] = actor.Id,
            ["placement"] = effect.Placement,
        });
    }

    private void Transition(CombatAction action, Combatant? target, Action<Vec2> arrive)
    {
        var transition = action.Skill!.Action.Transition!;
        port.Runtime.Transition(action.Caster.Id, transition, target, action.Caster.Position,
            new DispatcherTransitionPort(port), arrive);
    }

    public void CasterStrikes(string casterId, CasterStrikes strikes)
    {
        for (var index = 0; index < strikes.Offsets.Count; index++)
        {
            var offset = strikes.Offsets[index];
            var ordinal = index;
            void Strike()
            {
                var caster = port.Actor(casterId);
                if (caster is null) return;
                port.Effect(strikes.Effect, "default",
                    new Vec2(caster.Position.X + offset.X, caster.Position.Y + offset.Y));
            }

            if (ordinal == 0) Strike();
            else port.Runtime.Schedule(casterId, ordinal, Strike);
        }
    }

    public void Frame(
        CombatAction action,
        int index,
        Action<string, string?>? contact = null,
        SkillUnitsReady? ready = null,
        Action<FeedbackEvent>? feedback = null,
        ActionDispatchRunner? dispatch = null,
        Action? area = null)
    {
        contact ??= (_, _) => { };
        ready ??= _ => [];
        feedback ??= _ => { };
        dispatch ??= (_, work, after) =>
        {
            work();
            after?.Invoke();
        };
        area ??= () => { };

        var caster = action.Caster;
        var target = action.Target;
        var skill = action.Skill;
        var facing = action.Facing;

        port.Pose(action, index);

        if (skill is null)
        {
            var basicAttack = action.BasicAttack;
            if (basicAttack is null) return;

            var definition = basicAttack.Definition;
            var events = BasicAttackRules.Events(basicAttack, index);
            void Swing()
            {
                if (!events.Swing) return;
                var variant = facing;
                var position = target?.Position ?? caster.Position;
                foreach (var name in definition.Swings)
                    port.Effect(name, variant, position, definition.SwingPlacement ?? "world", caster.Id);
            }

            if (!definition.SwingAfterStrike) Swing();

            var strikes = BasicAttackRules.StrikesAtFrame(basicAttack, index);
            for (var ordinal = 0; ordinal < strikes.Count; ordinal++)
            {
                var hand = strikes[ordinal];
                var detail = definition.Strikes is not null
                    ? new Dictionary<string, object?> { ["casterId"] = caster.Id, ["targetId"] = target?.Id, ["hand"] = hand }
                    : new Dictionary<string, object?> { ["casterId"] = caster.Id, ["targetId"] = target?.Id };
                port.Notify("basic-attack-strike", detail);

                void Work()
                {
                    if (definition.Targeting is not null)
                    {
                        area();
                    }
                    else if (target is not null)
                    {
                        void Arrive(Vec2 _)
                        {
                            var live = port.Actor(target.Id);
                            if (live is null) return;

                            foreach (var effect in definition.TargetEffects ?? [])
                            {
                                var recipient = effect.Recipient == "caster" ? port.Actor(caster.Id) : live;
                                if (recipient is not null) Visual(effect, "default", recipient);
                            }

                            port.Notify("basic-attack-impact", new Dictionary<string, object?>
                            {
                                ["casterId"] = caster.Id,
                                ["targetId"] = target.Id,
                                ["hand"] = hand,
                            });
                            contact(target.Id, hand);
                        }

                        if (definition.Projectile is { } flight)
                        {
                            var offsetX = facing == "east" ? flight.OffsetX : -flight.OffsetX;
                            port.Runtime.Fly(new FlightRequest
                            {
                                OwnerId = caster.Id,
                                Effect = flight.Effect,
                                Position = new Vec2(caster.Position.X + offsetX, caster.Position.Y + flight.OffsetY),
                                TargetId = target.Id,
                                Kind = "trace",
                                StepDistancePx = (int)flight.StepDistancePx,
                                StepDurationMs = flight.StepDurationMs,
                                AboveCharacters = definition.ProjectilePlacement == "above-characters",
                            }, new DispatcherFlightPort(port), Arrive);
                        }
                        else if (definition.Transition is not null)
                        {
                            port.Runtime.Transition(caster.Id, definition.Transition, target, caster.Position,
                                new DispatcherTransitionPort(port), Arrive);
                        }
                        else
                        {
                            Arrive(caster.Position);
                        }
                    }

                    foreach (var effect in definition.Dispatch?.CasterEffects ?? []) Visual(effect, "default", caster);
                }

                var last = ordinal == strikes.Count - 1;
                Action? after = definition.SwingAfterStrike && last ? Swing : null;
                if (definition.Dispatch is not null) dispatch(definition.Dispatch, Work, after);
                else
                {
                    Work();
                    after?.Invoke();
                }
            }

            if (strikes.Count == 0 && definition.SwingAfterStrike) Swing();
            return;
        }

        foreach (var skillEvent in SkillActionRules.SkillPlaybackEvents(skill.Action, index, skill.Level ?? 1,
                     action.RepeatState))
        {
            // 第二次调用读的是被上一事件改写过的重复状态：停在新的边界之后，
            // 本帧原本要播的起手事件就不再成立，必须跟着丢掉（判据是「第二次算出来还含这一条」）。
            if (action.RepeatState is not null &&
                !SkillActionRules.SkillPlaybackEvents(skill.Action, index, skill.Level ?? 1,
                    action.RepeatState).Contains(skillEvent)) continue;
            switch (skillEvent)
            {
                case FeedbackEvent feedbackEvent:
                    feedback(feedbackEvent);
                    break;

                case CastEvent cast:
                    Visual(cast.Effect, "default", caster);
                    break;

                case UnitsEvent units:
                {
                    void Work()
                    {
                        Action<Vec2?> ExecuteAt(string? selectedTargetId)
                        {
                            var executed = false;
                            return center =>
                            {
                                if (executed) return;
                                executed = true;

                                var request = new SkillUnitsArrival
                                {
                                    Event = units,
                                    SelectedTargetId = selectedTargetId,
                                    ImpactCenter = center,
                                };
                                var results = ready(request);
                                if (center is { } spot && results.Count == 0)
                                    foreach (var effect in skill.Action.Transition?.MissEffects ?? [])
                                        port.Effect(effect.Effect, "default", spot, effect.Placement, caster.Id);
                            };
                        }

                        if (skill.Action.Transition?.CasterStrikes is { } strikes)
                            CasterStrikes(caster.Id, strikes);

                        if (skill.Action.Transition is not null)
                        {
                            var anchor = skill.Action.Transition.Anchor == "caster" ? caster : target;
                            if (action.CapturedTargets is { } captured)
                            {
                                foreach (var entry in captured)
                                {
                                    var capturedId = entry.Id;
                                    var capturedPosition = entry.Position;
                                    var arrive = ExecuteAt(capturedId);
                                    Transition(action, CapturedCombatant(capturedId, capturedPosition),
                                        spot => arrive(spot));
                                }
                            }
                            else
                            {
                                var arrive = ExecuteAt(anchor?.Id);
                                Transition(action, anchor, spot => arrive(spot));
                            }
                        }
                        else
                        {
                            ExecuteAt(target?.Id)(null);
                        }

                        foreach (var effect in units.Dispatch?.CasterEffects ?? []) Visual(effect, "default", caster);
                    }

                    if (units.Dispatch is not null) dispatch(units.Dispatch, Work);
                    else Work();
                    break;
                }

                case CaptureTargetsEvent:
                    break;

                case CapturedEffectEvent captured:
                    foreach (var entry in action.CapturedTargets ?? [])
                        port.Effect(captured.Effect, "default", entry.Position, captured.Placement, caster.Id);
                    break;

                case SummonEvent summon:
                {
                    void Work()
                    {
                        Summon(action, summon);
                        foreach (var effect in summon.Dispatch?.CasterEffects ?? []) Visual(effect, "default", caster);
                    }

                    if (summon.Dispatch is not null) dispatch(summon.Dispatch, Work);
                    else Work();
                    break;
                }

                case CasterEvent casterEvent:
                    foreach (var effect in casterEvent.Effects) Visual(effect, "default", caster);
                    break;

                case SwingEvent swing:
                    foreach (var effect in swing.Effects)
                    foreach (var recipient in effect.Recipient == "caster"
                                 ? new[] { caster }
                                 : target is not null ? new[] { target } : [])
                        Visual(effect, effect.Recipient == "caster" ? facing : "default", recipient);
                    break;

                case ImpactEvent impact:
                    foreach (var recipient in CueTargets.Resolve(
                                 impact.Target, float.PositiveInfinity, null, caster, target, port.Candidates()))
                        Visual(impact.Effect, "default", recipient);
                    break;

                case SpawnEvent spawn:
                {
                    var (width, height) = port.Viewport();
                    var x = Math.Max(0, width / 2 - (int)spawn.MarginPx);
                    var y = Math.Max(0, height / 2 - (int)spawn.MarginPx);
                    double Between(int span) => Math.Floor(port.Random() * (span * 2 + 1)) - span;
                    Between(x);
                    Between(y);
                    port.Effect(spawn.Effect, "default",
                        new Vec2(caster.Position.X + (float)Between(x), caster.Position.Y + (float)Between(y)),
                        spawn.Placement, caster.Id);
                    break;
                }

                case ScreenEvent screen:
                    port.Screen(screen.FillTransparency);
                    break;
            }
        }
    }

    /// <summary>
    /// 把「捕获目标」事件记下的目标包成一个临时 <see cref="Combatant"/>：
    /// 过渡只读它的 id 与位置，不需要队伍、碰撞盒这些真实属性（所以只填这两项）。
    /// </summary>
    private static Combatant CapturedCombatant(string id, Vec2 position) =>
        new(id, "captured", position, new CollisionBox());

    private void Summon(CombatAction action, SummonEvent summon)
    {
        var count = 0;
        port.BeginSummons(action.Caster.Id);

        foreach (var creature in summon.Creatures)
        {
            var definition = port.Catalog.Enemies.FirstOrDefault(enemy => enemy.Id == creature.EnemyId);
            var bounds = definition?.Actor.Collision;
            if (bounds is null) continue;

            var placement = summon.Placement;
            var offset = placement.OffsetChoices[
                (int)Math.Floor(port.Random() * placement.OffsetChoices.Count)];
            var baseSpot = SkillGeometry.PlanSummonSpot(action.Caster.Position, action.Caster.Bounds, action.Facing,
                placement, point => port.CanSummonAt(point, bounds));

            double Jitter() => Math.Floor(port.Random() * (placement.JitterPx * 2 + 1)) - placement.JitterPx;
            Vec2? candidate = baseSpot is { } spot
                ? new Vec2(spot.X + offset.X + (float)Jitter(), spot.Y + offset.Y + (float)Jitter())
                : null;
            var preferred = candidate is { } value && port.CanSummonAt(value, bounds) ? candidate : baseSpot;

            var final = port.NearestSummonSpot(action.Caster.Position, bounds, preferred);
            if (final is null) continue;

            var id = port.Summon(action.Caster.Id, creature.EnemyId, final.Value);
            if (id is null) continue;
            count++;
            port.Effect(summon.Effect, "default", final.Value, "attached", id);
        }

        port.Notify("skill-summon", new Dictionary<string, object?>
        {
            ["casterId"] = action.Caster.Id,
            ["skillId"] = action.Skill!.Id,
            ["count"] = count,
        });
    }
}

internal sealed class DispatcherFlightPort(ICombatActionPort port) : IFlightPort
{
    public Combatant? Actor(string id) => port.Actor(id);

    public void Emit(FlightEvent action) => port.Flight(action);
}

internal sealed class DispatcherTransitionPort(ICombatActionPort port) : ITransitionPort
{
    public Combatant? Actor(string id) => port.Actor(id);

    public void Emit(FlightEvent action) => port.Flight(action);

    public void Effect(string name, Vec2 position)
    {
        port.Effect(name, "default", position);
        port.Notify("skill-effect", new Dictionary<string, object?>
        {
            ["name"] = name,
            ["variant"] = "default",
        });
    }
}
