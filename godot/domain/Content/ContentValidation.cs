using System.Text.Json;
using Inotia4.Domain.Audio;
using Inotia4.Domain.Character;
using Inotia4.Domain.Combat;
using Inotia4.Domain.Party;
using Inotia4.Domain.Story;

namespace Inotia4.Domain.Content;

/// <summary>
/// 内容关系校验：只管「引用的东西在不在」，不管字段形状。
///
/// 形状由反序列化保证：该是数字的是数字、该是数组的是数组。但「引用的 id 存不存在」
/// 反序列化管不了——写错一个技能名、商店卖一件不存在的物品，都会静默变成空引用，
/// 直到玩家点开那个面板才炸。
///
/// 所以这里只做关系校验：解析阶段顺带做的那些检查不在这里重复实现。
/// 校验失败一律抛 <see cref="InvalidDataException"/>，消息里带出问题的位置与原因。
/// </summary>
public static class ContentValidation
{
    private static void Require(bool condition, string path, string message)
    {
        if (!condition) throw new InvalidDataException($"内容无效 {path}: {message}");
    }

    /// <summary>
    /// 物品技能候选与职业技能表逐项对齐。
    ///
    /// 两份表分别来自 items.json 与 battle.json，顺序也必须一致——技能树的候选顺序决定
    /// 玩家点出来的技能顺序，错一位就是「点了火球学到冰箭」。
    /// </summary>
    public static void ValidateItemSkills(ItemCatalog items, SkillCatalog skills)
    {
        Require(items.ClassSkills.Count == skills.Classes.Length, "items.classSkills", "职业数量不匹配");

        foreach (var character in skills.Classes)
        {
            var candidates = items.ClassSkills.GetValueOrDefault(character.Id);
            Require(candidates is not null, character.Id, "物品技能候选缺少这个职业");
            Require(candidates!.SequenceEqual(character.SkillIds), character.Id, "物品技能候选与职业不匹配");
        }
    }

    public static void ValidateShops(ShopCatalog shops, ItemCatalog items, WorldIndex world)
    {
        foreach (var (id, shop) in shops.Shops)
        {
            Require(id == shops.LampShop || world.Actors.ContainsKey(id), id, "商店人物不存在");
            foreach (var item in shop.FixedStock)
                Require(items.Items.TryGetValue(item, out var entry) && entry.Pickup is null, id,
                    $"固定商品不存在 {item}");
        }

        foreach (var profile in shops.Profiles.Values)
        foreach (var pool in profile.Pools.Values)
        foreach (var choice in pool)
        {
            var candidates = choice.Tiers is { Length: > 0 } tiers
                ? tiers.Select(tier => tier.Item)
                : [choice.Item];
            foreach (var item in candidates)
                if (item is { Length: > 0 })
                    Require(items.Items.ContainsKey(item), "shops.profiles", $"随机商品不存在 {item}");
        }

        foreach (var id in shops.Equipment.Keys)
            Require(items.Items.TryGetValue(id, out var entry) && entry.Equipment is not null, id, "随机装备不存在");

        foreach (var map in world.Maps)
            Require(shops.MapShopLevels.ContainsKey(map.Id), map.Id, "地图商品等级不存在");
    }

    public static void ValidateCharacterStats(CharacterStatsCatalog stats, ItemCatalog items, SkillCatalog skills)
    {
        foreach (var character in skills.Classes)
            Require(stats.Classes.ContainsKey(character.Id), "characterStats", $"职业缺失 {character.Id}");

        foreach (var (id, item) in items.Items)
        {
            if (item.Equipment is not null)
                Require(stats.Equipment.ContainsKey(id), "characterStats", $"装备缺失 {id}");
            if (item.Use?.Kind == ItemUseRules.Socket)
                Require(stats.Gems.ContainsKey(id), "characterStats", $"宝石缺失 {id}");
        }

        foreach (var id in stats.Equipment.Keys)
            Require(items.Items.TryGetValue(id, out var entry) && entry.Equipment is not null, "characterStats",
                $"装备引用无效 {id}");

        foreach (var id in stats.Gems.Keys)
            Require(items.Items.TryGetValue(id, out var entry) && entry.Use?.Kind == ItemUseRules.Socket,
                "characterStats", $"宝石引用无效 {id}");

        foreach (var (id, kinds) in stats.Enchantments)
        {
            Require(items.Items.TryGetValue(id, out var entry) && entry.Use?.Kind == ItemUseRules.Enchant,
                "characterStats", $"卷轴引用无效 {id}");

            foreach (var kind in kinds.Keys)
            {
                var found = items.Items.Values.Any(item => item.Equipment is not null && item.Kind == kind);
                Require(found, "characterStats", $"装备类型无效 {kind}");
            }
        }
    }

    public static void ValidateStoryItems(CampaignContent campaign, ItemCatalog items)
    {
        foreach (var quest in campaign.Quests)
        {
            foreach (var goal in quest.Goals)
                if (goal.Kind is "item" or "recruit")
                    Require(items.Items.ContainsKey(goal.Target), quest.Id, "任务目标物品不存在");

            foreach (var handover in quest.OnAcceptItems.Concat(quest.HandoverItems))
                Require(items.Items.ContainsKey(handover.Item), quest.Id, "任务收付物品不存在");

            if (quest.Rewards is not { } rewards) continue;
            foreach (var reward in rewards.Items)
            {
                Require(items.Items.ContainsKey(reward.Item), quest.Id, $"奖励物品不存在 {reward.Item}");
                foreach (var classId in reward.ClassIds)
                    Require(items.ClassSkills.ContainsKey(classId), quest.Id, $"奖励职业不存在 {classId}");
            }
        }

        foreach (var script in campaign.Scripts.Values)
        foreach (var node in script.Nodes)
        foreach (var command in node.Commands)
        {
            var type = StoryType.Text(command, "type");
            if (type is not ("item-give" or "item-take" or "item-drop" or "equip" or "unequip-destroy"))
                continue;

            var item = StoryType.Text(command, "item");
            var found = items.Items.TryGetValue(item, out var entry);
            Require(found, node.Id, "剧情命令物品不存在");
            if (type == "equip")
                Require(entry!.Equipment is not null, node.Id, "剧情穿戴物品需要是装备");
        }
    }

    /// <summary>
    /// 伙伴表：外观能在世界里走（有碰撞盒）、职业与四向动作齐、
    /// 技能动作齐、成长表满级、品质合法、固定技能与职业一致；训练表与职业绑定、等级递增；
    /// 徽章品质概率合计 100；佣兵团技能池四项齐全、属性骰两档各五项；
    /// 最后是剧情脚本里 <c>party-join</c>／<c>party-leave</c> 引用的伙伴必须登记成剧情伙伴。
    /// </summary>
    public static void ValidateCompanions(CompanionCatalog catalog, WorldIndex world, SkillCatalog skills,
        CharacterStatsCatalog stats, CampaignContent? campaign = null)
    {
        string[] directions = ["north", "south", "east", "west"];

        foreach (var (id, definition) in catalog.Definitions)
        {
            Require(id == definition.Id, id, "伙伴标识不一致");

            var hasActor = world.Actors.TryGetValue(definition.Actor, out var actor);
            Require(hasActor && actor!.Collision is not null, id, "伙伴缺少可移动外观");

            var character = skills.Classes.FirstOrDefault(entry => entry.Id == definition.ClassId);
            Require(character is not null, id, "伙伴职业不存在");

            foreach (var clip in new[] { "idle", "walk", "attack" })
            foreach (var direction in directions)
                Require(actor!.Clips.ContainsKey($"{clip}-{direction}"), id, $"伙伴动作缺失：{clip}-{direction}");

            foreach (var skillId in character!.SkillIds)
                if (skills.Skills.TryGetValue(skillId, out var skill) && skill.Action is not null)
                    foreach (var direction in directions)
                        Require(actor!.Clips.ContainsKey($"skill-{skillId}-{direction}"), id,
                            $"伙伴技能动作缺失：{skillId}-{direction}");

            Require(catalog.Growth.TryGetValue(definition.ClassId, out var growth) &&
                    growth.Length == stats.MaxLevel, id, "伙伴成长等级不完整");
            Require(definition.Quality is null or <= 3, id, "佣兵品质无效");

            foreach (var skillId in definition.FixedSkills)
                Require(catalog.Generation.Training.TryGetValue(skillId, out var training) &&
                        training.ClassId == definition.ClassId, id, "固定技能职业不一致");
        }

        foreach (var (id, training) in catalog.Generation.Training)
        {
            var character = skills.Classes.FirstOrDefault(entry => entry.Id == training.ClassId);
            Require(character is not null && character.SkillIds.Contains(id), id, "佣兵成长技能不存在");
            Require(skills.Skills.TryGetValue(id, out var skill) &&
                    training.RequiredLevels.Length == training.MaxLevel &&
                    training.MaxLevel <= skill.NormalMaxLevel, id, "佣兵技能成长不完整");
            Require(training.RequiredLevels
                    .Select((level, index) => index == 0 || level > training.RequiredLevels[index - 1])
                    .All(ok => ok), id, "佣兵技能成长等级需要递增");
        }

        foreach (var (id, emblem) in catalog.Generation.Emblems)
            Require(emblem.QualityWeights.Length == 4 && emblem.QualityWeights.Sum() == 100, id,
                "徽章品质概率需要合计100");

        foreach (var (classId, pool) in catalog.Generation.GroupPools)
        {
            Require(pool.Length == 4 && pool.All(id => catalog.GroupSkills.ContainsKey(id)), classId,
                "佣兵团技能池不完整");
            Require(catalog.Generation.StatDice.TryGetValue(classId, out var dice) &&
                    dice.Ordinary.Length == 5 && dice.Rare.Length == 5, classId, "佣兵属性骰子不完整");
        }

        if (campaign is null) return;
        foreach (var script in campaign.Scripts.Values)
        foreach (var node in script.Nodes)
        foreach (var command in node.Commands)
        {
            var type = StoryType.Text(command, "type");
            if (type is not ("party-join" or "party-leave")) continue;

            var companion = StoryType.Text(command, "companion");
            Require(catalog.Definitions.TryGetValue(companion, out var definition) &&
                    definition.SourceOrDefault == "story", companion, "剧情伙伴未定义");
        }
    }

    /// <summary>
    /// 对白立绘覆盖：人物定义里挂了 <c>portrait</c> 键的，
    /// 那个键必须在界面资源表里有图。
    ///
    /// 触屏版要给每个出场人物都备一张立绘，所以这里逐个人物查——对白取图时按的也是这个键。
    /// 缺图不会崩：那一句只是没立绘，玩家看到的是「这个人怎么没有脸」。
    /// </summary>
    public static void ValidateStoryPortraits(WorldIndex world, StoryInterfaceAssets face)
    {
        foreach (var (id, actor) in world.Actors)
            if (actor.Portrait is { Length: > 0 } key)
                Require(face.Portraits.ContainsKey(key), id, $"缺少立绘 {key}");
    }

    public static void ValidateMenuClasses(MenuPanelsCatalog menu, SkillCatalog skills)
    {
        Require(menu.Classes.Count == skills.Classes.Length, "menuPanels.classes", "职业数量不匹配");
        foreach (var character in skills.Classes)
            Require(menu.Classes.ContainsKey(character.Id) &&
                    character.SkillIds.Length == menu.SkillPositions.Length,
                character.Id, "缺少职业菜单资料或技能排列不匹配");
    }

    private static readonly string[] Facings = ["north", "south", "east", "west"];

    /// <summary>
    /// 换装目录：穿在身上的每一件都得有外观，动作里用到的样式都得备齐。
    ///
    /// ① 每件能穿的装备都要有外观配方，否则穿上身还是原来那身；
    /// ② 动作里最深的那一格决定每个部件需要几种样式，外观点到的样式不能少于它，否则出手时缺件；
    /// ③ 技能姿态的键必须是真的职业，且四向姿态的帧数要等于人物片段——对不上就是「动作放一半僵住」；
    /// ④ 普攻（含双持与狂气）用到的特效都要在特效表里，否则挥出去没有刀光。
    /// </summary>
    public static void ValidateWardrobe(WardrobeCatalog wardrobe, ItemCatalog items, SkillCatalog skills,
        BattleContent? battle = null)
    {
        foreach (var (id, item) in items.Items)
            if (item.Equipment is not null)
                Require(wardrobe.Equipment.ContainsKey(id), id, "缺少装备外观");

        var required = new Dictionary<string, int>();
        foreach (var pose in wardrobe.Poses.Values)
        foreach (var frame in pose.Frames)
        foreach (var part in frame)
            required[part.Part] = Math.Max(required.GetValueOrDefault(part.Part), part.Piece + 1);

        foreach (var (id, appearance) in wardrobe.Equipment)
        {
            Require(items.Items.TryGetValue(id, out var item) && item.Equipment is not null,
                id, "外观引用的装备不存在");
            foreach (var (part, variant) in appearance.Main.Concat(appearance.OffHand))
            {
                Require(wardrobe.Variants.TryGetValue(variant, out var pieces), id, $"服装样式不存在 {variant}");
                Require(pieces!.Count >= required.GetValueOrDefault(part), id, $"动作部件超出服装样式 {part}");
            }
        }

        foreach (var classId in wardrobe.SkillStances.Keys)
            Require(skills.Classes.Any(character => character.Id == classId),
                "wardrobe.skillStances", $"职业不存在 {classId}");

        foreach (var character in skills.Classes)
        foreach (var skillId in character.SkillIds)
        {
            if (!skills.Skills.TryGetValue(skillId, out var skill) || skill.Action is null) continue;

            foreach (var facing in Facings)
            {
                var key = $"skill-{skillId}-{facing}";
                var frames = 0;
                if (wardrobe.Skills.TryGetValue(character.Id, out var poses) &&
                    poses.TryGetValue(key, out var poseName) &&
                    wardrobe.Poses.TryGetValue(poseName, out var pose))
                    frames = pose.Frames.Count;
                var clipFrames = character.Actor.Clips.TryGetValue(key, out var clip) ? clip.Frames : 0;
                Require(frames > 0 && frames == clipFrames, "wardrobe.skills", $"技能动画缺失或时长不匹配 {key}");
            }
        }

        if (battle is null) return;

        foreach (var attack in WardrobeAttacks(wardrobe))
        {
            foreach (var effect in attack.Swings.Concat(attack.Impacts)
                         .Append(attack.Projectile?.Effect ?? string.Empty))
                if (effect.Length > 0)
                    Require(battle.AttackEffects.ContainsKey(effect), "wardrobe.attacks", $"普攻特效缺失 {effect}");
        }
    }

    public static void ValidateAudio(AudioCatalog audio, ItemCatalog items, WorldIndex world,
        BattleContent battle, SkillCatalog skills, WardrobeCatalog wardrobe)
    {
        Require(audio.Version == 3 && audio.Music.Count > 0 && audio.Effects.Count > 0, "audio", "音频目录不完整");
        Require(audio.Music.ContainsKey(audio.MenuMusic), "audio.menuMusic", "主菜单背景音乐缺失");
        Require(audio.Maps.Count == world.Maps.Length, "audio.maps", "音频对象映射不完整");
        Require(audio.Enemies.Count == battle.Enemies.Length, "audio.enemies", "音频对象映射不完整");

        foreach (var entry in world.Maps)
        {
            Require(audio.Maps.TryGetValue(entry.Id, out var mapAudio), entry.Id, "地图音频缺失");
            if (mapAudio!.Music is { } music)
                Require(audio.Music.ContainsKey(music), entry.Id, $"地图背景音乐缺失 {music}");
            if (mapAudio.Ambience is { } ambience)
                Require(audio.Effects.ContainsKey(ambience), entry.Id, $"地图环境音缺失 {ambience}");
        }

        foreach (var enemy in battle.Enemies)
        {
            Require(audio.Enemies.TryGetValue(enemy.Id, out var enemyAudio), enemy.Id, "怪物音效缺失");
            foreach (var cue in new[] { enemyAudio!.Attack, enemyAudio.Death, enemyAudio.Struck })
                if (cue is { Length: > 0 })
                    Require(audio.Effects.ContainsKey(cue), enemy.Id, $"怪物音效文件缺失 {cue}");
        }

        var weaponKinds = new HashSet<string> { "unarmed" };
        foreach (var item in items.Items.Values)
            if (item.Equipment?.Slot == "main-hand")
                weaponKinds.Add(item.Kind);

        foreach (var kind in weaponKinds)
        {
            Require(audio.Combat.Weapons.TryGetValue(kind, out var weapon) && weapon!.Normal.Length > 0,
                kind, "武器反馈缺失");
            foreach (var cue in weapon!.Normal)
            {
                Require(audio.Effects.ContainsKey(cue.Sound), kind, $"武器反馈音效缺失 {cue.Sound}");
                Require(cue.Weight > 0, kind, "音效权重无效");
            }

            foreach (var cue in new[] { weapon.Critical, weapon.Blocked, weapon.Miss })
                if (cue is { Length: > 0 })
                    Require(audio.Effects.ContainsKey(cue), kind, $"武器反馈音效缺失 {cue}");
        }

        foreach (var cue in new[] { audio.Combat.ShieldBlock, audio.Combat.PeriodicDamage })
            Require(cue.Length > 0 && audio.Effects.ContainsKey(cue), "audio.combat", $"战斗音效缺失 {cue}");

        foreach (var skill in skills.Skills.Values)
        {
            if (skill.Kind != "active") continue;
            var cue = audio.Skills.GetValueOrDefault(skill.Id);
            Require(cue is not null && audio.Effects.ContainsKey(cue), skill.Id, "人物技能音效缺失");
        }

        foreach (var skill in skills.Skills.Values)
        {
            CheckActionSounds(skill.Action, audio);
            CheckActionSounds(skill.FollowUpAction, audio);
            CheckActionSounds(skill.Companion?.Action, audio);
            CheckActionSounds(skill.Companion?.FollowUpAction, audio);
        }

        foreach (var stances in wardrobe.SkillStances.Values)
        foreach (var stance in stances.Values)
        foreach (var action in stance.Actions.Values)
            CheckActionSounds(action, audio);

        foreach (var attack in WardrobeAttacks(wardrobe))
            if (attack.Dispatch?.Sound is { Length: > 0 } dispatchSound)
                Require(audio.Effects.ContainsKey(dispatchSound), "wardrobe.attacks", $"动作派发音效缺失 {dispatchSound}");

        Require(audio.Ui.Enchant == "UI_Enchant" && audio.Ui.Craft == "UI_Craft", "audio.ui", "装备加工音效缺失");
        Require(audio.Effects.ContainsKey(audio.Ui.ChestOpen), "audio.ui", "宝箱开启音效缺失");
        foreach (var name in UiSoundNames)
        {
            var cue = audio.Ui.Of(name);
            Require(cue.Length > 0 && audio.Effects.ContainsKey(cue), "audio.ui", $"界面音效缺失 {name}");
        }
    }

    /// <summary>
    /// 地图与剧情之间的引用：
    ///
    /// ① 每张图的出口目标、摆放的人物、景物贴图、刷怪点都要能对上真实目录，刷怪点标识全局唯一；
    /// ② 有宝箱的地图要写掉落等级（0..255），否则开出来的东西没有档位；
    /// ③ 剧情起点、剧情摆放、交互后剧本与触发事件引用的地图／人物／剧本都要存在。
    ///
    /// 这些错通常表现为「走到出口没反应」「这个人不在」「宝箱里没有东西」，
    /// 而且只有玩到那张图才发现。这套检查只在构建期跑一次（要读全部 416 张图）。
    /// </summary>
    public static void ValidateMaps(WorldIndex world, Func<string, MapDefinition> loadMap,
        BattleContent battle, CampaignContent campaign)
    {
        var mapIds = world.Maps.Select(entry => entry.Id).ToHashSet();
        var enemyIds = battle.Enemies.Select(enemy => enemy.Id).ToHashSet();
        var propKeys = world.Props.Select(prop => prop.Key).ToHashSet();
        var spawnIds = new HashSet<string>();
        var extents = new Dictionary<string, (int Width, int Height)>(StringComparer.Ordinal);

        foreach (var entry in world.Maps)
        {
            var map = loadMap(entry.Id);
            extents[entry.Id] = (map.Width, map.Height);

            foreach (var exit in map.Exits)
                Require(mapIds.Contains(exit.Destination), entry.Id, $"出口目标缺失 {exit.Destination}");

            foreach (var entity in map.Entities)
            {
                Require(world.Actors.TryGetValue(entity.Actor, out var actor), entry.Id, $"人物缺失 {entity.Actor}");
                if (actor!.Chest is not null)
                    Require(map.LootLevel is >= 0 and <= 255, entry.Id, "宝箱地图掉落等级缺失");
            }

            foreach (var feature in map.Features)
                Require(propKeys.Contains(feature.Texture), entry.Id, $"场景物件缺失 {feature.Texture}");

            foreach (var spawn in map.EnemySpawns)
            {
                Require(enemyIds.Contains(spawn.EnemyId), entry.Id, $"敌人定义缺失 {spawn.EnemyId}");
                Require(spawnIds.Add(spawn.Id), entry.Id, $"敌人出生点标识重复 {spawn.Id}");
            }
        }

        Require(campaign.Start.Map is { } startMap && mapIds.Contains(startMap) &&
                world.Actors.ContainsKey(campaign.Start.Actor) &&
                campaign.Start.Script is { } startScript && campaign.Scripts.ContainsKey(startScript),
            "campaign.start", "剧情起点不完整");

        foreach (var placement in campaign.PlacementTable.Concat(campaign.EntryPlacements))
        {
            Require(mapIds.Contains(placement.Map) && world.Actors.ContainsKey(placement.Actor),
                placement.Id, "剧情人物缺失");
            if (placement.AfterInteractScript is { } script)
                Require(campaign.Scripts.ContainsKey(script), placement.Id, $"交互剧情缺失 {script}");
        }

        foreach (var trigger in campaign.EventTable.Triggers)
        {
            Require(campaign.Scripts.ContainsKey(trigger.Script), trigger.Id, "剧情事件缺剧本");
            if (trigger.Map.Length > 0)
                Require(mapIds.Contains(trigger.Map), trigger.Id, $"剧情事件地图缺失 {trigger.Map}");
        }

        ValidateStoryPlaces(campaign, mapIds.Contains, id => extents.GetValueOrDefault(id));
    }

    public static void ValidateStoryPlaces(CampaignContent campaign, Func<string, bool> mapExists,
        Func<string, (int Width, int Height)?> extentOf)
    {
        void Point(string map, float x, float y, string owner)
        {
            Require(mapExists(map), owner, $"地点地图缺失 {map}");
            Require(extentOf(map) is { } extent && x >= 0 && x < extent.Width && y >= 0 && y < extent.Height,
                owner, $"地点坐标超出地图 {map}");
        }

        void Spot(JsonElement spot, string owner)
        {
            var map = StoryType.Text(spot, "map");
            if (Number(spot, "x") is not { } x || Number(spot, "y") is not { } y)
            {
                Require(false, owner, "地点坐标无效");
                return;
            }

            Point(map, x, y, owner);
        }

        void Place(JsonElement place, string owner)
        {
            if (place.ValueKind != JsonValueKind.Object) return;

            switch (StoryType.Text(place, "type"))
            {
                case "fixed":
                    Spot(place, owner);
                    return;
                case "alternatives":
                    if (!place.TryGetProperty("spots", out var spots) || spots.ValueKind != JsonValueKind.Array)
                        return;
                    var index = 0;
                    foreach (var spot in spots.EnumerateArray()) Spot(spot, $"{owner}[{index++}]");
                    return;
            }
        }

        foreach (var trigger in campaign.EventTable.Triggers)
        foreach (var spot in trigger.Spots)
            Point(spot.Map, spot.Point.X, spot.Point.Y, trigger.Id);

        foreach (var quest in campaign.Quests)
        {
            if (quest.Places.ValueKind != JsonValueKind.Object) continue;
            foreach (var phase in quest.Places.EnumerateObject())
                Place(phase.Value, $"{quest.Id}:{phase.Name}");
        }
    }

    /// <summary>地点坐标必须是真数：缺字段、写字符串、NaN 与无穷都不算。</summary>
    private static float? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetSingle(out var number) && float.IsFinite(number)
            ? number
            : null;

    private static readonly string[] UiSoundNames =
        ["open", "move", "select", "questStart", "questComplete", "heal", "loot", "itemMove", "chestOpen",
         "enchant", "craft"];

    private static IEnumerable<BasicAttackDefinition> WardrobeAttacks(WardrobeCatalog wardrobe)
    {
        foreach (var attack in wardrobe.Attacks.Values) yield return attack;
        foreach (var attack in wardrobe.DualWieldAttacks?.Values ?? Enumerable.Empty<WardrobeAttack>()) yield return attack;
        foreach (var attack in wardrobe.FrenzyAttacks?.Values ?? Enumerable.Empty<WardrobeAttack>()) yield return attack;
    }

    private static void CheckActionSounds(SkillAction? action, AudioCatalog audio)
    {
        if (action is null) return;
        foreach (var sound in SkillActionRules.SkillSoundNames(action))
            Require(audio.Effects.ContainsKey(sound), "audio.effects", $"动作派发音效缺失 {sound}");
    }
}
