using System;
using System.Collections.Generic;
using System.Linq;
using Inotia4.Domain.Character;
using Inotia4.Domain.Content;

namespace Inotia4.Domain;

public sealed class FusionBench(FusionCatalog catalog, ItemCatalog items, Session session, Bag bag)
{
    public FusionCatalog Catalog => catalog;

    public Bag Bag => bag;

    /// <summary>
    /// 某个菜单页在界面上列得出来的配方。传说装备要先学会才出得来——
    /// 已登录的配方记在背包的配方簿里，列表按它过滤（原生 <c>MIXSYSTEM_MakeRecipeList</c>）；其余四类一开始就有。
    /// </summary>
    public IReadOnlyList<FusionRecipe> Recipes(string menu) =>
        menu == FusionCatalog.LegendaryMenu
            ? catalog.OfMenu(menu).Where(recipe => Knows(recipe.Id)).ToList()
            : catalog.OfMenu(menu);

    public bool Knows(string recipeId)
    {
        if (catalog.Recipe(recipeId) is not { } recipe) return false;
        return recipe.Menu != FusionCatalog.LegendaryMenu || bag.LearnedRecipes.Contains(recipeId);
    }

    /// <summary>这件装备现在能不能拿来改；能就返回 <c>null</c>。</summary>
    public string? TargetProblem(FusionRecipe recipe, string? target)
    {
        if (!recipe.NeedsTarget) return null;

        if (target is null) return CraftingCatalog.Notice.Target;
        var entry = bag.Get(target);
        if (entry is null) return CraftingCatalog.Notice.Target;

        var definition = items.Get(entry.ItemId);
        if (entry.Numbers is null || definition.Equipment is null ||
            definition.PriceKind is not ("weapon" or "armor"))
            return CraftingCatalog.Notice.Incompatible;

        // 宝石孔生成：孔已经开满就不能再开。上限取内容的「宝石孔数量」
        // （首饰那一类是 0，也就是根本不能开孔）。
        if (recipe.Menu == FusionCatalog.SocketMenu)
        {
            var limit = definition.Equipment.SocketCapacity;
            if (limit <= 0 || entry.Numbers.Sockets >= limit) return CraftingCatalog.Notice.Max;
        }

        return null;
    }

    /// <summary>
    /// 一次能连着做几次。只有**固定产出**类能批量（药水、宝石、传说装备）；
    /// 改造类（宝石孔）每批只能改同一件装备一次，所以恒为 1。
    /// 上限取材料够做几次——批量是「同一批里连着做」，中间产物不能反过来当材料用。
    /// </summary>
    public int MaxBatch(string recipeId)
    {
        if (catalog.Recipe(recipeId) is not { } recipe) return 1;
        if (recipe.Product is null || recipe.Materials.Length == 0) return 1;

        var most = int.MaxValue;
        foreach (var material in recipe.Materials)
            most = Math.Min(most, bag.Count(material.Item) / Math.Max(1, material.Quantity));
        return Math.Max(1, most);
    }

    public FusionQuote Quote(string recipeId, string? target, int batch = 1)
    {
        var recipe = catalog.Recipe(recipeId) ?? throw new FusionError(CraftingCatalog.Notice.Target);

        if (!Knows(recipeId)) throw new FusionError(CraftingCatalog.Notice.NoRecipe);

        if (TargetProblem(recipe, target) is { } problem) throw new FusionError(problem);

        var times = recipe.Product is null ? 1 : Math.Max(1, batch);

        foreach (var material in recipe.Materials)
            if (bag.Count(material.Item) < material.Quantity * times)
                throw new FusionError(CraftingCatalog.Notice.Materials);

        var cost = 0;
        if (recipe.NeedsTarget && target is not null && bag.Get(target) is { } entry)
            cost = items.Get(entry.ItemId).Level * recipe.CostPerLevel;

        cost *= times;

        if (session.Money < cost) throw new FusionError(CraftingCatalog.Notice.Money);

        return new FusionQuote(recipeId, recipe.NeedsTarget ? target : null, bag.Revision, cost, times);
    }

    public void Commit(FusionQuote quote)
    {
        if (bag.Revision != quote.Revision) throw new FusionError(CraftingCatalog.Notice.Target);

        var current = Quote(quote.RecipeId, quote.Target, quote.Batch);
        var recipe = catalog.Recipe(current.RecipeId)!;

        session.Transaction(() =>
        {
            for (var round = 0; round < current.Batch; round += 1)
            {
                if (recipe.Product is { } product) bag.Add(product);
                else if (current.Target is { } target) bag.ApplySocket(target, current.Revision);

                foreach (var material in recipe.Materials) bag.Remove(material.Item, material.Quantity);
            }

            if (current.Cost > 0) session.SpendMoney(current.Cost);
        });
    }
}

public sealed class FusionError(string notice) : Exception("无法进行合成")
{
    public string Notice { get; } = notice;
}

public sealed record FusionQuote(string RecipeId, string? Target, int Revision, int Cost, int Batch = 1);
