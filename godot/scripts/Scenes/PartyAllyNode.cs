using System;
using Godot;
using Inotia4.Domain.Combat;
using Inotia4.Domain.World;

namespace Inotia4.Scenes;

public partial class PartyAllyNode : ActorNode
{
    public string MemberId { get; set; } = string.Empty;

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

    public void StepMotion(float speed)
    {
        var target = NextMotionTarget();
        if (target is null)
        {
            UpdateMotion(Vec2.Zero, false);
            return;
        }

        var from = new Vec2(Position.X, Position.Y);
        var delta = target.Value - from;
        var distance = delta.Length;
        if (distance <= 0.001f)
        {
            UpdateMotion(Vec2.Zero, false);
            return;
        }

        var direction = new Vec2(delta.X / distance, delta.Y / distance);
        var step = MathF.Min(distance, speed);
        Position = new Vector2(from.X + direction.X * step, from.Y + direction.Y * step);
        // 这里**不能**调 SetFacing：它会立刻切到 idle-<朝向>，而下一步 UpdateMotion 又要切回 walk，
        // 于是每个物理步「待机 → 行走」来回换一次，行走片段永远停在第 1 帧——表现就是走路时脚不动。
        // 朝向交给 UpdateMotion 统一决定（主角走的是同一条路，所以主角一直是正常的）。
        UpdateMotion(direction, true);
    }
}
