using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Inotia4.Domain.Story;

namespace Inotia4.Domain;

public sealed class SceneSnapshotSaved
{
    [JsonPropertyName("map")] public string Map { get; set; } = string.Empty;

    [JsonPropertyName("actors")] public List<SceneActorSaved> Actors { get; set; } = [];
}

public sealed class SceneActorSaved
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;

    [JsonPropertyName("actor")] public string Actor { get; set; } = string.Empty;

    [JsonPropertyName("characterId")] public string? CharacterId { get; set; }

    [JsonPropertyName("worldEntityId")] public string? WorldEntityId { get; set; }

    [JsonPropertyName("authority")] public string? Authority { get; set; }

    [JsonPropertyName("memberId")] public string? MemberId { get; set; }

    [JsonPropertyName("enemyKey")] public string? EnemyKey { get; set; }

    [JsonPropertyName("enemyId")] public string? EnemyId { get; set; }

    [JsonPropertyName("summonedBy")] public string? SummonedBy { get; set; }

    [JsonPropertyName("x")] public double X { get; set; }

    [JsonPropertyName("y")] public double Y { get; set; }

    [JsonPropertyName("facing")] public string Facing { get; set; } = "south";

    [JsonPropertyName("visible")] public bool Visible { get; set; }

    [JsonPropertyName("stageHidden")] public bool? StageHidden { get; set; }

    [JsonPropertyName("followsPlayer")] public bool? FollowsPlayer { get; set; }

    [JsonPropertyName("followVisibility")] public bool? FollowVisibility { get; set; }

    [JsonPropertyName("removed")] public bool? Removed { get; set; }

    [JsonPropertyName("flip")] public bool? Flip { get; set; }

    /// <summary>当前动作名，形如 <c>idle-south</c>；必须以朝向结尾。</summary>
    [JsonPropertyName("clip")] public string? Clip { get; set; }

    [JsonPropertyName("animationProgress")] public double? AnimationProgress { get; set; }

    [JsonPropertyName("animationPlaying")] public bool? AnimationPlaying { get; set; }

    [JsonPropertyName("unpositioned")] public bool? Unpositioned { get; set; }

    [JsonPropertyName("alpha")] public double? Alpha { get; set; }
}

public sealed class StoryAudioSaved
{
    [JsonPropertyName("music")] public string? Music { get; set; }

    [JsonPropertyName("ambience")] public string? Ambience { get; set; }
}

/// <summary>
/// 场景快照的读档校验：存档里的人物必须能重新建出节点与纹理，坏档在切换场景之前就被拒绝，
/// 而不是铺到一半下标越界、或者画出一个人物却找不到它的动作片段。
/// </summary>
public static class SceneSnapshotRules
{
    private static readonly string[] Facings = ["north", "south", "east", "west"];

    private static readonly string[] Authorities = ["registered", "npc", "story", "static"];

    private static Exception Fail(string detail) =>
        new InvalidOperationException($"剧情场景快照格式无效：{detail}");

    public static void Validate(JsonNode? node)
    {
        if (node is null) return;
        if (node is not JsonObject snapshot) throw Fail("缺少地图或对象列表");
        if (Text(snapshot["map"]) is not { Length: > 0 }) throw Fail("缺少地图或对象列表");
        if (snapshot["actors"] is not JsonArray actors) throw Fail("缺少地图或对象列表");

        var ids = new HashSet<string>();
        for (var index = 0; index < actors.Count; index++)
        {
            var at = $"actors[{index}]";
            if (actors[index] is not JsonObject actor) throw Fail($"{at}.不是对象");

            var id = Text(actor["id"]);
            if (id is not { Length: > 0 }) throw Fail($"{at}.id 必须是非空字符串");
            if (!ids.Add(id)) throw Fail($"{at}.id 重复：{id}");
            if (Text(actor["actor"]) is not { Length: > 0 }) throw Fail($"{at}.actor 必须是非空字符串");
            if (!Number(actor["x"], out _) || !Number(actor["y"], out _))
                throw Fail($"{at}.x/y 必须是有限数字");
            if (Text(actor["facing"]) is not { } facing || Array.IndexOf(Facings, facing) < 0)
                throw Fail($"{at}.facing 无效");
            if (actor["visible"] is not JsonValue visibleValue ||
                !visibleValue.TryGetValue<bool>(out _))
                throw Fail($"{at}.visible 必须是布尔值");

            foreach (var key in new[] { "characterId", "worldEntityId", "memberId", "enemyKey", "enemyId", "summonedBy" })
            {
                if (actor[key] is null) continue;
                if (Text(actor[key]) is not { Length: > 0 }) throw Fail($"{at}.{key} 必须是非空字符串");
            }

            if (actor["authority"] is not null &&
                (Text(actor["authority"]) is not { } authority || Array.IndexOf(Authorities, authority) < 0))
                throw Fail($"{at}.authority 无效");

            foreach (var key in new[] { "flip", "unpositioned", "removed", "animationPlaying", "stageHidden" })
            {
                if (actor[key] is null) continue;
                if (actor[key] is not JsonValue flag || !flag.TryGetValue<bool>(out _))
                    throw Fail($"{at}.{key} 必须是布尔值");
            }

            if (actor["alpha"] is not null &&
                (!Number(actor["alpha"], out var alpha) || alpha < 0 || alpha > 1))
                throw Fail($"{at}.alpha 必须在 0..1");

            if (actor["animationProgress"] is not null &&
                (!Number(actor["animationProgress"], out var progress) || progress < 0 || progress > 1))
                throw Fail($"{at}.animationProgress 必须在 0..1（当前 {actor["animationProgress"]?.ToJsonString()}）");

            if (actor["clip"] is not null)
            {
                var clip = Text(actor["clip"]);
                if (clip is not { Length: > 0 } ||
                    !(clip.EndsWith("-north", StringComparison.Ordinal) ||
                      clip.EndsWith("-south", StringComparison.Ordinal) ||
                      clip.EndsWith("-east", StringComparison.Ordinal) ||
                      clip.EndsWith("-west", StringComparison.Ordinal)))
                    throw Fail($"{at}.clip 必须以朝向结尾（当前 {actor["clip"]?.ToJsonString()}）");
            }

            // appearance：原版存档里有一整份外观；这里一律不写（37 版之后舞台也不写外观），
            // 读档按 actor id 从内容加剧情外观覆盖重建，所以这里不校验它。
        }

        var parsed = node.Deserialize<SceneSnapshotSaved>(SaveGameStore.JsonOptions)
            ?? throw Fail("剧情场景快照无法物化");
        ActorCensus.AssertUnique(parsed.Actors, "剧情场景快照");
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool Number(JsonNode? node, out double number)
    {
        number = double.NaN;
        if (node is not JsonValue value || value.GetValueKind() != System.Text.Json.JsonValueKind.Number)
            return false;
        if (!double.TryParse(node!.ToJsonString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed)) return false;
        if (!double.IsFinite(parsed)) return false;
        number = parsed;
        return true;
    }
}
