namespace Inotia4.Domain.Combat;

/// <summary>
/// 一组状态键能做什么、不能做什么。
/// </summary>
public readonly record struct CombatStateCapabilities(
    bool CanMove,
    bool CanAct,
    bool CanTarget,
    bool CanReceiveDamage,
    bool CanAutoApproach);

/// <summary>
/// 状态能力求值。
///
/// 状态系统本身还没迁：世界里没有任何东西会往 <see cref="Combatant.States"/> 里写键，
/// 所以实际运行中这里恒为「什么都能做」。公式先写好，等状态系统接上即可生效。
/// </summary>
public static class CombatStateRules
{
    public static CombatStateCapabilities Capabilities(IEnumerable<string>? states, bool hostile = true)
    {
        var active = states is null ? new HashSet<string>() : new HashSet<string>(states);

        var canAct = !active.Contains("sleep") && !active.Contains("stun");
        var canMove = canAct && !active.Contains("immobilized");

        return new CombatStateCapabilities(
            CanMove: canMove,
            CanAct: canAct,
            CanTarget: !hostile || !active.Contains("stealthed"),
            CanReceiveDamage: !active.Contains("damage-immune"),
            CanAutoApproach: canMove && !active.Contains("ambush"));
    }
}
