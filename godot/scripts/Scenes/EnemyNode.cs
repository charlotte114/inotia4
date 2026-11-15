using System;
using Godot;
using Inotia4.Application;
using Inotia4.Domain.Combat;
using Inotia4.Domain.Content;
using Inotia4.Domain.World;

namespace Inotia4.Scenes;

public partial class EnemyNode : ActorNode
{

    /// <summary>战斗数值来源。不叫 Definition 是因为基类已经用它表示外观定义。</summary>
    public EnemyDefinition EnemyInfo { get; private set; } = new();

    public Fighter Fighter { get; private set; } = new();

    public int Health { get; private set; }

    public bool Dead => Health <= 0;

    public void SetupEnemy(EnemyDefinition definition, TextureCache textures, Fighter? fighter = null)
    {
        EnemyInfo = definition;
        Fighter = fighter ?? definition.ToFighter();
        Health = Fighter.MaxHealth;
        Setup(definition.Actor, textures);
    }

    /// <summary>
    /// 把召唤物按主人等级派生的属性盖到战斗数值上（<c>CHAR_GetAttrFromSummonMonster</c> 的落点）：
    /// 未列出的项沿用召唤物自己的数据。
    /// </summary>
    public void ApplySummonStats(SummonStats stats)
    {
        var fighter = Fighter;
        Fighter = new Fighter
        {
            Level = fighter.Level,
            PhysicalAttack = stats.PhysicalAttack ?? fighter.PhysicalAttack,
            MagicAttack = stats.MagicAttack ?? fighter.MagicAttack,
            Defense = fighter.Defense,
            DefenseRate = fighter.DefenseRate,
            PhysicalResistanceBonus = stats.PhysicalResistanceBonus,
            MagicResistance = stats.MagicResistance,
            MaxHealth = stats.MaxHealth,
            HitBase = fighter.HitBase,
            HitBonus = fighter.HitBonus,
            EvasionBase = fighter.EvasionBase,
            EvasionBonus = fighter.EvasionBonus,
            CriticalRate = fighter.CriticalRate,
            CriticalResistance = fighter.CriticalResistance,
            CriticalDamage = fighter.CriticalDamage,
            CriticalDamageResistance = fighter.CriticalDamageResistance,
            WeaponBlock = fighter.WeaponBlock,
            ShieldBlock = fighter.ShieldBlock,
        }.Resolved();
        Health = Fighter.MaxHealth;
        SummonAttributes = stats;
    }

    public SummonStats? SummonAttributes { get; private set; }

    public string? SummonedBy { get; set; }

    public int? SummonRemainingMs { get; set; }

    public string? SpawnKey { get; set; }

    public string CombatId => SpawnKey ?? Name;

    public ResolvedBasicAttack? Attack { get; private set; }

    public CombatController? Combat => Ai?.CombatController ?? SummonAi?.CombatController;

    public EnemyController? Ai { get; private set; }

    /// <summary>
    /// 玩家侧召唤物的受命 AI（两种控制器的类型不同，所以分两个属性存放）。
    /// </summary>
    public FriendlySummonController? SummonAi { get; private set; }

    public Vec2 Home { get; set; }

    public void AttachAi(EnemyBehavior behavior, ResolvedBasicAttack? attack, IEnemyPort port)
    {
        Attack = attack;
        Ai = new EnemyController(CombatId, behavior, attack, port, skills: EnemyInfo.Skills);
    }

    public void AttachSummonAi(EnemyBehavior behavior, ResolvedBasicAttack? attack, IFriendlySummonPort port,
        string ownerId, string role)
    {
        Attack = attack;
        SummonAi = new FriendlySummonController(CombatId, ownerId, role, behavior, attack, port);
    }

    private Vec2? _moveTarget;
    private Func<Vec2?>? _waypoints;

    public void FollowPath(Func<Vec2?> nextWaypoint)
    {
        _waypoints = nextWaypoint;
        _moveTarget = null;
    }

    public void MoveToward(Vec2 target)
    {
        _moveTarget = target;
        _waypoints = null;
    }

    public void HaltMotion()
    {
        _moveTarget = null;
        _waypoints = null;
    }

    public Vec2? NextMotionTarget() => _waypoints is not null ? _waypoints() : _moveTarget;

    /// <summary>
    /// 物理步更新动画。不能转向的敌人（炮台一类）不改朝向，
    /// 所以这里走完之后把朝向还原成走之前的那一面。
    /// </summary>
    public void StepMotion(Vec2 axis, bool moving, bool canTurn)
    {
        var facing = Facing;
        UpdateMotion(axis, moving);
        if (canTurn || Facing == facing) return;

        SetFacing(facing);
        var wanted = (moving ? "walk-" : "idle-") + facing;
        if (moving && Definition.HasClip(wanted) && Visual.Animation != wanted) Visual.Play(wanted);
    }

    public void RestoreHealth(int health)
    {
        Health = Math.Clamp(health, 0, Fighter.MaxHealth);
        QueueRedraw();
    }

    public int ApplyDamage(int damage)
    {
        if (Dead) return 0;

        var applied = Math.Min(damage, Health);
        Health -= applied;
        return applied;
    }
}
