using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Inotia4.Domain.Content;
using Inotia4.Domain.World;

namespace Inotia4.Domain.Combat;

public sealed record BasicAttackStrike
{
    [JsonPropertyName("frame")]
    public required int Frame { get; init; }

    [JsonPropertyName("hand")]
    public required string Hand { get; init; }
}

public sealed class BasicAttackBinding
{
    [JsonPropertyName("profileId")]
    public required string ProfileId { get; init; }

    /// <summary>朝向 → 片段名，四向必须齐全。</summary>
    [JsonPropertyName("clips")]
    public Dictionary<string, string> Clips { get; init; } = [];
}

public sealed class ResolvedBasicAttack : IApproachProfile
{
    public required BasicAttackDefinition Definition { get; init; }

    public required int EffectiveCycleFrames { get; init; }

    public required int DurationMs { get; init; }

    public required IReadOnlyDictionary<string, string> Clips { get; init; }

    public required IReadOnlyDictionary<string, int[]> Sequence { get; init; }

    public int FrameDurationMs => Definition.FrameDurationMs;

    public int ApproachDistanceUnits => Definition.ApproachDistanceUnits;

    public float SearchRadiusPx => Definition.SearchRadiusPx;

    public int StrikeFrame => Definition.StrikeFrame;

    public int? SwingFrame => Definition.SwingFrame;

    public IReadOnlyList<BasicAttackStrike>? Strikes => Definition.Strikes;

    /// <summary>固定动画按完整素材帧播放；否则由当前攻速决定周期。</summary>
    public bool FixedAnimation => Definition.Timing == "fixed-animation";

    public IReadOnlyList<string> StrikesAt(int index) => BasicAttackRules.StrikesAtFrame(this, index);
}

public static class BasicAttackRules
{
    public static readonly string[] Facings = ["north", "south", "east", "west"];

    /// <summary>
    /// 把源动画展开成逻辑帧序列：条目是素材帧号，下标是逻辑帧序号。
    /// 周期比素材短就截取，比素材长就重复引用末帧。序列要覆盖整个周期，
    /// 因为出手、命中这类事件按逻辑帧号触发，可能落在素材之外（弓在第 10 帧发箭，素材正好 10 帧）。
    /// </summary>
    public static int[] AttackSequence(int sourceFrameCount, int cycleFrames)
    {
        if (sourceFrameCount <= 0) throw new ArgumentException($"普攻源动画帧数无效：{sourceFrameCount}");
        if (cycleFrames <= 0) throw new ArgumentException($"普攻周期帧数无效：{cycleFrames}");

        var sequence = new int[cycleFrames];
        for (var index = 0; index < cycleFrames; index += 1)
            sequence[index] = Math.Min(index, sourceFrameCount - 1);
        return sequence;
    }

    public static int? IdlePoseFrame(int index, int sourceFrameCount, int idleFrameCount)
    {
        if (idleFrameCount <= 0) return null;
        return index < sourceFrameCount ? null : index % idleFrameCount;
    }

    public static (bool Swing, bool Strike) Events(ResolvedBasicAttack attack, int index)
    {
        if (attack is null) throw new ArgumentNullException(nameof(attack));
        return (attack.SwingFrame == index, StrikesAtFrame(attack, index).Count > 0);
    }

    public static IReadOnlyList<string> StrikesAtFrame(ResolvedBasicAttack attack, int index)
    {
        if (attack is null) throw new ArgumentNullException(nameof(attack));

        if (attack.Strikes is { Count: > 0 } strikes)
            return strikes.Where(strike => strike.Frame == index).Select(strike => strike.Hand).ToArray();

        return attack.StrikeFrame == index ? ["main-hand"] : [];
    }

    public static ResolvedBasicAttack Resolve(
        IReadOnlyDictionary<string, BasicAttackDefinition> catalog,
        ActorDefinition actor,
        BasicAttackBinding? binding,
        int? cycleFrames = null)
    {
        if (binding is null) throw new ArgumentException($"角色没有普攻：{actor.Id}");
        if (!catalog.TryGetValue(binding.ProfileId, out var definition))
            throw new ArgumentException($"普攻规则缺失：{binding.ProfileId}");

        var configured = definition.Timing == "fixed-animation" ? definition.CycleFrames : cycleFrames ?? definition.CycleFrames;
        var effectiveCycleFrames = Math.Max(configured, definition.StrikeFrame + 1);

        var clips = new Dictionary<string, string>();
        var sequence = new Dictionary<string, int[]>();
        foreach (var facing in Facings)
        {
            if (!binding.Clips.TryGetValue(facing, out var name) || string.IsNullOrEmpty(name))
                throw new ArgumentException($"普攻动画绑定缺失：{facing}");
            if (!actor.Clips.TryGetValue(name, out var clip))
                throw new ArgumentException($"普攻动画缺失：{name}");
            if (clip.Frames <= 0)
                throw new ArgumentException($"普攻源动画帧数无效：{name}");
            if (definition.Timing == "fixed-animation" && clip.Frames != effectiveCycleFrames)
                throw new ArgumentException($"固定普攻周期与素材帧数不匹配：{name}");

            clips[facing] = name;
            sequence[facing] = AttackSequence(clip.Frames, effectiveCycleFrames);
        }

        return new ResolvedBasicAttack
        {
            Definition = definition,
            EffectiveCycleFrames = effectiveCycleFrames,
            DurationMs = effectiveCycleFrames * definition.FrameDurationMs,
            Clips = clips,
            Sequence = sequence,
        };
    }

    public static int TargetDistance(Vec2 a, Vec2 b)
    {
        var x = Math.Abs((int)a.X - (int)b.X);
        var y = Math.Abs((int)a.Y - (int)b.Y);
        var small = Math.Min(x, y);
        return x + y - small / 4 - small / 2 + small / 8;
    }

    public static float TargetScore(Vec2 from, Vec2 to, string facing, float radius)
    {
        var distance = TargetDistance(from, to);
        if (distance > radius) return 0f;

        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var forward = facing == "south" ? dy : facing == "north" ? -dy : facing == "east" ? dx : -dx;
        var side = facing is "south" or "north" ? dx : dy;
        return radius - distance + (forward > 0 ? MathF.Abs(forward) > MathF.Abs(side) ? 8f : 4f : 0f);
    }

    public static int AttackDistance(Vec2 a, CollisionBox box, Vec2 b, CollisionBox target)
    {
        var ax = (int)a.X;
        var ay = (int)a.Y;
        var bx = (int)b.X;
        var by = (int)b.Y;

        var dx = Math.Max(0f, Math.Max(
            ax + box.X - bx - target.X - target.Width,
            bx + target.X - ax - box.X - box.Width));
        var dy = Math.Max(0f, Math.Max(
            ay + box.Y - by - target.Y - target.Height,
            by + target.Y - ay - box.Y - box.Height));

        return (int)MathF.Floor(dx / 8f) + (int)MathF.Floor(dy / 8f) + 2;
    }

    /// <summary>朝向目标：横向差更大就朝东西向，否则朝南北向。</summary>
    public static string FaceTarget(Vec2 from, Vec2 to)
    {
        var x = to.X - from.X;
        var y = to.Y - from.Y;
        return MathF.Abs(x) > MathF.Abs(y) ? x < 0f ? "west" : "east" : y < 0f ? "north" : "south";
    }

    public static int ProjectileHeading(Vec2 from, Vec2 to)
    {
        var x = to.X - from.X;
        var y = to.Y - from.Y;
        var slope = MathF.Abs(x) > 0f ? MathF.Abs((float)(int)(y * 100f / x)) : float.PositiveInfinity;
        var angle = slope < 21f ? 0 : slope < 68f ? 23 : slope < 151f ? 45 : slope < 504f ? 68 : 90;
        return x >= 0f ? y >= 0f ? angle : (360 - angle) % 360 : y >= 0f ? 180 - angle : 180 + angle;
    }

    public static (Vec2 Position, bool Arrived) ProjectileStep(Vec2 start, Vec2 target, int step)
    {
        if (step <= 0) throw new ArgumentException("投射物步长必须大于零");

        var x = (int)start.X;
        var y = (int)start.Y;
        var end = new Vec2((int)target.X, (int)target.Y);
        if (TargetDistance(new Vec2(x, y), end) < step) return (end, true);

        var dx = (int)end.X - x;
        var dy = (int)end.Y - y;
        var mx = 0;
        var my = 0;

        if (dx == 0)
        {
            my = Math.Sign(dy) * step;
        }
        else if (dy == 0)
        {
            mx = Math.Sign(dx) * step;
        }
        else if (Math.Abs(dx) > Math.Abs(dy))
        {
            mx = (int)((float)dx * dx * step * Math.Sign(dx) / (dx * dx + dy * dy));
            my = dy + (int)((-dx + mx) * (float)(int)(dy * 100f / dx) / 100f);
        }
        else
        {
            my = (int)((float)dy * dy * step * Math.Sign(dy) / (dx * dx + dy * dy));
            mx = dx + (int)((-dy + my) * (float)(int)(dx * 100f / dy) / 100f);
        }

        return (new Vec2(x + mx, y + my), false);
    }
}
