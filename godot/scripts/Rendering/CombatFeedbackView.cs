using System;
using System.Collections.Generic;
using Godot;
using Inotia4.Application;
using Inotia4.Domain.Combat;
using Inotia4.Domain.Content;
using Inotia4.Scenes;

namespace Inotia4.Rendering;

public sealed class CombatFeedbackView(
    Node2D root,
    Func<BattleContent> catalog,
    TextureLease textures,
    Camera2D camera)
{
    private const int DigitSlots = 32;

    private readonly EffectSprite?[] _digits = new EffectSprite?[DigitSlots];
    private readonly List<EffectSprite> _effects = [];

    private Tween? _shake;
    private Vector2 _shakeBase;

    public int DrawnDigits { get; private set; }

    public void Show(CombatHitFeedback feedback, ActorNode target, CombatFeedbackSelection selection,
        FeedbackPlan plan)
    {
        foreach (var impact in selection.Impacts) DrawImpact(impact, target);
        if (selection.DigitEffect is { } effect) DrawDigits(feedback.Hit, effect, target);
        if (plan.Shake) StartShake();
    }

    private void DrawImpact(FeedbackImpact impact, ActorNode target)
    {
        var clip = catalog().EffectClip(impact.Name, "default");
        if (clip is null)
        {
            GD.PushWarning($"[effect] 命中反馈素材缺失：{impact.Name}");
            return;
        }

        if (impact.Attached)
        {
            var anchor = target.Definition.EffectAnchor;
            var attached = EffectSprite.Create(clip, textures, anchor?.X ?? 0f, anchor?.Y ?? 0f);
            if (attached is null) return;
            attached.ZIndex = 1;   // 压在角色自己那几层之上
            target.AddChild(attached);
            Track(attached);
            return;
        }

        var sprite = EffectSprite.Create(clip, textures, target.Position.X, target.Position.Y);
        if (sprite is null) return;
        sprite.ZIndex = AboveCharactersZIndex;
        root.AddChild(sprite);
        Track(sprite);
    }

    private void DrawDigits(HitResult hit, string effect, ActorNode target)
    {
        foreach (var spec in CombatFeedback.DamageDigits(hit.Damage, hit.Kind == HitKind.Miss))
        {
            var slot = Array.IndexOf(_digits, null);
            if (slot < 0) slot = CombatFeedback.ReusableDigit(Progress());
            if (slot < 0) return;   // 32 位都在起步阶段，这一下不画

            var clip = catalog().EffectClip(effect, spec.Digit.ToString());
            if (clip is null)
            {
                GD.PushWarning($"[effect] 伤害数字片段缺失：{effect}[{spec.Digit}]");
                return;
            }

            _digits[slot]?.QueueFree();
            _digits[slot] = null;

            var sequence = CombatFeedback.DigitSequence(clip.Frames, spec.HoldFrames);
            var sprite = EffectSprite.CreateSequenced(clip, textures,
                target.Position.X + spec.X, target.Position.Y + spec.Y, sequence, spec.DelayMs);
            if (sprite is null) return;

            sprite.ZIndex = AboveCharactersZIndex;
            root.AddChild(sprite);
            _digits[slot] = sprite;
            var digitSlot = slot;
            sprite.TreeExited += () =>
            {
                if (ReferenceEquals(_digits[digitSlot], sprite)) _digits[digitSlot] = null;
            };
            DrawnDigits += 1;
            Track(sprite);
        }
    }

    private int[] Progress()
    {
        var progress = new int[DigitSlots];
        for (var index = 0; index < DigitSlots; index += 1)
            progress[index] = _digits[index] is { SequenceStarted: true } digit ? digit.SequenceIndex : -1;

        return progress;
    }

    public void StartShake()
    {
        var offsets = catalog().Feedback?.ShakeOffsets;
        if (offsets is null || offsets.Length == 0) return;

        CancelShake();
        _shakeBase = camera.Offset;

        _shake = camera.CreateTween();
        _shake.TweenMethod(Callable.From((float step) =>
        {
            var offset = offsets[Math.Min(offsets.Length - 1, (int)MathF.Floor(step))];
            camera.Offset = _shakeBase - new Vector2(offset.X, offset.Y);
        }), 0f, offsets.Length, offsets.Length * 0.06);

        _shake.TweenCallback(Callable.From(CancelShake));
    }

    private bool _paused;

    public void SetPaused(bool paused)
    {
        if (_paused == paused) return;
        _paused = paused;
        if (_shake is not { } shake) return;
        if (paused) shake.Pause();
        else shake.Play();
    }

    public void CancelShake()
    {
        if (_shake is { } shake)
        {
            _shake = null;
            shake.Kill();
        }

        camera.Offset = _shakeBase;
    }

    public void Clear()
    {
        CancelShake();

        foreach (var effect in _effects)
            if (GodotObject.IsInstanceValid(effect)) effect.QueueFree();
        _effects.Clear();

        Array.Clear(_digits);
        DrawnDigits = 0;
    }

    private void Track(EffectSprite effect)
    {
        _effects.Add(effect);
        effect.TreeExited += () => _effects.Remove(effect);
    }

    /// <summary>
    /// 「压在人物之上」的层号。与场景特效同一套口径：地图三档里取比树冠高一档。
    /// Godot 的 CanvasItem 层号上限是 4096，不能随手写个大数。
    /// </summary>
    private const int AboveCharactersZIndex = 1001;
}
