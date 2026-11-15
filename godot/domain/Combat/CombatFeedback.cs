using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Inotia4.Domain.Combat;

public sealed class CombatFeedbackDefinition
{
    [JsonPropertyName("digits")] public CombatDamageDigits Digits { get; init; } = new();

    [JsonPropertyName("normalImpacts")] public string[] NormalImpacts { get; init; } = [];

    [JsonPropertyName("skillImpact")] public string? SkillImpact { get; init; }

    [JsonPropertyName("criticalImpact")] public string? CriticalImpact { get; init; }

    [JsonPropertyName("criticalRing")] public string? CriticalRing { get; init; }

    [JsonPropertyName("criticalBursts")] public string[] CriticalBursts { get; init; } = [];

    [JsonPropertyName("missEffect")] public string? MissEffect { get; init; }

    [JsonPropertyName("shakeOffsets")] public CombatShakeOffset[] ShakeOffsets { get; init; } = [];

    public string[] DigitPool(string style) => style switch
    {
        "enemy" => Digits.Enemy,
        "enemyCritical" => Digits.EnemyCritical,
        "party" => Digits.Party,
        "partyCritical" => Digits.PartyCritical,
        "miss" => Digits.Miss,
        _ => [],
    };

    public IReadOnlyList<string> AllEffects()
    {
        var names = new List<string>();
        names.AddRange(Digits.Enemy);
        names.AddRange(Digits.EnemyCritical);
        names.AddRange(Digits.Party);
        names.AddRange(Digits.PartyCritical);
        names.AddRange(Digits.Miss);
        names.AddRange(NormalImpacts);
        if (SkillImpact is not null) names.Add(SkillImpact);
        if (CriticalImpact is not null) names.Add(CriticalImpact);
        if (CriticalRing is not null) names.Add(CriticalRing);
        names.AddRange(CriticalBursts);
        if (MissEffect is not null) names.Add(MissEffect);
        return names;
    }
}

public sealed class CombatDamageDigits
{
    [JsonPropertyName("enemy")] public string[] Enemy { get; init; } = [];

    [JsonPropertyName("enemyCritical")] public string[] EnemyCritical { get; init; } = [];

    [JsonPropertyName("party")] public string[] Party { get; init; } = [];

    [JsonPropertyName("partyCritical")] public string[] PartyCritical { get; init; } = [];

    [JsonPropertyName("miss")] public string[] Miss { get; init; } = [];
}

public sealed class CombatShakeOffset
{
    [JsonPropertyName("x")] public float X { get; init; }

    [JsonPropertyName("y")] public float Y { get; init; }
}

public sealed record CombatHitFeedback(
    HitResult Hit,
    string Style,
    bool Periodic,
    bool CasterControlled,
    bool TargetFriendly,
    bool TargetParty,
    bool InvolvesControlled);

public sealed record FeedbackPlan(bool Digits, string DigitStyle, string? Impact, bool Flash, bool Shake);

public sealed record FeedbackImpact(string Name, bool Attached);

public sealed record CombatFeedbackSelection(
    IReadOnlyList<FeedbackImpact> Impacts,
    string? DigitEffect);

public readonly record struct DamageDigitSpec(int Digit, float X, float Y, int DelayMs, int HoldFrames);

/// <summary>
/// 命中反馈的纯逻辑。
///
/// 这里只回答「该画什么」：数字用哪一组、冲击放哪一个、要不要震屏。
/// 真正画出来、放声音的那一层在场景里，随机源由调用方传进来——
/// 有一处次序必须守住：**先抽随机再判可见性**，否则关掉装饰会改变后续战斗的随机序列。
/// </summary>
public static class CombatFeedback
{
    public static FeedbackPlan Plan(CombatHitFeedback feedback)
    {
        var hit = feedback.Hit;
        var visible = feedback.InvolvesControlled || feedback.TargetParty;
        var critical = hit.Kind == HitKind.Critical;

        var digits = visible && (hit.Kind == HitKind.Miss || hit.Damage > 0);
        var digitStyle = hit.Kind == HitKind.Miss
            ? "miss"
            : feedback.TargetFriendly
                ? (critical ? "partyCritical" : "party")
                : (critical ? "enemyCritical" : "enemy");

        string? impact;
        if (hit.Kind == HitKind.Miss) impact = visible ? "miss" : null;
        else if (hit.Damage <= 0 || feedback.Periodic) impact = null;
        else if (critical) impact = feedback.InvolvesControlled ? "critical" : null;
        else impact = hit.Kind == HitKind.Hit ? "normal" : null;

        return new FeedbackPlan(
            Digits: digits,
            DigitStyle: digitStyle,
            Impact: impact,
            Flash: hit.Damage > 0,
            Shake: hit.Damage > 0 && critical && feedback.CasterControlled && !feedback.Periodic);
    }

    /// <summary>
    /// 选出这一下要放的素材。随机源由调用方给（战斗用可复现的种子）。
    ///
    /// 关键次序：暴击的爆闪**先抽一次**，即使 <paramref name="criticalDecorations"/>
    /// 关着也照抽——否则同一个种子在开关装饰后走向不同，战斗结果会跟着变。
    /// </summary>
    public static CombatFeedbackSelection Select(
        CombatHitFeedback feedback,
        CombatFeedbackDefinition definition,
        Func<double> random,
        bool criticalDecorations = true)
    {
        var plan = Plan(feedback);
        var impacts = new List<FeedbackImpact>();

        // 池里只有一个元素就取它，多个就按下标抽；空池同样抽一次随机数（下标恒为 0，
        // 什么也取不到），保留这次抽取，否则空池会让后续战斗的随机序列和别的选法错位。
        string? Choose(IReadOnlyList<string> values)
        {
            if (values.Count == 0)
            {
                random();
                return null;
            }

            if (values.Count == 1) return values[0];
            var index = Math.Min(values.Count - 1, (int)Math.Floor(random() * values.Count));
            return values[index];
        }

        if (plan.Impact == "miss" && definition.MissEffect is { } miss)
            impacts.Add(new FeedbackImpact(miss, true));

        if (plan.Impact == "normal")
        {
            var name = feedback.Style == "skill" ? definition.SkillImpact : Choose(definition.NormalImpacts);
            if (name is not null) impacts.Add(new FeedbackImpact(name, true));
        }

        if (plan.Impact == "critical")
        {
            var burst = Choose(definition.CriticalBursts);
            var name = feedback.Style == "skill" ? definition.SkillImpact : definition.CriticalImpact;
            if (name is not null) impacts.Add(new FeedbackImpact(name, true));

            if (criticalDecorations)
            {
                if (definition.CriticalRing is { } ring) impacts.Add(new FeedbackImpact(ring, true));
                if (burst is not null) impacts.Add(new FeedbackImpact(burst, false));
            }
        }

        var digitEffect = plan.Digits ? Choose(definition.DigitPool(plan.DigitStyle)) : null;
        return new CombatFeedbackSelection(impacts, digitEffect);
    }

    public static IReadOnlyList<DamageDigitSpec> DamageDigits(int damage, bool miss = false)
    {
        var digits = miss
            ? [10]
            : Math.Max(0, damage).ToString().Select(character => character - '0').ToArray();

        var specs = new List<DamageDigitSpec>(digits.Length);
        for (var index = 0; index < digits.Length; index += 1)
        {
            specs.Add(new DamageDigitSpec(
                digits[index],
                miss ? 0f : -digits.Length * 4f + index * 8f,
                miss ? 0f : (index % 2) * 2f,
                index * 60,
                digits.Length - 1 - index));
        }

        return specs;
    }

    public static IReadOnlyList<int> DigitSequence(int frames, int holdFrames)
    {
        var sequence = new List<int>(frames + Math.Max(0, holdFrames));
        for (var frame = 0; frame < frames; frame += 1)
        {
            var repeat = frame == 4 ? holdFrames + 1 : 1;
            for (var copy = 0; copy < repeat; copy += 1) sequence.Add(frame);
        }

        return sequence;
    }

    /// <summary>
    /// 数字池满了要复用哪一个：取进度最大的一项；还没有任何一项开始推进时返回 -1
    /// （尚未开始或仍在首帧的不能被替换）。
    /// </summary>
    public static int ReusableDigit(IReadOnlyList<int> progress)
    {
        var chosen = -1;
        var maximum = 0;
        for (var index = 0; index < progress.Count; index += 1)
        {
            if (progress[index] <= maximum) continue;
            chosen = index;
            maximum = progress[index];
        }

        return chosen;
    }
}
