using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Inotia4.Domain;

public static class SaveGameStore
{
    public const int ManualSaveSlotCount = 10;

    public const int SaveFormat = 43;

    public static readonly int[] LegacySaveFormats = [42, 41, 40, 39, 38, 37, 36, 35, 34, 33];

    public static bool IsReadableSaveFormat(int format) =>
        format == SaveFormat || LegacySaveFormats.Contains(format);

    public static string SaveSlotLabel(SaveSlotRef reference) =>
        reference.IsAuto ? "自动存档" : $"存档槽 {reference.Slot!.Value + 1}";

    public const int SaveNameMaxLength = 12;

    public static string SanitizeSaveName(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value)
        {
            var kept = (character >= '\u3400' && character <= '\u4DBF')   // 㐀-䶿
                || (character >= '\u4E00' && character <= '\u9FFF')       // 一-鿿
                || (character >= '\uF900' && character <= '\uFAFF')       // 豈-﫿
                || (character >= 'A' && character <= 'Z')
                || (character >= 'a' && character <= 'z')
                || (character >= '0' && character <= '9');
            if (kept) builder.Append(character);
        }

        return builder.Length <= SaveNameMaxLength
            ? builder.ToString()
            : builder.ToString(0, SaveNameMaxLength);
    }

    public static string? SessionClassId(Session session) => session.ClassId;

    public static SaveSummary SaveSummary(Session session, string mapId, string mapName,
        string storyClassName, string portrait)
    {
        var classId = SessionClassId(session) ?? throw new InvalidOperationException("存档缺少职业");
        return new SaveSummary
        {
            ClassId = classId,
            ClassName = storyClassName,
            Portrait = portrait,
            Level = session.PlayerLevel,
            MapId = mapId,
            MapName = mapName,
        };
    }

    public static SaveGame CaptureSaveGame(Session session, JsonNode? world, SaveSummary summary)
    {
        var snapshot = summary.Clone();
        snapshot.SavedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return new SaveGame
        {
            Format = SaveFormat,
            Summary = snapshot,
            Session = session.CapturePersistentState(),
            World = world?.DeepClone(),
        };
    }

    public static JsonNode? RestoreSaveGame(Session session, SaveGame game)
    {
        session.RestoreState(game.Session);
        return game.World;
    }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new DropRewardConverter() },
    };
}

public readonly record struct SaveSlotRef(int? Slot)
{
    public static SaveSlotRef Auto { get; } = new((int?)null);

    public static implicit operator SaveSlotRef(int slot) => new(slot);

    public bool IsAuto => Slot is null;

    public static bool TryParse(string value, out SaveSlotRef reference)
    {
        if (value == "auto")
        {
            reference = Auto;
            return true;
        }

        if (int.TryParse(value, out var slot) && slot is >= 0 and <= SaveGameStore.ManualSaveSlotCount - 1)
        {
            reference = new SaveSlotRef(slot);
            return true;
        }

        reference = Auto;
        return false;
    }
}

public sealed class SaveSummary
{
    public string ClassId { get; set; } = string.Empty;
    public string ClassName { get; set; } = string.Empty;
    public string Portrait { get; set; } = string.Empty;
    public int Level { get; set; }
    public string MapId { get; set; } = string.Empty;
    public string MapName { get; set; } = string.Empty;
    public string? Name { get; set; }
    public long? SavedAt { get; set; }

    public SaveSummary Clone() => new()
    {
        ClassId = ClassId,
        ClassName = ClassName,
        Portrait = Portrait,
        Level = Level,
        MapId = MapId,
        MapName = MapName,
        Name = Name,
        SavedAt = SavedAt,
    };
}

public sealed class SaveGame
{
    public int Format { get; set; }
    public SaveSummary Summary { get; set; } = new();
    public SessionState Session { get; set; } = new();
    public JsonNode? World { get; set; }
}

public abstract record SlotReading
{
    public sealed record Empty : SlotReading;

    public sealed record Unreadable(string Reason) : SlotReading;

    public sealed record Ok(SaveGame Game) : SlotReading;
}

public abstract record SlotSummaryReading
{
    public sealed record Empty : SlotSummaryReading;

    public sealed record Unreadable(string Reason) : SlotSummaryReading;

    public sealed record Ok(SaveSummary Summary) : SlotSummaryReading;
}

public interface ISaveStorage
{
    string? GetItem(string key);
    void SetItem(string key, string value);
    void RemoveItem(string key);
}

public sealed class NoSaveStorage : ISaveStorage
{
    public static readonly NoSaveStorage Instance = new();

    public string? GetItem(string key) => null;

    public void SetItem(string key, string value) =>
        throw new InvalidOperationException("当前环境不支持本地存档");

    public void RemoveItem(string key) =>
        throw new InvalidOperationException("当前环境不支持本地存档");
}

public sealed class SaveSlots
{
    public const string DefaultPrefix = "inotia4.story.v1";

    private readonly ISaveStorage _storage;
    private readonly string _prefix;

    private readonly Dictionary<string, (string Raw, SlotSummaryReading Reading)> _summaryCache = new();

    public SaveSlots(ISaveStorage? storage = null, string prefix = DefaultPrefix)
    {
        _storage = storage ?? NoSaveStorage.Instance;
        _prefix = prefix;
    }

    private string Key(SaveSlotRef reference)
    {
        if (reference.IsAuto) return $"{_prefix}.auto";
        var slot = reference.Slot!.Value;
        if (slot < 0 || slot >= SaveGameStore.ManualSaveSlotCount)
            throw new InvalidOperationException("存档槽编号无效");
        return $"{_prefix}.slot{slot}";
    }

    private string ManualKey(SaveSlotRef reference)
    {
        if (reference.IsAuto) throw new InvalidOperationException("自动存档不能删除或改名");
        return Key(reference);
    }

    public SlotSummaryReading Peek(SaveSlotRef reference)
    {
        var key = Key(reference);
        string? raw;
        try { raw = _storage.GetItem(key); }
        catch { return new SlotSummaryReading.Unreadable("无法读取本地存档"); }
        if (raw is null)
        {
            _summaryCache.Remove(key);
            return new SlotSummaryReading.Empty();
        }

        if (_summaryCache.TryGetValue(key, out var cached) && cached.Raw == raw) return cached.Reading;
        var reading = SummaryOf(raw);
        _summaryCache[key] = (raw, reading);
        return reading;
    }

    private SlotSummaryReading SummaryOf(string raw)
    {
        JsonObject? record;
        try { record = JsonNode.Parse(raw) as JsonObject; }
        catch { return new SlotSummaryReading.Unreadable("存档内容不是有效 JSON"); }
        if (record is null) return new SlotSummaryReading.Unreadable("存档内容不是对象");
        var format = ReadFormat(record);
        if (format is not null && !SaveGameStore.IsReadableSaveFormat(format.Value))
            return new SlotSummaryReading.Empty();
        if (format is null) return new SlotSummaryReading.Unreadable("存档缺少有效版本");
        if (record["session"] is null || record["world"] is null)
            return new SlotSummaryReading.Unreadable("存档缺少会话或世界状态");
        if (!ReadSummary(record, out var summary))
            return new SlotSummaryReading.Unreadable("存档摘要无效");
        return new SlotSummaryReading.Ok(summary);
    }

    public SlotReading Inspect(SaveSlotRef reference)
    {
        var key = Key(reference);
        string? raw;
        try { raw = _storage.GetItem(key); }
        catch { return new SlotReading.Unreadable("无法读取本地存档"); }
        if (raw is null) return new SlotReading.Empty();

        JsonObject? record;
        try { record = JsonNode.Parse(raw) as JsonObject; }
        catch { return new SlotReading.Unreadable("存档内容不是有效 JSON"); }
        if (record is null) return new SlotReading.Unreadable("存档内容不是对象");
        var format = ReadFormat(record);
        if (format is not null && !SaveGameStore.IsReadableSaveFormat(format.Value))
            return new SlotReading.Empty();
        if (format is null) return new SlotReading.Unreadable("存档缺少有效版本");
        if (record["session"] is null || record["world"] is null)
            return new SlotReading.Unreadable("存档缺少会话或世界状态");
        if (!ReadSummary(record, out _))
            return new SlotReading.Unreadable("存档摘要无效");

        SaveGame game;
        try { game = JsonSerializer.Deserialize<SaveGame>(raw, SaveGameStore.JsonOptions)!; }
        catch (JsonException) { return new SlotReading.Unreadable("存档内容不符合存档结构"); }
        return new SlotReading.Ok(game);
    }

    public void Write(SaveSlotRef reference, SaveGame game)
    {
        var existing = game.Summary.Name is null ? ExistingName(reference) : null;
        var record = game;
        if (existing is not null)
        {
            var summary = game.Summary.Clone();
            summary.Name = existing;
            record = new SaveGame
            {
                Format = game.Format,
                Summary = summary,
                Session = game.Session,
                World = game.World,
            };
        }

        var key = Key(reference);
        _storage.SetItem(key, JsonSerializer.Serialize(record, SaveGameStore.JsonOptions));
        _summaryCache.Remove(key);
    }

    public void Erase(SaveSlotRef reference)
    {
        var key = ManualKey(reference);
        _storage.RemoveItem(key);
        _summaryCache.Remove(key);
    }

    public string? Rename(SaveSlotRef reference, string name)
    {
        ManualKey(reference);
        var reading = Inspect(reference);
        if (reading is not SlotReading.Ok ok)
            throw new InvalidOperationException("这个存档槽里没有可以改名的存档");
        var applied = SaveGameStore.SanitizeSaveName(name);
        if (applied.Length == 0) applied = null;
        var summary = ok.Game.Summary.Clone();
        summary.Name = applied;
        var key = Key(reference);
        _storage.SetItem(key, JsonSerializer.Serialize(new SaveGame
        {
            Format = ok.Game.Format,
            Summary = summary,
            Session = ok.Game.Session,
            World = ok.Game.World,
        }, SaveGameStore.JsonOptions));
        _summaryCache.Remove(key);
        return applied;
    }

    private string? ExistingName(SaveSlotRef reference)
    {
        var reading = Inspect(reference);
        return reading is SlotReading.Ok ok ? ok.Game.Summary.Name : null;
    }

    private static int? ReadFormat(JsonObject record)
    {
        if (record["format"] is not JsonValue value) return null;
        if (!value.TryGetValue<double>(out var number) || !double.IsFinite(number) ||
            number != Math.Truncate(number) || number < int.MinValue || number > int.MaxValue)
            return null;
        return (int)number;
    }

    /// <summary>摘要校验：classId / mapId 必须是字符串，level 必须是有限数字。</summary>
    private static bool ReadSummary(JsonObject record, out SaveSummary summary)
    {
        summary = new SaveSummary();
        if (record["summary"] is not JsonObject node) return false;
        if (node["classId"] is not JsonValue classId || !classId.TryGetValue<string>(out var classIdValue))
            return false;
        if (node["mapId"] is not JsonValue mapId || !mapId.TryGetValue<string>(out var mapIdValue))
            return false;
        if (node["level"] is not JsonValue level || !level.TryGetValue<double>(out var levelValue)
            || !double.IsFinite(levelValue))
            return false;

        summary = JsonSerializer.Deserialize<SaveSummary>(node.ToJsonString(), SaveGameStore.JsonOptions) ??
                  new SaveSummary();
        summary.ClassId = classIdValue;
        summary.MapId = mapIdValue;
        summary.Level = (int)levelValue;
        return true;
    }
}
