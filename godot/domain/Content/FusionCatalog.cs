using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inotia4.Domain.Content;

public sealed class FusionMaterial
{
    [JsonPropertyName("item")] public string Item { get; init; } = string.Empty;

    [JsonPropertyName("quantity")] public int Quantity { get; init; } = 1;
}

public sealed class FusionRecipe
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;

    [JsonPropertyName("recipeId")] public int RecipeId { get; init; }

    [JsonPropertyName("menu")] public string Menu { get; init; } = string.Empty;

    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;

    [JsonPropertyName("target")] public string? Target { get; init; }

    [JsonPropertyName("product")] public string? Product { get; init; }

    /// <summary>
    /// 每级装备要花的金币：费用＝装备等级 × 这个数。原版的收费公式还没逆出来
    /// （见 <c>inotia4/docs/SO_REVERSE.md</c> §17.8），项目已决定合成一律免费，内容里因此是 0。
    /// </summary>
    [JsonPropertyName("costPerLevel")] public int CostPerLevel { get; init; }

    [JsonPropertyName("materials")] public FusionMaterial[] Materials { get; init; } = [];

    [JsonIgnore] public bool NeedsTarget => Target == FusionCatalog.EquipmentTarget;
}

public sealed class FusionMenu
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
}

/// <summary>
/// 融合器配方表，对应 <c>fusion.json</c>：由 <c>inotia4/tools/export_fusion_recipes.py</c>
/// 从原包的配方表与材料表导出，共 69 条、五个菜单。
/// 逆向依据见 <c>inotia4/docs/SO_REVERSE.md</c> §17。
/// </summary>
public sealed class FusionCatalog
{
    public const string PotionMenu = "potion";

    public const string GemMenu = "gem";

    public const string SocketMenu = "socket";

    public const string ChaosMenu = "chaos";

    public const string LegendaryMenu = "legendary";

    public const string EquipmentTarget = "equipment";

    [JsonPropertyName("version")] public int Version { get; init; }

    [JsonPropertyName("menus")] public FusionMenu[] Menus { get; init; } = [];

    [JsonPropertyName("recipes")] public FusionRecipe[] Recipes { get; init; } = [];

    [JsonIgnore] private Dictionary<string, FusionRecipe>? _byId;

    public FusionRecipe? Recipe(string id)
    {
        _byId ??= Recipes.ToDictionary(recipe => recipe.Id);
        return _byId.GetValueOrDefault(id);
    }

    public IReadOnlyList<FusionRecipe> OfMenu(string menu) =>
        Recipes.Where(recipe => recipe.Menu == menu).ToList();

    public static FusionCatalog Parse(JsonElement value)
    {
        var catalog = value.Deserialize<FusionCatalog>() ?? throw new InvalidOperationException("融合器配方表无效");

        if (catalog.Version != 1) throw new InvalidOperationException("融合器配方表版本不认识");
        if (catalog.Menus.Length == 0) throw new InvalidOperationException("融合器配方表没有菜单");
        if (catalog.Menus.Select(menu => menu.Id).Distinct().Count() != catalog.Menus.Length)
            throw new InvalidOperationException("融合器菜单重复");
        if (catalog.Menus.Any(menu => string.IsNullOrWhiteSpace(menu.Id) || string.IsNullOrWhiteSpace(menu.Name)))
            throw new InvalidOperationException("融合器菜单缺 id 或名字");
        if (catalog.Recipes.Length == 0) throw new InvalidOperationException("融合器配方表没有配方");
        if (catalog.Recipes.Select(recipe => recipe.Id).Distinct().Count() != catalog.Recipes.Length)
            throw new InvalidOperationException("融合器配方 id 重复");
        if (catalog.Recipes.Select(recipe => recipe.RecipeId).Distinct().Count() != catalog.Recipes.Length)
            throw new InvalidOperationException("融合器配方编号重复");

        var menus = catalog.Menus.Select(menu => menu.Id).ToHashSet();
        foreach (var recipe in catalog.Recipes)
        {
            if (string.IsNullOrWhiteSpace(recipe.Id) || string.IsNullOrWhiteSpace(recipe.Name))
                throw new InvalidOperationException("融合器配方缺 id 或名字");
            if (!menus.Contains(recipe.Menu))
                throw new InvalidOperationException($"融合器配方 {recipe.Id} 挂到了不存在的菜单 {recipe.Menu}");

            // 产物与目标必须二选一：要么给一件新东西，要么改你选中的那件。
            if ((recipe.Product is null) == (recipe.Target is null))
                throw new InvalidOperationException($"融合器配方 {recipe.Id} 的产物与目标必须二选一");
            if (recipe.Target is not null && recipe.Target != EquipmentTarget)
                throw new InvalidOperationException($"融合器配方 {recipe.Id} 的目标类型不认识：{recipe.Target}");

            if (recipe.Materials.Length == 0)
                throw new InvalidOperationException($"融合器配方 {recipe.Id} 没有材料");
            if (recipe.Materials.Select(material => material.Item).Distinct().Count() != recipe.Materials.Length)
                throw new InvalidOperationException($"融合器配方 {recipe.Id} 的材料重复");
            if (recipe.Materials.Any(material => string.IsNullOrEmpty(material.Item) || material.Quantity <= 0))
                throw new InvalidOperationException($"融合器配方 {recipe.Id} 的材料数量无效");
            if (recipe.CostPerLevel < 0)
                throw new InvalidOperationException($"融合器配方 {recipe.Id} 的费用为负");
        }

        return catalog;
    }

    /// <summary>材料与产物都得是真实物品；材料不能是装备，也不能是拾取物。</summary>
    public void Validate(ItemCatalog items)
    {
        foreach (var recipe in Recipes)
        {
            foreach (var material in recipe.Materials)
            {
                var definition = items.Items.GetValueOrDefault(material.Item);
                if (definition is null || definition.Equipment is not null || definition.IsPickup)
                    throw new InvalidOperationException($"融合器材料不存在或不可用：{recipe.Id} → {material.Item}");
            }

            if (recipe.Product is { } product && items.Items.GetValueOrDefault(product) is null)
                throw new InvalidOperationException($"融合器产物不存在：{recipe.Id} → {product}");
        }
    }
}
