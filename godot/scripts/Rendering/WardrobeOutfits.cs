using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Inotia4.Application;
using Inotia4.Domain.Character;
using Inotia4.Domain.Content;

namespace Inotia4.Rendering;

public sealed class WardrobeOutfits
{
    public sealed record Outfit(ActorDefinition Actor, IReadOnlyList<string> TextureKeys)
    {
        public int Holders;
    }

    public sealed class Channel
    {
        public Outfit? Current { get; internal set; }

        internal readonly List<string> Order = [];
    }

    private static bool IsImmediatePose(string name) =>
        name.StartsWith("idle-", StringComparison.Ordinal) ||
        name.StartsWith("walk-", StringComparison.Ordinal);

    private const int CacheLimit = 3;

    private readonly WardrobeCatalog _catalog;
    private readonly ItemCatalog _items;
    private readonly TextureLease _lease;
    private readonly Dictionary<string, string> _files = new();
    private readonly Dictionary<string, Outfit> _cache = new();
    private readonly Dictionary<string, Channel> _channels = new();
    private int _serial;

    private readonly List<Outfit> _retired = [];

    public WardrobeOutfits(WardrobeCatalog catalog, ItemCatalog items, TextureLease lease)
    {
        _catalog = catalog;
        _items = items;
        _lease = lease;
        foreach (var texture in catalog.Textures) _files[texture.Key] = texture.File;
    }

    public Channel ChannelFor(string key)
    {
        if (!_channels.TryGetValue(key, out var channel))
        {
            channel = new Channel();
            _channels[key] = channel;
        }
        return channel;
    }

    public Outfit Prepare(ActorDefinition baseActor, IReadOnlyDictionary<string, string> equipment,
        string? classId = null, string? appearanceId = null, Channel? channel = null)
    {
        ReleaseRetired();

        var recipe = WardrobeRules.CostumeRecipe(_catalog, _items, equipment, classId, appearanceId);
        var signature = Signature(baseActor.Id, recipe);
        if (_cache.TryGetValue(signature, out var cached))
        {
            Touch(channel, signature);
            return cached;
        }

        var clips = new Dictionary<string, ActorClip>();
        var keys = new List<string>();

        foreach (var (name, poseId) in recipe.Poses)
        {
            if (poseId is null) continue;

            var key = $"outfit-{++_serial}";
            if (IsImmediatePose(name))
            {
                var composed = CostumeComposer.Compose(_catalog, recipe, poseId, Atlas, key);
                if (composed is null) continue;

                ComposedTextures.Register(key, composed.Texture);
                clips[name] = composed.Clip;
                keys.Add(key);
                continue;
            }

            // 其余姿态只登记形状，像素等第一次播到再合（实测一段 3–7 ms，看不出来）。
            // 片段本身必须是齐的：动作名查不到就会退回待机，走路／进攻的动画会静默消失。
            var lazyClip = CostumeComposer.Measure(_catalog, recipe, poseId, key);
            if (lazyClip is null) continue;

            var lazyPose = poseId;
            ComposedTextures.RegisterLazy(key, () => CostumeComposer.Render(_catalog, recipe, lazyPose, key, Atlas));
            clips[name] = lazyClip;
            keys.Add(key);
        }

        var merged = new Dictionary<string, ActorClip>(baseActor.Clips);
        foreach (var (name, clip) in clips) merged[name] = clip;

        var actor = new ActorDefinition
        {
            Id = baseActor.Id,
            Name = baseActor.Name,
            Portrait = baseActor.Portrait,
            Description = baseActor.Description,
            Service = baseActor.Service,
            Chest = baseActor.Chest,
            Collision = baseActor.Collision,
            EffectAnchor = baseActor.EffectAnchor,
            HeadAnchor = baseActor.HeadAnchor,
            Clips = merged,
        };

        var outfit = new Outfit(actor, keys);
        _cache[signature] = outfit;

        if (channel is not null)
        {
            channel.Order.Add(signature);
            Activate(channel, outfit);
            Evict(channel);
        }

        return outfit;
    }

    public void Activate(Channel channel, Outfit outfit)
    {
        if (ReferenceEquals(channel.Current, outfit)) return;
        if (channel.Current is not null) channel.Current.Holders = Math.Max(0, channel.Current.Holders - 1);
        outfit.Holders++;
        channel.Current = outfit;
    }

    private void Evict(Channel channel)
    {
        while (channel.Order.Count > CacheLimit)
        {
            var oldest = channel.Order.FirstOrDefault(signature =>
                _cache.TryGetValue(signature, out var outfit) && outfit.Holders <= 0);
            if (oldest is null) break;

            if (_cache.TryGetValue(oldest, out var retired))
            {
                foreach (var key in retired.TextureKeys) ComposedTextures.Retire(key);
                _retired.Add(retired);
            }

            foreach (var other in _channels.Values) other.Order.Remove(oldest);
            _cache.Remove(oldest);
        }
    }

    private static void Touch(Channel? channel, string signature)
    {
        if (channel is null) return;
        channel.Order.Remove(signature);
        channel.Order.Add(signature);
    }

    private void ReleaseRetired()
    {
        foreach (var outfit in _retired)
            foreach (var key in outfit.TextureKeys) ComposedTextures.Destroy(key);
        _retired.Clear();
    }

    public void Dispose()
    {
        foreach (var outfit in _cache.Values)
            foreach (var key in outfit.TextureKeys) ComposedTextures.Remove(key);
        foreach (var outfit in _retired)
            foreach (var key in outfit.TextureKeys) ComposedTextures.Destroy(key);
        _cache.Clear();
        _retired.Clear();
        _channels.Clear();
    }

    private Image? Atlas(string key)
    {
        if (!_files.TryGetValue(key, out var file)) return null;
        try
        {
            return _lease.Image(file);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Signature(string actorId, CostumeRecipe recipe)
    {
        var poses = string.Join(",", recipe.Poses.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}"));
        var variants = string.Join(",", recipe.Variants.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}"));
        return $"{actorId}|{poses}|{variants}";
    }
}
