namespace Inotia4.Domain.Combat;

/// <summary>
/// 技能音效的取舍。
///
/// 规则只有一条，但不写就会**双声**：技能动作里如果已经有自带音效的事件，那一下由事件
/// 自己出声，不能再叠一次技能音效。两类算「自带」：
/// <list type="bullet">
/// <item><c>feedback</c> 且带 <c>sound</c>——时间轴直接指定的音效；</item>
/// <item><c>units</c>／<c>summon</c> 且带 <c>dispatch</c>——派发动作自己会出声，
/// **不看 dispatch 里有没有写 sound**。</item>
/// </list>
///
/// 原判定是一串否定条件串起来的，这里拆成肯定句，读起来不容易反。
/// </summary>
public static class SkillSoundRules
{
    public static bool PlaysOwnSound(SkillAction action)
    {
        foreach (var skillEvent in action.Events)
        {
            switch (skillEvent)
            {
                case FeedbackEvent { Sound: { Length: > 0 } }:
                case UnitsEvent { Dispatch: not null }:
                case SummonEvent { Dispatch: not null }:
                    return true;
            }
        }

        return false;
    }
}
