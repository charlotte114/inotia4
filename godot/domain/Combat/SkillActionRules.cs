using System;
using System.Collections.Generic;
using System.Linq;

namespace Inotia4.Domain.Combat;

public static class SkillActionRules
{
    private static readonly string[] EnemyTargets = ["current", "enemy", "area", "target-area"];

    public static string SkillClipName(string skillId, string facing) => $"skill-{skillId}-{facing}";

    public static Dictionary<string, string> SkillClips(string skillId) => new()
    {
        ["north"] = SkillClipName(skillId, "north"),
        ["south"] = SkillClipName(skillId, "south"),
        ["east"] = SkillClipName(skillId, "east"),
        ["west"] = SkillClipName(skillId, "west"),
    };

    public static List<SkillEffectCue> ActionCues(SkillAction action)
    {
        var cues = new List<SkillEffectCue>();
        foreach (var skillEvent in action.Events)
            if (skillEvent is UnitsEvent units) cues.AddRange(units.Cues);
        return cues;
    }

    public static string[] SkillEffectNames(SkillAction action)
    {
        if (action.EffectNames is { } compiled) return compiled;

        var names = new List<string>();
        var seen = new HashSet<string>();

        void Add(string name)
        {
            if (seen.Add(name)) names.Add(name);
        }

        foreach (var skillEvent in action.Events)
        {
            switch (skillEvent)
            {
                case UnitsEvent units:
                    foreach (var cue in units.Cues)
                        foreach (var visual in cue.Effects) Add(visual.Effect);
                    foreach (var visual in units.Dispatch?.CasterEffects ?? []) Add(visual.Effect);
                    break;
                case ScreenEvent or CaptureTargetsEvent or FeedbackEvent:
                    break;
                case SummonEvent summon:
                    Add(summon.Effect);
                    foreach (var visual in summon.Dispatch?.CasterEffects ?? []) Add(visual.Effect);
                    break;
                case SpawnEvent spawn:
                    Add(spawn.Effect);
                    break;
                case CapturedEffectEvent captured:
                    Add(captured.Effect);
                    break;
                case CasterEvent caster:
                    foreach (var visual in caster.Effects) Add(visual.Effect);
                    break;
                case SwingEvent swing:
                    foreach (var visual in swing.Effects) Add(visual.Effect);
                    break;
                case CastEvent cast:
                    Add(cast.Effect.Effect);
                    break;
                case ImpactEvent impact:
                    Add(impact.Effect.Effect);
                    break;
                default:
                    throw new InvalidOperationException($"未处理的技能事件：{skillEvent.GetType().Name}");
            }
        }

        foreach (var visual in action.Transition?.MissEffects ?? []) Add(visual.Effect);

        foreach (var unit in action.CombatUnits ?? [])
        {
            if (unit is SummonCombatUnit summon)
                foreach (var visual in summon.AppearanceEffects ?? []) Add(visual.Effect);

            if (unit is not BuffCombatUnit buff) continue;
            foreach (var rank in buff.Ranks)
            {
                foreach (var name in BuffEffectNames(rank.Effect)) Add(name);
                foreach (var reaction in rank.Effect.Reactions ?? [])
                {
                    if (reaction is ManaOnPhysicalSuccessReaction mana) Add(mana.Effect);
                    else if (reaction is IBuffReactionWithBuff holder)
                        foreach (var name in BuffEffectNames(holder.Buff.Effect)) Add(name);
                }
            }
        }

        if (action.Transition?.CasterStrikes is { } strikes) Add(strikes.Effect);
        if (action.Transition?.ActionEffect is { } actionEffect) Add(actionEffect);
        if (action.Transition?.Effect is { } effect) Add(effect);

        return [.. names];
    }

    public static string[] SkillSoundNames(SkillAction action)
    {
        if (action.SoundNames is { } compiled) return compiled;

        var names = new List<string>();
        var seen = new HashSet<string>();

        void Add(string name)
        {
            if (seen.Add(name)) names.Add(name);
        }

        foreach (var skillEvent in action.Events)
        {
            switch (skillEvent)
            {
                case UnitsEvent units:
                    foreach (var cue in units.Cues)
                        if (cue.Sound is { } cueSound) Add(cueSound);
                    if (units.Dispatch?.Sound is { } dispatchSound) Add(dispatchSound);
                    break;
                case SummonEvent summon:
                    if (summon.Dispatch?.Sound is { } summonSound) Add(summonSound);
                    break;
                case FeedbackEvent feedback:
                    if (feedback.Sound is { } feedbackSound) Add(feedbackSound);
                    break;
            }
        }

        return [.. names];
    }

    public static string[] SkillSummonNames(SkillAction action)
    {
        if (action.SummonNames is { } compiled) return compiled;

        var names = new List<string>();
        var seen = new HashSet<string>();
        foreach (var skillEvent in action.Events)
        {
            if (skillEvent is not SummonEvent summon) continue;
            foreach (var creature in summon.Creatures)
                if (seen.Add(creature.EnemyId)) names.Add(creature.EnemyId);
        }

        return [.. names];
    }

    public static bool NeedsTarget(SkillAction action)
    {
        if (action.RequiresTarget is { } declared) return declared;

        foreach (var cue in ActionCues(action))
            if (Array.IndexOf(EnemyTargets, cue.Target) >= 0 && cue.TargetRangePx > 0) return true;
        return false;
    }

    /// <summary>
    /// 这一逻辑帧要执行的事件，按内容里的先后排列。
    ///
    /// 事件按帧号去重由战斗控制器负责：它从上一个已执行帧往后逐帧补发，
    /// 同一帧只通知一次，所以动画回调跳帧也不会把事件放两遍。
    /// </summary>
    public static List<SkillEvent> SkillEvents(SkillAction action, int frame)
    {
        if (action.EventFrames is { } frames)
        {
            var result = new List<SkillEvent>();
            if (!frames.TryGetValue(frame, out var indices)) return result;
            foreach (var index in indices)
                if (index >= 0 && index < action.Events.Count) result.Add(action.Events[index]);
            return result;
        }

        var filtered = new List<SkillEvent>();
        foreach (var skillEvent in action.Events)
        {
            var due = skillEvent is SpawnEvent spawn
                ? frame >= spawn.Frame && frame < spawn.Until
                : skillEvent.Frame == frame;
            if (due) filtered.Add(skillEvent);
        }

        return filtered;
    }

    public static int[] SkillSequence(SkillAction action, int level, int clipFrames)
    {
        var repeat = action.Repeat;
        var repeatedFrames = repeat is null ? 0 : repeat.RestartAfterFrame * RepeatCount(action, level);
        var total = repeatedFrames + clipFrames;
        var sequence = new int[total];
        for (var frame = 0; frame < total; frame += 1)
            sequence[frame] = frame < repeatedFrames ? frame % repeat!.RestartAfterFrame : frame - repeatedFrames;
        return sequence;
    }

    public sealed class SkillRepeatState
    {
        public int? StoppedAtFrame { get; set; }
    }

    public static void SettleSkillRepeat(SkillAction action, int frame, int level, bool targetAlive,
        SkillRepeatState state)
    {
        var repeat = action.Repeat;
        if (repeat?.RequiresLivingTarget != true || state.StoppedAtFrame is not null || targetAlive) return;
        if (frame > 0 && frame % repeat.RestartAfterFrame == 0 &&
            frame <= repeat.RestartAfterFrame * RepeatCount(action, level))
            state.StoppedAtFrame = frame;
    }

    public static int SkillRepeatSkippedFrames(SkillAction action, int level, SkillRepeatState? state)
    {
        if (state?.StoppedAtFrame is not { } stoppedAt || action.Repeat is not { } repeat) return 0;
        return repeat.RestartAfterFrame * (RepeatCount(action, level) + 1) - stoppedAt;
    }

    public static List<SkillEvent> SkillPlaybackEvents(SkillAction action, int frame, int level = 1,
        SkillRepeatState? state = null)
    {
        var repeat = action.Repeat;
        if (repeat is null) return SkillEvents(action, frame);

        if (state?.StoppedAtFrame is { } stoppedAt)
        {
            var localFrame = frame - stoppedAt + repeat.RestartAfterFrame;
            var stopped = new List<SkillEvent>();
            foreach (var skillEvent in action.Events)
            {
                var due = skillEvent switch
                {
                    UnitsEvent => frame == stoppedAt,
                    SpawnEvent spawn => localFrame >= spawn.Frame && localFrame < spawn.Until,
                    _ => skillEvent.Frame == localFrame,
                };
                if (due) stopped.Add(skillEvent);
            }

            return stopped;
        }

        var count = RepeatCount(action, level);
        var events = new List<SkillEvent>();
        foreach (var skillEvent in action.Events)
        {
            if (repeat.RequiresLivingTarget == true && skillEvent.Frame == 0 &&
                frame == count * repeat.RestartAfterFrame)
            {
                events.Add(skillEvent);
                continue;
            }

            if (skillEvent.Frame > repeat.RestartAfterFrame)
            {
                if (frame == count * repeat.RestartAfterFrame + skillEvent.Frame) events.Add(skillEvent);
                continue;
            }

            var elapsed = frame - skillEvent.Frame;
            if (elapsed >= 0 && elapsed % repeat.RestartAfterFrame == 0 &&
                elapsed / repeat.RestartAfterFrame < count)
                events.Add(skillEvent);
        }

        return repeat.RequiresLivingTarget == true
            ? events.OrderBy(item => item is UnitsEvent ? 0 : 1).ToList()
            : events;
    }

    public static int RepeatCount(SkillAction action, int level)
    {
        var repeat = action.Repeat ?? throw new InvalidOperationException("技能没有连续施放配置");
        var index = level - 1;
        if (index < 0 || index >= repeat.CountByLevel.Count)
            throw new InvalidOperationException($"技能重播缺少等级 {level} 的次数");
        return repeat.CountByLevel[index];
    }

    public static List<string> BuffEffectNames(BattleBuffEffect effect)
    {
        var names = new List<string>();
        foreach (var loop in effect.LoopEffects ?? []) names.Add(loop.Effect);
        if (effect.ApplicationEffect is { } application) names.Add(application);
        foreach (var tick in effect.Periodic)
        {
            if (tick is VisualTick visual) names.Add(visual.Effect);
            else if (tick is MagicCurseTick { HealEffect: { } heal }) names.Add(heal);
        }

        foreach (var reaction in effect.Reactions ?? [])
            if (reaction is IBuffReactionWithBuff holder) names.AddRange(BuffEffectNames(holder.Buff.Effect));

        return names;
    }
}

/// <summary>
/// 构建期把技能动作预编译成运行时结构。
///
/// 不改变 events／combatUnits／transition 的结构，只把运行时每次都要算的东西预先写进
/// 派生字段；源 JSON 没有这些字段时，<see cref="SkillActionRules"/> 仍走原计算。
///
/// 内容目录只建一次，所以就地写入那几个派生字段
/// （重复调用是幂等的，值不会变）。
/// </summary>
public static class SkillActionCompiler
{
    public static SkillAction Compile(SkillAction action)
    {
        action.RequiresTarget ??= SkillActionRules.NeedsTarget(action);
        action.EffectNames = SkillActionRules.SkillEffectNames(action);
        action.SoundNames = SkillActionRules.SkillSoundNames(action);
        action.SummonNames = SkillActionRules.SkillSummonNames(action);
        if (action.Repeat is null) action.EventFrames = EventFrames(action.Events);
        return action;
    }

    public static Dictionary<int, int[]> EventFrames(IReadOnlyList<SkillEvent> events)
    {
        var frames = new SortedDictionary<int, List<int>>();
        for (var index = 0; index < events.Count; index += 1)
        {
            var skillEvent = events[index];
            if (skillEvent is SpawnEvent spawn)
            {
                for (var frame = spawn.Frame; frame < spawn.Until; frame += 1) Append(frames, frame, index);
                continue;
            }

            Append(frames, skillEvent.Frame, index);
        }

        var result = new Dictionary<int, int[]>(frames.Count);
        foreach (var (frame, indices) in frames) result[frame] = [.. indices];
        return result;
    }

    private static void Append(SortedDictionary<int, List<int>> frames, int frame, int index)
    {
        if (!frames.TryGetValue(frame, out var list))
        {
            list = [];
            frames[frame] = list;
        }

        list.Add(index);
    }
}
