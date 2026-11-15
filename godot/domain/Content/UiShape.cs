using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using static Inotia4.Domain.Content.ContentShape;

namespace Inotia4.Domain.Content;

/// <summary>
/// 数值表与几份界面目录的形状校验：<c>character-stats.json</c>、<c>menu-panels.json</c>、
/// <c>status-hud.json</c>、<c>control-skin.json</c>、<c>story-interface.json</c>。
///
/// 这几份文件的错法都很安静：数值表少一列装备、菜单少一个装备槽、状态皮肤少一张素材，
/// 表现为「某个格子空着」「血条画歪」，不会抛异常。所以在构建期补一层 schema 与布局断言。
///
/// 素材清单只校验「结构」与「关键条数」，没有逐项列全菜单素材与小地图素材：
/// 那些键名与导出器一一对应，抄一份到 C# 反而会与导出器脱节；
/// 缺键的表现是界面空白，验收时看得见。状态条与操作控件两份清单较短且是布局契约，照列。
/// </summary>
public static class UiShape
{
    /// <summary>状态面板必须齐备的素材分段：缺一张，对应那块面板就画不出来。</summary>
    private static readonly string[] StatusParts =
    [
        "player-shadow", "player-frame", "player-portrait",
        "member-shadow", "member-frame", "member-portrait", "member-portrait-down", "player-portrait-down",
        "health-start", "health-fill", "health-end", "mana-start", "mana-fill", "mana-end",
        "experience-start", "experience-fill", "experience-end",
        "target-start", "target-fill", "target-end", "overhead-frame", "overhead-leader",
    ];

    /// <summary>触屏控件必须齐备的素材分段：摇杆底盘、按钮与方向键的常态／按下态。</summary>
    private static readonly string[] ControlParts =
    [
        "stick-base", "button", "button-pressed", "slot-shadow", "slot-ring",
        "slot-pressed", "quantity-badge", "swap", "swap-pressed", "skip", "skip-pressed",
        "arrow-up", "arrow-up-glow", "arrow-down", "arrow-down-glow",
        "arrow-left", "arrow-left-glow", "arrow-right", "arrow-right-glow",
    ];

    private static readonly string[] EquipmentSlots =
        ["head", "hands", "back", "body", "feet", "main-hand", "off-hand", "neck", "ring", "accessory"];

    private static readonly ShapeCheck RgbColor = (value, path) =>
    {
        Text(value, path);
        var text = Str(value.Node)!;
        Require(text.Length == 7 && text[0] == '#' && IsHex(text.AsSpan(1)), path, "需要六位颜色");
    };

    private static readonly ShapeCheck ModifierRange = Object(Fields(
        ("stat", Text), ("min", Integer), ("max", Integer)));

    private static readonly ShapeCheck EnhancementRule = Object(Fields(
        ("step", Count), ("threshold", Count), ("extra", Integer), ("bonusChance", Optional(Count))));

    private static readonly ShapeCheck CharacterStatsShape = BuildCharacterStats();

    private static readonly ShapeCheck StatusHudShape = BuildStatusHud();

    private static readonly ShapeCheck MenuPanelsShape = BuildMenuPanels();

    private static readonly ShapeCheck StoryInterfaceShape = BuildStoryInterface();

    public static void ValidateCharacterStats(JsonNode? root)
    {
        CharacterStatsShape(new ShapeValue(root), "characterStats");
        if (root is not JsonObject stats) return;

        if (stats["experience"] is not JsonArray experience) return;
        TryDouble(stats["maxLevel"], out var maxLevel);
        Require(experience.Count == maxLevel, "characterStats.experience", "经验表必须覆盖全部等级");
        for (var index = 0; index < experience.Count; index++)
        {
            var last = index == experience.Count - 1;
            Require(last ? experience[index] is null : experience[index] is not null,
                "characterStats.experience", "仅满级可以没有下一级经验");
        }

        if (stats["equipment"] is JsonObject equipment)
            foreach (var (_, numbers) in equipment)
            {
                foreach (var key in new[] { "fixedOptions", "randomOptions" })
                    if (numbers?[key] is JsonArray ranges)
                        foreach (var range in ranges.OfType<JsonObject>())
                        {
                            var stat = Str(range["stat"]) ?? string.Empty;
                            TryDouble(range["min"], out var min);
                            TryDouble(range["max"], out var max);
                            Require(stats["modifiers"]?[stat] is not null && min <= max,
                                "characterStats", "词条类型或数值范围无效");
                        }

                if (TryDouble(numbers?["defaultGrade"], out var defaultGrade))
                    Require(defaultGrade >= 0 && defaultGrade < 15, "characterStats.equipment", "装备品级超出范围");

                if (numbers?["gradeVariants"] is JsonArray variants)
                {
                    Require(variants.Count == 15, "characterStats.equipment", "装备品级档位不完整");
                    foreach (var variant in variants.OfType<JsonObject>())
                    {
                        TryDouble(variant["rarity"], out var rarity);
                        Require(rarity >= 0 && rarity <= 4, "characterStats.equipment", "装备品级档位数值无效");
                        TryDouble(variant["optionCount"], out var optionCount);
                        Require(optionCount >= 0, "characterStats.equipment", "装备品级档位数值无效");
                        TryDouble(variant["socketLimit"], out var socketLimit);
                        Require(socketLimit <= 4, "characterStats.equipment", "装备品级档位数值无效");
                    }
                }

                if (TryDouble(numbers?["defaultRarity"], out var defaultRarity))
                    Require(defaultRarity >= 0 && defaultRarity <= 4, "characterStats.equipment", "装备稀有度超出范围");

                if (numbers?["magicPercent"] is JsonArray magicPercent)
                {
                    Require(magicPercent.Count == 2, "characterStats.equipment", "装备生成范围无效");
                    if (magicPercent.Count == 2 &&
                        TryDouble(magicPercent[0], out var low) && TryDouble(magicPercent[1], out var high))
                        Require(low <= high, "characterStats.equipment", "装备生成范围无效");
                }

                if (numbers?["enhancementUses"] is JsonArray uses)
                {
                    Require(uses.Count == 2, "characterStats.equipment", "装备生成范围无效");
                    if (uses.Count == 2 &&
                        TryDouble(uses[0], out var first) && TryDouble(uses[1], out var last))
                        Require(first <= last, "characterStats.equipment", "装备生成范围无效");
                }

                if (TryDouble(numbers?["socketLimit"], out var socketLimitValue))
                    Require(socketLimitValue <= 4, "characterStats.equipment", "装备生成范围无效");
            }

        if (stats["gems"] is JsonObject gems)
            foreach (var (_, levels) in gems)
            {
                Require(levels is JsonArray { Count: > 0 }, "characterStats.gems", "宝石需要能力等级");
                if (levels is not JsonArray levelArray) continue;
                foreach (var options in levelArray.OfType<JsonArray>())
                {
                    Require(options.Count > 0, "characterStats.gems", "宝石需要候选词条");
                    foreach (var range in options.OfType<JsonObject>())
                    {
                        var stat = Str(range["stat"]) ?? string.Empty;
                        TryDouble(range["min"], out var min);
                        TryDouble(range["max"], out var max);
                        Require(stats["modifiers"]?[stat] is not null && min <= max,
                            "characterStats.gems", "词条类型或数值范围无效");
                    }
                }
            }
    }

    public static void ValidateStatusHud(JsonNode? root)
    {
        StatusHudShape(new ShapeValue(root), "statusHud");
        if (root is not JsonObject hud) return;

        if (hud["assets"] is JsonObject assets)
            foreach (var part in StatusParts)
                Require(assets[part] is not null, "statusHud.assets", $"状态素材缺失 {part}");

        if (hud["levelColors"] is JsonArray colors)
        {
            Require(colors.Count > 0, "statusHud.levelColors", "等级阈值必须按降序排列");
            for (var index = 0; index < colors.Count; index++)
            {
                TryDouble(colors[index]?["minDifference"], out var difference);
                Require(difference == Math.Truncate(difference), "statusHud.levelColors", "等级阈值必须按降序排列");
                if (index == 0) continue;
                TryDouble(colors[index - 1]?["minDifference"], out var previous);
                Require(previous > difference, "statusHud.levelColors", "等级阈值必须按降序排列");
            }
        }

        foreach (var name in new[] { "player", "member" })
        {
            if (hud[name] is not JsonObject panel || panel["inset"] is not JsonObject inset) continue;
            var left = NumberOf(inset["left"]);
            var top = NumberOf(inset["top"]);
            var right = NumberOf(inset["right"]);
            var bottom = NumberOf(inset["bottom"]);
            var width = NumberOf(panel["width"]);
            var height = NumberOf(panel["height"]);
            Require(left >= 0 && top >= 0 && right >= 0 && bottom >= 0 &&
                    left + right < width && top + bottom < height,
                "statusHud.inset", "面板透明留白必须非负且小于面板尺寸");

            foreach (var key in new[] { "portrait", "level" })
                Require(Inside(panel[key], width, height), "statusHud", "布局超出面板");

            if (panel["gauges"] is JsonObject gauges)
                foreach (var (_, gauge) in gauges)
                    Require(GaugeInside(gauge, width), "statusHud", "血条超出面板");
        }

        if (hud["target"] is JsonObject target)
        {
            Require(Inside(target["name"], NumberOf(target["width"]), NumberOf(target["height"])),
                "statusHud", "布局超出面板");
            Require(GaugeInside(target["gauge"], NumberOf(target["width"])), "statusHud", "血条超出面板");
        }

        if (hud["member"]?["slots"] is JsonArray slots)
        {
            Require(slots.Count > 0, "statusHud.member.slots", "队友面板槽位必须递增");
            for (var index = 0; index < slots.Count; index++)
            {
                TryDouble(slots[index], out var slot);
                TryDouble(index == 0 ? null : slots[index - 1], out var previous);
                Require(slot >= 0 && (index == 0 || slot > previous),
                    "statusHud.member.slots", "队友面板槽位必须递增");
            }
        }

        if (hud["buffIcons"] is JsonObject buffIcons)
        {
            TryDouble(buffIcons["size"], out var size);
            TryDouble(buffIcons["step"], out var step);
            Require(size > 0 && step > 0, "statusHud.buffIcons", "状态图标尺寸与间距必须为正");
            if (buffIcons["catalog"] is JsonObject catalog)
                foreach (var (name, file) in catalog)
                    Require(name.Length > 0 && file is not null, "statusHud.buffIcons.catalog", "状态图标条目无效");
        }
    }

    public static void ValidateControlSkin(JsonNode? root)
    {
        Record(AssetPath)(new ShapeValue(root), "controlSkin");
        if (root is not JsonObject skin) return;
        foreach (var part in ControlParts)
            Require(skin[part] is not null, "controlSkin", $"操作素材缺失 {part}");
    }

    public static void ValidateStoryInterface(JsonNode? root)
    {
        StoryInterfaceShape(new ShapeValue(root), "storyInterface");
        if (root is not JsonObject face || face["player"] is not JsonObject player) return;

        Require(NumberOf(player["health"]) <= NumberOf(player["maxHealth"]) &&
                NumberOf(player["mana"]) <= NumberOf(player["maxMana"]) &&
                NumberOf(player["experience"]) < NumberOf(player["nextLevelExperience"]),
            "storyInterface.player", "当前状态超过上限");
    }

    public static void ValidateMenuPanels(JsonNode? root)
    {
        MenuPanelsShape(new ShapeValue(root), "menuPanels");
        if (root is not JsonObject menu) return;

        if (menu["character"]?["combatLabels"] is JsonArray labels)
        {
            var unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (var label in labels) unique.Add(Str(label) ?? string.Empty);
            Require(labels.Count == 11 && unique.Count == 11 &&
                    labels.All(label => (Str(label) ?? string.Empty).Trim().Length > 0),
                "menuPanels.character.combatLabels", "需要十一个独立战斗属性");
        }

        if (menu["skillColumns"] is JsonArray columns)
        {
            var unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (var column in columns) unique.Add(Str(column?["label"]) ?? string.Empty);
            Require(columns.Count == 3 && unique.Count == 3, "menuPanels.skillColumns", "需要三个独立技能列标题");
        }

        if (menu["equipmentSlots"] is JsonArray slots)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var slot in slots) ids.Add(Str(slot?["id"]) ?? string.Empty);
            Require(slots.Count == 10 && ids.Count == 10 && EquipmentSlots.All(ids.Contains),
                "menuPanels.equipmentSlots", "需要十个独立装备槽");
        }

        if (menu["skillPositions"] is JsonArray positions)
        {
            var unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (var position in positions)
            {
                TryDouble(position?["column"], out var column);
                TryDouble(position?["row"], out var row);
                Require(column < 3 && row < 6, "menuPanels.skillPositions", "技能位置超出排列范围");
                unique.Add($"{column},{row}");
            }

            Require(positions.Count == 15 && unique.Count == 15, "menuPanels.skillPositions", "需要十五个独立技能位置");
        }
    }

    private static ShapeCheck BuildCharacterStats()
    {
        var grades = ArrayOf(Object(Fields(
            ("index", Integer), ("name", Text), ("rarity", Integer), ("weight", Integer))));

        var classRow = Object(Fields(
            ("strength", Integer), ("dexterity", Integer), ("vitality", Integer),
            ("intelligence", Integer), ("mentality", Integer)));

        var gradeVariant = Object(Fields(
            ("attack", Integer), ("defense", Integer), ("name", Text),
            ("rarity", Integer), ("optionCount", Integer), ("socketLimit", Integer)));

        var equipment = Object(Fields(
            ("attack", Integer),
            ("defense", Integer),
            ("gradeName", Text),
            ("defaultGrade", Optional(Integer)),
            ("defaultRarity", Optional(Integer)),
            ("gradeVariants", Optional(ArrayOf(gradeVariant))),
            ("magicPercent", ArrayOf(Integer)),
            ("criticalRate", Integer),
            ("hitBonus", Number),
            ("magicAttackRate", Integer),
            ("shieldBlock", Integer),
            ("fixedOptions", ArrayOf(ModifierRange)),
            ("randomOptions", ArrayOf(ModifierRange)),
            ("optionCount", Integer),
            ("socketLimit", Integer),
            ("enhancementUses", ArrayOf(Count)),
            ("fixedEnhancementUses", Nullable(Count))));

        return Object(Fields(
            ("maxLevel", Count),
            ("statPointsPerLevel", Count),
            ("skillPointsPerLevel", Count),
            ("grades", Optional(grades)),
            ("experience", ArrayOf(Nullable(Count))),
            ("classes", Record(classRow)),
            ("modifiers", Record(Object(Fields(("label", Text), ("percent", Flag))))),
            ("equipment", Record(equipment)),
            ("gems", Record(ArrayOf(ArrayOf(ModifierRange)))),
            ("enchantments", Record(Record(EnhancementRule)))));
    }

    private static ShapeCheck BuildStatusHud()
    {
        var hudRect = Object(Fields(
            ("x", Number), ("y", Number), ("width", Positive), ("height", Positive)));

        var hudInset = Object(Fields(
            ("left", Number), ("top", Number), ("right", Number), ("bottom", Number)));

        var hudGauge = Object(Fields(
            ("x", Number), ("y", Number), ("fillOffset", Integer), ("fillWidth", Count),
            ("startWidth", Count), ("endWidth", Count), ("height", Count)));

        var gauges = Object(Fields(
            ("health", hudGauge), ("mana", hudGauge), ("experience", hudGauge)));

        var overheadBar = Object(Fields(
            ("x", Number), ("y", Number), ("width", Count), ("height", Count), ("color", RgbColor)));

        var playerPanel = Object(Fields(
            ("width", Count), ("height", Count), ("inset", hudInset),
            ("portrait", hudRect), ("level", hudRect), ("gauges", gauges)));

        var memberPanel = Object(Fields(
            ("slots", ArrayOf(Count)), ("width", Count), ("height", Count), ("inset", hudInset),
            ("portrait", hudRect), ("level", hudRect), ("gauges", gauges)));

        var buffIcons = Object(Fields(
            ("size", Count), ("step", Count), ("catalog", Record(AssetPath))));

        var targetPanel = Object(Fields(
            ("width", Count), ("height", Count), ("name", hudRect), ("gauge", hudGauge)));

        var overhead = Object(Fields(
            ("frame", hudRect), ("leader", hudRect), ("health", overheadBar), ("mana", overheadBar)));

        var levelColor = Object(Fields(("minDifference", Number), ("color", RgbColor)));

        return Object(Fields(
            ("assets", Record(AssetPath)),
            ("player", playerPanel),
            ("member", memberPanel),
            ("buffIcons", buffIcons),
            ("target", targetPanel),
            ("overhead", overhead),
            ("levelColors", ArrayOf(levelColor)),
            ("lowestLevelColor", RgbColor)));
    }

    private static ShapeCheck BuildMenuPanels()
    {
        var equipmentSlot = Object(Fields(
            ("id", Text), ("name", Text), ("x", Number), ("y", Number), ("emptyIcon", AssetPath)));

        var skillPosition = Object(Fields(("column", Integer), ("row", Integer)));

        var skillColumn = Object(Fields(("label", Text), ("image", AssetPath)));

        var classEntry = Object(Fields(("preview", AssetPath)));

        var portrait = Object(Fields(("image", AssetPath), ("partyIcon", AssetPath)));

        var characterLabels = Object(Fields(
            ("statPoints", Text), ("skillPoints", Text), ("health", Text),
            ("mana", Text), ("experience", Text)));

        var character = Object(Fields(
            ("portraits", Record(portrait)), ("combatLabels", ArrayOf(Text)), ("labels", characterLabels)));

        var skillPanelLabels = Object(Fields(
            ("counterattackTitle", Text), ("initialSkillPoints", Text), ("resetButton", Text)));

        var skillPanel = Object(Fields(("labels", skillPanelLabels)));

        return Object(Fields(
            ("itemMessages", Record(Text)),
            ("assets", Record(AssetPath)),
            ("equipmentSlots", ArrayOf(equipmentSlot)),
            ("skillPositions", ArrayOf(skillPosition)),
            ("skillColumns", ArrayOf(skillColumn)),
            ("classes", Record(classEntry)),
            ("character", character),
            ("skillPanel", skillPanel)));
    }

    private static ShapeCheck BuildStoryInterface()
    {
        var player = Object(Fields(
            ("level", Count), ("health", Integer), ("maxHealth", Count),
            ("mana", Integer), ("maxMana", Count),
            ("experience", Integer), ("nextLevelExperience", Count)));

        return Object(Fields(
            ("assets", Record(AssetPath)),
            ("portraits", Record(AssetPath)),
            ("player", player)));
    }

    private static bool Inside(JsonNode? rect, double width, double height) =>
        rect is JsonObject && NumberOf(rect["x"]) >= 0 && NumberOf(rect["y"]) >= 0 &&
        NumberOf(rect["x"]) + NumberOf(rect["width"]) <= width &&
        NumberOf(rect["y"]) + NumberOf(rect["height"]) <= height;

    private static bool GaugeInside(JsonNode? gauge, double width) =>
        gauge is JsonObject && NumberOf(gauge["x"]) >= 0 && NumberOf(gauge["y"]) >= 0 &&
        NumberOf(gauge["x"]) + NumberOf(gauge["fillOffset"]) + NumberOf(gauge["fillWidth"]) +
        NumberOf(gauge["endWidth"]) <= width;

    private static double NumberOf(JsonNode? node)
    {
        TryDouble(node, out var value);
        return value;
    }

    private static bool IsHex(ReadOnlySpan<char> text)
    {
        foreach (var character in text)
            if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f') ||
                  (character >= 'A' && character <= 'F')))
                return false;
        return true;
    }
}
