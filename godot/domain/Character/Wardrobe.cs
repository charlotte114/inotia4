using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Inotia4.Domain.Combat;
using Inotia4.Domain.Content;

namespace Inotia4.Domain.Character;

public sealed class CostumePiece
{
    [JsonPropertyName("texture")] public string Texture { get; init; } = string.Empty;

    [JsonPropertyName("x")] public float X { get; init; }

    [JsonPropertyName("y")] public float Y { get; init; }

    [JsonPropertyName("width")] public float Width { get; init; }

    [JsonPropertyName("height")] public float Height { get; init; }

    [JsonPropertyName("anchorX")] public float AnchorX { get; init; }

    [JsonPropertyName("anchorY")] public float AnchorY { get; init; }
}

public sealed class CostumePart
{
    [JsonPropertyName("part")] public string Part { get; init; } = string.Empty;

    [JsonPropertyName("piece")] public int Piece { get; init; }

    [JsonPropertyName("x")] public float X { get; init; }

    [JsonPropertyName("y")] public float Y { get; init; }

    [JsonPropertyName("scaleX")] public float ScaleX { get; init; }

    [JsonPropertyName("scaleY")] public float ScaleY { get; init; }

    [JsonPropertyName("angle")] public float Angle { get; init; }

    [JsonPropertyName("alpha")] public float Alpha { get; init; }
}

public sealed class CostumePose
{
    [JsonPropertyName("mirrorX")] public bool MirrorX { get; init; }

    [JsonPropertyName("frameRate")] public float FrameRate { get; init; }

    [JsonPropertyName("frames")] public List<List<CostumePart>> Frames { get; init; } = [];
}

public sealed class WardrobeCompanion
{
    [JsonPropertyName("variants")] public Dictionary<string, string> Variants { get; init; } = [];

    [JsonPropertyName("fixedParts")] public List<string> FixedParts { get; init; } = [];
}

public sealed class WardrobeSkillStance
{
    [JsonPropertyName("poses")] public Dictionary<string, string> Poses { get; init; } = [];

    [JsonPropertyName("actions")] public Dictionary<string, SkillAction?> Actions { get; init; } = [];
}

public sealed class WardrobeEquipmentAppearance
{
    [JsonPropertyName("main")] public Dictionary<string, string> Main { get; init; } = [];

    [JsonPropertyName("offHand")] public Dictionary<string, string> OffHand { get; init; } = [];
}

/// <summary>
/// 换装目录。
/// 它也持有「每对武器 → 普攻规则」的配方，所以战斗侧要用它取当前装备的普攻。
/// </summary>
public sealed class WardrobeCatalog
{
    [JsonPropertyName("defaultShield")] public bool DefaultShield { get; init; }

    [JsonPropertyName("companions")] public Dictionary<string, WardrobeCompanion>? Companions { get; init; }

    [JsonPropertyName("attacks")] public Dictionary<string, BasicAttackDefinition> Attacks { get; init; } = [];

    [JsonPropertyName("frenzyWaitFrames")] public Dictionary<string, int>? FrenzyWaitFrames { get; init; }

    [JsonPropertyName("frenzyAttacks")] public Dictionary<string, WardrobeAttack>? FrenzyAttacks { get; init; }

    [JsonPropertyName("dualWieldAttacks")] public Dictionary<string, WardrobeAttack>? DualWieldAttacks { get; init; }

    [JsonPropertyName("skills")] public Dictionary<string, Dictionary<string, string>> Skills { get; init; } = [];

    [JsonPropertyName("skillStances")] public Dictionary<string, Dictionary<string, WardrobeSkillStance>> SkillStances { get; init; } = [];

    [JsonPropertyName("textures")] public List<WardrobeTexture> Textures { get; init; } = [];

    [JsonPropertyName("sprites")] public Dictionary<string, CostumePiece> Sprites { get; init; } = [];

    [JsonPropertyName("variants")] public Dictionary<string, List<string?>> Variants { get; init; } = [];

    [JsonPropertyName("defaults")] public Dictionary<string, string> Defaults { get; init; } = [];

    [JsonPropertyName("equipment")] public Dictionary<string, WardrobeEquipmentAppearance> Equipment { get; init; } = [];

    [JsonPropertyName("poses")] public Dictionary<string, CostumePose> Poses { get; init; } = [];

    [JsonPropertyName("stances")] public Dictionary<string, Dictionary<string, string>> Stances { get; init; } = [];
}

public sealed class WardrobeTexture
{
    [JsonPropertyName("key")] public string Key { get; init; } = string.Empty;

    [JsonPropertyName("file")] public string File { get; init; } = string.Empty;
}

/// <summary>
/// 带四向片段的普攻规则：在普攻定义之外再给一组「动作名 → 片段」。
/// 普通普攻的片段名是写死的四向，所以只有双持与狂气这两套规则需要额外指定。
/// </summary>
public sealed class WardrobeAttack : BasicAttackDefinition
{
    [JsonPropertyName("clips")] public Dictionary<string, string> Clips { get; init; } = [];
}

/// <summary>
/// 换装的产物：变体表、姿态表、动作覆盖，加上这组武器的普攻配方。战斗侧只用后半段，
/// 外观侧用前面的变体与姿态——两边读的是同一份目录，所以保持同一个函数。
/// </summary>
public sealed class CostumeRecipe
{
    public required Dictionary<string, string> Variants { get; init; }

    public required Dictionary<string, string?> Poses { get; init; }

    public required Dictionary<string, SkillAction?> Actions { get; init; }

    public BasicAttackDefinition? Attack { get; init; }

    public WardrobeAttack? DualWieldAttack { get; init; }

    public WardrobeAttack? FrenzyAttack { get; init; }

    public int? FrenzyWaitFrames { get; init; }
}

public static class WardrobeRules
{
    public static string PoseRelativeFile(string id) => $"wardrobe/poses/{Uri.EscapeDataString(id)}.json";

    public static string PoseFile(string id) => $"content/{PoseRelativeFile(id)}";

    public static bool HasOffhandShield(ItemCatalog items,
        IReadOnlyDictionary<string, string> equipment, bool defaultShield = false)
    {
        if (equipment.TryGetValue("off-hand", out var offhand))
            return items.Items.TryGetValue(offhand, out var item) && item.Kind == "shield";
        return defaultShield;
    }

    public static CostumeRecipe CostumeRecipe(WardrobeCatalog catalog, ItemCatalog items,
        IReadOnlyDictionary<string, string> equipment, string? classId = null, string? appearanceId = null)
    {
        WardrobeCompanion? companion = appearanceId is not null
            ? catalog.Companions?.GetValueOrDefault(appearanceId)
            : null;

        var variants = new Dictionary<string, string>(catalog.Defaults);
        if (companion is not null)
            foreach (var (part, variant) in companion.Variants) variants[part] = variant;

        foreach (var (slot, id) in equipment)
        {
            if (!catalog.Equipment.TryGetValue(id, out var appearance))
                throw new ArgumentException($"装备外观缺失：{id}");
            if (!items.Items.TryGetValue(id, out var item) || item.Equipment is null)
                throw new ArgumentException($"装备定义缺失：{id}");

            var parts = slot == "off-hand" && item.Equipment.DualWield ? appearance.OffHand : appearance.Main;
            foreach (var (part, variant) in parts)
            {
                if (companion is { } value && value.FixedParts.Contains(part)) continue;
                variants[part] = variant;
            }
        }

        var main = equipment.TryGetValue("main-hand", out var mainId)
            ? items.Items.GetValueOrDefault(mainId)?.Kind ?? "unarmed"
            : "unarmed";
        var off = equipment.TryGetValue("off-hand", out var offId)
            ? items.Items.GetValueOrDefault(offId)?.Kind ?? "empty"
            : "empty";

        var stanceKey = $"{main}:{off}";
        if (!catalog.Stances.TryGetValue(stanceKey, out var stance))
            throw new ArgumentException("这组武器缺少人物动作");

        var skillStance = classId is not null
            ? catalog.SkillStances.GetValueOrDefault(classId)?.GetValueOrDefault(stanceKey)
            : null;

        Dictionary<string, string?> poses = new();
        foreach (var (name, file) in stance) poses[name] = file;
        if (classId is not null)
        {
            if (catalog.Skills.GetValueOrDefault(classId) is { } classClips)
                foreach (var (name, file) in classClips) poses[name] = file;
            if (skillStance is not null)
                foreach (var (name, file) in skillStance.Poses) poses[name] = file;
        }

        return new CostumeRecipe
        {
            Variants = variants,
            Poses = poses,
            Actions = skillStance?.Actions ?? [],
            Attack = catalog.Attacks.GetValueOrDefault(stanceKey),
            DualWieldAttack = catalog.DualWieldAttacks?.GetValueOrDefault(stanceKey),
            FrenzyAttack = catalog.FrenzyAttacks?.GetValueOrDefault(stanceKey),
            // 注意不能写成 GetValueOrDefault：缺键时它给 0，会被当成「有等待帧数」。
            FrenzyWaitFrames = catalog.FrenzyWaitFrames is { } waits &&
                               waits.TryGetValue(stanceKey, out var waitFrames)
                ? waitFrames
                : null,
        };
    }
}
