using System;
using System.Collections.Generic;
using Godot;
using Inotia4.Application;
using Inotia4.Domain.Content;

namespace Inotia4.Rendering;

public static class MapLayerRenderer
{
    private const int GroundZIndex = -1000;

    private const int CanopyZIndex = 1000;

    public static Node2D Build(
        MapDefinition map, IReadOnlyDictionary<string, string> propFiles, TextureLease textures, Node2D parent)
    {
        var groundImage = textures.Image(map.Ground);

        var ground = new Sprite2D
        {
            Name = "Ground",
            Texture = textures.Texture(map.Ground),
            Centered = false,
            ZIndex = GroundZIndex,
        };
        parent.AddChild(ground);

        var entities = new Node2D { Name = "Entities", YSortEnabled = true };
        parent.AddChild(entities);

        var missing = new HashSet<string>();

        foreach (var feature in map.Features)
        {
            if (!propFiles.TryGetValue(feature.Texture, out var file))
            {
                if (missing.Add(feature.Texture))
                    GD.PushWarning($"景物 {feature.Texture} 不在 world.json 的 props 清单里，已跳过。");
                continue;
            }

            entities.AddChild(CreateFeature(feature, file, textures, groundImage));
        }

        var canopy = new Sprite2D
        {
            Name = "Canopy",
            Texture = textures.Texture(map.Canopy),
            Centered = false,
            ZIndex = CanopyZIndex,
        };
        parent.AddChild(canopy);

        return entities;
    }

    private static Sprite2D CreateFeature(
        MapFeature feature, string propFile, TextureLease textures, Image groundImage)
    {
        var left = feature.X - (feature.Flip ? feature.Width - feature.OffsetX : feature.OffsetX);

        var sprite = new Sprite2D
        {
            Centered = false,
            Position = new Vector2(left, feature.Y),
            Offset = new Vector2(0f, -feature.OffsetY),
        };

        if (feature.Blend == "color-dodge")
        {
            var propImage = textures.Image(propFile);
            var width = Math.Max(1, (int)MathF.Round(feature.Width));
            sprite.Texture = DodgeComposer.Compose(
                groundImage, propImage, (int)left, (int)(feature.Y - feature.OffsetY),
                width, propImage.GetHeight(), feature.Flip);
            return sprite;
        }

        sprite.Texture = textures.Texture(propFile);
        sprite.FlipH = feature.Flip;
        ApplyBlend(sprite, feature.Blend);
        return sprite;
    }

    private static void ApplyBlend(Sprite2D sprite, string blend)
    {
        sprite.Material = BlendMaterials.For(blend);
    }
}
