using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Inotia4.Domain.Character;

namespace Inotia4.Domain;

public static class SavedGameUpgrades
{
    private static void Upgrade33(SaveGame game)
    {
        game.Format = 34;
    }

    private static void Upgrade34(SaveGame game)
    {
        foreach (var owner in game.Session.Bag.Shortcuts)
        {
            var deduped = ShortcutLayout.DedupeSkills(owner.Row);
            for (var index = 0; index < owner.Row.Count; index++)
                owner.Row[index] = index < deduped.Length ? deduped[index] : null;
        }

        game.Format = 35;
    }

    private static void Upgrade35(SaveGame game)
    {
        game.Format = 36;
    }

    private static void Upgrade36(SaveGame game)
    {
        game.Format = 37;
    }

    private static void Upgrade37(SaveGame game)
    {
        game.Format = 38;
    }

    private static void Upgrade38(SaveGame game)
    {
        game.Format = 39;
    }

    private static void Upgrade39(SaveGame game)
    {
        game.Format = 40;
    }

    /// <summary>
    /// 40 → 41：世界载荷开始带场景快照、进图掷骰结果与剧情音轨。这三项都是「有就照它铺，
    /// 没有就按当前内容重新投影」，所以旧档不需要搬数据，只推进版本号。
    /// </summary>
    private static void Upgrade40(SaveGame game)
    {
        game.Format = 41;
    }

    private static void Upgrade41(SaveGame game)
    {
        game.Format = 42;
    }

    private static void Upgrade42(SaveGame game)
    {
        game.Format = SaveGameStore.SaveFormat;
    }

    public static SaveGame Upgrade(SaveGame game)
    {
        if (game.Format == SaveGameStore.SaveFormat) return game;
        if (!SaveGameStore.IsReadableSaveFormat(game.Format))
            throw new InvalidOperationException("存档版本不受支持");
        var upgraded = Clone(game);
        while (upgraded.Format != SaveGameStore.SaveFormat)
        {
            switch (upgraded.Format)
            {
                case 33: Upgrade33(upgraded); break;
                case 34: Upgrade34(upgraded); break;
                case 35: Upgrade35(upgraded); break;
                case 36: Upgrade36(upgraded); break;
                case 37: Upgrade37(upgraded); break;
                case 38: Upgrade38(upgraded); break;
                case 39: Upgrade39(upgraded); break;
                case 40: Upgrade40(upgraded); break;
                case 41: Upgrade41(upgraded); break;
                case 42: Upgrade42(upgraded); break;
                default: throw new InvalidOperationException("存档版本不受支持");
            }
        }

        return upgraded;
    }

    private static SaveGame Clone(SaveGame game) =>
        System.Text.Json.JsonSerializer.Deserialize<SaveGame>(
            System.Text.Json.JsonSerializer.Serialize(game, SaveGameStore.JsonOptions),
            SaveGameStore.JsonOptions) ?? throw new InvalidOperationException("存档深拷贝失败");

    private static readonly Dictionary<string, string> LegacyBagReplacements = new()
    {
        ["handbag"] = "healing-potion-m",
        ["small-bag"] = "healing-potion-l",
        ["backpack"] = "healing-potion-xl",
        ["infinity-bag"] = "elixir",
    };

    private static readonly HashSet<string> LegacyRemovedItems =
    [
        "novice-dice", "standard-dice", "advanced-dice", "supreme-dice", "chaos-dice", "stats-reset-spell",
    ];

    private static bool IsLegacy(string? id) =>
        !string.IsNullOrEmpty(id) &&
        (LegacyBagReplacements.ContainsKey(id) || LegacyRemovedItems.Contains(id));

    private static string Replace(string id) => LegacyBagReplacements.GetValueOrDefault(id, id);

    /// <summary>
    /// 已删除的道具在旧档里这样处理：扩容背包换成可用的消耗品，其余直接丢弃。
    /// 丢弃时必须同时清掉格位与快捷栏引用，否则背包自检会拒绝整份存档。
    /// 奖励收据与地面掉落这两本账目前还没有对应的子系统，缺席的那两处不迁移。
    /// </summary>
    public static SaveGame MigrateLegacyItems(SaveGame game)
    {
        var session = game.Session;
        var needsMigration =
            session.Bag.Instances.Any(entry => IsLegacy(entry.ItemId)) ||
            session.Bag.Shortcuts.Any(owner => owner.Row.Any(slot => slot?.Kind == "item" && IsLegacy(slot.Id))) ||
            session.Shops.Stock.Any(pair => pair.Value.Any(offer => IsLegacy(offer.Instance.ItemId)));
        if (!needsMigration) return game;

        var migrated = Clone(game);
        var s = migrated.Session;
        var removedIds = new HashSet<string>();
        s.Bag.Instances = s.Bag.Instances
            .Select(entry => ReplaceInstanceItem(entry, Replace(entry.ItemId)))
            .Where(entry =>
            {
                if (!LegacyRemovedItems.Contains(entry.ItemId)) return true;
                removedIds.Add(entry.Id);
                return false;
            })
            .ToList();
        if (removedIds.Count > 0)
        {
            foreach (var key in s.Bag.Pouches.Keys.ToList())
                s.Bag.Pouches[key] = s.Bag.Pouches[key]
                    .Where(id => id is null || !removedIds.Contains(id)).ToList();
            s.Bag.AllCells = s.Bag.AllCells
                .Where(id => id is null || !removedIds.Contains(id)).ToList();
        }

        foreach (var owner in s.Bag.Shortcuts)
        for (var index = 0; index < owner.Row.Count; index++)
        {
            var slot = owner.Row[index];
            if (slot?.Kind != "item") continue;
            if (LegacyRemovedItems.Contains(slot.Id)) owner.Row[index] = null;
            else owner.Row[index] = slot with { Id = Replace(slot.Id) };
        }

        foreach (var pair in s.Shops.Stock)
            pair.Value.RemoveAll(offer => LegacyRemovedItems.Contains(offer.Instance.ItemId));

        return migrated;
    }

    private static ItemInstance ReplaceInstanceItem(ItemInstance instance, string itemId)
    {
        var node = System.Text.Json.JsonSerializer.SerializeToNode(instance, SaveGameStore.JsonOptions)!;
        node["itemId"] = itemId;
        return System.Text.Json.JsonSerializer.Deserialize<ItemInstance>(node, SaveGameStore.JsonOptions)!;
    }

    public static PreparedSavedGame PrepareSavedGame(SaveGame game,
        Content.StoryClassCatalog storyClasses, Content.WorldIndex world)
    {
        var upgraded = MigrateLegacyItems(Upgrade(game));

        var selected = Array.Find(storyClasses.Classes, entry => entry.Id == upgraded.Summary.ClassId);
        if (selected is null) throw new InvalidOperationException("存档记录的职业在当前内容里不存在");

        var location = upgraded.World?["location"];
        var mapId = location?["map"]?.GetValue<string>();
        var x = ReadNumber(location?["x"]);
        var y = ReadNumber(location?["y"]);
        var facing = location?["facing"]?.GetValue<string>();

        var map = Array.Find(world.Maps, entry => entry.Id == mapId);
        if (map is null || upgraded.Summary.MapId != mapId || !double.IsFinite(x) || !double.IsFinite(y) ||
            facing is not ("north" or "south" or "east" or "west"))
            throw new InvalidOperationException("存档记录的地图或位置无效");

        ValidateWorldPayload(upgraded.World, mapId!, x, y, facing!, world);

        return new PreparedSavedGame
        {
            Game = upgraded,
            MapId = mapId!,
            X = x,
            Y = y,
            Facing = facing!,
            Map = map,
        };
    }

    private static double ReadNumber(JsonNode? node)
    {
        if (node is null || node.GetValueKind() != System.Text.Json.JsonValueKind.Number) return double.NaN;
        return double.TryParse(node.ToJsonString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : double.NaN;
    }

    private static string? ReadText(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>
    /// 世界载荷校验：
    /// 进图掷骰结果必须指向存在的角色、坐标有限、id 不重复；场景快照结构必须完整，
    /// 且主角在场景里的位置与落点一致；音轨名字必须是字符串。
    /// 这几项是 40 → 41 才写进存档的，旧档缺项时跳过（读档按当前内容重新投影）。
    /// </summary>
    private static void ValidateWorldPayload(JsonNode? world, string mapId, double x, double y,
        string facing, Content.WorldIndex index)
    {
        if (world is not JsonObject payload) throw new InvalidOperationException("存档记录的世界状态无效");

        ValidateGeneratedEntities(payload["generatedEntities"], index);
        ValidateScene(payload["scene"], mapId, x, y, facing);
        ValidateAudio(payload["audio"]);
    }

    /// <summary>
    /// 进图掷骰结果（<c>world.generatedEntities</c>）：每条都要能重新摆出来——
    /// 角色在内容里存在、坐标有限、id 不重复，否则读档会摆出一堆没有贴图的对象。
    /// </summary>
    private static void ValidateGeneratedEntities(JsonNode? node, Content.WorldIndex index)
    {
        if (node is null) return;
        if (node is not JsonArray entities) throw new InvalidOperationException("存档记录的地图人物无效");

        var ids = new HashSet<string>();
        foreach (var entry in entities)
        {
            if (entry is not JsonObject entity) throw new InvalidOperationException("存档记录的地图人物无效");
            var id = ReadText(entity["id"]);
            var actor = ReadText(entity["actor"]);
            if (id is not { Length: > 0 } || !ids.Add(id)) throw new InvalidOperationException("存档记录的地图人物无效");
            if (actor is not { Length: > 0 } || !index.Actors.ContainsKey(actor) ||
                !double.IsFinite(ReadNumber(entity["x"])) || !double.IsFinite(ReadNumber(entity["y"])))
                throw new InvalidOperationException("存档记录的地图人物无效");
        }
    }

    private static void ValidateScene(JsonNode? node, string mapId, double x, double y, string facing)
    {
        SceneSnapshotRules.Validate(node);
        if (node is not JsonObject scene) return;

        if (ReadText(scene["map"]) != mapId) throw new InvalidOperationException("存档场景与主角位置不一致");
        if (scene["actors"] is not JsonArray actors) return;
        JsonObject? player = null;
        foreach (var entry in actors)
            if (entry is JsonObject actor && ReadText(actor["id"]) == "player")
            {
                player = actor;
                break;
            }

        if (player is null || ReadNumber(player["x"]) != x || ReadNumber(player["y"]) != y ||
            ReadText(player["facing"]) != facing)
            throw new InvalidOperationException("存档场景与主角位置不一致");
    }

    private static void ValidateAudio(JsonNode? node)
    {
        if (node is null) return;
        if (node is not JsonObject audio) throw new InvalidOperationException("存档记录的音轨无效");
        foreach (var key in new[] { "music", "ambience" })
        {
            if (audio[key] is null) continue;
            if (ReadText(audio[key]) is not { Length: > 0 }) throw new InvalidOperationException("存档记录的音轨无效");
        }
    }
}

/// <summary>读档校验后的产物：升级完的存档、落点与地图条目。
/// 会话由场景层装配后调 <see cref="SaveGameStore.RestoreSaveGame"/> 恢复，所以这里不建会话。</summary>
public sealed class PreparedSavedGame
{
    public SaveGame Game { get; init; } = null!;

    public string MapId { get; init; } = string.Empty;

    public double X { get; init; }

    public double Y { get; init; }

    public string Facing { get; init; } = string.Empty;

    public Content.MapEntry Map { get; init; } = null!;
}
