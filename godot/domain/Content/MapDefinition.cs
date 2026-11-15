using System.Text.Json.Serialization;

namespace Inotia4.Domain.Content;

public sealed class MapDefinition
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;

    [JsonPropertyName("lootLevel")] public int LootLevel { get; set; }

    [JsonPropertyName("chestLevel")] public int? ChestLevel { get; set; }

    [JsonPropertyName("width")] public int Width { get; set; }

    [JsonPropertyName("height")] public int Height { get; set; }

    [JsonPropertyName("tileSize")] public int TileSize { get; set; } = 16;

    [JsonPropertyName("columns")] public int Columns { get; set; }

    [JsonPropertyName("rows")] public int Rows { get; set; }

    [JsonPropertyName("blocked")] public bool[] Blocked { get; set; } = Array.Empty<bool>();

    [JsonPropertyName("water")] public int[]? Water { get; set; }

    [JsonPropertyName("ground")] public string Ground { get; set; } = string.Empty;

    [JsonPropertyName("canopy")] public string Canopy { get; set; } = string.Empty;

    [JsonPropertyName("features")] public MapFeature[] Features { get; set; } = Array.Empty<MapFeature>();

    [JsonPropertyName("entities")] public MapEntity[] Entities { get; set; } = Array.Empty<MapEntity>();

    [JsonPropertyName("exits")] public MapExit[] Exits { get; set; } = Array.Empty<MapExit>();

    [JsonPropertyName("enemySpawns")] public EnemySpawn[] EnemySpawns { get; set; } = Array.Empty<EnemySpawn>();

    [JsonPropertyName("breakableSpawn")] public string? BreakableSpawn { get; set; }

    [JsonPropertyName("questItemSpawns")] public QuestItemSpawn[] QuestItemSpawns { get; set; } = Array.Empty<QuestItemSpawn>();
}

public sealed class QuestItemSpawn
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;

    [JsonPropertyName("quest")] public string Quest { get; set; } = string.Empty;

    [JsonPropertyName("item")] public string Item { get; set; } = string.Empty;

    [JsonPropertyName("x")] public float X { get; set; }

    [JsonPropertyName("y")] public float Y { get; set; }

    [JsonPropertyName("respawnFrames")] public long RespawnFrames { get; set; }
}

public sealed class EnemySpawn
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;

    [JsonPropertyName("enemyId")] public string EnemyId { get; set; } = string.Empty;

    [JsonPropertyName("x")] public float X { get; set; }

    [JsonPropertyName("y")] public float Y { get; set; }

    [JsonPropertyName("tierLevel")] public int? TierLevel { get; set; }

    [JsonPropertyName("breakableEligible")] public bool BreakableEligible { get; set; }

    [JsonPropertyName("respawn")] public bool? Respawn { get; set; }
}

public sealed class MapFeature
{
    [JsonPropertyName("texture")] public string Texture { get; set; } = string.Empty;

    [JsonPropertyName("x")] public float X { get; set; }

    [JsonPropertyName("y")] public float Y { get; set; }

    [JsonPropertyName("offsetX")] public float OffsetX { get; set; }

    [JsonPropertyName("offsetY")] public float OffsetY { get; set; }

    [JsonPropertyName("width")] public float Width { get; set; }

    [JsonPropertyName("flip")] public bool Flip { get; set; }

    [JsonPropertyName("blend")] public string Blend { get; set; } = "normal";
}

public sealed class MapEntity
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;

    [JsonPropertyName("actor")] public string Actor { get; set; } = string.Empty;

    [JsonPropertyName("x")] public float X { get; set; }

    [JsonPropertyName("y")] public float Y { get; set; }

    [JsonPropertyName("flip")] public bool Flip { get; set; }

    [JsonPropertyName("characterId")] public string? CharacterId { get; set; }

    /// <summary>
    /// 出现判定：每次进图掷原生 1..99，不超过 <see cref="EntityAppearRule.AtMost"/> 才生成。
    /// 见 <see cref="World.ChestPlacement.RollPlacement"/>（原版 <c>MAP_AddNPCItemLocation</c> 的类型 4）。
    /// </summary>
    [JsonPropertyName("appear")] public EntityAppearRule? Appear { get; set; }

    [JsonPropertyName("downgrade")] public EntityDowngradeRule? Downgrade { get; set; }

    [JsonPropertyName("authority")] public string? Authority { get; set; }

    [JsonPropertyName("hint")] public string? Hint { get; set; }

    /// <summary>
    /// 交互可以推进的目标 id（对应任务目标里的 <c>target</c>）。
    /// 原版开店的判定是「actor id 是商店 id + **没有 objective** + 没有任务槽」，
    /// 所以这一条决定商人到底开不开店。
    /// </summary>
    [JsonPropertyName("objective")] public string? Objective { get; set; }

    [JsonPropertyName("collectible")] public bool Collectible { get; set; }

    [JsonPropertyName("afterInteractScript")] public string? AfterInteractScript { get; set; }

    [JsonPropertyName("spring")] public bool Spring { get; set; }

    public MapEntity WithActor(string actor) => new()
    {
        Id = Id,
        Actor = actor,
        X = X,
        Y = Y,
        Flip = Flip,
        CharacterId = CharacterId,
        Appear = Appear,
        Downgrade = Downgrade,
        Authority = Authority,
        Hint = Hint,
        Objective = Objective,
        Collectible = Collectible,
        AfterInteractScript = AfterInteractScript,
        Spring = Spring,
    };
}

public sealed class EntityAppearRule
{
    [JsonPropertyName("atMost")] public int AtMost { get; set; }
}

public sealed class EntityDowngradeRule
{
    [JsonPropertyName("atLeast")] public int AtLeast { get; set; }

    [JsonPropertyName("actor")] public string Actor { get; set; } = string.Empty;
}

public sealed class MapExit
{
    [JsonPropertyName("id")] public string? Id { get; set; }

    [JsonPropertyName("x")] public float X { get; set; }

    [JsonPropertyName("y")] public float Y { get; set; }

    [JsonPropertyName("width")] public float Width { get; set; }

    [JsonPropertyName("height")] public float Height { get; set; }

    [JsonPropertyName("destination")] public string Destination { get; set; } = string.Empty;

    [JsonPropertyName("spawn")] public MapPoint Spawn { get; set; } = new();

    [JsonPropertyName("facing")] public string Facing { get; set; } = "south";
}

public sealed class MapPoint
{
    [JsonPropertyName("x")] public float X { get; set; }

    [JsonPropertyName("y")] public float Y { get; set; }
}
