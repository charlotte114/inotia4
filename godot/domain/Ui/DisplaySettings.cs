using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inotia4.Domain.Ui;

public sealed class DisplaySettings
{
    private readonly Func<string, string?> _read;
    private readonly Action<string, string> _write;

    private bool _fps = true;
    private bool _criticalDecorations = true;

    private bool _highRefresh = true;

    private bool _haptics = true;

    public DisplaySettings(Func<string, string?> read, Action<string, string> write)
    {
        _read = read;
        _write = write;
        Load();
    }

    public static string StorageKey => "display-settings.json";

    public bool Fps => _fps;

    public bool CriticalDecorations => _criticalDecorations;

    public bool HighRefresh => _highRefresh;

    public bool Haptics => _haptics;

    public void SetHaptics(bool enabled)
    {
        if (_haptics == enabled) return;
        _haptics = enabled;
        Save();
    }

    public void SetHighRefresh(bool enabled)
    {
        if (_highRefresh == enabled) return;
        _highRefresh = enabled;
        Save();
    }

    public void SetFps(bool enabled)
    {
        _fps = enabled;
        Save();
    }

    public void SetCriticalDecorations(bool enabled)
    {
        _criticalDecorations = enabled;
        Save();
    }

    private void Load()
    {
        try
        {
            var raw = _read(StorageKey);
            if (string.IsNullOrEmpty(raw)) return;
            var saved = JsonSerializer.Deserialize<Preferences>(raw, SaveGameStore.JsonOptions);
            if (saved is null) return;
            _fps = saved.Fps != false;
            _criticalDecorations = saved.CriticalDecorations != false;
            _highRefresh = saved.HighRefresh != false;
            _haptics = saved.Haptics != false;
        }
        catch
        {
            // 存储不可读时按默认值（四项都开）。
            _fps = true;
            _criticalDecorations = true;
            _highRefresh = true;
            _haptics = true;
        }
    }

    private void Save()
    {
        try
        {
            _write(StorageKey, JsonSerializer.Serialize(
                new Preferences
                {
                    Fps = _fps, CriticalDecorations = _criticalDecorations, HighRefresh = _highRefresh,
                    Haptics = _haptics,
                },
                SaveGameStore.JsonOptions));
        }
        catch
        {
        }
    }

    private sealed class Preferences
    {
        [JsonPropertyName("fps")] public bool? Fps { get; set; }

        [JsonPropertyName("criticalDecorations")] public bool? CriticalDecorations { get; set; }

        [JsonPropertyName("highRefresh")] public bool? HighRefresh { get; set; }

        [JsonPropertyName("haptics")] public bool? Haptics { get; set; }
    }
}
