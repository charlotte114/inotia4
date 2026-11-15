using System;
using System.Collections.Generic;
using Godot;
using Inotia4.Domain;
using Inotia4.Domain.Content;
using Inotia4.Domain.World;
using Inotia4.Domain.Ui;

namespace Inotia4.Rendering;

public partial class PickupMessages : Node2D
{
    private sealed class Active
    {
        public Node2D Root = null!;
        public string RecipientId = "";
        public int Row;
        public int ElapsedMs;
    }

    private readonly List<(Drops.DropReward Reward, string RecipientId)> _pending = new();
    private readonly List<Active> _active = new();

    private Func<string, (Vec2 Position, float HeadX, float HeadY)?> _anchor = null!;
    private Func<string, Texture2D?> _texture = null!;
    private ItemCatalog _items = null!;
    private float _renderScale = 1f;
    private Func<(float Left, float Width)> _view = null!;

    private long _nextAllowedMs;
    private bool _paused;

    public PickupMessages AnchorOf(Func<string, (Vec2 Position, float HeadX, float HeadY)?> anchor)
    {
        _anchor = anchor;
        return this;
    }

    public PickupMessages TextureOf(Func<string, Texture2D?> texture)
    {
        _texture = texture;
        return this;
    }

    public PickupMessages Items(ItemCatalog items)
    {
        _items = items;
        return this;
    }

    public PickupMessages Presentation(float renderScale, Func<(float Left, float Width)> view)
    {
        _renderScale = renderScale;
        _view = view;
        return this;
    }

    public void Enqueue(IReadOnlyList<Drops.DropReward> rewards, string recipientId)
    {
        foreach (var reward in rewards)
        {
            if (_pending.Count + _active.Count >= PickupMessageRules.Limit) break;
            _pending.Add((reward, recipientId));
        }
    }

    public int ActiveCount => _active.Count;

    public int PendingCount => _pending.Count;

    public void SetPaused(bool paused)
    {
        if (_paused == paused) return;
        _paused = paused;
        foreach (var entry in _active) entry.Root.Visible = !paused;
    }

    public void Clear()
    {
        _pending.Clear();
        foreach (var entry in _active) entry.Root.QueueFree();
        _active.Clear();
        _nextAllowedMs = 0;
    }

    public void Tick(long nowMs, int deltaMs)
    {
        if (_paused) return;

        while (_pending.Count > 0 && nowMs >= _nextAllowedMs)
        {
            var (reward, recipient) = _pending[0];
            _pending.RemoveAt(0);
            if (_anchor(recipient) is not { } anchor) continue;
            Show(reward, recipient, anchor);
            _nextAllowedMs = nowMs + PickupMessageRules.SpacingMs;
        }

        var view = _view();
        for (var i = _active.Count - 1; i >= 0; i--)
        {
            var entry = _active[i];
            entry.ElapsedMs += deltaMs;
            if (entry.ElapsedMs >= PickupMessageRules.TotalMs)
            {
                entry.Root.QueueFree();
                _active.RemoveAt(i);
                continue;
            }
            if (_anchor(entry.RecipientId) is not { } anchor)
            {
                entry.Root.QueueFree();
                _active.RemoveAt(i);
                continue;
            }
            var lift = PickupMessageRules.LiftAt(entry.ElapsedMs);
            entry.Root.Position = new Vector2(
                PickupMessageRules.AnchorX(anchor.Position.X, view.Left, view.Width, anchor.HeadX),
                PickupMessageRules.AnchorY(anchor.Position.Y, anchor.HeadY, entry.Row, lift));
        }
    }

    private void Show(Drops.DropReward reward, string recipientId, (Vec2 Position, float HeadX, float HeadY) anchor)
    {
        var body = new Node2D { Scale = new Vector2(_renderScale, _renderScale) };
        var root = new Node2D { Name = "PickupMessage", ZIndex = 1500 };
        root.AddChild(body);
        AddChild(root);

        switch (reward)
        {
            case Drops.DropReward.MoneyDrop money:
                LayoutMoney(body, money.Amount);
                break;
            case Drops.DropReward.ItemDrop item:
                LayoutText(body, ItemName(item.Item), PickupMessageRules.ItemNameColor(item.Roll?.Rarity));
                break;
        }

        var occupied = new List<int>();
        foreach (var entry in _active) occupied.Add(entry.Row);
        _active.Add(new Active { Root = root, RecipientId = recipientId, Row = PickupMessageRules.RowFor(occupied) });
    }

    private void LayoutMoney(Node2D body, int amount)
    {
        var parts = MoneyDisplay.SplitMoney(amount);
        float x = 0;
        foreach (var (unit, value, file) in new[]
                 {
                     ("gold", parts.Gold, "assets/ui/items/pickup-money-gold.png"),
                     ("silver", parts.Silver, "assets/ui/items/pickup-money-silver.png"),
                     ("copper", parts.Copper, "assets/ui/items/pickup-money-copper.png"),
                 })
        {
            if (value <= 0) continue;
            var text = AddText(body, value.ToString(), x, null);
            if (_texture(file) is { } icon)
            {
                var sprite = new Sprite2D
                {
                    Texture = icon,
                    Centered = false,
                    Position = new Vector2(x + text + 2f, -2f),
                };
                body.AddChild(sprite);
            }
            x += text + 36f;
        }
    }

    private void LayoutText(Node2D body, string text, string? color)
    {
        AddText(body, text, 0, color);
    }

    private float AddText(Node2D body, string text, float x, string? color)
    {
        var label = new Label
        {
            Position = new Vector2(x, 0),
            Text = text,
        };
        var settings = new LabelSettings
        {
            FontSize = 20,
            FontColor = color is null ? Colors.White : new Color(color),
            OutlineSize = 2,
            OutlineColor = Colors.Black,
        };
        label.LabelSettings = settings;
        body.AddChild(label);
        return label.GetMinimumSize().X;
    }

    private string ItemName(string itemId) =>
        _items.Items.GetValueOrDefault(itemId)?.Name ?? itemId;
}
