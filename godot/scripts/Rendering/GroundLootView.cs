using System;
using System.Collections.Generic;
using Godot;
using Inotia4.Domain;
using Inotia4.Domain.Combat;
using Inotia4.Domain.Content;

namespace Inotia4.Rendering;

public partial class GroundLootView : Node2D
{
    private static readonly GroundItemSprite MoneySprite = new()
    {
        File = "assets/ui/items/money-ground.png",
        Width = 16f,
        Height = 12f,
        AnchorX = 7f,
        AnchorY = 4f,
    };

    private readonly Dictionary<string, Node2D> _visuals = new();
    private readonly HashSet<string> _flying = [];

    private Func<string, Texture2D?> _texture = null!;
    private ItemCatalog _items = null!;
    private Func<EffectClip, float, float, int, Node2D?> _flight = null!;

    public GroundLootView TextureOf(Func<string, Texture2D?> texture)
    {
        _texture = texture;
        return this;
    }

    public GroundLootView Items(ItemCatalog items)
    {
        _items = items;
        return this;
    }

    public GroundLootView FlightEffectOf(Func<EffectClip, float, float, int, Node2D?> flight)
    {
        _flight = flight;
        return this;
    }

    public void Sync(IReadOnlyList<GroundLootStore.GroundDrop> drops)
    {
        var wanted = new HashSet<string>();
        foreach (var drop in drops) wanted.Add(drop.Id);

        foreach (var (id, visual) in _visuals)
            if (!wanted.Contains(id))
            {
                visual.QueueFree();
                _visuals.Remove(id);
                _flying.Remove(id);
            }

        foreach (var drop in drops)
        {
            var old = _visuals.GetValueOrDefault(drop.Id);
            if (old is not null && _flying.Contains(drop.Id) == (drop.Flight is not null)) continue;
            old?.QueueFree();
            _visuals.Remove(drop.Id);
            _flying.Remove(drop.Id);

            if (drop.Flight is { } flight && drop.Reward is Drops.DropReward.ItemDrop flyingItem &&
                _items.Items.GetValueOrDefault(flyingItem.Item)?.Pickup.AsManaPickup() is { } pickup &&
                flight.Direction >= 0 && flight.Direction < pickup.HitDrop.Directions.Count)
            {
                var clip = pickup.HitDrop.Directions[flight.Direction].Clip;
                var frame = (int)MathF.Min(clip.Frames - 1,
                    MathF.Floor((flight.DurationMs - flight.RemainingMs) * clip.FrameRate / 1000f));
                if (_flight(clip, flight.X, flight.Y, Math.Max(0, frame)) is not { } node) continue;
                AddChild(node);
                _visuals[drop.Id] = node;
                _flying.Add(drop.Id);
                continue;
            }

            var sprite = drop.Reward switch
            {
                Drops.DropReward.MoneyDrop => MoneySprite,
                Drops.DropReward.ItemDrop item => _items.Items.GetValueOrDefault(item.Item)?.GroundSprite,
                _ => null,
            };
            if (sprite is null) continue;
            if (_texture(sprite.File) is not { } source) continue;

            var visual = new Sprite2D
            {
                Name = drop.Id,
                Texture = source,
                Scale = sprite.Width > 0
                    ? new Vector2(sprite.Width / source.GetWidth(), sprite.Height / source.GetHeight())
                    : Vector2.One,
                Centered = false,
                Offset = new Vector2(-sprite.AnchorX, -sprite.AnchorY),
                Position = new Vector2(drop.X, drop.Y),
            };
            AddChild(visual);
            _visuals[drop.Id] = visual;
        }
    }

    public void SetPaused(bool paused)
    {
        foreach (var (id, visual) in _visuals)
            if (_flying.Contains(id) && visual is EffectSprite effect)
                effect.SpeedScale = paused ? 0f : 1f;
    }
}
