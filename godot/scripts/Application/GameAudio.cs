using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using Inotia4.Domain.Audio;

namespace Inotia4.Application;

/// <summary>
/// 音频播放层：音乐与环境音各占一条循环轨，音效按需解码、播放后即弃；两条开关各自管一条轨。
///
/// 解码与播放上的几处取舍：
/// <list type="bullet">
/// <item>Godot 的 <see cref="AudioStreamMP3"/> 直接从磁盘读，没有「解码进缓存」这一步，
///   所以按路径缓存、暂留音乐、失败冷却这类播放策略在这里没有对象，
///   取而代之的是「每条音轨一个 <see cref="AudioStreamPlayer"/>」；</item>
/// <item>没有「加载完成再补播」这一步：磁盘读取是同步的，解码失败就不播，
///   不存在等待窗口，补播队列因此没有必要；</item>
/// <item>同一轮的多次请求直接各自同步解码，不合并成一批（同一个音效连点两次就是响两次）。</item>
/// </list>
/// 路径一律相对 <c>public/</c>，与贴图走同一个 <see cref="ContentRoot"/>。
/// </summary>
public sealed class GameAudio
{
    private const float EffectVolume = .75f;

    private const float MusicVolume = .65f;

    private const float AmbienceVolume = .45f;

    private readonly AudioCatalog _catalog;

    private readonly Node _host;

    private readonly Func<bool> _isSuspended;

    private readonly Dictionary<string, AudioStream?> _decoded = new();

    private readonly AudioStreamPlayer _music;

    private readonly AudioStreamPlayer _ambience;

    private readonly List<AudioStreamPlayer> _effects = [];

    private int _effectCursor;

    private string? _playingMusicKey;

    private string? _playingAmbienceKey;

    public GameAudio(Node host, AudioCatalog catalog, AudioSettings settings, Func<bool> isSuspended)
    {
        _host = host;
        _catalog = catalog;
        Settings = settings;
        _isSuspended = isSuspended;

        _music = new AudioStreamPlayer { Name = "Music", Bus = "Master" };
        _ambience = new AudioStreamPlayer { Name = "Ambience", Bus = "Master" };
        host.AddChild(_music);
        host.AddChild(_ambience);
    }

    public AudioCatalog Catalog => _catalog;

    public AudioSettings Settings { get; }

    public string? MusicId { get; private set; }

    public string? AmbienceId { get; private set; }

    public void SetEnabled(string channel, bool enabled)
    {
        if (!Settings.SetEnabled(channel, enabled)) return;
        ApplyLoop(channel == "music" ? "music" : "ambience");
    }

    public void EnterMenu()
    {
        SetLoop("music", _catalog.MenuMusic);
        SetLoop("ambience", null);
    }

    public void EnterStory()
    {
        SetLoop("music", null);
        SetLoop("ambience", null);
    }

    /// <summary>
    /// 进地图。地图记录里的音乐与环境音各自可以缺省，缺省表示「这一项不更换」，
    /// 所以 <c>null</c> 只能表示保留、不能表示停止。
    /// </summary>
    public void EnterMap(string mapId)
    {
        if (!_catalog.Maps.TryGetValue(mapId, out var entry)) return;
        if (entry.Music is { Length: > 0 }) SetLoop("music", entry.Music);
        if (entry.Ambience is { Length: > 0 }) SetLoop("ambience", entry.Ambience);
    }

    public (string? Music, string? Ambience) CaptureStoryAudio() => (MusicId, AmbienceId);

    public void RestoreStoryAudio(string? music, string? ambience)
    {
        if (music is not null && !_catalog.Music.ContainsKey(music))
            throw new InvalidOperationException($"剧情音乐不存在：{music}");
        if (ambience is not null && !_catalog.Effects.ContainsKey(ambience))
            throw new InvalidOperationException($"剧情环境音不存在：{ambience}");

        SetLoop("music", music);
        SetLoop("ambience", ambience);
    }

    public void SetStoryMusic(string? sound)
    {
        if (sound is not null && !_catalog.Music.ContainsKey(sound))
            throw new InvalidOperationException($"剧情音乐不存在：{sound}");
        SetLoop("music", sound);
    }

    public void StopStoryAudio()
    {
        RestoreStoryAudio(null, null);
        foreach (var player in _effects) player.Stop();
    }

    public void Effect(string? name)
    {
        if (string.IsNullOrEmpty(name) || !Settings.Effects) return;
        if (!_catalog.Effects.TryGetValue(name, out var path) || string.IsNullOrEmpty(path)) return;
        if (_isSuspended()) return;

        var stream = Decode(path);
        if (stream is null) return;

        var player = NextEffectPlayer();
        player.Stream = stream;
        player.VolumeDb = Mathf.LinearToDb(EffectVolume);
        player.Play();
    }

    public void Ui(string name)
    {
        LastUi = name;
        UiCount += 1;
        Effect(_catalog.Ui.Of(name));
    }

    public string LastUi { get; private set; } = string.Empty;

    public int UiCount { get; private set; }

    public void PlayerHit(string weaponKind, string hitKind, string? targetEnemy) =>
        Effect(AudioCueRules.PlayerHit(_catalog, weaponKind, hitKind, targetEnemy, Random.Shared.NextDouble));

    public void ShieldBlock() => Effect(_catalog.Combat.ShieldBlock);

    public void PeriodicDamage() => Effect(_catalog.Combat.PeriodicDamage);

    public void PlayerLevelUp() => Effect(AudioCueRules.LevelUpEffect);

    public void PlayerSkill(string id) => Effect(_catalog.Skills.GetValueOrDefault(id));

    public void MonsterAttack(string id) =>
        Effect(_catalog.Enemies.TryGetValue(id, out var entry) ? entry.Attack : null);

    public void MonsterDeath(string id) =>
        Effect(_catalog.Enemies.TryGetValue(id, out var entry) ? entry.Death : null);

    public void PrepareCombat(IEnumerable<string?> skillIds, IEnumerable<string?> actionSounds)
    {
        foreach (var name in AudioCueRules.PrepareCombatNames(_catalog, skillIds, actionSounds))
            if (!string.IsNullOrEmpty(name) && _catalog.Effects.TryGetValue(name, out var path)) Decode(path);
    }

    public void PrepareUi()
    {
        foreach (var name in AudioCueRules.PrepareUiNames(_catalog))
            if (!string.IsNullOrEmpty(name) && _catalog.Effects.TryGetValue(name, out var path)) Decode(path);
    }

    public void PrepareStoryEffects(IEnumerable<string?> names)
    {
        foreach (var name in names)
            if (!string.IsNullOrEmpty(name) && _catalog.Effects.TryGetValue(name, out var path)) Decode(path);
    }

    private void SetLoop(string channel, string? id)
    {
        if (channel == "music")
        {
            if (MusicId == id) return;
            MusicId = id;
        }
        else
        {
            if (AmbienceId == id) return;
            AmbienceId = id;
        }

        ApplyLoop(channel);
    }

    private void ApplyLoop(string channel)
    {
        var music = channel == "music";
        var player = music ? _music : _ambience;
        var id = music ? MusicId : AmbienceId;
        var enabled = music ? Settings.Music : Settings.Effects;

        player.Stop();
        player.Stream = null;
        if (music) _playingMusicKey = null;
        else _playingAmbienceKey = null;

        if (!enabled || id is null) return;
        var path = music ? _catalog.Music.GetValueOrDefault(id) : _catalog.Effects.GetValueOrDefault(id);
        if (string.IsNullOrEmpty(path)) return;

        var stream = Decode(path, loop: true);
        if (stream is null) return;

        player.Stream = stream;
        player.VolumeDb = Mathf.LinearToDb(music ? MusicVolume : AmbienceVolume);
        player.Play();
        if (_isSuspended()) player.StreamPaused = true;

        if (music) _playingMusicKey = id;
        else _playingAmbienceKey = id;
    }

    private AudioStream? Decode(string path, bool loop = false)
    {
        // 环境音也可能作为一次性音效播放，两种播放方式不能共用可变的循环标记。
        var cacheKey = loop ? $"loop:{path}" : path;
        if (_decoded.TryGetValue(cacheKey, out var cached)) return cached;

        AudioStream? stream = null;
        try
        {
            var full = ContentRoot.Resolve(path);
            if (!File.Exists(full))
            {
                GD.PushWarning($"[audio] 找不到音频文件：{path}");
            }
            else
            {
                stream = Path.GetExtension(full).ToLowerInvariant() switch
                {
                    ".mp3" => AudioStreamMP3.LoadFromFile(full),
                    _ => AudioStreamWav.LoadFromFile(full),
                };
                if (loop && stream is AudioStreamMP3 mp3) mp3.Loop = true;
                if (loop && stream is AudioStreamWav wav)
                {
                    wav.LoopBegin = 0;
                    wav.LoopEnd = (int)Math.Round(wav.GetLength() * wav.MixRate);
                    wav.LoopMode = AudioStreamWav.LoopModeEnum.Forward;
                }
            }
        }
        catch (Exception error)
        {
            GD.PushWarning($"[audio] 音频解码失败：{path} — {error.Message}");
            stream = null;
        }

        _decoded[cacheKey] = stream;
        return stream;
    }

    private AudioStreamPlayer NextEffectPlayer()
    {
        if (_effects.Count < EffectVoices)
        {
            var created = new AudioStreamPlayer { Name = $"Effect{_effects.Count}" };
            _host.AddChild(created);
            _effects.Add(created);
            return created;
        }

        var player = _effects[_effectCursor];
        _effectCursor = (_effectCursor + 1) % _effects.Count;
        return player;
    }

    private const int EffectVoices = 12;

    /// <summary>
    /// 退出前主动收尾。顺序按「让引擎退出清理时看不到任何音频对象」来排：
    /// 停播 → 摘掉播放器节点并释放 → 释放解码缓存里的流。
    ///
    /// <c>AudioStreamMP3</c> 的 playback 对象由音频线程持有，引擎在退出清理时会与它
    /// 抢同一个对象表；偶发的「ObjectDB instances leaked」就是这一下抢出来的。
    /// 所以这里在停播之后把播放器节点与流都立刻释放，尽可能把窗口压到最小。
    /// 这一步必须在 <c>GetTree().Quit()</c> 之前发生。
    /// </summary>
    public void Shutdown()
    {
        if (_shutdown) return;
        _shutdown = true;

        var players = new List<AudioStreamPlayer>(_effects);
        players.Add(_music);
        players.Add(_ambience);

        foreach (var player in players)
            if (GodotObject.IsInstanceValid(player)) player.Stop();

        foreach (var player in players)
        {
            if (!GodotObject.IsInstanceValid(player)) continue;
            player.Stream = null;
            player.GetParent()?.RemoveChild(player);
            player.Free();
        }

        foreach (var stream in _decoded.Values)
            if (stream is not null && GodotObject.IsInstanceValid(stream)) stream.Dispose();

        _effects.Clear();
        _decoded.Clear();
        MusicId = null;
        AmbienceId = null;
        _playingMusicKey = null;
        _playingAmbienceKey = null;
    }

    private bool _shutdown;

    public string Describe() =>
        $"音乐 {(_playingMusicKey ?? "（无）")}／环境音 {(_playingAmbienceKey ?? "（无）")}" +
        $"，开关 音乐 {(Settings.Music ? "开" : "关")} 音效 {(Settings.Effects ? "开" : "关")}";
}
