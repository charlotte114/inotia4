using System;
using System.Collections.Generic;
using System.Linq;

namespace Inotia4.Domain;

public sealed class LootPity
{
    public sealed record PityCounters(int EquipmentMisses, int RarePlusMisses);

    public sealed record LootPityState(
        int Version,
        IReadOnlyList<KeyValuePair<string, PityCounters>> Members,
        IReadOnlyList<KeyValuePair<string, PityCounters>> Chests,
        IReadOnlyList<string> BossDefeats);

    private readonly Dictionary<string, PityCounters> _members = new();
    private readonly Dictionary<string, PityCounters> _chests = new();
    private readonly HashSet<string> _bossDefeats = new();

    public PityCounters Counters(string ownerId)
    {
        if (!_members.TryGetValue(ownerId, out var value)) _members[ownerId] = value = new PityCounters(0, 0);
        return value;
    }

    public PityCounters ChestCounters(string key)
    {
        if (!_chests.TryGetValue(key, out var value)) _chests[key] = value = new PityCounters(0, 0);
        return value;
    }

    public int EquipmentBonus(string ownerId, Drops.PityConfig config) =>
        Math.Min(Counters(ownerId).EquipmentMisses * config.Equipment.StepPermille, config.Equipment.CapPermille);

    public int RareBonus(string ownerId, Drops.PityConfig config) =>
        Math.Min(Counters(ownerId).RarePlusMisses * config.Rare.StepPermille, config.Rare.CapPermille);

    public int ChestEquipmentBonus(string key, Drops.PityConfig config) =>
        Math.Min(ChestCounters(key).EquipmentMisses * config.Equipment.StepPermille, config.Equipment.CapPermille);

    public void RecordEnemy(string ownerId, bool equipment, int bestRarity, bool withinWindow)
    {
        if (!withinWindow) return;
        var counters = Counters(ownerId);
        if (equipment)
        {
            var rarePlus = bestRarity >= 2 ? 0 : counters.RarePlusMisses + 1;
            _members[ownerId] = counters with { EquipmentMisses = 0, RarePlusMisses = rarePlus };
        }
        else
        {
            _members[ownerId] = counters with
            {
                EquipmentMisses = counters.EquipmentMisses + 1,
                RarePlusMisses = counters.RarePlusMisses + 1,
            };
        }
    }

    public void RecordChest(string key, bool equipment)
    {
        var counters = ChestCounters(key);
        _chests[key] = counters with
        {
            EquipmentMisses = equipment ? 0 : counters.EquipmentMisses + 1,
        };
    }

    public bool TakeBossFirstDefeat(string enemyId)
    {
        if (_bossDefeats.Contains(enemyId)) return false;
        _bossDefeats.Add(enemyId);
        return true;
    }

    public bool BossDefeated(string enemyId) => _bossDefeats.Contains(enemyId);

    public void RemoveMember(string ownerId) => _members.Remove(ownerId);

    public LootPityState CaptureState() => new(
        1,
        _members.Select(pair => new KeyValuePair<string, PityCounters>(pair.Key, pair.Value)).ToList(),
        _chests.Select(pair => new KeyValuePair<string, PityCounters>(pair.Key, pair.Value)).ToList(),
        _bossDefeats.OrderBy(id => id, StringComparer.Ordinal).ToList());

    /// <summary>
    /// 读回保底状态并逐项复核：版本必须是 1、三张表都要在、
    /// 计数必须是非负整数。旧档里没有 version 字段（反序列化成 0），按 1 接受——
    /// 早期存档没有这个字段，不认会把现有档全部拒掉。
    /// </summary>
    public void RestoreState(LootPityState? state)
    {
        if (state is null || state.Version is not (0 or 1) ||
            state.Members is null || state.Chests is null || state.BossDefeats is null)
            throw new InvalidOperationException("掉落保底状态无效");

        foreach (var (_, value) in state.Members.Concat(state.Chests))
            if (value is null || value.EquipmentMisses < 0 || value.RarePlusMisses < 0)
                throw new InvalidOperationException("掉落保底计数无效");

        _members.Clear();
        _chests.Clear();
        _bossDefeats.Clear();
        foreach (var (id, value) in state.Members)
        {
            _members[id] = value;
        }

        foreach (var (id, value) in state.Chests)
        {
            _chests[id] = value;
        }

        foreach (var id in state.BossDefeats) _bossDefeats.Add(id);
    }
}
