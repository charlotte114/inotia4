using System;
using System.Collections.Generic;
using Godot;
using Inotia4.Application;
using Inotia4.Domain.Content;
using Inotia4.Domain.Effects;

namespace Inotia4.Rendering;

public partial class EffectSprite : Node2D
{
    private readonly List<AnimatedSprite2D> _layers = [];

    public static float TextureScale { get; set; }

    public static float ScaleFor(EffectClip clip)
    {
        if (TextureScale > 0f) return TextureScale;

        var sheetWidth = clip.Frames * clip.FrameWidth;
        return clip.FrameWidth >= 256 || sheetWidth >= 4096 ? 0.5f : 1f;
    }

    private float _lifetimeMs;

    private float _elapsedMs;

    private bool _looping;

    public bool DestroyOnFinish { get; init; } = true;

    public static EffectSprite? Create(EffectClip clip, TextureLease textures, float x, float y,
        bool loop = false, bool destroyOnFinish = true)
    {
        if (clip.Layers.Length == 0 || clip.Frames <= 0 || clip.FrameWidth <= 0 || clip.FrameHeight <= 0)
            return null;

        var sprite = new EffectSprite { Position = new Vector2(x, y), ZIndex = (int)y + 1, _looping = loop,
            DestroyOnFinish = destroyOnFinish };

        foreach (var layer in clip.Layers)
        {
            var draw = EffectColors.LayerDrawOf(layer);
            var blend = draw.IsBackdrop ? EffectColors.BackdropApproximation(draw.Operations) : draw.Blend!;
            var animated = BuildLayer(clip, layer, textures, blend, loop);
            if (animated is null) continue;
            sprite.AddChild(animated);
            sprite._layers.Add(animated);
        }

        if (sprite._layers.Count == 0)
        {
            sprite.QueueFree();
            return null;
        }

        sprite._lifetimeMs = loop ? 0f : clip.Frames / MathF.Max(1f, clip.FrameRate) * 1000f;
        sprite._looping = loop;
        return sprite;
    }

    private static AnimatedSprite2D? BuildLayer(EffectClip clip, EffectLayer layer, TextureLease textures, string blend, bool loop)
    {
        if (string.IsNullOrEmpty(layer.File)) return null;

        // 条带整体缩放（--texscale）：贴图按 scale 缩小，帧矩形跟着缩，节点再放大 1/scale，
        // 于是屏幕上的大小一像素不差，只有采样密度降下来——显存与带宽省的是 scale²。
        // 帧宽必须一起缩：区域是按 content 里的 frameWidth 切的，只缩图会切错。
        var scale = ScaleFor(clip);
        var frameWidth = Mathf.Max(1, Mathf.RoundToInt(clip.FrameWidth * scale));
        var frameHeight = Mathf.Max(1, Mathf.RoundToInt(clip.FrameHeight * scale));
        var stretch = (float)clip.FrameWidth / frameWidth;

        Texture2D sheet;
        try
        {
            sheet = textures.Texture(layer.File, scale);
        }
        catch (Exception error)
        {
            GD.PushWarning($"[effect] 贴图缺失：{layer.Key} — {error.Message}");
            return null;
        }

        var frames = new SpriteFrames();
        if (frames.HasAnimation("default")) frames.RemoveAnimation("default");
        frames.AddAnimation("default");
        frames.SetAnimationSpeed("default", clip.FrameRate);
        frames.SetAnimationLoop("default", loop);

        for (var index = 0; index < clip.Frames; index++)
        {
            frames.AddFrame("default", new AtlasTexture
            {
                Atlas = sheet,
                Region = new Rect2(index * frameWidth, 0, frameWidth, frameHeight),
            });
        }

        var offset = new Vector2(-clip.OriginX * frameWidth, -clip.OriginY * frameHeight);
        if (clip.MirrorX) offset = new Vector2(-(1f - clip.OriginX) * frameWidth, offset.Y);

        var sprite = new AnimatedSprite2D
        {
            SpriteFrames = frames,
            Animation = "default",
            Centered = false,
            Offset = offset,
            Scale = new Vector2(stretch, stretch),
            FlipH = clip.MirrorX,
            Material = MaterialFor(blend),
        };

        sprite.Play("default");
        return sprite;
    }

    private static Material? MaterialFor(string blend) => BlendMaterials.For(blend, transient: true);

    public override void _Process(double delta)
    {
        if (_speed <= 0f) return;
        if (_looping) return;

        if (_sequence is not null) StepSequence(delta);

        _elapsedMs += (float)delta * 1000f;
        if (_elapsedMs < _lifetimeMs) return;

        if (DestroyOnFinish) QueueFree();
        else foreach (var layer in _layers) layer.Pause();
    }

    private float _speed = 1f;

    public float SpeedScale
    {
        get => _speed;
        set
        {
            if (MathF.Abs(_speed - value) < 0.0001f) return;
            _speed = value;
            foreach (var layer in _layers) layer.SpeedScale = value;
        }
    }

    public bool Playing => !_looping && _elapsedMs < _lifetimeMs;

    public int LayerCount => _layers.Count;

    public EffectSprite StartAt(int frame)
    {
        foreach (var layer in _layers)
        {
            var count = layer.SpriteFrames.GetFrameCount("default");
            if (count > 0) layer.Frame = Math.Clamp(frame, 0, count - 1);
        }
        return this;
    }

    private IReadOnlyList<int>? _sequence;
    private int _sequenceStep;
    private float _sequenceAccumMs;
    private float _delayRemainingMs;
    private float _frameDurationMs;

    public int SequenceIndex => _sequenceStep;

    public bool SequenceStarted => _sequence is not null && _delayRemainingMs <= 0f;

    public static EffectSprite? CreateSequenced(EffectClip clip, TextureLease textures, float x, float y,
        IReadOnlyList<int> sequence, int delayMs)
    {
        if (sequence.Count == 0) return null;

        var sprite = Create(clip, textures, x, y, loop: true);
        if (sprite is null) return null;

        sprite._looping = false;
        sprite._sequence = sequence;
        sprite._sequenceStep = 0;
        sprite._sequenceAccumMs = 0f;
        sprite._delayRemainingMs = delayMs;
        sprite._frameDurationMs = 1000f / MathF.Max(1f, clip.FrameRate);
        sprite._lifetimeMs = delayMs + sequence.Count * sprite._frameDurationMs;

        foreach (var layer in sprite._layers)
        {
            layer.Pause();
            layer.Frame = sequence[0];
            layer.Visible = delayMs <= 0;
        }

        return sprite;
    }

    private void StepSequence(double delta)
    {
        var milliseconds = (float)delta * 1000f;

        if (_delayRemainingMs > 0f)
        {
            _delayRemainingMs -= milliseconds;
            if (_delayRemainingMs > 0f) return;
            milliseconds = -_delayRemainingMs;
            _delayRemainingMs = 0f;
            foreach (var layer in _layers) layer.Visible = true;
        }

        _sequenceAccumMs += milliseconds;
        while (_sequenceAccumMs >= _frameDurationMs && _sequenceStep < _sequence!.Count - 1)
        {
            _sequenceAccumMs -= _frameDurationMs;
            _sequenceStep += 1;
            foreach (var layer in _layers) layer.Frame = _sequence[_sequenceStep];
        }
    }
}
