using Godot;

namespace Inotia4.Ui;

/// <summary>
/// 把一块 960×640 的设计画布钉在屏幕正中。
///
/// 项目的界面坐标都是按 960×640 写死的绝对值。桌面窗口就是 960×640，所以一直没出问题；
/// 但手机横向比 3:2 宽得多，<c>stretch/aspect="expand"</c> 之后视口会变宽（例如 1422×640），
/// 不居中的画布会整块贴在左边——右边留一片空。这里只调画布的锚点与偏移，
/// 画布内部那套绝对坐标一行都不用改。
/// </summary>
internal static class DesignStage
{
    public const float Width = 960f;

    public const float Height = 640f;

    public static void Center(Control canvas, float width = Width, float height = Height)
    {
        canvas.AnchorLeft = 0.5f;
        canvas.AnchorRight = 0.5f;
        canvas.AnchorTop = 0.5f;
        canvas.AnchorBottom = 0.5f;
        canvas.OffsetLeft = -width * 0.5f;
        canvas.OffsetRight = width * 0.5f;
        canvas.OffsetTop = -height * 0.5f;
        canvas.OffsetBottom = height * 0.5f;
    }
}
