using Inotia4.Domain.Content;

namespace Inotia4.Domain.World;

/// <summary>
/// 地形通行查询。只回答「这块矩形能不能站」，不认识画面也不认识引擎。
///
/// 判定方式很直接：
/// 把矩形换算成覆盖到的格子逐个检查，界外一律算挡住。
/// 浮点误差用 <see cref="Epsilon"/> 抹掉，否则贴着格子右边界时会被判到下一格。
/// </summary>
public sealed class CollisionGrid : IStandability
{
    private const double Epsilon = 1e-7;

    private readonly bool[] _blocked;

    private readonly bool[]? _water;

    public CollisionGrid(MapDefinition map)
    {
        TileSize = map.TileSize;
        Columns = map.Columns;
        Rows = map.Rows;
        _blocked = map.Blocked;

        if (map.Water is { Length: > 0 } cells)
        {
            _water = new bool[Columns * Rows];
            foreach (var cell in cells)
                if (cell >= 0 && cell < _water.Length)
                    _water[cell] = true;
        }
    }

    public int TileSize { get; }

    public int Columns { get; }

    public int Rows { get; }

    public Rect Bounds => new(0f, 0f, Columns * TileSize, Rows * TileSize);

    public bool CanStand(Vec2 point) => CanStandOnTerrain(point.X, point.Y);

    public bool CanStandInRect(Rect rect)
    {
        if (TileSize <= 0 || Columns <= 0 || Rows <= 0) return false;

        var left = (int)Math.Floor((rect.X + Epsilon) / TileSize);
        var right = (int)Math.Floor(((double)rect.X + rect.Width - Epsilon) / TileSize);
        var top = (int)Math.Floor((rect.Y + Epsilon) / TileSize);
        var bottom = (int)Math.Floor(((double)rect.Y + rect.Height - Epsilon) / TileSize);

        for (var row = top; row <= bottom; row++)
        {
            for (var col = left; col <= right; col++)
            {
                if (col < 0 || row < 0 || col >= Columns || row >= Rows) return false;
                var index = row * Columns + col;
                if (index >= _blocked.Length || _blocked[index]) return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 以脚点为心、边长为半径两倍的方形足底判定。
    /// 走路用窄足底（半径 4 像素），比整个碰撞盒宽松，所以能贴着墙走。
    /// </summary>
    public bool CanStandOnTerrain(float x, float y, float radius = 4f) =>
        CanStandInRect(Rect.AroundPoint(x, y, radius));

    public bool IsBlocked(int col, int row)
    {
        if (col < 0 || row < 0 || col >= Columns || row >= Rows) return true;
        var index = row * Columns + col;
        return index >= _blocked.Length || _blocked[index];
    }

    /// <summary>
    /// 脚点所在格是不是水：越界与没有水格的图都算岸上。
    /// 只影响移速，不影响通行——水格能不能站由 <c>blocked</c> 决定。
    /// </summary>
    public bool IsWater(Vec2 point)
    {
        if (_water is null || TileSize <= 0) return false;
        var column = (int)Math.Floor(point.X / TileSize);
        var row = (int)Math.Floor(point.Y / TileSize);
        if (column < 0 || row < 0 || column >= Columns || row >= Rows) return false;
        return _water[row * Columns + column];
    }
}
