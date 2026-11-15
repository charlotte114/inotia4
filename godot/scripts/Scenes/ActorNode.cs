using System;
using Godot;
using Inotia4.Application;
using Inotia4.Domain.Combat;
using Inotia4.Domain.Content;
using Inotia4.Domain.World;
using Inotia4.Rendering;

namespace Inotia4.Scenes;

public partial class ActorNode : Node2D
{
    public ActorDefinition Definition { get; private set; } = new();

    public CharacterVisual Visual { get; private set; } = null!;

    public string Facing { get; private set; } = "south";

    private TextureLease? _ownedLease;

    private TextureLease? _lease;

    private bool _placementFlip;

    public void Setup(
        ActorDefinition definition, TextureCache textures, string facing = "south", bool placementFlip = false)
    {
        _ownedLease = textures.Lease();
        Build(definition, _ownedLease, facing, placementFlip);
    }

    /// <summary>
    /// 借用外部租约：适合会被 reparent 的节点（比如主角）。节点离开场景树时不要动租约，
    /// 否则重新挂回去会拿着已经释放的贴图。
    /// </summary>
    public void Setup(
        ActorDefinition definition, TextureLease lease, string facing = "south", bool placementFlip = false) =>
        Build(definition, lease, facing, placementFlip);

    private void Build(ActorDefinition definition, TextureLease lease, string facing, bool placementFlip)
    {
        Definition = definition;
        Facing = facing;
        _lease = lease;
        _placementFlip = placementFlip;
        Visual = new CharacterVisual();
        Visual.Build(definition, lease, "idle-" + facing, placementFlip);
        AddChild(Visual);
    }

    public void SetAppearance(ActorDefinition definition)
    {
        if (_lease is null) return;

        Definition = definition;
        var previous = Visual;
        Visual = new CharacterVisual();
        Visual.Build(definition, _lease, "idle-" + Facing, _placementFlip);
        AddChild(Visual);
        previous?.QueueFree();
    }

    public override void _ExitTree()
    {
        _ownedLease?.Dispose();
        _ownedLease = null;
    }

    public void SetFacing(string facing)
    {
        Facing = facing;
        var wanted = "idle-" + facing;
        if (Definition.HasClip(wanted) && Visual.Animation != wanted) Visual.Play(wanted);
    }

    /// <summary>挥击动作播放期间不接受移动动画覆盖，否则出招会被走路瞬间打断。</summary>
    private ulong _busyUntilMs;

    /// <summary>
    /// 开始一次按逻辑帧驱动的普攻。原版取帧方式（<c>CHAR_Draw</c>）。
    ///
    /// 引擎自己的动画播放器按素材帧率走，与战斗的逻辑帧（60 毫秒）不同步；
    /// 出手帧、命中帧都按逻辑帧号判定，所以动作期间要接过取帧权：先暂停自动播放，
    /// 之后每一逻辑帧由 <see cref="DrawAttackFrame"/> 指定画哪一张素材。
    /// </summary>
    public bool BeginAttack(ResolvedBasicAttack attack, string facing)
    {
        if (!attack.Clips.TryGetValue(facing, out var clip) || !Definition.HasClip(clip)) return false;

        Facing = facing;
        Visual.Play(clip);
        Visual.Pause();
        Visual.Frame = 0;
        _busyUntilMs = Time.GetTicksMsec() + (ulong)Math.Max(120, attack.DurationMs);
        return true;
    }

    public void DrawAttackFrame(ResolvedBasicAttack attack, string facing, int index)
    {
        if (!attack.Clips.TryGetValue(facing, out var clip)) return;
        if (!attack.Sequence.TryGetValue(facing, out var sequence) || sequence.Length == 0) return;
        if (!Definition.Clips.TryGetValue(clip, out var source)) return;

        var idleClip = "idle-" + facing;
        var idleFrames = Definition.Clips.TryGetValue(idleClip, out var idle) ? idle.Frames : 0;
        var pose = BasicAttackRules.IdlePoseFrame(index, source.Frames, idleFrames);

        var wanted = pose is null ? clip : idleClip;
        if (Visual.Animation != wanted) Visual.SetAnimation(wanted);
        Visual.Frame = pose ?? sequence[Math.Min(index, sequence.Length - 1)];
    }

    public void EndAttack()
    {
        _busyUntilMs = 0;

        var wanted = "idle-" + Facing;
        if (!Definition.HasClip(wanted)) return;

        Visual.Play(wanted);
        Visual.Frame = 0;
    }

    public void UpdateMotion(Vec2 axis, bool moving)
    {
        if (Time.GetTicksMsec() < _busyUntilMs) return;   // 出招中，把镜头让给攻击动作

        if (moving) Facing = FacingFor(axis);

        var wanted = (moving ? "walk-" : "idle-") + Facing;
        if (!Definition.HasClip(wanted)) wanted = "idle-" + Facing;   // 没有行走片段就退回待机
        if (!Definition.HasClip(wanted)) return;

        if (Visual.Animation != wanted) Visual.Play(wanted);
    }

    private static string FacingFor(Vec2 axis)
    {
        if (MathF.Abs(axis.Y) >= MathF.Abs(axis.X)) return axis.Y < 0f ? "north" : "south";
        return axis.X < 0f ? "west" : "east";
    }
}
