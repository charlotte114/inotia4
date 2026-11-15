namespace Inotia4.Domain.Combat;

/// <summary>
/// 普攻连打的输入门控。
///
/// 攻击键只在角色站立时发起一次默认攻击，而触摸层按住会持续重复按键事件，
/// 所以出手一结束、人还站着就接下一击。这里按同一门控做成「开始攻击之后站定自动连打」：
/// 摇杆一动会清当前目标，连打随之停止，要再按一次攻击键才会重新开始。
/// </summary>
public sealed class BasicAttackRepeat
{
    private bool _engaged;

    public bool Active => _engaged;

    public void Engage() => _engaged = true;

    public void Release() => _engaged = false;

    public bool Next(string? targetId, CombatState attackState)
    {
        if (!_engaged) return false;
        if (string.IsNullOrEmpty(targetId))
        {
            Release();
            return false;
        }

        return attackState == CombatState.Idle;
    }
}
