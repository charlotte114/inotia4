using System;
using System.Collections.Generic;

namespace Inotia4.Domain.Combat;

/// <summary>
/// 攻击附率执行器。
///
/// 六个阶段按固定次序掷骰：风击（眩晕）→ 火附（溅射）→ 冰击（减速）→
/// 圣击（破防）→ 暗击（重击）→ 毒击（中毒）。骰子按次序消耗，挪一步后面的判定
/// 就会拿到不同的随机数——所以次序是内容，不是实现细节。
/// 一个执行器实例横跨一次同步伤害链：前一段的结算回调跑完才读下一段的概率。
/// </summary>
public sealed class AttackProcExecutor
{
    private readonly record struct Stage(string Chance, string Kind, int DamageRate, string? Buff);

    private static readonly Stage[] Stages =
    [
        new("windStrikeChance", "damage", 700, "attack-stun"),
        new("fireSplashChance", "fire", 500, null),
        new("frostStrikeChance", "damage", 700, "attack-slow"),
        new("divineStrikeChance", "damage", 700, "divine-defense-break"),
        new("darknessStrikeChance", "damage", 3000, "darkness-strike-impact"),
        new("poisonStrikeChance", "buff", 0, "poisoned"),
    ];

    private bool _running;

    public enum AttackProcStatus
    {
        Completed,
        BudgetExhausted,
        Reentrant,
    }

    public readonly record struct AttackProcResult(AttackProcStatus Status, int StagesExecuted);

    public interface IAttackProcPort
    {
        int ReadChance(string key);

        void ApplyDamage(int amount);

        void ApplyBuff(string key);

        int FireSplash(int amount);

        Func<double>? Random { get; }
    }

    public AttackProcResult Execute(int normalDamage, IAttackProcPort port, int budget = 4096)
    {
        if (_running) return new AttackProcResult(AttackProcStatus.Reentrant, 0);
        if (budget < 0) throw new ArgumentOutOfRangeException(nameof(budget), "攻击附率预算必须是非负整数");

        _running = true;
        try
        {
            var index = 0;
            var stagesExecuted = 0;
            var random = port.Random;

            while (index < Stages.Length)
            {
                if (stagesExecuted == budget) return new AttackProcResult(AttackProcStatus.BudgetExhausted, stagesExecuted);

                var stage = Stages[index];
                stagesExecuted += 1;

                var chance = port.ReadChance(stage.Chance);
                if (chance <= 0 || (int)(Roll(random) * 999) + 1 >= chance)
                {
                    index += 1;
                    continue;
                }

                if (stage.Kind == "fire")
                {
                    var count = port.FireSplash((int)Math.Truncate((double)normalDamage * stage.DamageRate / 1000));
                    if (count is < 0 or > 16)
                        throw new ArgumentOutOfRangeException(nameof(port), "火附溅射目标数必须在 0 到 16 之间");
                    index = count + 1;
                }
                else
                {
                    if (stage.Kind == "damage")
                        port.ApplyDamage((int)Math.Truncate((double)normalDamage * stage.DamageRate / 1000));
                    port.ApplyBuff(stage.Buff!);
                    index += 1;
                }
            }

            return new AttackProcResult(AttackProcStatus.Completed, stagesExecuted);
        }
        finally
        {
            _running = false;
        }
    }

    private static double Roll(Func<double>? random) => random?.Invoke() ?? Random.Shared.NextDouble();
}
