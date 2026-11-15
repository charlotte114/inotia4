using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inotia4.Domain.Combat;

public sealed class ActiveCondition
{
    public required string Kind { get; init; }

    public required string Id { get; init; }

    public required double RemainingMs { get; init; }

    public required int Strength { get; init; }

    public required string SourceId { get; init; }

    public required int StackCount { get; init; }

    public string? OwnerBuff { get; init; }
}

public readonly record struct BuffLoopFrame(string Buff, string Effect, int FrameAge);

public sealed class PeriodicTick
{
    public required string SourceId { get; init; }

    public required string TargetId { get; init; }

    public required string BuffId { get; init; }

    public required int StackCount { get; init; }

    public required BuffPeriodicEffect Effect { get; init; }
}

public readonly record struct ResourceCapacities(int MaxHealth, int MaxMana);

public interface IInvincibilityPort
{
    bool Has(string actorId);

    void Add(string actorId);

    void Delete(string actorId);
}

public interface IBuffResourcePort
{
    ResourceCapacities CaptureResourceCapacities(string actorId);

    void OnApply(string actorId, BattleBuffEffect effect, ResourceCapacities entry);
}

public sealed class FighterEquipment
{
    public bool? OffhandShield { get; init; }
}

public sealed class TemporaryAttribute
{
    public int? Value { get; init; }

    public CombatOperation? Operation { get; init; }
}

public sealed class StoredBuff
{
    public required string Id { get; init; }

    public required string SourceId { get; init; }

    public double RemainingMs { get; set; }

    public double ElapsedMs { get; set; }

    public double NextTickMs { get; set; }

    public int? NextFrameAge { get; set; }

    public required int Strength { get; init; }

    public required int StackCount { get; init; }

    public required BattleBuffEffect Effect { get; init; }
}

public sealed class StoredState
{
    public required string Id { get; init; }

    public required string SourceId { get; init; }

    public string? OwnerBuff { get; set; }

    public double RemainingMs { get; set; }

    public BuffSimulationFrames? SimulationFrames { get; init; }

    public int? NextFrameAge { get; set; }
}

public sealed class CombatConditionsState
{
    public Dictionary<string, List<StoredBuff>> Buffs { get; init; } = [];

    public Dictionary<string, List<StoredState>> States { get; init; } = [];

    public Dictionary<string, Dictionary<string, TemporaryAttribute>> Temporary { get; init; } = [];

    public Dictionary<string, Dictionary<string, int>> ResourceCapacities { get; init; } = [];

    public Dictionary<string, Dictionary<string, string>> ResourceCapacityInputs { get; init; } = [];

    public double SimulationFrameRemainderMs { get; init; }
}

public sealed class CombatConditions
{
    public const int MaxStateNodes = 128;

    private const string MaxHealth = "maxHealth";

    private const string MaxMana = "maxMana";

    private readonly IBuffResourcePort? _resources;
    private readonly IInvincibilityPort? _invincibility;
    private readonly HashSet<string> _invincible = [];

    private Dictionary<string, Dictionary<string, StoredBuff>> _buffs = [];
    private Dictionary<string, Dictionary<string, StoredState>> _states = [];
    private Dictionary<string, Dictionary<string, TemporaryAttribute>> _temporary = [];
    private Dictionary<string, Dictionary<string, int>> _resourceCapacities = [];
    private Dictionary<string, Dictionary<string, string>> _resourceCapacityInputs = [];
    private double _simulationFrameRemainderMs;

    public CombatConditions(IBuffResourcePort? resources = null, IInvincibilityPort? invincibility = null)
    {
        _resources = resources;
        _invincibility = invincibility;
    }

    public void SetInvincible(string actorId, bool enabled)
    {
        if (_invincibility is not null)
        {
            if (enabled) _invincibility.Add(actorId);
            else _invincibility.Delete(actorId);
            return;
        }

        if (enabled) _invincible.Add(actorId);
        else _invincible.Remove(actorId);
    }

    public bool IsInvincible(string actorId) =>
        _invincibility is not null ? _invincibility.Has(actorId) : _invincible.Contains(actorId);

    public CombatConditionsState CaptureState() => CopySnapshot(new CombatConditionsState
    {
        Buffs = _buffs.ToDictionary(entry => entry.Key, entry => entry.Value.Values.ToList()),
        States = _states.ToDictionary(entry => entry.Key, entry => entry.Value.Values.ToList()),
        Temporary = _temporary.ToDictionary(entry => entry.Key,
            entry => new Dictionary<string, TemporaryAttribute>(entry.Value)),
        ResourceCapacities = _resourceCapacities.ToDictionary(entry => entry.Key,
            entry => new Dictionary<string, int>(entry.Value)),
        ResourceCapacityInputs = _resourceCapacityInputs.ToDictionary(entry => entry.Key,
            entry => new Dictionary<string, string>(entry.Value)),
        SimulationFrameRemainderMs = _simulationFrameRemainderMs,
    });

    public void RestoreState(CombatConditionsState state)
    {
        state = CopySnapshot(state);
        _buffs = state.Buffs.ToDictionary(entry => entry.Key,
            entry => entry.Value.ToDictionary(buff => buff.Id));
        _states = state.States.ToDictionary(entry => entry.Key,
            entry => entry.Value.ToDictionary(value => value.Id));
        _temporary = state.Temporary.ToDictionary(entry => entry.Key,
            entry => new Dictionary<string, TemporaryAttribute>(entry.Value));
        _resourceCapacities = state.ResourceCapacities.ToDictionary(entry => entry.Key,
            entry => new Dictionary<string, int>(entry.Value));
        _resourceCapacityInputs = state.ResourceCapacityInputs.ToDictionary(entry => entry.Key,
            entry => new Dictionary<string, string>(entry.Value));
        _simulationFrameRemainderMs = state.SimulationFrameRemainderMs;
    }

    public int? CachedAttribute(string actorId, string id) =>
        _temporary.TryGetValue(actorId, out var own) &&
        own.TryGetValue(id, out var value) && value.Value is { } number
            ? number
            : null;

    public int ReadResourceCapacity(string actorId, string key, Func<int> compute)
    {
        if (CachedAttribute(actorId, key) is { } temporary) return temporary;
        if (_resourceCapacities.TryGetValue(actorId, out var cached) && cached.TryGetValue(key, out var value))
            return value;

        var computed = compute();
        if (!_resourceCapacities.TryGetValue(actorId, out var own))
        {
            own = [];
            _resourceCapacities[actorId] = own;
        }

        own[key] = computed;
        return computed;
    }

    public void InvalidateResourceCapacities(string actorId, string? key = null)
    {
        if (key is not null)
        {
            _resourceCapacities.GetValueOrDefault(actorId)?.Remove(key);
            _temporary.GetValueOrDefault(actorId)?.Remove(key);
            return;
        }

        _resourceCapacities.Remove(actorId);
        var temporary = _temporary.GetValueOrDefault(actorId);
        temporary?.Remove(MaxHealth);
        temporary?.Remove(MaxMana);
    }

    public void SetResourceCapacityInputs(string actorId, string key, string signature)
    {
        if (!_resourceCapacityInputs.TryGetValue(actorId, out var own))
        {
            own = [];
            _resourceCapacityInputs[actorId] = own;
        }

        if (own.TryGetValue(key, out var previous) && previous != signature)
            InvalidateResourceCapacities(actorId, key);
        own[key] = signature;
    }

    public void SetTemporaryAttribute(string actorId, string id, int value)
    {
        if (!_temporary.TryGetValue(actorId, out var own))
        {
            own = [];
            _temporary[actorId] = own;
        }

        own[id] = new TemporaryAttribute { Value = value };
    }

    public void ClearTemporaryAttribute(string actorId, string id)
    {
        _temporary.GetValueOrDefault(actorId)?.Remove(id);
        if (id is MaxHealth or MaxMana) InvalidateResourceCapacities(actorId, id);
    }

    public void ClearTemporary(string actorId)
    {
        var own = _temporary.GetValueOrDefault(actorId);
        foreach (var key in new[] { MaxHealth, MaxMana })
            if (own?.ContainsKey(key) == true) InvalidateResourceCapacities(actorId, key);
        _temporary.Remove(actorId);
    }

    private void InvalidateBuffAttributes(string actorId, BattleBuffEffect effect)
    {
        var attributes = new HashSet<string>(effect.Modifiers.Select(modifier => modifier.Attribute));
        foreach (var attribute in effect.InvalidatesAttributes ?? []) attributes.Add(attribute);

        foreach (var attribute in attributes)
        {
            ClearTemporaryAttribute(actorId, attribute);
            if (attribute is "physicalAttack" or "physicalDamageRate" or "magicAttack" or "magicDamageRate")
                ClearTemporaryAttribute(actorId, "bestNormalDamage");
        }
    }

    private void InvalidateStateAttributes(string actorId, string state)
    {
        if (state != "stealthed") return;
        foreach (var attribute in new[]
                 {
                     "physicalDamageRate", "criticalRate", "stunImmunity", "manaRecoveryPerPulse",
                     "bestNormalDamage",
                 })
            ClearTemporaryAttribute(actorId, attribute);
    }

    /// <summary>
    /// 挂一份增益。同一种增益只保留最强的一份：新强度不高于旧的直接拒绝，
    /// 否则层数 +1（上限取效果自己的 <c>maxStacks</c>）并重算时长。
    /// </summary>
    public bool ApplyBuff(string actorId, string id, int strength, BattleBuffEffect effect,
        string? sourceId = null, Func<string, StateDurationBound?>? initializeState = null)
    {
        if (!_buffs.TryGetValue(actorId, out var own))
        {
            own = [];
            _buffs[actorId] = own;
        }

        var previous = own.GetValueOrDefault(id);
        if (previous is not null && previous.Strength > strength) return false;

        var stackCount = Math.Min((previous?.StackCount ?? 0) + 1, effect.MaxStacks ?? 1);
        RemoveBuff(actorId, id);

        var buff = new StoredBuff
        {
            Id = id,
            SourceId = sourceId ?? actorId,
            Strength = strength,
            StackCount = stackCount,
            Effect = effect,
            RemainingMs = effect.DurationMs,
            ElapsedMs = 0,
            NextTickMs = 0,
            NextFrameAge = effect.SimulationFrames is null ? null : 0,
        };
        buff.RemainingMs = BuffRemainingMs(buff);

        own[id] = buff;
        _buffs[actorId] = own;

        var resourceEntry = _resources?.CaptureResourceCapacities(actorId);
        InvalidateBuffAttributes(actorId, effect);

        foreach (var state in effect.States)
        {
            if (initializeState is not null)
            {
                var timing = initializeState(state);
                if (timing is not null) ApplyStateFrames(actorId, state, timing.LastActiveFrame, sourceId);
            }
            else if (effect.StateDurationProfiles?.GetValueOrDefault(state) is { } profiles)
            {
                ApplyStateFrames(actorId, state, profiles.Normal.LastActiveFrame, sourceId);
            }
            else if (effect.SimulationFrames is { } frames)
            {
                ApplyStateFrames(actorId, state, frames.LastActiveFrame, sourceId);
            }
            else
            {
                ApplyState(actorId, state, effect.DurationMs, sourceId);
            }

            var current = _states.GetValueOrDefault(actorId)?.GetValueOrDefault(state);
            if (current is not null) current.OwnerBuff = id;
        }

        if (resourceEntry is { } entry) _resources!.OnApply(actorId, effect, entry);
        return true;
    }

    public bool ApplyState(string actorId, string id, double durationMs, string? sourceId = null) =>
        StoreState(actorId, new StoredState
        {
            Id = id,
            SourceId = sourceId ?? actorId,
            RemainingMs = durationMs,
        });

    public bool ApplyStateFrames(string actorId, string id, int lastActiveFrame, string? sourceId = null)
    {
        if (lastActiveFrame < 0) throw new ArgumentOutOfRangeException(nameof(lastActiveFrame), "状态最后保留帧必须为非负整数");

        var state = new StoredState
        {
            Id = id,
            SourceId = sourceId ?? actorId,
            RemainingMs = 0,
            SimulationFrames = new BuffSimulationFrames { LastActiveFrame = lastActiveFrame },
            NextFrameAge = 0,
        };
        state.RemainingMs = StateRemainingMs(state);
        return StoreState(actorId, state);
    }

    private bool StoreState(string actorId, StoredState state)
    {
        if (!_states.TryGetValue(actorId, out var own))
        {
            own = [];
            _states[actorId] = own;
        }

        own.Remove(state.Id);
        if (_states.Values.Sum(states => states.Count) >= MaxStateNodes) return false;

        InvalidateStateAttributes(actorId, state.Id);
        own[state.Id] = state;
        _states[actorId] = own;
        return true;
    }

    public void RemoveState(string actorId, string id, string? sourceId = null)
    {
        var state = _states.GetValueOrDefault(actorId)?.GetValueOrDefault(id);
        if (state is null || (sourceId is not null && state.SourceId != sourceId)) return;

        if (state.OwnerBuff is { } ownerBuff)
        {
            RemoveBuff(actorId, ownerBuff);
            return;
        }

        _states.GetValueOrDefault(actorId)?.Remove(id);
        InvalidateStateAttributes(actorId, id);
    }

    public void Update(double deltaMs, Action<PeriodicTick>? onTick = null)
    {
        if (double.IsNaN(deltaMs) || double.IsInfinity(deltaMs) || deltaMs <= 0) return;

        var statesAtStart = StateEntries().Where(entry => entry.State.SimulationFrames is null).ToList();
        var buffsAtStart = BuffEntries().Where(entry => entry.Buff.Effect.SimulationFrames is null).ToList();

        AdvanceSimulationFrames(deltaMs, onTick);

        foreach (var (actorId, own, buff) in buffsAtStart)
        {
            var id = buff.Id;
            bool IsCurrent() => _buffs.GetValueOrDefault(actorId) == own && own.GetValueOrDefault(id) == buff;
            if (!IsCurrent()) continue;

            var ending = Math.Min(buff.Effect.DurationMs, buff.ElapsedMs + deltaMs);
            if (buff.Effect.TickMs > 0 && buff.Effect.Periodic.Count > 0)
            {
                while (buff.NextTickMs <= ending && IsCurrent())
                {
                    foreach (var effect in buff.Effect.Periodic)
                    {
                        if (!IsCurrent()) break;
                        onTick?.Invoke(new PeriodicTick
                        {
                            SourceId = buff.SourceId,
                            TargetId = actorId,
                            BuffId = id,
                            StackCount = buff.StackCount,
                            Effect = effect,
                        });
                    }

                    buff.NextTickMs += buff.Effect.TickMs;
                }
            }

            buff.ElapsedMs += deltaMs;
            buff.RemainingMs = Math.Max(0, buff.Effect.DurationMs - buff.ElapsedMs);
            if (buff.RemainingMs <= 0 && IsCurrent()) RemoveBuff(actorId, id);
        }

        foreach (var (actorId, own, state) in statesAtStart)
        {
            if (_states.GetValueOrDefault(actorId) != own || own.GetValueOrDefault(state.Id) != state) continue;
            state.RemainingMs = Math.Max(0, state.RemainingMs - deltaMs);
            if (state.RemainingMs > 0) continue;
            own.Remove(state.Id);
            InvalidateStateAttributes(actorId, state.Id);
        }

        RefreshFrameRemainingTimes();
    }

    private List<(string ActorId, Dictionary<string, StoredBuff> Own, StoredBuff Buff)> BuffEntries() =>
        _buffs.SelectMany(entry => entry.Value.Values.Reverse()
            .Select(buff => (entry.Key, entry.Value, buff))).ToList();

    private List<(string ActorId, Dictionary<string, StoredState> Own, StoredState State)> StateEntries() =>
        _states.SelectMany(entry => entry.Value.Values
            .Select(state => (entry.Key, entry.Value, state))).ToList();

    private void AdvanceSimulationFrames(double deltaMs, Action<PeriodicTick>? onTick)
    {
        var remaining = deltaMs;
        while (remaining >= CombatClock.LogicFrameMs - _simulationFrameRemainderMs)
        {
            var entries = BuffEntries()
                .Where(entry => entry.Buff.Effect.SimulationFrames is not null).ToList();
            var stateEntries = StateEntries()
                .Where(entry => entry.State.SimulationFrames is not null).ToList();
            if (entries.Count == 0 && stateEntries.Count == 0) break;

            remaining -= CombatClock.LogicFrameMs - _simulationFrameRemainderMs;
            _simulationFrameRemainderMs = 0;

            foreach (var (actorId, own, state) in stateEntries)
            {
                if (_states.GetValueOrDefault(actorId) != own || own.GetValueOrDefault(state.Id) != state) continue;
                var age = state.NextFrameAge ?? 0;
                if (age > state.SimulationFrames!.LastActiveFrame)
                {
                    own.Remove(state.Id);
                    InvalidateStateAttributes(actorId, state.Id);
                }
                else
                {
                    SetFrameAge(state, age + 1);
                }
            }

            foreach (var (actorId, own, buff) in entries)
            {
                bool IsCurrent() => _buffs.GetValueOrDefault(actorId) == own && own.GetValueOrDefault(buff.Id) == buff;
                if (!IsCurrent()) continue;

                var timing = buff.Effect.SimulationFrames!;
                var age = buff.NextFrameAge ?? 0;
                var afterAgeIncrement = timing.PulseAfterAgeIncrement == true;
                if (afterAgeIncrement)
                {
                    if (age > timing.LastActiveFrame)
                    {
                        RemoveBuff(actorId, buff.Id);
                        continue;
                    }

                    SetFrameAge(buff, age + 1);
                }

                var pulseAge = afterAgeIncrement ? age + 1 : age;
                if (timing.PulseIntervalFrames is > 0 and var interval && pulseAge % interval == 0)
                {
                    foreach (var effect in buff.Effect.Periodic)
                    {
                        if (!IsCurrent()) break;
                        onTick?.Invoke(new PeriodicTick
                        {
                            SourceId = buff.SourceId,
                            TargetId = actorId,
                            BuffId = buff.Id,
                            StackCount = buff.StackCount,
                            Effect = effect,
                        });
                    }
                }

                if (!IsCurrent() || afterAgeIncrement) continue;
                if (age > timing.LastActiveFrame) RemoveBuff(actorId, buff.Id);
                else SetFrameAge(buff, age + 1);
            }
        }

        _simulationFrameRemainderMs =
            (_simulationFrameRemainderMs + remaining) % CombatClock.LogicFrameMs;
    }

    private static void SetFrameAge(StoredBuff buff, int age) => buff.NextFrameAge = age;

    private static void SetFrameAge(StoredState state, int age) => state.NextFrameAge = age;

    private double BuffRemainingMs(StoredBuff buff)
    {
        var timing = buff.Effect.SimulationFrames;
        return timing is null
            ? buff.RemainingMs
            : (timing.LastActiveFrame + 1 - (buff.NextFrameAge ?? 0)) * CombatClock.LogicFrameMs +
              CombatClock.LogicFrameMs - _simulationFrameRemainderMs;
    }

    private double StateRemainingMs(StoredState state)
    {
        var timing = state.SimulationFrames;
        return timing is null
            ? state.RemainingMs
            : (timing.LastActiveFrame + 1 - (state.NextFrameAge ?? 0)) * CombatClock.LogicFrameMs +
              CombatClock.LogicFrameMs - _simulationFrameRemainderMs;
    }

    private void RefreshFrameRemainingTimes()
    {
        foreach (var (_, _, buff) in BuffEntries())
            if (buff.Effect.SimulationFrames is not null) buff.RemainingMs = BuffRemainingMs(buff);
        foreach (var (_, _, state) in StateEntries())
            if (state.SimulationFrames is not null) state.RemainingMs = StateRemainingMs(state);
    }

    public void Remove(string actorId)
    {
        _buffs.Remove(actorId);
        _states.Remove(actorId);
        _temporary.Remove(actorId);
        _resourceCapacities.Remove(actorId);
        _resourceCapacityInputs.Remove(actorId);
    }

    public void RemoveBuff(string actorId, string id)
    {
        var buff = _buffs.GetValueOrDefault(actorId)?.GetValueOrDefault(id);
        if (buff is null) return;

        _resources?.CaptureResourceCapacities(actorId);
        InvalidateBuffAttributes(actorId, buff.Effect);
        _buffs.GetValueOrDefault(actorId)?.Remove(id);

        var own = _states.GetValueOrDefault(actorId);
        foreach (var state in buff.Effect.States)
        {
            own?.Remove(state);
            InvalidateStateAttributes(actorId, state);
        }
    }

    public void Clear()
    {
        _buffs.Clear();
        _states.Clear();
        _temporary.Clear();
        _resourceCapacities.Clear();
        _resourceCapacityInputs.Clear();
    }

    public bool HasState(string actorId, string id) =>
        _states.GetValueOrDefault(actorId)?.ContainsKey(id) == true;

    public IReadOnlyList<string> ActiveStates(string actorId) =>
        _states.TryGetValue(actorId, out var own) ? own.Keys.ToList() : [];

    public bool HasBuff(string actorId, string id) =>
        _buffs.GetValueOrDefault(actorId)?.ContainsKey(id) == true;

    public IReadOnlyList<BuffReaction> BuffReactions(string actorId) =>
        _buffs.TryGetValue(actorId, out var own)
            ? CopySnapshot(own.Values.Reverse().SelectMany(buff => buff.Effect.Reactions ?? []).ToList())
            : [];

    // 快照必须连同增益的嵌套定义独立保存，更新计龄或修改快照不能影响另一份状态。
    private static readonly JsonSerializerOptions SnapshotOptions = CreateSnapshotOptions();

    private static JsonSerializerOptions CreateSnapshotOptions()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new SnapshotDefinitionConverter<BuffReaction>());
        options.Converters.Add(new SnapshotDefinitionConverter<BuffPeriodicEffect>());
        options.Converters.Add(new SnapshotDefinitionConverter<BuffOnApply>());
        return options;
    }

    private static T CopySnapshot<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, SnapshotOptions), SkillActionJson.Options)!;

    private sealed class SnapshotDefinitionConverter<T> : JsonConverter<T> where T : class
    {
        public override T? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            JsonSerializer.Deserialize<T>(ref reader, SkillActionJson.Options);

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }

    public IReadOnlyList<BuffLoopFrame> BuffLoopFrames(string actorId)
    {
        if (_buffs.GetValueOrDefault(actorId) is not { Count: > 0 } own) return [];

        var frames = new List<BuffLoopFrame>();
        foreach (var buff in own.Values.Reverse())
        {
            var frameAge = buff.NextFrameAge ?? (int)Math.Floor(buff.ElapsedMs / CombatClock.LogicFrameMs);
            foreach (var loop in buff.Effect.LoopEffects ?? [])
                if (frameAge >= loop.FirstFrameAge)
                    frames.Add(new BuffLoopFrame(buff.Id, loop.Effect, frameAge));
        }

        return frames;
    }

    private CombatStateCapabilities Capabilities(string actorId, bool hostile = true) =>
        CombatStateRules.Capabilities(ActiveStates(actorId), hostile);

    public bool CanMove(string actorId) => Capabilities(actorId).CanMove;

    public bool CanAct(string actorId) => Capabilities(actorId).CanAct;

    public bool CanTarget(string actorId, bool hostile = true) => Capabilities(actorId, hostile).CanTarget;

    public bool CanReceiveDamage(string actorId) => Capabilities(actorId).CanReceiveDamage;

    public bool CanLoseHealth(string actorId) => !IsInvincible(actorId) && CanReceiveDamage(actorId);

    public bool LosesMana(string actorId) => !IsInvincible(actorId);

    public bool CanAutoApproach(string actorId) => Capabilities(actorId).CanAutoApproach;

    public bool CannotAct(string actorId) => !CanAct(actorId);

    public IReadOnlyList<ActiveCondition> Snapshot(string actorId)
    {
        var result = new List<ActiveCondition>();
        if (_buffs.TryGetValue(actorId, out var buffs))
            foreach (var buff in buffs.Values)
                result.Add(new ActiveCondition
                {
                    Kind = "buff",
                    Id = buff.Id,
                    Strength = buff.Strength,
                    RemainingMs = buff.RemainingMs,
                    SourceId = buff.SourceId,
                    StackCount = buff.StackCount,
                });

        if (_states.TryGetValue(actorId, out var states))
            foreach (var state in states.Values)
                result.Add(new ActiveCondition
                {
                    Kind = "state",
                    Id = state.Id,
                    Strength = 0,
                    RemainingMs = state.RemainingMs,
                    SourceId = state.SourceId,
                    StackCount = 1,
                    OwnerBuff = state.OwnerBuff,
                });

        return result;
    }

    public int? BuffLifetimeFrames(string actorId, string id) =>
        _buffs.GetValueOrDefault(actorId)?.GetValueOrDefault(id)?.Effect.SimulationFrames?.LastActiveFrame;

    public IReadOnlyList<BuffColor>? AppearanceColors(string actorId)
    {
        if (_buffs.GetValueOrDefault(actorId) is not { Count: > 0 } buffs) return null;

        IReadOnlyList<BuffColor>? found = null;
        foreach (var buff in buffs.Values)
            if (buff.Effect.AppearanceColors is { Count: > 0 } colors) found = colors;
        return found;
    }

    public int Attribute(string actorId, string id, int baseValue,
        IReadOnlyList<AttributeModifier>? passives = null)
    {
        var temporary = _temporary.GetValueOrDefault(actorId)?.GetValueOrDefault(id);
        if (temporary?.Value is { } fixedValue) return fixedValue;

        var value = AttributeModifier.Apply(id, baseValue, passives ?? []);

        if (_buffs.TryGetValue(actorId, out var buffs) && buffs.Count > 0)
        {
            foreach (var buff in buffs.Values.Reverse())
            {
                foreach (var modifier in buff.Effect.Modifiers)
                    if (modifier.Attribute == id)
                        value = CombatOperations.Apply(value, StackOperation(modifier.Operation, buff.StackCount));

                foreach (var ramp in buff.Effect.AttributeRamps ?? [])
                    if (ramp.Attribute == id)
                        value = CombatOperations.Apply(value,
                            StackOperation(ramp.Operation, AttributeRampSteps(buff, ramp.IntervalFrames)));
            }
        }

        if (id is "stealthAttackEnabled" or "stealthDamageRate") value = Math.Max(0, value);

        if (temporary?.Operation is { } operation)
        {
            if (id == "magicResistance") value = Fighter.MagicResistanceLimit(value);
            value = CombatOperations.Apply(value, operation);
            SetTemporaryAttribute(actorId, id, value);
        }

        return value;
    }

    private static CombatOperation StackOperation(CombatOperation operation, int stackCount) => operation.Kind switch
    {
        "keep" => operation,
        "set" or "add" or "subtract" => new CombatOperation
        {
            Kind = operation.Kind, Amount = operation.Amount * stackCount,
        },
        "multiply" => new CombatOperation { Kind = operation.Kind, Factor = operation.Factor * stackCount },
        "divide" => new CombatOperation { Kind = operation.Kind, Divisor = operation.Divisor * stackCount },
        "multiply-thousandth" or "increase-thousandth" or "decrease-thousandth" => new CombatOperation
        {
            Kind = operation.Kind, RatePerThousand = operation.RatePerThousand * stackCount,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(operation), $"未知的运算类型：{operation.Kind}"),
    };

    private int AttributeRampSteps(StoredBuff buff, int intervalFrames)
    {
        var frameAge = buff.NextFrameAge ?? (int)Math.Floor(buff.ElapsedMs / CombatClock.LogicFrameMs);
        return frameAge / intervalFrames + 1;
    }

    public Fighter FighterView(string actorId, Fighter baseValue,
        IReadOnlyList<AttributeModifier>? passives = null, FighterEquipment? equipment = null)
    {
        int Value(string id, int original) => Attribute(actorId, id, original, passives);

        var physicalAttack = Value("physicalAttack", baseValue.PhysicalAttack);
        var physicalBonus = Value("physicalDamageRate", 0);
        var stealthAttackEnabled =
            Value("stealthAttackEnabled", baseValue.StealthAttackEnabled ? 1 : 0) > 0;
        var stealthDamageRate = Value("stealthDamageRate", baseValue.StealthDamageRate);
        var defense = Math.Max(0, Value("defense", baseValue.Defense ?? 0));
        var defenseRate = Math.Max(0, Value("defenseRate", baseValue.DefenseRate));
        var effectiveDefense = Fighter.EffectiveDefense(defense, defenseRate);
        var physicalResistance = baseValue.Defense is null
            ? Value("physicalResistanceBonus", baseValue.PhysicalResistance)
            : (int)Math.Truncate(1000.0 * effectiveDefense / (effectiveDefense + 20 * (baseValue.Level + 3))) +
              Value("physicalResistanceBonus", baseValue.PhysicalResistanceBonus);
        var magicResistance = Value("magicResistance", baseValue.MagicResistance);

        return new Fighter
        {
            Level = baseValue.Level,
            PhysicalAttack = (int)Math.Truncate(
                physicalAttack * (1000.0 + physicalBonus + (stealthAttackEnabled ? stealthDamageRate : 0)) / 1000),
            PhysicalDamageRate = physicalBonus,
            StealthAttackEnabled = stealthAttackEnabled,
            StealthDamageRate = stealthDamageRate,
            MagicDamageRate = Value("magicDamageRate", 0),
            ConditionalMagicDamageRate = Value("conditionalMagicDamageRate", baseValue.ConditionalMagicDamageRate),
            MagicAttack = Value("magicAttack", baseValue.MagicAttack),
            CriticalRate = Value("criticalRate", baseValue.CriticalRate),
            CriticalResistance = Value("criticalResistance", baseValue.CriticalResistance),
            CriticalDamageResistance = Value("criticalDamageResistance", baseValue.CriticalDamageResistance),
            CriticalDamage = Value("criticalDamage", baseValue.CriticalDamage),
            PhysicalResistance = physicalResistance,
            MagicResistance = CachedAttribute(actorId, "magicResistance") is null
                ? Fighter.MagicResistanceLimit(magicResistance)
                : magicResistance,
            Defense = baseValue.Defense is null ? null : defense,
            DefenseRate = defenseRate,
            PhysicalResistanceBonus = baseValue.PhysicalResistanceBonus,
            EvasionBonus = Value("evasionBonus", baseValue.EvasionBonus),
            HitBonus = Value("hitBonus", baseValue.HitBonus),
            HitBase = Value("hitBase", baseValue.HitBase),
            EvasionBase = Value("evasionBase", baseValue.EvasionBase),
            WeaponBlock = Value("weaponBlock", baseValue.WeaponBlock),
            MaxHealth = Value("maxHealth", baseValue.MaxHealth),
            ShieldBlock = equipment?.OffhandShield == false ? 0 : Value("shieldBlock", baseValue.ShieldBlock),
        };
    }
}
