using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Inotia4.Domain.Character;

namespace Inotia4.Domain;

public sealed class GroundLootStore
{
    private const int MaxGroundDropsPerMap = 64;

    private const int GroundPickupRadiusPx = 12;

    public sealed record GroundFlight(float X, float Y, int Direction, int RemainingMs, int DurationMs);

    public sealed record GroundDrop(
        string Id, string Map, float X, float Y, Drops.DropReward Reward,
        long? CreatedAtMs = null, GroundFlight? Flight = null);

    public sealed record GroundLootState(int NextId, IReadOnlyList<GroundDrop> Drops);

    private int _nextId;
    private readonly Dictionary<string, GroundDrop> _drops = new();

    private readonly Dictionary<string, List<string>> _byMap = new();

    public int Revision { get; private set; }

    public GroundLootState CaptureState() => new(_nextId, _drops.Values.Select(Clone).ToList());

    public void RestoreState(GroundLootState state)
    {
        _nextId = state.NextId;
        _drops.Clear();
        _byMap.Clear();
        foreach (var drop in state.Drops)
        {
            _drops[drop.Id] = Clone(drop);
            Track(_drops[drop.Id]);
        }

        Revision++;
    }

    public IReadOnlyList<GroundDrop> Add(string map, float x, float y, IReadOnlyList<Drops.DropReward> rewards,
        long? nowMs = null)
    {
        var added = new List<GroundDrop>();
        foreach (var reward in rewards)
        {
            var id = $"ground-{++_nextId}";
            var drop = new GroundDrop(id, map, x, y, CloneReward(reward), CreatedAtMs: nowMs);
            _drops[drop.Id] = drop;
            Track(drop);
            added.Add(Clone(drop));
        }

        Prune(map);
        if (added.Count > 0) Revision++;
        return added;
    }

    private void Prune(string map)
    {
        var onMap = OnMap(map);
        if (onMap.Count <= MaxGroundDropsPerMap) return;
        var overflow = onMap
            .OrderBy(drop => drop.CreatedAtMs ?? long.MaxValue)
            .Take(onMap.Count - MaxGroundDropsPerMap);
        foreach (var drop in overflow)
        {
            _drops.Remove(drop.Id);
            Untrack(drop);
        }
    }

    private void Track(GroundDrop drop)
    {
        if (!_byMap.TryGetValue(drop.Map, out var ids)) _byMap[drop.Map] = ids = new List<string>();
        if (!ids.Contains(drop.Id)) ids.Add(drop.Id);
    }

    private void Untrack(GroundDrop drop)
    {
        if (!_byMap.TryGetValue(drop.Map, out var ids)) return;
        ids.Remove(drop.Id);
        if (ids.Count == 0) _byMap.Remove(drop.Map);
    }

    private List<GroundDrop> OnMap(string map)
    {
        if (!_byMap.TryGetValue(map, out var ids)) return [];
        var result = new List<GroundDrop>();
        foreach (var id in ids)
            if (_drops.TryGetValue(id, out var drop))
                result.Add(drop);
        return result;
    }

    public void AddFlying(string map, (float X, float Y) origin, (float X, float Y, int Direction) landing,
        string item, int durationMs)
    {
        var added = Add(map, landing.Item1, landing.Item2, [new Drops.DropReward.ItemDrop(item)]);
        var drop = _drops[added[0].Id];
        _drops[drop.Id] = drop with
        {
            Flight = new GroundFlight(origin.Item1, origin.Item2, landing.Direction, durationMs, durationMs),
        };
    }

    public void Advance(string map, int deltaMs)
    {
        if (deltaMs <= 0) return;
        foreach (var drop in OnMap(map))
            if (drop.Flight is { } flight)
            {
                var remaining = flight.RemainingMs - deltaMs;
                if (remaining <= 0)
                {
                    _drops[drop.Id] = drop with { Flight = null };
                    Revision++;
                }
                else
                {
                    _drops[drop.Id] = drop with
                    {
                        Flight = flight with { RemainingMs = remaining },
                    };
                }
            }
    }

    public IReadOnlyList<GroundDrop> ForMap(string map) => OnMap(map).Select(Clone).ToList();

    public IReadOnlyList<GroundDrop> CollectNearby(string map, float x, float y,
        Func<Drops.DropReward, bool> collect, Action<GroundDrop>? reject = null)
    {
        var nearby = OnMap(map).Where(drop => drop.Flight is null &&
            Math.Abs(drop.X - x) <= GroundPickupRadiusPx && Math.Abs(drop.Y - y) <= GroundPickupRadiusPx);
        var taken = new List<GroundDrop>();
        foreach (var drop in nearby)
        {
            if (!collect(CloneReward(drop.Reward))) { reject?.Invoke(drop); continue; }
            _drops.Remove(drop.Id);
            Untrack(drop);
            taken.Add(Clone(drop));
        }

        if (taken.Count > 0) Revision++;
        return taken;
    }

    public IReadOnlyList<GroundDrop> CollectAll(string map, Func<Drops.DropReward, bool> collect)
    {
        var taken = new List<GroundDrop>();
        foreach (var drop in OnMap(map))
        {
            if (!collect(CloneReward(drop.Reward))) continue;
            _drops.Remove(drop.Id);
            Untrack(drop);
            taken.Add(Clone(drop));
        }

        if (taken.Count > 0) Revision++;
        return taken;
    }

    private static GroundDrop Clone(GroundDrop drop) => drop with { Reward = CloneReward(drop.Reward) };

    private static Drops.DropReward CloneReward(Drops.DropReward reward) => reward switch
    {
        Drops.DropReward.ItemDrop item => item with { Instance = item.Instance?.DeepClone() },
        _ => reward,
    };
}

public sealed class DropRewardConverter : JsonConverter<Drops.DropReward>
{
    public override Drops.DropReward Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var kind = root.TryGetProperty("kind", out var kindElement) ? kindElement.GetString() : null;
        return kind switch
        {
            "item" => new Drops.DropReward.ItemDrop(
                root.GetProperty("item").GetString() ?? throw new JsonException("掉落物品引用无效"),
                root.TryGetProperty("instance", out var instance) && instance.ValueKind != JsonValueKind.Null
                    ? instance.Deserialize<ItemInstance>(options)
                    : null,
                root.TryGetProperty("roll", out var roll) && roll.ValueKind != JsonValueKind.Null
                    ? roll.Deserialize<Drops.DropRoll>(options)
                    : null),
            "money" => new Drops.DropReward.MoneyDrop(root.GetProperty("amount").GetInt32()),
            _ => throw new JsonException($"掉落奖励种类无效：{kind}"),
        };
    }

    public override void Write(Utf8JsonWriter writer, Drops.DropReward value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        switch (value)
        {
            case Drops.DropReward.ItemDrop item:
                writer.WriteString("kind", "item");
                writer.WriteString("item", item.Item);
                writer.WriteNumber("quantity", 1);
                if (item.Instance is not null)
                {
                    writer.WritePropertyName("instance");
                    JsonSerializer.Serialize(writer, item.Instance, options);
                }

                if (item.Roll is not null)
                {
                    writer.WritePropertyName("roll");
                    JsonSerializer.Serialize(writer, item.Roll, options);
                }

                break;
            case Drops.DropReward.MoneyDrop money:
                writer.WriteString("kind", "money");
                writer.WriteNumber("amount", money.Amount);
                break;
            default:
                throw new JsonException($"掉落奖励种类无效：{value.GetType().Name}");
        }

        writer.WriteEndObject();
    }
}
