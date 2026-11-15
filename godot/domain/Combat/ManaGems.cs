using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Inotia4.Domain.Content;
using Inotia4.Domain.World;

namespace Inotia4.Domain.Combat;

public static class ManaGems
{
    public static RecoveryResult? RecoverManaPickup(ManaPickupDefinition pickup,
        IReadOnlyList<ManaMember> members, string collectorId)
    {
        ManaMember? collector = null;
        foreach (var candidate in members)
            if (candidate.Id == collectorId) collector = candidate;
        // 结算失败只表示这枚宝石暂时收不了；不能从帧回调和剧情准备里抛异常。
        if (collector is null || collector.Health <= 0) return null;

        var amount = (int)Math.Floor(collector.MaxMana * pickup.PercentOfCollectorMaxMana / 100.0);
        var restored = new List<string>();
        var effects = new List<string>();
        foreach (var member in members)
        {
            if (member.Health <= 0) continue;
            if (member.Mana < member.MaxMana && amount > 0)
            {
                member.SetMana(Math.Min(member.MaxMana, member.Mana + amount));
                restored.Add(member.Id);
            }

            if (restored.Count > 0) effects.Add(member.Id);
        }

        if (effects.Count == 0) effects.Add(collectorId);
        return new RecoveryResult(amount, restored, effects);
    }

    /// <summary>
    /// 一次伤害接触掷一次掉落：先按阈值判出不出，出了再从四个方向里挑一个落点，
    /// 落点不可以站就顺着方向序号往后试；四向都站不住时仍然返回最后一个落点——
    /// 宝石照样落地，只是捡不到。
    /// </summary>
    public static ManaLanding? RollManaGem(ManaPickupDefinition pickup, int damage, string rule,
        string? weaponKind, Vec2 target, Func<Vec2, bool> reachable, Func<double> random)
    {
        if (damage <= 0) return null;
        var drop = pickup.HitDrop;
        var threshold = rule == "skill"
            ? drop.SkillThreshold
            : drop.WeaponThresholds.GetValueOrDefault(weaponKind ?? "", 0);
        if (threshold == 0) return null;

        int Roll(int maximum)
        {
            var value = Math.Max(0, Math.Min(1 - 2.220446049250313e-16, random()));
            return (int)Math.Floor(value * maximum);
        }

        if (1 + Roll(drop.RollMaximum) >= threshold) return null;

        var first = Roll(drop.Directions.Count);
        ManaLanding? landing = null;
        for (var attempt = 0; attempt < drop.Directions.Count; attempt++)
        {
            var direction = (first + attempt) % drop.Directions.Count;
            var offset = drop.Directions[direction];
            landing = new ManaLanding(
                target.X + offset.X + Roll(drop.JitterPx * 2 + 1) - drop.JitterPx,
                target.Y + offset.Y + Roll(drop.JitterPx * 2 + 1) - drop.JitterPx,
                direction);
            if (reachable(new Vec2(landing.X, landing.Y))) return landing;
        }

        return landing;
    }

    public sealed record RecoveryResult(int Amount, IReadOnlyList<string> Restored, IReadOnlyList<string> Effects);
}

public interface ManaMember
{
    string Id { get; }

    int Health { get; }

    int Mana { get; }

    int MaxMana { get; }

    void SetMana(int value);
}

public sealed record ManaLanding(float X, float Y, int Direction);

public sealed class ManaPickupDefinition
{
    [JsonPropertyName("kind")] public string Kind { get; init; } = "party-mana";

    [JsonPropertyName("percentOfCollectorMaxMana")]
    public int PercentOfCollectorMaxMana { get; init; }

    [JsonPropertyName("hitDrop")] public ManaHitDropDefinition HitDrop { get; init; } = new();
}

public sealed class ManaHitDropDefinition
{
    [JsonPropertyName("rollMaximum")] public int RollMaximum { get; init; }

    [JsonPropertyName("skillThreshold")] public int SkillThreshold { get; init; }

    [JsonPropertyName("weaponThresholds")]
    public Dictionary<string, int> WeaponThresholds { get; init; } = [];

    [JsonPropertyName("jitterPx")] public int JitterPx { get; init; }

    [JsonPropertyName("directions")] public List<ManaDropDirection> Directions { get; init; } = [];
}

public sealed class ManaDropDirection
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";

    [JsonPropertyName("x")] public int X { get; init; }

    [JsonPropertyName("y")] public int Y { get; init; }

    [JsonPropertyName("clip")] public EffectClip Clip { get; init; } = new();
}

public static class ManaPickupReader
{
    public static ManaPickupDefinition? AsManaPickup(this JsonElement? pickup) =>
        pickup is { } value ? value.Deserialize<ManaPickupDefinition>(SkillActionJson.Options) : null;
}
