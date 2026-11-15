using System;
using System.Collections.Generic;
using System.Linq;
using Inotia4.Domain.Content;

namespace Inotia4.Domain.World;

public sealed record ChestEnemySpawn(string Id, string EnemyId, float X, float Y, bool Respawn = false);

public sealed record ChestVisitsState(
    string? Map,
    int Visit,
    IReadOnlyList<string> Opened,
    IReadOnlyList<string> Consumed,
    IReadOnlyList<ChestEnemySpawn> Enemies);

public sealed class ChestVisits
{
    private string? _map;
    private int _visit;
    private readonly HashSet<string> _opened = new();

    /// <summary>剧情摆放的一次性宝箱：开启后跨地图访问保持已消耗，不能反复领取。</summary>
    private readonly HashSet<string> _consumed = new();

    private readonly List<ChestEnemySpawn> _enemies = [];

    public int Revision { get; private set; }

    public string? MapId => _map;

    public IReadOnlyList<ChestEnemySpawn> Spawns => _enemies;

    public int Visit => _visit;

    public IReadOnlyCollection<string> Opened => _opened;

    public IReadOnlyCollection<string> Consumed => _consumed;

    public ChestVisitsState CaptureState() => new(
        _map, _visit, _opened.ToList(), _consumed.ToList(), _enemies.ToList());

    public void RestoreState(ChestVisitsState state)
    {
        Revision++;
        _map = state.Map;
        _visit = state.Visit;
        _opened.Clear();
        foreach (var id in state.Opened) _opened.Add(id);
        _consumed.Clear();
        foreach (var id in state.Consumed ?? []) _consumed.Add(id);
        _enemies.Clear();
        _enemies.AddRange(state.Enemies ?? []);
    }

    public void EnterMap(string map)
    {
        if (map == _map) return;
        Revision++;
        _map = map;
        _visit++;
        _opened.Clear();
        _enemies.Clear();
    }

    private static string PersistentKey(string map, string entity) => $"{map}:{entity}";

    public bool IsOpened(string map, string entity, bool oneTime = false) =>
        oneTime ? _consumed.Contains(PersistentKey(map, entity)) : _map == map && _opened.Contains(entity);

    public void SpawnEnemy(string map, string key, string enemyId, Vec2 position)
    {
        Revision++;
        _enemies.Add(new ChestEnemySpawn($"spring:{_visit}:{map}:{key}", enemyId, position.X, position.Y));
    }

    public void SpawnBreakableEnemies(string map, string key, IReadOnlyList<string> enemyIds, Vec2 position)
    {
        if (map != _map) return;
        Revision++;
        for (var index = 0; index < enemyIds.Count; index += 1)
        {
            _enemies.Add(new ChestEnemySpawn(
                $"breakable:{_visit}:{map}:{key}:{index}", enemyIds[index], position.X, position.Y));
        }
    }

    public Drops.ChestOpenResult? Open(string map, string entity, Vec2 position,
        Func<Drops.ChestOpenResult> roll, bool oneTime = false)
    {
        if (map != _map) return null;
        var key = PersistentKey(map, entity);
        if (oneTime ? _consumed.Contains(key) : _opened.Contains(entity)) return null;

        Revision++;
        if (oneTime) _consumed.Add(key);
        else _opened.Add(entity);

        try
        {
            var result = roll();
            for (var index = 0; index < result.Enemies.Count; index += 1)
            {
                _enemies.Add(new ChestEnemySpawn(
                    $"chest:{_visit}:{map}:{entity}:{index}", result.Enemies[index], position.X, position.Y));
            }

            return result;
        }
        catch
        {
            if (oneTime) _consumed.Remove(key);
            else _opened.Remove(entity);
            throw;
        }
    }

    public const int OpenMs = 600;
}

/// <summary>
/// 宝箱的出现判定：每次进图重掷一次。
///
/// 普通宝箱掷 1..99，不超过 29 才出现（约 29%）；优质宝箱先过同一道关，再过一道
/// 「1..99 达到 60」的关——过了才是优质宝箱，没过就降级成普通宝箱，所以优质宝箱
/// 实际约 11.6%。这一次没出现就返回 <c>null</c>。
/// </summary>
public static class ChestPlacement
{
    private const int PlacementDice = 99;

    public static MapEntity? RollPlacement(MapEntity entity, Func<double> random)
    {
        int Dice() => (int)Math.Floor(random() * PlacementDice) + 1;

        if (entity.Appear is { } appear && Dice() > appear.AtMost) return null;
        if (entity.Downgrade is { } downgrade && Dice() < downgrade.AtLeast)
            return entity.WithActor(downgrade.Actor);
        return entity;
    }
}

public static class ChestReach
{
    private const float FootBoxSize = 16f;

    /// <summary><c>UTIL_GetRectDistance</c> 把两轴间隙之和右移 3 位，所以距离单位是 8 像素。</summary>
    private const float RectDistanceUnit = 8f;

    /// <summary><c>GAMESTATE_PressKeyPlay</c> 要求 <c>CHAR_GetDistance</c> 不超过 6。</summary>
    private const float RectDistanceLimit = 6f;

    public const float NearObjectDistance = 24f;

    /// <summary>近距离交互：两个脚底坐标的曼哈顿距离在 24 像素以内（<c>PLAYER_DoCheckNearNPC</c> 的判定）。</summary>
    public static bool NearInteractable(Vec2 from, Vec2 target) =>
        MathF.Abs(from.X - target.X) + MathF.Abs(from.Y - target.Y) <= NearObjectDistance;

    public static bool NearInteractive(Vec2 from, Vec2 target, bool blocking) =>
        NearInteractable(from, target) || (blocking && CanReach(from, target));

    /// <summary>
    /// 够不够得着：<c>CHAR_GetDistance</c> 走 <c>UTIL_GetRectDistance</c>，
    /// 两个 16×16 脚底矩形的两轴间隙之和 ÷ 8，要求不超过 6。
    /// </summary>
    public static bool CanReach(Vec2 from, Vec2 target)
    {
        var gapX = MathF.Max(0f, MathF.Abs(from.X - target.X) - FootBoxSize);
        var gapY = MathF.Max(0f, MathF.Abs(from.Y - target.Y) - FootBoxSize);
        return (gapX + gapY) / RectDistanceUnit <= RectDistanceLimit;
    }

    /// <summary>
    /// 按交互键还要求玩家正面朝着目标：<c>CHAR_GetDirectionForCharacter</c> 给出目标所在的
    /// 方向码（取间隙较大的那一轴），必须等于玩家当前朝向，否则这一下是普攻。
    /// </summary>
    public static bool FacesTarget(Vec2 from, string facing, Vec2 target)
    {
        var dx = target.X - from.X;
        var dy = target.Y - from.Y;
        if (MathF.Abs(dx) >= MathF.Abs(dy)) return facing == (dx >= 0f ? "east" : "west");
        return facing == (dy >= 0f ? "south" : "north");
    }
}
