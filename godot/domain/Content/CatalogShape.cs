using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using static Inotia4.Domain.Content.ContentShape;

namespace Inotia4.Domain.Content;

public static class CatalogShape
{
    private static readonly string[] ItemCategories = ["equipment", "consumable", "material", "quest"];

    private static readonly string[] EquipmentSlots =
        ["head", "hands", "back", "body", "feet", "main-hand", "off-hand", "neck", "ring", "accessory"];

    private static readonly string[] ItemUses =
    [
        "recover-health", "consume", "enchant", "socket", "purify", "pack", "box", "unseal", "learn",
        "regenerate", "reset-skills", "recruit", "merchant", "revive", "recipe", "dungeon",
    ];

    private static readonly string[] PriceKinds = ["weapon", "armor", "accessory", "fixed"];

    private static readonly ShapeCheck Rgb = (value, path) =>
    {
        Text(value, path);
        var text = Str(value.Node)!;
        Require(text.Length == 7 && text[0] == '#' && IsHex(text.AsSpan(1)), path, "需要 RGB 颜色");
    };

    private static readonly ShapeCheck DescriptionParts = ArrayOf(Object(Fields(
        ("text", Text), ("color", Optional(Rgb)))));

    private static readonly ShapeCheck Strings = ArrayOf(Text);

    private static readonly ShapeCheck ItemReward = Object(Fields(("item", Text), ("quantity", Count)));

    private static readonly ShapeCheck DropDirection = Object(Fields(
        ("name", Text), ("x", Number), ("y", Number), ("clip", WorldShape.EffectClipShape)));

    private static readonly ShapeCheck Item = BuildItem();

    private static readonly ShapeCheck ItemCatalogShape = Object(Fields(
        ("items", Record(Item)),
        ("startingEquipment", Record(Record(Text))),
        ("startingInventory", ArrayOf(ItemReward)),
        ("startingShortcut", Text),
        ("dualWieldClasses", Strings),
        ("merchantItems", Strings),
        ("classSkills", Record(Strings)),
        ("skillDescriptions", Optional(Record(Object(Fields(("name", Text), ("className", Text)))))),
        ("recipes", Record(Object(Fields(("name", Text), ("result", Text),
            ("materials", ArrayOf(ItemReward))))))));

    private static readonly ShapeCheck StockRule = Variant("kind", new Dictionary<string, ShapeCheck>
    {
        ["level-range"] = Object(Fields(
            ("kind", OneOf("level-range")), ("minimumLevel", Integer), ("maximumLevel", Integer),
            ("category", OneOf("weapons", "armor")))),
        ["random-stock"] = Object(Fields(
            ("kind", OneOf("random-stock")), ("attempts", Count), ("levelRadius", Integer),
            ("profile", OneOf("black-market", "lamp")))),
        ["rare-equipment"] = Object(Fields(("kind", OneOf("rare-equipment")), ("attempts", Count))),
    });

    private static readonly ShapeCheck NullableItem = (value, path) =>
    {
        if (value.Present && value.Node is null) return;
        Text(value, path);
    };

    private static readonly ShapeCheck ShopPoolEntry = Object(Fields(
        ("weight", Integer),
        ("item", Optional(NullableItem)),
        ("tiers", Optional(ArrayOf(Object(Fields(("item", NullableItem), ("level", Integer))))))));

    private static readonly ShapeCheck ShopEquipmentVariant = Object(Fields(
        ("attack", Integer), ("defense", Integer), ("gradeName", Text),
        ("optionCount", Integer), ("socketLimit", Integer)));

    private static readonly ShapeCheck ShopEquipmentEntry = Object(Fields(
        ("defaultGrade", Integer), ("randomGrade", Flag), ("dropLevel", Number),
        ("randomGroup", OneOf("weapon", "armor", "accessory", "none")),
        ("randomListed", Flag), ("uniqueListed", Flag),
        ("variants", Optional(ArrayOf(ShopEquipmentVariant))),
        ("concealedPrices", ArrayOf(Integer))));

    private static readonly ShapeCheck ShopProfile = Object(Fields(
        ("pools", Record(ArrayOf(ShopPoolEntry))),
        ("chances", Object(Fields(
            ("generic", Integer), ("equipment", Integer), ("consumable", Integer),
            ("upgrade", Integer), ("crafting", Integer), ("misc", Integer)))),
        ("uniqueChanceByLevel", ArrayOf(Integer))));

    private static readonly ShapeCheck ShopCatalogShape = Object(Fields(
        ("shops", Record(Object(Fields(
            ("name", Text), ("priceRatePercent", Count), ("fixedStock", Strings),
            ("categories", Strings), ("stockRules", ArrayOf(StockRule)))))),
        ("lampShop", Text),
        ("mapShopLevels", Record(Integer)),
        ("categories", Record(Object(Fields(("name", Text), ("group", OneOf("weapons", "armor")))))),
        ("assets", Record(AssetPath)),
        ("profiles", Record(ShopProfile)),
        ("equipment", Optional(Record(ShopEquipmentEntry))),
        ("grades", ArrayOf(Object(Fields(
            ("name", Text), ("armorName", Optional(Text)), ("rarity", Integer), ("weight", Integer))))),
        ("rarityWeights", Object(Fields(("ordinary", ArrayOf(Integer)), ("rare", ArrayOf(Integer)))))));

    private static readonly ShapeCheck Companion = Object(Fields(
        ("defaultShield", Optional(Flag)),
        ("id", Text), ("name", Text), ("actor", Text), ("classId", Text),
        ("elite", Flag),
        ("source", Optional(OneOf("mercenary", "story"))),
        ("quality", Optional(Integer)),
        ("recruitable", Optional(Flag)),
        ("fixedSkills", Optional(ArrayOf(Text)))));

    private static readonly ShapeCheck GrowthRow = Object(Fields(
        ("strength", Integer), ("dexterity", Integer), ("vitality", Integer),
        ("intelligence", Integer), ("mentality", Integer)));

    private static readonly ShapeCheck DiceEntry = Object(Fields(("sides", Count), ("count", Count)));

    private static readonly ShapeCheck CompanionGeneration = Object(Fields(
        ("emblems", Record(Object(Fields(
            ("classId", Nullable(Text)),
            ("qualityWeights", ArrayOf(Integer)),
            ("guaranteedRare", Flag))))),
        ("training", Record(Object(Fields(
            ("classId", Text), ("position", Integer), ("maxLevel", Count),
            ("requiredLevels", ArrayOf(Count)))))),
        ("skillChoices", ArrayOf(ArrayOf(Integer))),
        ("skillCountWeights", ArrayOf(ArrayOf(ArrayOf(Integer)))),
        ("statDice", Record(Object(Fields(
            ("ordinary", ArrayOf(DiceEntry)), ("rare", ArrayOf(DiceEntry)))))),
        ("groupPools", Record(ArrayOf(Text)))));

    private static readonly ShapeCheck CompanionCatalogShape = Object(Fields(
        ("definitions", Record(Companion)),
        ("growth", Record(ArrayOf(GrowthRow))),
        ("generation", CompanionGeneration),
        ("groupSkills", Record(Object(Fields(
            ("id", Text), ("effect", Text), ("name", Text), ("description", Text), ("icon", Text),
            ("value", Integer),
            ("unit", OneOf("drop-ten-thousandth", "milliseconds", "percent", "flag", "per-thousand")))))),
        ("autoSkills", Record(Object(Fields(
            ("kind", OneOf("chance", "combat-chance", "heal", "ally-combat", "emergency-heal"))))))));

    private static readonly ShapeCheck PlayerPreview = Object(Fields(
        ("level", Count), ("health", Integer), ("maxHealth", Count),
        ("mana", Integer), ("maxMana", Count),
        ("experience", Integer), ("nextLevelExperience", Count)));

    private static readonly ShapeCheck StoryClassAssetSet = Object(Fields(
        ("selected", AssetPath), ("title", AssetPath), ("back", AssetPath),
        ("start", AssetPath), ("startPressed", AssetPath),
        ("descriptionTop", AssetPath), ("attributesTop", AssetPath),
        ("panelLeft", AssetPath), ("panelRight", AssetPath)));

    private static readonly ShapeCheck StoryClassEntry = Object(Fields(
        ("id", Text), ("name", Text), ("description", Text), ("icon", AssetPath),
        ("attributes", ArrayOf(Object(Fields(("name", Text), ("value", Integer))))),
        ("previewActor", WorldShape.ActorShape),
        ("playerActor", WorldShape.ActorShape),
        ("player", PlayerPreview)));

    private static readonly ShapeCheck StoryClassCatalogShape = Object(Fields(
        ("defaultClass", Text),
        ("background", AssetPath),
        ("assets", StoryClassAssetSet),
        ("classes", ArrayOf(StoryClassEntry))));

    public static void ValidateItems(JsonNode? root)
    {
        ItemCatalogShape(new ShapeValue(root), "items");
        if (root is not JsonObject items) return;

        if (items["startingEquipment"] is JsonObject starting)
            Require(starting.Count == 6, "items.startingEquipment", "需要六个职业");

        // 初始背包不能空，且初始快捷物品必须在背包里。
        if (items["startingInventory"] is JsonArray inventory)
        {
            Require(inventory.Count > 0, "items.startingInventory", "需要初始背包物品");
            var starter = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in inventory)
                Require(starter.Add(Str(entry?["item"]) ?? string.Empty), "items.startingInventory",
                    $"初始物品重复 {Str(entry?["item"])}");
            var shortcut = Str(items["startingShortcut"]);
            Require(shortcut is not null && starter.Contains(shortcut), "items.startingShortcut",
                "初始快捷物品必须在背包且可用");
        }
    }

    public static void ValidateShopCatalog(JsonNode? root)
    {
        ShopCatalogShape(new ShapeValue(root), "shops");
        if (root is not JsonObject shops) return;

        if (shops["shops"] is JsonObject entries) Require(entries.Count == 18, "shops", "需要 18 个商店 NPC");
        var lamp = Str(shops["lampShop"]);
        Require(lamp is not null && (shops["shops"] as JsonObject)?.ContainsKey(lamp) == true,
            "shops.lampShop", "缺少神灯商人");

        if (shops["grades"] is JsonArray grades &&
            shops["rarityWeights"] is JsonObject weights &&
            weights["ordinary"] is JsonArray ordinary && weights["rare"] is JsonArray rare)
            Require(grades.Count == 15 && ordinary.Count == 5 && rare.Count == 5, "shops", "品质规则不完整");
    }

    /// <summary>校验 <c>companions.json</c>：形状，另加一条标识一致性（键必须等于内部 id）。</summary>
    public static void ValidateCompanionCatalog(JsonNode? root)
    {
        CompanionCatalogShape(new ShapeValue(root), "companions");
        if (root is not JsonObject catalog || catalog["definitions"] is not JsonObject definitions) return;
        foreach (var (id, definition) in definitions)
            Require(Str(definition?["id"]) == id, id, "伙伴标识不一致");
    }

    public static void ValidateStoryClassCatalog(JsonNode? root)
    {
        StoryClassCatalogShape(new ShapeValue(root), "storyClasses");
        if (root is not JsonObject catalog) return;

        if (catalog["classes"] is not JsonArray classes) return;
        Require(classes.Count == 6, "storyClasses", "需要六个不同职业");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in classes)
            Require(ids.Add(Str(entry?["id"]) ?? string.Empty), "storyClasses", "需要六个不同职业");

        var defaultClass = Str(catalog["defaultClass"]);
        Require(defaultClass is not null && ids.Contains(defaultClass), "storyClasses.defaultClass",
            "默认职业不存在");

        foreach (var entry in classes)
        {
            var id = Str(entry?["id"]) ?? "storyClasses";
            Require(entry?["playerActor"]?["headAnchor"] is not null, id, "头顶状态条需要人物头部锚点");
            Require(entry?["previewActor"]?["clips"]?["idle-south"] is not null, $"storyClasses.{id}",
                "缺少预览待机动画");
            Require(entry?["attributes"] is JsonArray attributes && attributes.Count == 5, id, "需要五项基础属性");
        }
    }

    private static ShapeCheck BuildItem()
    {
        var groundSprite = Object(Fields(
            ("file", AssetPath), ("width", Count), ("height", Count),
            ("anchorX", Integer), ("anchorY", Integer)));

        var hitDrop = Object(Fields(
            ("rollMaximum", Count), ("skillThreshold", Count),
            ("weaponThresholds", Record(Integer)), ("jitterPx", Integer),
            ("directions", ArrayOf(DropDirection))));

        var pickup = Object(Fields(
            ("kind", OneOf("party-mana")),
            ("percentOfCollectorMaxMana", Positive),
            ("hitDrop", hitDrop)));

        var equipmentDescription = Object(Fields(
            ("showEnhancementUses", Flag), ("attackSpeed", Optional(Text)),
            ("classAttributes", DescriptionParts)));

        var equipment = Object(Fields(
            ("slot", OneOf(EquipmentSlots)), ("classes", Strings),
            ("twoHanded", Flag), ("dualWield", Flag), ("locked", Flag),
            ("socketCapacity", Integer), ("enchantable", Flag),
            ("description", Optional(equipmentDescription))));

        var regenerate = Object(Fields(
            ("durationMs", Count), ("healthRecoveryPerPulse", Count), ("manaRecoveryPerPulse", Count)));

        var recovery = Variant("mode", new Dictionary<string, ShapeCheck>
        {
            ["amount"] = Object(Fields(
                ("mode", OneOf("amount")), ("minHealth", Count), ("maxHealth", Count),
                ("restoresMana", Flag), ("cooldownMs", Count))),
            ["rate"] = Object(Fields(
                ("mode", OneOf("rate")), ("ratePercent", Count),
                ("restoresMana", Flag), ("cooldownMs", Count))),
        });

        var use = Object(Fields(
            ("kind", OneOf(ItemUses)),
            ("recovery", Optional(recovery)),
            ("regenerate", Optional(regenerate)),
            ("rewards", Optional(ArrayOf(ItemReward))),
            ("classId", Optional(Text)),
            ("elite", Optional(Flag)),
            ("recipes", Optional(Strings)),
            ("target", Optional(OneOf("weapon", "armor"))),
            ("maxLevel", Optional(Integer)),
            ("requiresEnhanced", Optional(Flag))));

        return Object(Fields(
            ("name", Text), ("description", Text),
            ("descriptionParts", Optional(DescriptionParts)),
            ("category", OneOf(ItemCategories)),
            ("icon", AssetPath),
            ("groundSprite", groundSprite),
            ("pickup", Optional(pickup)),
            ("kind", Text), ("typeName", Text),
            ("stackable", Flag), ("discardable", Flag), ("sellable", Flag),
            ("priceKind", OneOf(PriceKinds)), ("pricePremium", Flag), ("shortcut", Flag),
            ("level", Integer), ("basePrice", Integer), ("storeListed", Flag),
            ("equipment", Optional(equipment)),
            ("use", Optional(use))));
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
