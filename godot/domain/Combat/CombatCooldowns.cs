using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inotia4.Domain.Combat;

public sealed record Cooldown(long RemainingMs, long DurationMs);

public interface IActorCooldownPort
{
    bool IsReady(string skillId);

    void Start(string skillId, int baseFrames, int ratePerThousand = 1000);

    void Reset();

    Dictionary<string, Cooldown> Snapshot();
}

public sealed class AlwaysReadyCooldownPort : IActorCooldownPort
{
    public static readonly AlwaysReadyCooldownPort Instance = new();

    public bool IsReady(string skillId) => true;

    public void Start(string skillId, int baseFrames, int ratePerThousand = 1000)
    {
    }

    public void Reset()
    {
    }

    public Dictionary<string, Cooldown> Snapshot() => [];
}

public sealed class CooldownDeadline
{
    [JsonPropertyName("readyFrame")] public required long ReadyFrame { get; init; }

    [JsonPropertyName("durationFrames")] public required long DurationFrames { get; init; }
}

public sealed class ActorCooldownEntry
{
    [JsonPropertyName("actorId")] public required string ActorId { get; init; }

    [JsonPropertyName("skills")] public List<SkillCooldownEntry> Skills { get; init; } = [];
}

public sealed class SkillCooldownEntry
{
    [JsonPropertyName("skillId")] public required string SkillId { get; init; }

    [JsonPropertyName("deadline")] public required CooldownDeadline Deadline { get; init; }
}

[JsonConverter(typeof(CombatCooldownStateConverter))]
public sealed class CombatCooldownState
{
    public List<ActorCooldownEntry> Actors { get; init; } = [];
}

public sealed class CombatCooldownStateConverter : JsonConverter<CombatCooldownState>
{
    public override CombatCooldownState Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("冷却快照结构无效");
        var state = new CombatCooldownState();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) return state;
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("冷却快照结构无效");
            var property = reader.GetString();
            reader.Read();
            if (property != "actors")
            {
                reader.Skip();
                continue;
            }

            if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("冷却角色列表无效");
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                state.Actors.Add(ReadActor(ref reader));
        }

        throw new JsonException("冷却快照结构无效");
    }

    private static ActorCooldownEntry ReadActor(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("冷却角色记录无效");
        if (!reader.Read() || reader.TokenType != JsonTokenType.String)
            throw new JsonException("冷却角色 id 无效");
        var entry = new ActorCooldownEntry { ActorId = reader.GetString()! };
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray) throw new JsonException("冷却技能列表无效");
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            entry.Skills.Add(ReadSkill(ref reader));
        if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray) throw new JsonException("冷却角色记录无效");
        return entry;
    }

    private static SkillCooldownEntry ReadSkill(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("冷却技能记录无效");
        if (!reader.Read() || reader.TokenType != JsonTokenType.String)
            throw new JsonException("冷却技能 id 无效");
        var skillId = reader.GetString()!;
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw new JsonException("冷却截止帧无效");

        long? readyFrame = null, durationFrames = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("冷却截止帧无效");
            var property = reader.GetString();
            reader.Read();
            if (property == "readyFrame" && reader.TokenType == JsonTokenType.Number) readyFrame = reader.GetInt64();
            else if (property == "durationFrames" && reader.TokenType == JsonTokenType.Number) durationFrames = reader.GetInt64();
            else reader.Skip();
        }

        if (readyFrame is null || durationFrames is null) throw new JsonException("冷却截止帧无效");
        if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray) throw new JsonException("冷却技能记录无效");
        return new SkillCooldownEntry
        {
            SkillId = skillId,
            Deadline = new CooldownDeadline
            {
                ReadyFrame = readyFrame.Value,
                DurationFrames = durationFrames.Value,
            },
        };
    }

    public override void Write(Utf8JsonWriter writer, CombatCooldownState value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteStartArray("actors");
        foreach (var entry in value.Actors)
        {
            writer.WriteStartArray();
            writer.WriteStringValue(entry.ActorId);
            writer.WriteStartArray();
            foreach (var skill in entry.Skills)
            {
                writer.WriteStartArray();
                writer.WriteStringValue(skill.SkillId);
                writer.WriteStartObject();
                writer.WriteNumber("readyFrame", skill.Deadline.ReadyFrame);
                writer.WriteNumber("durationFrames", skill.Deadline.DurationFrames);
                writer.WriteEndObject();
                writer.WriteEndArray();
            }

            writer.WriteEndArray();
            writer.WriteEndArray();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}

public sealed class CombatCooldownStore
{
    private readonly Func<long> _currentFrame;
    private Dictionary<string, Dictionary<string, CooldownDeadline>> _actors = [];
    private readonly HashSet<string> _freeActors = [];

    public CombatCooldownStore(Func<long> currentFrame) => _currentFrame = currentFrame;

    public void SetFree(string actorId, bool free)
    {
        if (free) _freeActors.Add(actorId);
        else _freeActors.Remove(actorId);
    }

    public bool IsFree(string actorId) => _freeActors.Contains(actorId);

    public IActorCooldownPort ForActor(string actorId) => new ActorCooldownPort(this, actorId);

    public void ResetActor(string actorId) => _actors.Remove(actorId);

    public void RemoveActor(string actorId)
    {
        _actors.Remove(actorId);
        _freeActors.Remove(actorId);
    }

    public CombatCooldownState CaptureState()
    {
        var state = new CombatCooldownState();
        foreach (var (actorId, skills) in _actors)
        {
            var entry = new ActorCooldownEntry { ActorId = actorId };
            foreach (var (skillId, deadline) in skills)
                entry.Skills.Add(new SkillCooldownEntry
                {
                    SkillId = skillId,
                    Deadline = new CooldownDeadline
                    {
                        ReadyFrame = deadline.ReadyFrame,
                        DurationFrames = deadline.DurationFrames,
                    },
                });
            state.Actors.Add(entry);
        }

        return state;
    }

    public void RestoreState(CombatCooldownState state)
    {
        var actors = new Dictionary<string, Dictionary<string, CooldownDeadline>>();
        foreach (var entry in state.Actors)
        {
            var skills = new Dictionary<string, CooldownDeadline>();
            foreach (var record in entry.Skills)
                skills[record.SkillId] = new CooldownDeadline
                {
                    ReadyFrame = RequireFrame(record.Deadline.ReadyFrame, "readyFrame"),
                    DurationFrames = RequireFrame(record.Deadline.DurationFrames, "durationFrames"),
                };
            if (skills.Count > 0) actors[entry.ActorId] = skills;
        }

        _actors = actors;
    }

    internal static long RequireFrame(long value, string name) =>
        value >= 0 ? value : throw new ArgumentOutOfRangeException(name, value, $"{name} 必须是非负整数帧");

    internal bool Ready(string actorId, string skillId)
    {
        if (_freeActors.Contains(actorId)) return true;
        var now = Now();
        return !_actors.TryGetValue(actorId, out var skills) ||
               !skills.TryGetValue(skillId, out var deadline) ||
               now >= deadline.ReadyFrame;
    }

    internal void Start(string actorId, string skillId, int baseFrames, int ratePerThousand)
    {
        if (_freeActors.Contains(actorId)) return;

        var durationFrames = RequireFrame(
            (long)Math.Truncate(baseFrames * (double)Math.Max(0, ratePerThousand) / 1000), "durationFrames");
        var readyFrame = RequireFrame(Now() + durationFrames, "readyFrame");

        if (durationFrames == 0)
        {
            if (_actors.TryGetValue(actorId, out var existing))
            {
                existing.Remove(skillId);
                if (existing.Count == 0) _actors.Remove(actorId);
            }

            return;
        }

        if (!_actors.TryGetValue(actorId, out var skills))
        {
            skills = [];
            _actors[actorId] = skills;
        }

        skills[skillId] = new CooldownDeadline { ReadyFrame = readyFrame, DurationFrames = durationFrames };
    }

    internal Dictionary<string, Cooldown> Snapshot(string actorId)
    {
        var snapshot = new Dictionary<string, Cooldown>();
        if (_freeActors.Contains(actorId)) return snapshot;

        var now = Now();
        if (!_actors.TryGetValue(actorId, out var skills)) return snapshot;
        foreach (var (skillId, deadline) in skills)
        {
            if (deadline.ReadyFrame <= now) continue;
            snapshot[skillId] = new Cooldown(
                (deadline.ReadyFrame - now) * CombatClock.LogicFrameMs,
                deadline.DurationFrames * CombatClock.LogicFrameMs);
        }

        return snapshot;
    }

    private long Now() => RequireFrame(_currentFrame(), "currentFrame");

    private sealed class ActorCooldownPort(CombatCooldownStore store, string actorId) : IActorCooldownPort
    {
        public bool IsReady(string skillId) => store.Ready(actorId, skillId);

        public void Start(string skillId, int baseFrames, int ratePerThousand = 1000) =>
            store.Start(actorId, skillId, baseFrames, ratePerThousand);

        public void Reset() => store.ResetActor(actorId);

        public Dictionary<string, Cooldown> Snapshot() => store.Snapshot(actorId);
    }
}
