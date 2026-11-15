using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inotia4.Domain.Combat;

public sealed class FrenzyAnimationMemory
{
    private readonly Dictionary<string, int> _frames = [];

    /// <summary>本次狂气该等多少帧：有动画就用动画帧数，否则退回上次记住的，再否则用调用方给的初值。</summary>
    public int Select(string actorId, int? animationFrames, int initialWaitFrames)
    {
        var frames = animationFrames ?? _frames.GetValueOrDefault(actorId, initialWaitFrames);
        _frames[actorId] = frames;
        return frames;
    }

    public void UpdateEquipment(string actorId, int? animationFrames)
    {
        if (_frames.ContainsKey(actorId) && animationFrames is not null)
            _frames[actorId] = animationFrames.Value;
    }

    public void RemoveActor(string actorId) => _frames.Remove(actorId);

    public List<KeyValuePair<string, int>> CaptureState() => [.. _frames];

    public void RestoreState(IReadOnlyList<KeyValuePair<string, int>> state)
    {
        _frames.Clear();
        foreach (var (id, frames) in state) _frames[id] = frames;
    }
}

/// <summary>
/// 狂气动画记忆的存档形状：就是一组「角色 → 帧数」的二元组，
/// 所以写成裸数组 <c>[[角色, 帧数], …]</c>，不多包一层对象——旧档才读得回来。
/// </summary>
[JsonConverter(typeof(FrenzyAnimationStateConverter))]
public sealed class FrenzyAnimationState
{
    public List<FrenzyAnimationEntry> Actors { get; init; } = [];
}

public sealed class FrenzyAnimationEntry
{
    public required string Id { get; init; }

    public required int Frames { get; init; }
}

public sealed class FrenzyAnimationStateConverter : JsonConverter<FrenzyAnimationState>
{
    public override FrenzyAnimationState Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("狂气动画快照结构无效");
        var state = new FrenzyAnimationState();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("狂气动画记录无效");
            if (!reader.Read() || reader.TokenType != JsonTokenType.String)
                throw new JsonException("狂气动画角色 id 无效");
            var id = reader.GetString()!;
            if (!reader.Read() || reader.TokenType != JsonTokenType.Number)
                throw new JsonException("狂气动画帧数无效");
            var frames = reader.GetInt32();
            if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray)
                throw new JsonException("狂气动画记录无效");
            state.Actors.Add(new FrenzyAnimationEntry { Id = id, Frames = frames });
        }

        return state;
    }

    public override void Write(Utf8JsonWriter writer, FrenzyAnimationState value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var entry in value.Actors)
        {
            writer.WriteStartArray();
            writer.WriteStringValue(entry.Id);
            writer.WriteNumberValue(entry.Frames);
            writer.WriteEndArray();
        }

        writer.WriteEndArray();
    }
}
