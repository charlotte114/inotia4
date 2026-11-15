using System.Text.Json.Serialization;
using Inotia4.Domain.World;

namespace Inotia4.Domain;

public sealed class WorldLocationSaved
{
    [JsonPropertyName("map")] public string Map { get; set; } = string.Empty;

    [JsonPropertyName("x")] public double X { get; set; }

    [JsonPropertyName("y")] public double Y { get; set; }

    [JsonPropertyName("facing")] public string Facing { get; set; } = "south";
}

public sealed class WorldSnapshotState
{
    [JsonPropertyName("location")] public WorldLocationSaved Location { get; set; } = new();

    [JsonPropertyName("conditions")] public Combat.CombatConditionsState? Conditions { get; set; }

    [JsonPropertyName("threat")] public Combat.ThreatLedgerState? Threat { get; set; }

    [JsonPropertyName("party")] public List<WorldMemberSaved> Party { get; set; } = [];

    [JsonPropertyName("enemies")] public List<WorldEnemySaved> Enemies { get; set; } = [];

    /// <summary>
    /// 当前锁定的攻击目标：点选敌人或按下攻击时锁定，
    /// 目标消失／不可瞄准／团灭时清空；不在场上时读档静默丢弃，不拒整份快照。
    /// </summary>
    [JsonPropertyName("focusTargetId")] public string? FocusTargetId { get; set; }

    [JsonPropertyName("damageCount")] public int? DamageCount { get; set; }

    [JsonPropertyName("generatedEntities")] public List<Content.MapEntity>? GeneratedEntities { get; set; }

    [JsonPropertyName("scene")] public SceneSnapshotSaved? Scene { get; set; }

    [JsonPropertyName("audio")] public StoryAudioSaved? Audio { get; set; }

    [JsonIgnore] public bool HasWorld => Conditions is not null || Threat is not null ||
                                         Party.Count > 0 || Enemies.Count > 0 ||
                                         Scene is not null || GeneratedEntities is { Count: > 0 } ||
                                         Audio is not null;
}

public sealed class WorldMemberSaved
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;

    [JsonPropertyName("x")] public double X { get; set; }

    [JsonPropertyName("y")] public double Y { get; set; }

    [JsonPropertyName("facing")] public string Facing { get; set; } = "south";

    [JsonPropertyName("fallen")] public bool Fallen { get; set; }

    [JsonPropertyName("ai")] public Combat.PartyControllerState? Ai { get; set; }
}

public sealed class WorldEnemySaved
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;

    [JsonPropertyName("enemyId")] public string? EnemyId { get; set; }

    [JsonPropertyName("x")] public double? X { get; set; }

    [JsonPropertyName("y")] public double? Y { get; set; }

    [JsonPropertyName("health")] public int? Health { get; set; }

    [JsonPropertyName("summonedBy")] public string? SummonedBy { get; set; }

    [JsonPropertyName("summonAttributes")] public Combat.SummonStats? SummonAttributes { get; set; }

    [JsonPropertyName("facing")] public string Facing { get; set; } = "south";

    [JsonPropertyName("summonRemainingMs")] public int? SummonRemainingMs { get; set; }

    [JsonPropertyName("enemyAi")] public Combat.EnemyControllerState? EnemyAi { get; set; }

    [JsonPropertyName("summonAi")] public Combat.FriendlySummonState? SummonAi { get; set; }
}
