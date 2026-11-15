using Godot;

namespace Inotia4.Scenes;

/// <summary>
/// 直接落位：传送、出生、剧情摆位。
///
/// 工程开了 <c>physics/common/physics_interpolation</c>，画面不再直接用节点的当前位置，
/// 而是在「上一个物理步」与「这一个物理步」之间按剩余时间插值。逐帧移动正是要这个，
/// 但**瞬移**会被插成「从旧位置一路滑过来」——所以落位之后要清掉插帧历史。
///
/// 逐帧移动（<c>WorldScene.Move</c>、敌人每一步走位、飞行物）不要走这里：
/// 每帧清一次插帧历史，等于把插帧关掉。
/// </summary>
internal static class NodePlacement
{
    public static void TeleportTo(this Node2D node, Vector2 position)
    {
        node.Position = position;
        node.ResetPhysicsInterpolation();
    }
}
