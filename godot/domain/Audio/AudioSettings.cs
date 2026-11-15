using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inotia4.Domain.Audio;

/// <summary>
/// 音频偏好：音乐与音效两个开关。本机偏好，不进存档——
/// 与画面设置一样落 <c>user://</c> 下的一个文件。
///
/// 只有显式写 <c>false</c> 才算关，缺字段或读取失败一律按开启：
/// 旧版存档里没有这一项时，玩家不该莫名其妙地被静音。
/// </summary>
public sealed class AudioSettings
{
    private readonly Func<string, string?> _read;
    private readonly Action<string, string> _write;

    private bool _music = true;
    private bool _effects = true;

    public AudioSettings(Func<string, string?> read, Action<string, string> write)
    {
        _read = read;
        _write = write;
        Load();
    }

    public static string StorageKey => "audio-settings.json";

    public bool Music => _music;

    public bool Effects => _effects;

    public bool Enabled(string channel) => channel switch
    {
        "music" => _music,
        "effects" => _effects,
        _ => throw new ArgumentException($"未知的音频通道：{channel}"),
    };

    public bool SetEnabled(string channel, bool enabled)
    {
        if (channel is not ("music" or "effects")) throw new ArgumentException($"未知的音频通道：{channel}");
        if (Enabled(channel) == enabled) return false;

        if (channel == "music") _music = enabled;
        else _effects = enabled;
        Save();
        return true;
    }

    private void Load()
    {
        try
        {
            var raw = _read(StorageKey);
            if (string.IsNullOrEmpty(raw)) return;
            var saved = JsonSerializer.Deserialize<Preferences>(raw, SaveGameStore.JsonOptions);
            if (saved is null) return;
            _music = saved.Music != false;
            _effects = saved.Effects != false;
        }
        catch
        {
            // 存储不可读时按默认值（两项都开）。
            _music = true;
            _effects = true;
        }
    }

    private void Save()
    {
        try
        {
            _write(StorageKey, JsonSerializer.Serialize(
                new Preferences { Music = _music, Effects = _effects }, SaveGameStore.JsonOptions));
        }
        catch
        {
        }
    }

    private sealed class Preferences
    {
        [JsonPropertyName("music")] public bool? Music { get; set; }

        [JsonPropertyName("effects")] public bool? Effects { get; set; }
    }
}
