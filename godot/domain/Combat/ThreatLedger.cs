using System;
using System.Collections.Generic;
using System.Linq;

namespace Inotia4.Domain.Combat;

public sealed class ThreatEntry
{
    public required string OwnerId { get; set; }

    public required string SourceId { get; set; }

    public int Score { get; set; }

    public long UpdatedTick { get; set; }

    public ThreatEntry Clone() => new() { OwnerId = OwnerId, SourceId = SourceId, Score = Score, UpdatedTick = UpdatedTick };
}

public sealed class ThreatLedgerState
{
    public List<ThreatEntry> Entries { get; init; } = [];
}

public sealed record ThreatAdditionRules(int? SourceReductionRate = null, string? AllowedSourceId = null);

public sealed class ThreatLedger
{
    public const int Capacity = 256;

    private const int RemoteThreatRangeUnits = 30;

    private const int RemoteDamageThreatPercent = 80;

    private const int NearbyDamageThreatPercent = 100;

    private const int RemoteHealingThreatWeight = 15;

    private const int NearbyHealingThreatWeight = 20;

    private const int HealingThreatDivisor = 1_000_000;

    private const int StateThreatPerLevel = 200;

    private const int StateThreatDivisor = 100_000;

    private const int ThreatSwitchPercent = 110;

    private const int TauntRivalCeilingPercent = 80;

    private const int DecayFlatPenalty = 10;

    private readonly List<ThreatEntry> _records = [];

    public static int ReduceThreat(int amount, int reductionRate = 0)
    {
        Integer(amount, "仇恨增量");
        Integer(reductionRate, "仇恨减率");

        return reductionRate > 0 ? unchecked(amount - Divide(Imul(amount, reductionRate), 1000)) : amount;
    }

    public static int CalculateDamageThreat(int damage, int rectangleDistanceUnits, int generationRate = 1000)
    {
        Integer(damage, "伤害");
        Distance(rectangleDistanceUnits);
        var rate = GenerationRate(generationRate);

        if (damage < 1) return 0;

        var percent = rectangleDistanceUnits > RemoteThreatRangeUnits
            ? RemoteDamageThreatPercent
            : NearbyDamageThreatPercent;
        var adjusted = Divide(Imul(damage, percent), 100);
        return Divide(Imul(adjusted, rate), 1000);
    }

    public static int CalculateHealingThreat(int actualHealing, int rectangleDistanceUnits, int generationRate = 1000)
    {
        Integer(actualHealing, "实际治疗量");
        Distance(rectangleDistanceUnits);

        var weight = rectangleDistanceUnits > RemoteThreatRangeUnits
            ? RemoteHealingThreatWeight
            : NearbyHealingThreatWeight;

        return Divide(Imul(Imul(actualHealing, weight), GenerationRate(generationRate)), HealingThreatDivisor);
    }

    public static int CalculateStateThreat(int casterLevel, int generationRate = 1000)
    {
        var level = Integer(casterLevel, "施法者等级");
        if (level < 0) throw new InvalidOperationException("施法者等级必须非负");

        return Divide(Imul(Imul(level, StateThreatPerLevel), GenerationRate(generationRate)), StateThreatDivisor);
    }

    public bool Add(string ownerId, string sourceId, int amount, long tick, ThreatAdditionRules? rules = null)
    {
        ActorId(ownerId);
        ActorId(sourceId);
        Integer(amount, "仇恨增量");
        LogicTick(tick);

        rules ??= new ThreatAdditionRules();
        if (rules.AllowedSourceId is not null && rules.AllowedSourceId != sourceId) return false;

        var increment = ReduceThreat(amount, rules.SourceReductionRate ?? 0);
        var entry = EnsureEntry(ownerId, sourceId, tick);
        entry.Score = unchecked(entry.Score + increment);
        entry.UpdatedTick = tick;
        return true;
    }

    public int? Highest(string ownerId, Func<string, bool> isEligible)
    {
        int? maximum = null;
        foreach (var entry in _records)
            if (entry.OwnerId == ownerId && isEligible(entry.SourceId) && (maximum is null || entry.Score > maximum))
                maximum = entry.Score;

        return maximum;
    }

    public bool RaiseTo(string ownerId, string sourceId, int floor, long tick)
    {
        ActorId(ownerId);
        ActorId(sourceId);
        Integer(floor, "仇恨下限");
        LogicTick(tick);
        if (floor < 0) throw new InvalidOperationException("仇恨下限必须非负");

        var entry = _records.FirstOrDefault(record => record.OwnerId == ownerId && record.SourceId == sourceId);
        if (entry is not null && entry.Score >= floor) return false;

        entry ??= EnsureEntry(ownerId, sourceId, tick);
        entry.Score = floor;
        entry.UpdatedTick = tick;
        return true;
    }

    public int? Score(string ownerId, string sourceId) =>
        _records.FirstOrDefault(entry => entry.OwnerId == ownerId && entry.SourceId == sourceId)?.Score;

    public IReadOnlyList<ThreatEntry> Entries(string? ownerId = null) =>
        _records.Where(entry => ownerId is null || entry.OwnerId == ownerId).Select(entry => entry.Clone()).ToList();

    public void ClearOwner(string ownerId) => RemoveWhere(entry => entry.OwnerId == ownerId);

    public void RemoveActor(string id) => RemoveWhere(entry => entry.OwnerId == id || entry.SourceId == id);

    public void Clear() => _records.Clear();

    public void Decay(Func<string, string, bool> isBeyondSight)
    {
        for (var index = 0; index < _records.Count;)
        {
            var entry = _records[index];
            if (isBeyondSight(entry.OwnerId, entry.SourceId)) entry.Score = (entry.Score >> 1) - DecayFlatPenalty;

            if (entry.Score < 1) RemoveAt(index);
            else index += 1;
        }
    }

    public string? Select(string ownerId, string? currentSourceId, Func<string, bool> isEligible)
    {
        ThreatEntry? maximum = null;
        foreach (var entry in _records)
        {
            if (entry.OwnerId != ownerId || !isEligible(entry.SourceId)) continue;
            if (maximum is null || entry.Score > maximum.Score) maximum = entry;
        }

        if (maximum is null) return currentSourceId;

        var current = currentSourceId is null ? null : Score(ownerId, currentSourceId);
        if (current is not null && maximum.Score < Divide(Imul(current.Value, ThreatSwitchPercent), 100))
            return currentSourceId;

        return maximum.SourceId;
    }

    public bool ClampCompetitors(string ownerId, string casterId, Func<string, bool> isSameCasterFaction)
    {
        var casterScore = Score(ownerId, casterId);
        if (casterScore is null) return false;

        var ceiling = Divide(Imul(casterScore.Value, TauntRivalCeilingPercent), 100);
        foreach (var entry in _records)
        {
            if (entry.OwnerId != ownerId || entry.SourceId == casterId) continue;
            if (entry.Score > ceiling && isSameCasterFaction(entry.SourceId)) entry.Score = ceiling;
        }

        return true;
    }

    public ThreatLedgerState CaptureState() =>
        new() { Entries = _records.Select(entry => entry.Clone()).ToList() };

    public void RestoreState(ThreatLedgerState state)
    {
        if (state.Entries.Count > Capacity) throw new InvalidOperationException("仇恨快照超过全局容量");

        var restored = new List<ThreatEntry>();
        foreach (var entry in state.Entries)
        {
            ActorId(entry.OwnerId);
            ActorId(entry.SourceId);
            Integer(entry.Score, "仇恨分数");
            LogicTick(entry.UpdatedTick);

            if (restored.Any(previous => previous.OwnerId == entry.OwnerId && previous.SourceId == entry.SourceId))
                throw new InvalidOperationException("仇恨快照包含重复来源");

            restored.Add(new ThreatEntry
            {
                OwnerId = entry.OwnerId,
                SourceId = entry.SourceId,
                Score = entry.Score,
                UpdatedTick = entry.UpdatedTick,
            });
        }

        _records.Clear();
        _records.AddRange(restored);
    }

    private ThreatEntry EnsureEntry(string ownerId, string sourceId, long tick)
    {
        var existing = _records.FirstOrDefault(record => record.OwnerId == ownerId && record.SourceId == sourceId);
        if (existing is not null) return existing;

        if (_records.Count == Capacity)
        {
            var oldest = 0;
            for (var index = 1; index < _records.Count; index += 1)
                if (_records[index].UpdatedTick < _records[oldest].UpdatedTick) oldest = index;

            RemoveAt(oldest);
        }

        var entry = new ThreatEntry { OwnerId = ownerId, SourceId = sourceId, Score = 0, UpdatedTick = tick };
        _records.Add(entry);
        return entry;
    }

    private void RemoveWhere(Func<ThreatEntry, bool> predicate)
    {
        for (var index = 0; index < _records.Count;)
        {
            if (predicate(_records[index])) RemoveAt(index);
            else index += 1;
        }
    }

    private void RemoveAt(int index)
    {
        var last = _records[^1];
        _records.RemoveAt(_records.Count - 1);
        if (index < _records.Count) _records[index] = last;
    }

    /// <summary>
    /// 每笔金额都必须是 32 位有符号整数。
    /// C# 的 <see cref="int"/> 本身就是这个范围，所以这里只保留调用点，
    /// 让算式读起来能看出「这一步在防越界」。
    /// </summary>
    private static int Integer(int value, string name)
    {
        _ = name;
        return value;
    }

    private static int Distance(int value)
    {
        if (Integer(value, "矩形距离") < 0) throw new InvalidOperationException("矩形距离必须非负");
        return value;
    }

    private static void ActorId(string value)
    {
        if (string.IsNullOrEmpty(value)) throw new InvalidOperationException("仇恨角色标识不能为空");
    }

    private static void LogicTick(long value)
    {
        if (value < 0) throw new InvalidOperationException("仇恨更新时间必须是非负安全整数");
    }

    private static int GenerationRate(int value = 1000) => Math.Max(0, Integer(value, "仇恨生成倍率"));

    private static int Divide(int value, int divisor)
    {
        var quotient = Math.Truncate((double)value / divisor);
        return quotient == 0 ? 0 : (int)quotient;
    }

    private static int Imul(int left, int right) => unchecked(left * right);
}
