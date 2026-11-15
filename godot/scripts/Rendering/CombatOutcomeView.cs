using System;
using System.Collections.Generic;
using Godot;
using Inotia4.Application;
using Inotia4.Domain.Content;
using Inotia4.Domain.World;
using Inotia4.Scenes;

namespace Inotia4.Rendering;

public sealed class CombatOutcomeView(
    Node2D root,
    Func<BattleContent> catalog,
    TextureLease textures)
{
    public const int EnemyDefeatHoldMs = 60;

    public const int EnemyDefeatFadeMs = 600;

    private const int AboveCharactersZIndex = 1001;

    public static readonly string[] EffectNames =
        ["energy-column", "enemy-vanish", "level-up-aura", "level-up-light", "level-up-burst"];

    private readonly List<EffectSprite> _effects = [];

    private readonly Dictionary<string, Tween> _deaths = [];

    private bool _paused;

    private bool _visible = true;

    public int ActiveEffects => _effects.Count;

    public int PendingDeaths => _deaths.Count;

    public void Vanish(string key, ActorNode character, Action? complete = null)
    {
        StopCharacterAnimation(character);
        Spawn("enemy-vanish", new Vec2(character.Position.X, character.Position.Y), attached: null, aboveAll: true);

        var tween = root.CreateTween();
        if (EnemyDefeatHoldMs > 0) tween.TweenInterval(EnemyDefeatHoldMs / 1000f);
        tween.TweenMethod(Callable.From<float>(value =>
        {
            if (!GodotObject.IsInstanceValid(character)) return;
            character.Modulate = new Color(1f, 1f, 1f, MathF.Floor(value * 10f) / 10f);
        }), 1f, 0f, EnemyDefeatFadeMs / 1000f);
        tween.TweenCallback(Callable.From(() =>
        {
            _deaths.Remove(key);
            if (GodotObject.IsInstanceValid(character)) character.Visible = false;
            complete?.Invoke();
        }));

        if (_paused) tween.Pause();
        _deaths[key] = tween;
    }

    public void CancelDeath(string key)
    {
        if (_deaths.Remove(key, out var tween)) tween.Kill();
    }

    public void LevelUp(ActorNode character)
    {
        if (character.Definition.EffectAnchor is not { } anchor)
        {
            GD.PushWarning($"[outcome] 人物缺少升级特效锚点：{character.Definition.Id}");
            return;
        }

        var position = new Vec2(character.Position.X, character.Position.Y);
        Spawn("level-up-aura", new Vec2(anchor.X, anchor.Y), character, aboveAll: false);
        Spawn("level-up-light", new Vec2(anchor.X, anchor.Y), character, aboveAll: false);
        Spawn("level-up-burst", position, attached: null, aboveAll: true);
    }

    public void Recover(ActorNode character)
    {
        if (character.Definition.EffectAnchor is not { } anchor)
        {
            GD.PushWarning($"[outcome] 人物缺少恢复特效锚点：{character.Definition.Id}");
            return;
        }

        Spawn("energy-column", new Vec2(anchor.X, anchor.Y), character, aboveAll: false);
    }

    public void SetPaused(bool paused)
    {
        if (paused == _paused) return;
        _paused = paused;

        foreach (var tween in _deaths.Values)
        {
            if (paused) tween.Pause();
            else tween.Play();
        }

        foreach (var effect in _effects)
            if (GodotObject.IsInstanceValid(effect)) effect.SpeedScale = paused ? 0f : 1f;
    }

    public void SetVisible(bool visible)
    {
        if (visible == _visible) return;
        _visible = visible;

        foreach (var effect in _effects)
            if (GodotObject.IsInstanceValid(effect)) effect.Visible = visible;
    }

    public void Clear()
    {
        foreach (var tween in _deaths.Values) tween.Kill();
        _deaths.Clear();

        foreach (var effect in _effects)
            if (GodotObject.IsInstanceValid(effect)) effect.QueueFree();
        _effects.Clear();
    }

    private void Spawn(string name, Vec2 position, ActorNode? attached, bool aboveAll)
    {
        var clip = catalog().EffectClip(name, "default");
        if (clip is null)
        {
            GD.PushWarning($"[outcome] 表现素材缺失：{name}");
            return;
        }

        var sprite = EffectSprite.Create(clip, textures, position.X, position.Y);
        if (sprite is null) return;

        if (attached is not null)
        {
            sprite.ZIndex = 1;
            sprite.Position = new Vector2(position.X, position.Y);
            attached.AddChild(sprite);
        }
        else
        {
            if (aboveAll) sprite.ZIndex = AboveCharactersZIndex;
            root.AddChild(sprite);
        }

        sprite.Visible = _visible;
        sprite.SpeedScale = _paused ? 0f : 1f;
        sprite.TreeExited += () => _effects.Remove(sprite);
        _effects.Add(sprite);
    }

    private static void StopCharacterAnimation(ActorNode character)
    {
        if (GodotObject.IsInstanceValid(character) && character.Visual is not null)
            character.Visual.Pause();
    }
}
