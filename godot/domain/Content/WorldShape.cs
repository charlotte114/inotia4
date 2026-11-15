using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Inotia4.Domain.Effects;
using static Inotia4.Domain.Content.ContentShape;

namespace Inotia4.Domain.Content;

public static class WorldShape
{
    internal static readonly string[] Facings = ["north", "south", "east", "west"];

    private static readonly string[] ExitFacings = ["north", "south", "east", "west", "any"];

    private static readonly string[] DirectBlends =
        [EffectBlends.Normal, EffectBlends.Multiply, EffectBlends.Screen, EffectBlends.Add];

    private static readonly string[] CharacterBlends = ["normal", "screen", "add", "multiply", "color-dodge"];

    private static readonly string[] Authorities = ["registered", "npc", "story", "static"];

    private static readonly ShapeCheck Facing = OneOf(Facings);

    private static readonly ShapeCheck ExitFacing = OneOf(ExitFacings);

    /// <summary>矩形：位置可以是任意有限数，尺寸必须为正。</summary>
    internal static readonly Dictionary<string, ShapeCheck> BoxFields = Fields(
        ("x", Number), ("y", Number), ("width", Positive), ("height", Positive));

    private static readonly ShapeCheck Box = Object(BoxFields);

    private static readonly ShapeCheck Clip = Object(Fields(
        ("frameWidth", Positive), ("frameHeight", Positive), ("frames", Positive),
        ("originX", Number), ("originY", Number), ("frameRate", Positive), ("mirrorX", Flag),
        ("layers", ArrayOf(Object(Fields(("key", Text), ("file", AssetPath), ("blend", OneOf(CharacterBlends))))))));

    private static readonly ShapeCheck EffectLayer = Object(Fields(
        ("key", Text), ("file", AssetPath),
        ("operations", ArrayOf(OneOf(EffectColors.Operations))),
        ("blend", OneOf([.. DirectBlends, EffectBlends.Backdrop]))));

    internal static readonly ShapeCheck EffectClipShape = Object(Fields(
        ("version", Integer), ("frameWidth", Positive), ("frameHeight", Positive), ("frames", Positive),
        ("originX", Number), ("originY", Number), ("frameRate", Positive), ("mirrorX", Flag),
        ("layers", ArrayOf(EffectLayer))));

    internal static readonly ShapeCheck ActorShape = Object(Fields(
        ("chest", Optional(OneOf("ordinary", "improved"))),
        ("service", Optional(OneOf("soul-healer", "fusion-machine"))),
        ("id", Text), ("name", Text), ("description", Text),
        ("clips", Record(Clip)),
        ("portrait", Nullable(Text)),
        ("collision", Optional(Box)),
        ("effectAnchor", Optional(Object(Fields(("x", Number), ("y", Number))))),
        ("headAnchor", Optional(Object(Fields(("x", Number), ("y", Number)))))));

    internal static readonly ShapeCheck Condition = BuildCondition();

    internal static readonly Dictionary<string, ShapeCheck> EntityFields = Merge(
        Fields(("id", Text), ("condition", Optional(Condition))),
        Fields(
            ("actor", Text),
            ("characterId", Optional(Text)),
            ("authority", Optional(OneOf(Authorities))),
            ("x", Number), ("y", Number),
            ("flip", Optional(Flag)),
            ("name", Optional(Text)),
            ("hint", Optional(Text)),
            ("objective", Optional(Text)),
            ("collectible", Optional(Flag)),
            ("afterInteractScript", Optional(Text)),
            ("appear", Optional(Object(Fields(("atMost", DiceFace))))),
            ("downgrade", Optional(Object(Fields(("atLeast", DiceFace), ("actor", Text))))),
            ("spring", Optional(Flag))));

    private static readonly ShapeCheck MapEntity = Object(EntityFields);

    internal static readonly Dictionary<string, ShapeCheck> EnemySpawnFields = Fields(
        ("respawn", Optional(NeverRespawn)),
        ("id", Text), ("enemyId", Text), ("x", Number), ("y", Number),
        ("tierLevel", Optional(Count)),
        ("breakableEligible", Optional(Flag)));

    private static readonly ShapeCheck EnemySpawn = Object(EnemySpawnFields);

    private static readonly ShapeCheck Exit = Object(Merge(Fields(
        ("id", Optional(Text)),
        ("destination", Text),
        ("spawn", Object(Fields(("x", Number), ("y", Number)))),
        ("exitFacing", ExitFacing),
        ("facing", ExitFacing)), BoxFields));

    private static readonly ShapeCheck Feature = Object(Fields(
        ("texture", Text), ("x", Number), ("y", Number), ("offsetX", Number), ("offsetY", Number),
        ("width", Positive), ("flip", Flag), ("blend", OneOf(CharacterBlends))));

    private static readonly ShapeCheck QuestItemSpawn = Object(Fields(
        ("id", Text), ("quest", Text), ("item", Text), ("x", Number), ("y", Number),
        ("respawnFrames", Count)));

    private static readonly ShapeCheck Map = Object(Fields(
        ("lootLevel", Optional(Integer)),
        ("chestLevel", Optional(Integer)),
        ("chestLevelSource", Optional(OneOf("native", "neighbor", "floor", "override"))),
        ("id", Text), ("name", Text),
        ("width", Positive), ("height", Positive), ("tileSize", Positive),
        ("columns", Positive), ("rows", Positive),
        ("blocked", ArrayOf(Flag)),
        ("water", Optional(ArrayOf(Integer))),
        ("ground", AssetPath), ("canopy", AssetPath),
        ("features", ArrayOf(Feature)),
        ("entities", ArrayOf(MapEntity)),
        ("enemySpawns", Optional(ArrayOf(EnemySpawn))),
        ("breakableSpawn", Optional(OneOf("bush", "jar"))),
        ("questItemSpawns", Optional(ArrayOf(QuestItemSpawn))),
        ("exits", ArrayOf(Exit))));

    private static readonly ShapeCheck World = Object(Fields(
        ("storyEffects", Optional(Record(Object(Fields(
            ("clips", Record(EffectClipShape)),
            ("placement", Optional(OneOf("ground", "above-characters")))))))),
        ("version", Integer),
        ("maps", ArrayOf(Object(Fields(("id", Text), ("name", Text), ("file", AssetPath))))),
        ("actors", Record(ActorShape)),
        ("portraits", Record(Object(Fields(("file", AssetPath), ("width", Positive), ("height", Positive))))),
        ("props", ArrayOf(Object(Fields(("key", Text), ("file", AssetPath)))))));

    public static void ValidateWorld(JsonNode? root)
    {
        World(new ShapeValue(root), "world");
        if (root is not JsonObject world || world["maps"] is not JsonArray maps) return;
        Unique(maps.Select(node => Str(node?["id"]) ?? string.Empty), "world.maps");
    }

    public static void ValidateMap(JsonNode? root, string mapId)
    {
        var path = $"map.{mapId}";
        Map(new ShapeValue(root), path);
        if (root is not JsonObject map) return;

        if (map["lootLevel"] is not null)
        {
            TryDouble(map["lootLevel"], out var lootLevel);
            Require(lootLevel is >= 0 and <= 255, $"{path}.lootLevel", "掉落等级需要在 0–255 之间");
        }

        if (map["chestLevel"] is not null)
        {
            TryDouble(map["chestLevel"], out var chestLevel);
            Require(chestLevel is >= 1 and <= 255, $"{path}.chestLevel", "宝箱等级需要在 1–255 之间");
        }

        var columns = NumberOf(map["columns"]);
        var rows = NumberOf(map["rows"]);
        var tileSize = NumberOf(map["tileSize"]);
        var width = NumberOf(map["width"]);
        var height = NumberOf(map["height"]);

        if (map["entities"] is JsonArray entities)
            Unique(entities.Select(entity => Str(entity?["id"]) ?? string.Empty), $"{path}.entities");

        if (map["blocked"] is JsonArray blocked)
            Require(blocked.Count == columns * rows, $"{path}.blocked", "格数不匹配");

        if (map["water"] is JsonArray water)
        {
            var cells = columns * rows;
            for (var index = 0; index < water.Count; index++)
            {
                TryDouble(water[index], out var cell);
                Require(cell >= 0 && cell < cells && (index == 0 || cell > WaterAt(water[index - 1])),
                    $"{path}.water", "水格索引需要严格递增且不超出地图");
            }
        }

        Require(width == columns * tileSize && height == rows * tileSize, path, "尺寸不匹配");

        if (map["enemySpawns"] is JsonArray spawns)
        {
            Unique(spawns.Select(spawn => Str(spawn?["id"]) ?? string.Empty), $"{path}.enemySpawns");
            foreach (var spawn in spawns)
            {
                TryDouble(spawn?["x"], out var x);
                TryDouble(spawn?["y"], out var y);
                Require(x >= 0 && x < width && y >= 0 && y < height, Str(spawn?["id"]) ?? path, "敌人站位超出地图");
            }
        }

        if (map["questItemSpawns"] is JsonArray questSpawns)
        {
            Unique(questSpawns.Select(spawn => Str(spawn?["id"]) ?? string.Empty), $"{path}.questItemSpawns");
            foreach (var spawn in questSpawns)
            {
                TryDouble(spawn?["x"], out var x);
                TryDouble(spawn?["y"], out var y);
                Require(x >= 0 && x < width && y >= 0 && y < height, Str(spawn?["id"]) ?? path,
                    "任务物品生成点超出地图");
            }
        }
    }

    private static double WaterAt(JsonNode? node)
    {
        TryDouble(node, out var value);
        return value;
    }

    private static double NumberOf(JsonNode? node)
    {
        TryDouble(node, out var value);
        return value;
    }

    private static ShapeCheck BuildCondition()
    {
        ShapeCheck condition = null!;
        var checks = new Dictionary<string, ShapeCheck>(StringComparer.Ordinal)
        {
            ["always"] = Object(Fields(("type", OneOf("always")), ("value", Flag))),
            ["all"] = Object(Fields(("type", OneOf("all")), ("conditions", ArrayOf((value, path) => condition(value, path))))),
            ["any"] = Object(Fields(("type", OneOf("any")), ("conditions", ArrayOf((value, path) => condition(value, path))))),
            ["not"] = Object(Fields(("type", OneOf("not")), ("condition", (value, path) => condition(value, path)))),
            ["quest"] = Object(Fields(("type", OneOf("quest")), ("quest", Text),
                ("status", OneOf("locked", "available", "active", "ready", "completed", "accepted", "settled")))),
            ["event"] = Object(Fields(("type", OneOf("event")), ("event", Text),
                ("status", OneOf("idle", "running", "waiting", "completed")))),
            ["choice"] = Object(Fields(("type", OneOf("choice")), ("choice", Text), ("value", Text))),
            ["flag"] = Object(Fields(("type", OneOf("flag")), ("flag", Text), ("value", Optional(Flag)))),
            ["world"] = Object(Fields(("type", OneOf("world")), ("key", Text), ("value", WorldValue))),
        };
        condition = Variant("type", checks);
        return condition;
    }

    private static readonly ShapeCheck WorldValue = (value, path) =>
        Require(value.Present && value.Node is JsonValue json &&
                (json.GetValueKind() is JsonValueKind.String or JsonValueKind.True or JsonValueKind.False ||
                 json.GetValueKind() == JsonValueKind.Number && TryDouble(value.Node, out _)),
            path, "世界条件值无效");

    /// <summary>
    /// 刷怪点与任务敌人都不能刷新：只接受写成 <c>false</c> 或不写。
    ///
    /// 写成方法而不是静态字段：它在上面的 <see cref="EnemySpawnFields"/> 里当值用，
    /// 静态字段按书写顺序初始化，声明在后面时那一刻还是 null——方法没有这个先后问题。
    /// </summary>
    private static void NeverRespawn(ShapeValue value, string path) =>
        Require(value.Present && value.Node is JsonValue json && json.GetValueKind() == JsonValueKind.False,
            path, "一次性敌人不可刷新");
}
