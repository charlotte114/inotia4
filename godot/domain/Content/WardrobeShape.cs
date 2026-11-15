using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using static Inotia4.Domain.Content.ContentShape;

namespace Inotia4.Domain.Content;

public static class WardrobeShape
{
    private static readonly string[] Facings = ["north", "south", "east", "west"];

    private static readonly ShapeCheck PosePart = Object(Fields(
        ("part", Text),
        ("piece", Integer),
        ("x", Number),
        ("y", Number),
        ("scaleX", Number),
        ("scaleY", Number),
        ("angle", Number),
        ("alpha", Number)));

    private static readonly ShapeCheck Pose = Object(Fields(
        ("mirrorX", Flag),
        ("frameRate", Positive),
        ("frames", ArrayOf(ArrayOf(PosePart)))));

    private static readonly ShapeCheck Sprite = Object(Fields(
        ("texture", Text),
        ("x", Integer),
        ("y", Integer),
        ("width", Count),
        ("height", Count),
        ("anchorX", Number),
        ("anchorY", Number)));

    private static readonly ShapeCheck EquipmentAppearance = Object(Fields(
        ("main", Record(Text)),
        ("offHand", Record(Text))));

    private static readonly ShapeCheck CompanionAppearance = Object(Fields(
        ("variants", Record(Text)),
        ("fixedParts", ArrayOf(Text))));

    private static readonly ShapeCheck SkillStance = Object(Fields(
        ("poses", Record(Text)),
        ("actions", Record(Nullable(BattleActionShape.SkillAction)))));

    private static readonly ShapeCheck Wardrobe = Object(Fields(
        ("defaultShield", Optional(Flag)),
        ("companions", Optional(Record(CompanionAppearance))),
        ("textures", ArrayOf(Object(Fields(("key", Text), ("file", AssetPath))))),
        ("sprites", Record(Sprite)),
        ("variants", Record(ArrayOf(Nullable(Text)))),
        ("defaults", Record(Text)),
        ("equipment", Record(EquipmentAppearance)),
        ("poses", Record(Pose)),
        ("stances", Record(Record(Text))),
        ("skills", Record(Record(Text))),
        ("attacks", Record(BattleShape.BasicAttackShape)),
        ("dualWieldAttacks", Optional(Record(BattleShape.BoundBasicAttackShape))),
        ("frenzyAttacks", Optional(Record(BattleShape.BoundBasicAttackShape))),
        ("frenzyWaitFrames", Optional(Record(Count))),
        ("skillStances", Record(Record(SkillStance)))));

    public static void ValidateWardrobe(JsonNode? root)
    {
        Wardrobe(new ShapeValue(root), "wardrobe");
        if (root is not JsonObject wardrobe) return;

        var textures = new HashSet<string>(StringComparer.Ordinal);
        if (wardrobe["textures"] is JsonArray textureList)
            foreach (var texture in textureList)
                textures.Add(Str(texture?["key"]) ?? string.Empty);

        if (wardrobe["sprites"] is not JsonObject sprites) return;

        foreach (var (id, node) in sprites)
            Require(textures.Contains(Str(node?["texture"]) ?? string.Empty),
                $"wardrobe.sprites.{id}", "部件纹理不存在");

        var variants = wardrobe["variants"] as JsonObject;
        if (variants is not null)
            foreach (var (id, node) in variants)
                foreach (var piece in node as JsonArray ?? [])
                    if (piece is not null)
                        Require(sprites.ContainsKey(Str(piece) ?? string.Empty),
                            $"wardrobe.variants.{id}", "部件不存在");

        var variantSources = new List<JsonObject>();
        if (wardrobe["defaults"] is JsonObject defaults) variantSources.Add(defaults);
        if (wardrobe["companions"] is JsonObject companions)
            foreach (var (_, companion) in companions)
                if (companion?["variants"] is JsonObject companionVariants) variantSources.Add(companionVariants);
        if (wardrobe["equipment"] is JsonObject equipment)
            foreach (var (_, entry) in equipment)
            {
                if (entry?["main"] is JsonObject main) variantSources.Add(main);
                if (entry?["offHand"] is JsonObject offHand) variantSources.Add(offHand);
            }

        foreach (var source in variantSources)
            foreach (var (id, node) in source)
                Require(variants is not null && variants.ContainsKey(Str(node) ?? string.Empty),
                    $"wardrobe.equipment.{id}", "服装样式不存在");

        var poses = wardrobe["poses"] as JsonObject;
        foreach (var key in new[] { "stances", "skills" })
        {
            if (wardrobe[key] is not JsonObject stances) continue;
            foreach (var (id, node) in stances)
                foreach (var (_, poseNode) in node as JsonObject ?? Enumerable.Empty<KeyValuePair<string, JsonNode?>>())
                    Require(poses is not null && poses.ContainsKey(Str(poseNode) ?? string.Empty),
                        $"wardrobe.{key}.{id}", "动作不存在");
        }

        if (wardrobe["stances"] is JsonObject stanceTable)
        {
            var attacks = wardrobe["attacks"] as JsonObject;
            foreach (var (id, _) in stanceTable)
                Require(attacks is not null && attacks.ContainsKey(id), "wardrobe.attacks", $"普攻规则缺失 {id}");
        }

        CheckBoundAttacks(wardrobe, poses);

        if (poses is not null)
            foreach (var (id, node) in poses)
            {
                var frames = node?["frames"] as JsonArray;
                Require(frames is { Count: > 0 }, $"wardrobe.poses.{id}", "动作不能为空");
                foreach (var frame in frames ?? [])
                foreach (var part in frame as JsonArray ?? [])
                {
                    TryDouble(part?["scaleX"], out var scaleX);
                    TryDouble(part?["scaleY"], out var scaleY);
                    TryDouble(part?["alpha"], out var alpha);
                    Require(scaleX != 0 && scaleY != 0 && alpha is >= 0 and <= 1,
                        $"wardrobe.poses.{id}", "部件变换无效");
                }
            }
    }

    private static void CheckBoundAttacks(JsonObject wardrobe, JsonObject? poses)
    {
        var stances = wardrobe["stances"] as JsonObject;

        if (wardrobe["dualWieldAttacks"] is JsonObject dualWield && stances is not null)
            foreach (var (id, node) in dualWield)
            {
                Require(stances.ContainsKey(id), "wardrobe.dualWieldAttacks", $"武器动作不存在 {id}");
                var strikes = node?["strikes"] as JsonArray;
                Require(strikes is { Count: 2 } &&
                        Str(strikes[0]?["hand"]) == "main-hand" &&
                        Str(strikes[1]?["hand"]) == "offhand" &&
                        NumberAt(strikes[0]?["frame"]) == NumberAt(node?["strikeFrame"]) &&
                        NumberAt(strikes[1]?["frame"]) > NumberAt(strikes[0]?["frame"]),
                    "wardrobe.dualWieldAttacks", "双持出手顺序无效");
                foreach (var facing in Facings)
                {
                    var poseName = Str((node?["clips"] as JsonObject)?[facing]);
                    Require(stances[id] is JsonObject stance && poseName is not null && stance.ContainsKey(poseName),
                        "wardrobe.dualWieldAttacks", "双持人物动作缺失");
                }
            }

        if (wardrobe["frenzyAttacks"] is JsonObject frenzy && stances is not null)
            foreach (var (id, node) in frenzy)
            {
                Require(stances.ContainsKey(id), "wardrobe.frenzyAttacks", $"武器动作不存在 {id}");
                Require(Str(node?["timing"]) == "fixed-animation" &&
                        (node?["targeting"] as JsonObject)?["target"] is { } target && Str(target) == "area",
                    "wardrobe.frenzyAttacks", "范围普攻需要固定动画与范围目标");
                foreach (var facing in Facings)
                {
                    var clipName = Str((node?["clips"] as JsonObject)?[facing]);
                    var poseName = clipName is null || stances[id] is not JsonObject stance
                        ? null
                        : Str(stance[clipName]);
                    var frames = poseName is null ? null : poses?[poseName]?["frames"] as JsonArray;
                    Require(frames is not null && frames.Count == NumberAt(node?["cycleFrames"]),
                        "wardrobe.frenzyAttacks", "范围普攻人物动作缺失或帧数不匹配");
                }
            }

        if (wardrobe["frenzyWaitFrames"] is JsonObject waits && stances is not null)
            foreach (var (id, _) in waits)
            {
                Require(stances.ContainsKey(id), "wardrobe.frenzyWaitFrames", $"武器动作不存在 {id}");
                Require((wardrobe["frenzyAttacks"] as JsonObject)?.ContainsKey(id) != true,
                    "wardrobe.frenzyWaitFrames", "同一装备组合只能声明范围普攻或无动画等待");
            }

        if (wardrobe["skillStances"] is JsonObject skillStances && stances is not null)
            foreach (var (_, weaponStances) in skillStances)
                foreach (var (id, stance) in weaponStances as JsonObject ?? new JsonObject())
                {
                    Require(stances.ContainsKey(id), "wardrobe.skillStances", $"武器动作不存在 {id}");
                    foreach (var (_, poseNode) in stance?["poses"] as JsonObject ?? new JsonObject())
                        Require(poses is not null && poses.ContainsKey(Str(poseNode) ?? string.Empty),
                            "wardrobe.skillStances", "技能动作不存在");
                }

    }

    private static double NumberAt(JsonNode? node)
    {
        TryDouble(node, out var number);
        return number;
    }
}
