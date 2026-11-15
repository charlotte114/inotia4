using System;
using System.Collections.Generic;

namespace Inotia4.Domain.Combat;

public sealed class CombatActorAttributes
{
    public required int PhysicalAttack { get; init; }

    public required int MagicAttack { get; init; }

    public required int PhysicalDamageRate { get; init; }

    public required int MagicDamageRate { get; init; }

    public required int StealthDamageRate { get; init; }

    public required int MaxHealth { get; init; }

    public required PrimaryAttributes PrimaryAttributes { get; init; }
}

public sealed class PeriodicSource
{
    public required string? ClassId { get; init; }

    public required CombatActorAttributes Attributes { get; init; }

    public required int LifeFeastRate { get; init; }

    public required Func<string, int> AttributeValue { get; init; }
}

public sealed class PeriodicContext
{
    public required string BuffId { get; init; }

    public required int StackCount { get; init; }

    public required PeriodicSource? Source { get; init; }

    public required int TargetHealingRate { get; init; }
}

public abstract class PeriodicCommand
{
    public required int Amount { get; init; }
}

public sealed class DamageTargetCommand : PeriodicCommand
{
}

public abstract class HealCommand : PeriodicCommand
{
    public string? Effect { get; init; }
}

public sealed class HealTargetCommand : HealCommand
{
}

public sealed class HealSourceCommand : HealCommand
{
}

public sealed class HealPartyCommand : HealCommand
{
}

public sealed class PeriodicVisualCommand : PeriodicCommand
{
    public required string EffectName { get; init; }
}

public static class CombatPeriodic
{
    public static List<PeriodicCommand> Resolve(BuffPeriodicEffect effect, PeriodicContext context)
    {
        switch (effect)
        {
            case VisualTick visual:
                return [new PeriodicVisualCommand { Amount = 0, EffectName = visual.Effect }];

            case FixedDamageTick fixedDamage:
                return [new DamageTargetCommand { Amount = fixedDamage.Amount * context.StackCount }];

            case MagicCurseTick curse:
            {
                var source = RequiredSource(context);
                var attributes = source.Attributes;
                var baseValue = attributes.MagicAttack + attributes.PrimaryAttributes.Intelligence +
                                (int)Math.Truncate(attributes.PrimaryAttributes.Mentality * 12.0 / 10);
                var amount = (int)Math.Truncate(
                    baseValue * (double)curse.RatePerThousand * context.StackCount / 1000);

                if (source.LifeFeastRate > 0)
                    amount = (int)Math.Truncate(amount * (1000.0 + source.LifeFeastRate) / 1000);

                HealCommand heal = source.LifeFeastRate > 0
                    ? new HealPartyCommand { Amount = amount, Effect = curse.HealEffect }
                    : new HealSourceCommand { Amount = amount, Effect = curse.HealEffect };
                return [heal, new DamageTargetCommand { Amount = amount }];
            }

            case NormalDamageTick normal:
            {
                var baseValue = context.Source is { } source
                    ? NormalDamageForClass(source.Attributes, source.ClassId)
                    : 0;
                return
                [
                    new DamageTargetCommand
                    {
                        Amount = CombatOperations.Apply(baseValue,
                            StackedOperation(normal.Operation, context.StackCount)),
                    },
                ];
            }

            case AttackDamageTick attack:
            {
                var source = RequiredSource(context);
                var baseValue = attack.Source == "physical"
                    ? DamageFormula.PhysicalNormalDamage(source.Attributes.PhysicalAttack,
                        source.Attributes.PhysicalDamageRate, source.Attributes.StealthDamageRate)
                    : DamageFormula.MagicNormalDamage(source.Attributes.MagicAttack,
                        source.Attributes.MagicDamageRate);
                return
                [
                    new DamageTargetCommand
                    {
                        Amount = CombatOperations.Apply(baseValue,
                            StackedOperation(attack.Operation, context.StackCount)),
                    },
                ];
            }

            case AttributeDamageTick attribute:
            {
                var baseValue = RequiredSource(context).AttributeValue(attribute.Attribute);
                return
                [
                    new DamageTargetCommand
                    {
                        Amount = CombatOperations.Apply(baseValue,
                            StackedOperation(attribute.Operation, context.StackCount)),
                    },
                ];
            }

            case HealingTick healing:
            {
                var source = RequiredSource(context);
                var baseValue = (int)Math.Truncate(
                    source.AttributeValue(healing.Attribute) * (double)context.TargetHealingRate / 1000);
                if (context.BuffId == "light-command")
                    baseValue += (int)Math.Truncate(source.Attributes.PrimaryAttributes.Mentality / 10.0);

                var amount = CombatOperations.Apply(baseValue, StackedOperation(healing.Operation, context.StackCount));
                return [new HealTargetCommand { Amount = amount }];
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(effect), $"未知的周期效果：{effect.GetType().Name}");
        }
    }

    public static int NormalDamageForClass(CombatActorAttributes attributes, string? classId) =>
        classId is "warlock" or "priest"
            ? DamageFormula.MagicNormalDamage(attributes.MagicAttack, attributes.MagicDamageRate)
            : DamageFormula.PhysicalNormalDamage(attributes.PhysicalAttack, attributes.PhysicalDamageRate,
                attributes.StealthDamageRate);

    private static CombatOperation StackedOperation(CombatOperation operation, int count) => operation.Kind switch
    {
        "keep" => operation,
        "set" or "add" or "subtract" => new CombatOperation
        {
            Kind = operation.Kind, Amount = operation.Amount * count,
        },
        "multiply" => new CombatOperation { Kind = operation.Kind, Factor = operation.Factor * count },
        "divide" => new CombatOperation { Kind = operation.Kind, Divisor = operation.Divisor * count },
        "multiply-thousandth" or "increase-thousandth" or "decrease-thousandth" => new CombatOperation
        {
            Kind = operation.Kind, RatePerThousand = operation.RatePerThousand * count,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(operation), $"未知的运算类型：{operation.Kind}"),
    };

    private static PeriodicSource RequiredSource(PeriodicContext context) =>
        context.Source ?? throw new InvalidOperationException($"Periodic buff {context.BuffId} requires its source actor");
}
