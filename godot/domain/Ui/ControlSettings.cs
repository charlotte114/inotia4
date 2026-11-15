using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inotia4.Domain.Ui;

public enum MoveControlStyle
{
    Stick,

    Dpad,

    FourWay,
}

public sealed class ControlSettings
{
    private readonly Func<string, string?> _read;
    private readonly Action<string, string> _write;

    private readonly List<Action<MoveControlStyle>> _listeners = [];

    private MoveControlStyle _moveStyle = MoveControlStyle.Stick;

    public ControlSettings(Func<string, string?> read, Action<string, string> write)
    {
        _read = read;
        _write = write;
        Load();
    }

    public static string StorageKey => "control-settings.json";

    public MoveControlStyle MoveStyle => _moveStyle;

    public void SetMoveStyle(MoveControlStyle style)
    {
        if (_moveStyle == style) return;
        _moveStyle = style;
        Save();
        foreach (var listener in _listeners) listener(style);
    }

    public static MoveControlStyle Next(MoveControlStyle style) => style switch
    {
        MoveControlStyle.Stick => MoveControlStyle.Dpad,
        MoveControlStyle.Dpad => MoveControlStyle.FourWay,
        _ => MoveControlStyle.Stick,
    };

    public static string Describe(MoveControlStyle style) => style switch
    {
        MoveControlStyle.Dpad => "十字按键",
        MoveControlStyle.FourWay => "四向移动",
        _ => "虚拟摇杆",
    };

    public IDisposable Subscribe(Action<MoveControlStyle> listener)
    {
        _listeners.Add(listener);
        return new Unsubscribe(this, listener);
    }

    private void Load()
    {
        try
        {
            var raw = _read(StorageKey);
            if (string.IsNullOrEmpty(raw)) return;
            var saved = JsonSerializer.Deserialize<Preferences>(raw, SaveGameStore.JsonOptions);
            _moveStyle = saved?.MoveStyle switch
            {
                "dpad" => MoveControlStyle.Dpad,
                "fourway" => MoveControlStyle.FourWay,
                _ => MoveControlStyle.Stick,
            };
        }
        catch
        {
            _moveStyle = MoveControlStyle.Stick;
        }
    }

    private void Save()
    {
        try
        {
            _write(StorageKey, JsonSerializer.Serialize(
                new Preferences
            {
                MoveStyle = _moveStyle switch
                {
                    MoveControlStyle.Dpad => "dpad",
                    MoveControlStyle.FourWay => "fourway",
                    _ => "stick",
                },
            },
                SaveGameStore.JsonOptions));
        }
        catch
        {
        }
    }

    private sealed class Unsubscribe(ControlSettings owner, Action<MoveControlStyle> listener) : IDisposable
    {
        public void Dispose() => owner._listeners.Remove(listener);
    }

    private sealed class Preferences
    {
        [JsonPropertyName("moveStyle")] public string? MoveStyle { get; set; }
    }
}
