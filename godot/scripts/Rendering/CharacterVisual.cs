using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Inotia4.Application;
using Inotia4.Domain.Combat;
using Inotia4.Domain.Content;

namespace Inotia4.Rendering;

/// <summary>
/// 地图上的人：一个动作片段可能有多层贴图（身体 + 发光 + 残影），
/// 逐层建一个精灵，按同一帧号同步推进。
///
/// 按当前增益颜色给各层染色。
/// 受击闪白：亮一帧，下一帧恢复。
/// 动作类动画播一次，行走与待机循环：
/// 走→走之间切换时保留当前帧与帧内进度（转向时把起始帧与累计进度
///
/// Godot 这边每层的贴图是横向片带（每帧切一张 <see cref="AtlasTexture"/>），
/// 所以一层就是一个 <see cref="AnimatedSprite2D"/>，层内动画统一叫 <c>default</c>；

public partial class CharacterVisual : Node2D
{
    private const string LayerAnimation = "default";

    private readonly List<AnimatedSprite2D> _layers = [];

    private readonly List<string> _layerFiles = [];

    private readonly List<string> _layerBlends = [];

    private readonly List<ShaderMaterial?> _layerStatusMaterials = [];

    private readonly List<Material?> _layerPlainMaterials = [];

    private IReadOnlyList<BuffColor>? _statusColors;

    private int _statusFrame;

    private bool _statusFlash;

    private ulong _flashFrame;

    private readonly Dictionary<string, SpriteFrames> _frameCache = new(StringComparer.Ordinal);

    private readonly Dictionary<string, bool> _loopOverrides = new(StringComparer.Ordinal);

    private ActorDefinition _actor = new();

    private TextureLease? _lease;

    private bool _placementFlip;

    private string _signature = string.Empty;

    private string _animation = string.Empty;

    private int _frame;

    private float _progress;

    private bool _playing;

    private float _speed = 1f;

    private bool _stealthed;

    public event Action? AnimationFinished;

    public void Build(ActorDefinition actor, TextureLease lease, string initialAnimation, bool placementFlip)
    {
        _actor = actor;
        _lease = lease;
        _placementFlip = placementFlip;
        Play(initialAnimation);
    }

    public string Animation => _animation;

    public void Play(string name)
    {
        _playing = true;
        SetAnimation(name);
    }

    public void SetAnimation(string name)
    {
        if (!_actor.Clips.TryGetValue(name, out var clip)) return;

        var carried = CarryFrame(name, clip);
        ApplyClip(name, clip);
        _frame = carried;
        ApplyPlayback();
    }

    public void Play()
    {
        _playing = true;
        ApplyPlayback();
    }

    public void Pause()
    {
        _playing = false;
        foreach (var layer in _layers) layer.Pause();
    }

    public int Frame
    {
        get => _frame;
        set
        {
            _frame = value;
            ApplyFrame();
        }
    }

    public float SpeedScale
    {
        get => _speed;
        set
        {
            _speed = value;
            foreach (var layer in _layers) layer.SpeedScale = value;
        }
    }

    public Vector2 Offset { get; private set; }

    public bool HasArtwork => _layers.Count > 0;

    public int LayerCount => _layers.Count;

    public IReadOnlyList<AnimatedSprite2D> Layers => _layers;

    public IReadOnlyList<string> LayerFiles => _layerFiles;

    public bool HasAnimation(string name) => _actor.Clips.ContainsKey(name);

    public void SetAnimationLoop(string name, bool loop)
    {
        _loopOverrides[name] = loop;
        if (_animation != name) return;
        if (!_actor.Clips.TryGetValue(name, out var clip)) return;

        ApplyClip(name, clip);
        ApplyPlayback();
    }

    public int GetFrameCount(string name) =>
        _actor.Clips.TryGetValue(name, out var clip) ? clip.Frames : 0;

    public void SetFrameAndProgress(int frame, float progress)
    {
        _frame = frame;
        _progress = progress;
        foreach (var layer in _layers)
        {
            var count = layer.SpriteFrames.GetFrameCount(LayerAnimation);
            if (count <= 0) continue;
            layer.SetFrameAndProgress(Math.Clamp(frame, 0, count - 1), progress);
        }
    }

    public void SetStealth(bool stealthed)
    {
        if (_stealthed == stealthed) return;
        _stealthed = stealthed;
        foreach (var layer in _layers) layer.Modulate = StealthTint(stealthed);
    }

    private void ApplyClip(string name, ActorClip clip)
    {
        _animation = name;

        // 签名里带上循环标志：剧情姿态显式改过循环设置时，层要按新的循环重建。
        //
        // 每层的贴图来源必须算进签名：换装合成出来的图层只有 key（贴图在合成登记表里）、
        // File 是空串，只看 File 的话 idle-south／walk-south 等所有合成片段签名完全相同，
        // 换动作时不会重建图层，人物就停在上一套动作的帧上（原版 replaceLayers 是每次
        // 换片段都整批重建的，这里等价于把「片段本身」也算作图层身份）。
        var signature = name + "#" +
                        string.Join("|", clip.Layers.Select(layer =>
                            $"{(layer.File.Length > 0 ? layer.File : layer.Key)}:{layer.Blend}")) +
                        $"|loop={LoopFor(name)}";
        if (signature != _signature)
        {
            _signature = signature;

            if (_layers.Count > 0) _progress = _layers[0].FrameProgress;

            foreach (var layer in _layers)
            {
                RemoveChild(layer);
                layer.QueueFree();
            }
            _layers.Clear();
            _layerFiles.Clear();
            _layerBlends.Clear();
            _layerStatusMaterials.Clear();
            _layerPlainMaterials.Clear();

            foreach (var layer in clip.Layers)
            {
                var sprite = BuildLayer(clip, layer);
                if (sprite is null) continue;
                AddChild(sprite);
                _layers.Add(sprite);
                _layerFiles.Add(layer.File);
                _layerBlends.Add(layer.Blend);
                _layerStatusMaterials.Add(null);
                _layerPlainMaterials.Add(BlendMaterials.For(layer.Blend));
            }

            if (_layers.Count > 0) _layers[0].AnimationFinished += OnLayerFinished;
        }

        Offset = new Vector2(-clip.OriginX * clip.FrameWidth, -clip.OriginY * clip.FrameHeight);
        var flip = _actor.IsMirrored(clip, _placementFlip);
        if (flip) Offset = new Vector2(-(1f - clip.OriginX) * clip.FrameWidth, Offset.Y);
        foreach (var layer in _layers)
        {
            layer.Offset = Offset;
            layer.FlipH = flip;
        }

        ApplyStatusMaterials();
    }

    public void PaintStatus(IReadOnlyList<BuffColor>? colors, int logicalFrame)
    {
        _statusColors = colors;
        _statusFrame = logicalFrame;
        ApplyStatusMaterials();
    }

    public void FlashStatus()
    {
        _statusFlash = true;
        _flashFrame = Engine.GetProcessFrames();
        ApplyStatusMaterials();
    }

    public override void _Process(double delta)
    {
        if (!_statusFlash) return;
        if (Engine.GetProcessFrames() <= _flashFrame) return;

        _statusFlash = false;
        ApplyStatusMaterials();
    }

    private void ApplyStatusMaterials()
    {
        var colors = _statusColors;
        var flash = _statusFlash;

        for (var index = 0; index < _layers.Count; index++)
        {
            var layer = _layers[index];
            var blend = _layerBlends[index];

            if (colors is not { Count: > 0 } && !flash)
            {
                var plain = _layerPlainMaterials[index];
                if (!ReferenceEquals(layer.Material, plain)) layer.Material = plain;
                continue;
            }

            var material = _layerStatusMaterials[index];
            if (material is null)
            {
                material = StatusColorMaterial.Create(blend);
                _layerStatusMaterials[index] = material;
            }

            var matrix = StatusColorRules.Matrix(
                colors is { Count: > 0 } ? colors : NoColors,
                _statusFrame, flash, StatusColorMaterial.IsBlackBackdrop(blend));
            StatusColorMaterial.Apply(material, matrix);
            layer.Material = material;
        }
    }

    private static readonly BuffColor[] NoColors = [new BuffColor { Red = 0, Green = 0, Blue = 0 }];

    private AnimatedSprite2D? BuildLayer(ActorClip clip, ActorClipLayer layer)
    {
        var frames = FramesFor(clip, layer);
        if (frames is null) return null;

        return new AnimatedSprite2D
        {
            SpriteFrames = frames,
            Animation = LayerAnimation,
            Centered = false,
            Modulate = StealthTint(_stealthed),
        };
    }

    private SpriteFrames? FramesFor(ActorClip clip, ActorClipLayer layer)
    {
        if (_lease is null) return null;

        var loop = LoopFor(_animation);
        var source = string.IsNullOrEmpty(layer.File) ? layer.Key : layer.File;
        var key = $"{source}|{clip.Frames}|{clip.FrameRate}|{loop}";
        if (_frameCache.TryGetValue(key, out var cached)) return cached;

        Texture2D sheet;
        try
        {
            sheet = string.IsNullOrEmpty(layer.File)
                ? ComposedTextures.Find(layer.Key) ?? throw new InvalidOperationException("合成贴图不在登记表里")
                : _lease.Texture(layer.File);
        }
        catch (Exception error)
        {
            GD.PushWarning($"[character] 贴图缺失：{layer.Key} — {error.Message}");
            return null;
        }

        var frames = new SpriteFrames();
        if (frames.HasAnimation("default")) frames.RemoveAnimation("default");
        frames.AddAnimation(LayerAnimation);
        frames.SetAnimationSpeed(LayerAnimation, clip.FrameRate);
        frames.SetAnimationLoop(LayerAnimation, loop);

        for (var index = 0; index < clip.Frames; index++)
            frames.AddFrame(LayerAnimation, new AtlasTexture
            {
                Atlas = sheet,
                Region = new Rect2(index * clip.FrameWidth, 0, clip.FrameWidth, clip.FrameHeight),
            });

        _frameCache[key] = frames;
        return frames;
    }

    private bool LoopFor(string name)
    {
        if (_loopOverrides.TryGetValue(name, out var overridden)) return overridden;
        if (name.StartsWith("attack-", StringComparison.Ordinal)) return false;
        if (name.StartsWith("skill-", StringComparison.Ordinal)) return false;
        if (name.StartsWith("die-", StringComparison.Ordinal)) return false;
        return !StoryPoses.IsPose(name);
    }

    ///
    /// 循环是引擎自己在推的，<see cref="_frame"/>／<see cref="_progress"/> 只在被显式赋值时
    /// 更新，所以这里必须先读画面此刻停在哪一帧——否则转身会跳回字段里那个旧帧号，
    /// 走路循环从中间倒回去重播一次。
    /// </summary>
    private int CarryFrame(string name, ActorClip clip)
    {
        if (!_animation.StartsWith("walk-", StringComparison.Ordinal)) return 0;
        if (!name.StartsWith("walk-", StringComparison.Ordinal)) return 0;
        if (clip.Frames <= 0) return 0;

        if (_layers.Count > 0)
        {
            var live = _layers[0];
            if (live.SpriteFrames.GetFrameCount(LayerAnimation) > 0)
            {
                _frame = live.Frame;
                _progress = live.FrameProgress;
            }
        }

        return _frame % clip.Frames;
    }

    private void ApplyPlayback()
    {
        foreach (var layer in _layers)
        {
            if (_playing)
            {
                layer.SpeedScale = _speed;
                layer.Play(LayerAnimation);
            }
            else layer.Pause();
        }

        foreach (var layer in _layers)
        {
            var count = layer.SpriteFrames.GetFrameCount(LayerAnimation);
            if (count <= 0) continue;
            layer.SetFrameAndProgress(Math.Clamp(_frame, 0, count - 1), _progress);
        }
    }

    private void ApplyFrame()
    {
        foreach (var layer in _layers)
        {
            var count = layer.SpriteFrames.GetFrameCount(LayerAnimation);
            if (count <= 0) continue;
            layer.Frame = Math.Clamp(_frame, 0, count - 1);
        }
    }

    private void OnLayerFinished() => AnimationFinished?.Invoke();

    private static Color StealthTint(bool stealthed) =>
        stealthed ? new Color(1f, 1f, 1f, 0.5f) : Colors.White;

    public static Vector2 FootOffset(ActorDefinition actor, string animation) =>
        actor.Clips.TryGetValue(animation, out var clip)
            ? new Vector2(-clip.OriginX * clip.FrameWidth, -clip.OriginY * clip.FrameHeight)
            : Vector2.Zero;
}
