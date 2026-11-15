using System;
using System.Collections.Generic;
using System.Linq;
using Inotia4.Domain.Content;

namespace Inotia4.Domain.World;

public sealed class QuestItemGenerator
{
    public required string Id { get; init; }

    public long RemainingFrames { get; set; }

    public string? DropId { get; set; }

    public bool Exhausted { get; set; }
}

public sealed class QuestItemGenerationState
{
    public string? Map { get; set; }

    public int MapVisit { get; set; }

    public long NextCheckFrame { get; set; }

    public List<QuestItemGenerator> Generators { get; set; } = [];
}

public sealed class QuestItemGenerators
{
    private const long CheckIntervalFrames = 150;

    private string? _map;
    private int _mapVisit;
    private long _nextCheckFrame;
    private readonly List<QuestItemGenerator> _generators = [];
    private readonly Dictionary<string, QuestItemSpawn> _definitions = [];

    public string? MapId => _map;

    public int MapVisit => _mapVisit;

    public IReadOnlyList<QuestItemGenerator> Generators => _generators;

    /// <summary>
    /// 进图。同一张图的同一次访问沿用现场（重进图会重建地图对象，但生成器状态不该重来）；
    /// 换图或换访问时把上一次留下的任务物品从地面清掉，再按本图的定义重建。
    /// </summary>
    public void Enter(string map, int mapVisit, IReadOnlyList<QuestItemSpawn> definitions, GroundLootStore ground)
    {
        var incoming = definitions.ToDictionary(definition => definition.Id, StringComparer.Ordinal);

        if (_map == map && _mapVisit == mapVisit)
        {
            // 同上一次访问：定义表必须对得上，否则说明调用方换了地图却沿用了旧状态。
            if (_generators.Count != incoming.Count || _generators.Any(generator => !incoming.ContainsKey(generator.Id)))
                throw new InvalidOperationException("任务地面物品生成点与当前地图不符");
            ReplaceDefinitions(incoming);
            return;
        }

        var previousDrops = _generators
            .Where(generator => generator.DropId is not null)
            .Select(generator => generator.DropId!)
            .ToHashSet(StringComparer.Ordinal);
        if (previousDrops.Count > 0)
        {
            var saved = ground.CaptureState();
            ground.RestoreState(new GroundLootStore.GroundLootState(saved.NextId,
                saved.Drops.Where(drop => !previousDrops.Contains(drop.Id)).ToList()));
        }

        ReplaceDefinitions(incoming);
        _map = map;
        _mapVisit = mapVisit;
        _nextCheckFrame = 0;
        _generators.Clear();
        foreach (var definition in definitions)
            _generators.Add(new QuestItemGenerator { Id = definition.Id });
    }

    public void Advance(long frame, Func<string, bool> isActive, GroundLootStore ground)
    {
        if (_map is null || _nextCheckFrame >= frame) return;

        foreach (var generator in _generators)
        {
            if (generator.Exhausted || generator.DropId is not null) continue;
            if (!_definitions.TryGetValue(generator.Id, out var definition)) continue;

            if (generator.RemainingFrames > 0) generator.RemainingFrames -= CheckIntervalFrames;
            if (generator.RemainingFrames > 0 || !isActive(definition.Quest)) continue;

            var added = ground.Add(_map, definition.X, definition.Y,
                [new Drops.DropReward.ItemDrop(definition.Item)]);
            if (added.Count > 0) generator.DropId = added[0].Id;
        }

        _nextCheckFrame = frame + CheckIntervalFrames;
    }

    public void Collected(IReadOnlyCollection<string> dropIds, long frame)
    {
        var collected = new HashSet<string>(dropIds, StringComparer.Ordinal);
        foreach (var generator in _generators)
        {
            if (generator.DropId is null || !collected.Contains(generator.DropId)) continue;
            if (!_definitions.TryGetValue(generator.Id, out var definition))
                throw new InvalidOperationException("任务地面物品生成点尚未载入");

            generator.DropId = null;
            if (definition.RespawnFrames == 0)
            {
                generator.Exhausted = true;
                generator.RemainingFrames = 0;
            }
            else
            {
                generator.RemainingFrames =
                    definition.RespawnFrames - _nextCheckFrame + frame + generator.RemainingFrames;
            }
        }
    }

    public QuestItemGenerationState CaptureState() => new()
    {
        Map = _map,
        MapVisit = _mapVisit,
        NextCheckFrame = _nextCheckFrame,
        Generators = _generators
            .Select(generator => new QuestItemGenerator
            {
                Id = generator.Id,
                RemainingFrames = generator.RemainingFrames,
                DropId = generator.DropId,
                Exhausted = generator.Exhausted,
            })
            .ToList(),
    };

    /// <summary>
    /// 读档恢复。校验一条不落：访问序号与检查帧要非负、生成器 id 不重复、
    /// 落地物品必须真在那张图的地面上。
    /// </summary>
    public void RestoreState(QuestItemGenerationState state, GroundLootStore? ground = null)
    {
        if (state.MapVisit < 0 || state.NextCheckFrame < 0)
            throw new InvalidOperationException("任务地面物品生成状态无效");
        if (state.Map is not null && state.Map.Length == 0)
            throw new InvalidOperationException("任务地面物品地图无效");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var drops = new HashSet<string>(StringComparer.Ordinal);
        foreach (var generator in state.Generators)
        {
            if (generator.Id.Length == 0 || !ids.Add(generator.Id))
                throw new InvalidOperationException("任务地面物品生成器无效");
            if (generator.DropId is null) continue;
            if (!drops.Add(generator.DropId) || state.Map is null ||
                (ground is not null && ground.ForMap(state.Map).All(drop => drop.Id != generator.DropId)))
                throw new InvalidOperationException("任务地面物品实例无效");
        }

        if (state.Map is null && state.Generators.Count > 0)
            throw new InvalidOperationException("任务地面物品缺少地图");

        var retain = _map == state.Map && state.Generators.All(generator => _definitions.ContainsKey(generator.Id));
        _map = state.Map;
        _mapVisit = state.MapVisit;
        _nextCheckFrame = state.NextCheckFrame;
        _generators.Clear();
        foreach (var generator in state.Generators)
        {
            _generators.Add(new QuestItemGenerator
            {
                Id = generator.Id,
                RemainingFrames = generator.RemainingFrames,
                DropId = generator.DropId,
                Exhausted = generator.Exhausted,
            });
        }

        if (!retain) _definitions.Clear();
    }

    private void ReplaceDefinitions(Dictionary<string, QuestItemSpawn> incoming)
    {
        _definitions.Clear();
        foreach (var (id, definition) in incoming) _definitions[id] = definition;
    }
}
