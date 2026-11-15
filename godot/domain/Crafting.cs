using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Inotia4.Domain.Character;
using Inotia4.Domain.Content;
using Inotia4.Domain.Story;

namespace Inotia4.Domain;

public sealed class CraftingMaterial
{
    [JsonPropertyName("item")] public string Item { get; init; } = string.Empty;

    [JsonPropertyName("quantity")] public int Quantity { get; init; } = 1;
}

public sealed class CraftingOperation
{
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;

    [JsonPropertyName("costPerLevel")] public int CostPerLevel { get; init; }

    [JsonPropertyName("materials")] public CraftingMaterial[] Materials { get; init; } = [];

    [JsonPropertyName("rule")] public CraftingRule Rule { get; init; } = new();
}

public sealed class CraftingRule
{
    [JsonPropertyName("valueDelta")] public int[] ValueDelta { get; init; } = [0, 0];

    [JsonPropertyName("valueBounds")] public int[] ValueBounds { get; init; } = [0, 100];

    [JsonPropertyName("levelDiceDivisor")] public int LevelDiceDivisor { get; init; } = 1;

    [JsonPropertyName("levelClampDivisor")] public int LevelClampDivisor { get; init; } = 1;
}

public sealed class CraftingCatalog
{
    [JsonPropertyName("operations")] public Dictionary<string, CraftingOperation> Operations { get; init; } = new();

    public const string ChaosTier = "chaos";
    public const string EpicChaosTier = "epic-chaos";

    public static CraftingCatalog Parse(JsonElement value)
    {
        var catalog = value.Deserialize<CraftingCatalog>() ?? throw new InvalidOperationException("合成目录无效");
        if (catalog.Operations.Count != 2) throw new InvalidOperationException("合成目录无效");

        foreach (var tier in new[] { ChaosTier, EpicChaosTier })
        {
            if (!catalog.Operations.TryGetValue(tier, out var operation) ||
                string.IsNullOrWhiteSpace(operation.Name) ||
                operation.CostPerLevel < 0 ||
                operation.Materials.Length != 4 ||
                operation.Materials.Select(material => material.Item).Distinct().Count() != 4 ||
                operation.Materials.Any(material => string.IsNullOrEmpty(material.Item) || material.Quantity <= 0))
                throw new InvalidOperationException("合成目录无效");

            var rule = operation.Rule;
            if (rule.LevelDiceDivisor <= 0 || rule.LevelClampDivisor <= 0 ||
                rule.ValueDelta.Length != 2 || rule.ValueBounds.Length != 2 ||
                rule.ValueDelta[0] > rule.ValueDelta[1] || rule.ValueBounds[0] > rule.ValueBounds[1] ||
                rule.ValueBounds[0] != 0 || rule.ValueBounds[1] != (tier == ChaosTier ? 150 : 200))
                throw new InvalidOperationException("合成目录无效");
        }

        return catalog;
    }

    /// <summary>材料必须是普通物品：不能是装备，也不能是拾取物。</summary>
    public void Validate(ItemCatalog items)
    {
        foreach (var operation in Operations.Values)
            foreach (var material in operation.Materials)
            {
                var definition = items.Items.GetValueOrDefault(material.Item);
                if (definition is null || definition.Equipment is not null || definition.IsPickup)
                    throw new InvalidOperationException($"合成材料不存在：{material.Item}");
            }
    }

    public static class Notice
    {
        public const string Target = "craft-target";
        public const string Incompatible = "craft-incompatible";
        public const string Epic = "craft-epic";
        public const string NeedsChaos = "craft-needs-chaos";
        public const string Max = "craft-max";
        public const string Money = "craft-money";
        public const string Materials = "craft-materials";

        public const string NoRecipe = "craft-no-recipe";
    }
}

public sealed class CraftingError(string notice) : Exception("无法进行合成")
{
    public string Notice { get; } = notice;
}

public sealed record CraftingQuote(string Tier, string Target, int Revision, int Cost);

public sealed class EquipmentCrafting(CraftingCatalog catalog, ItemCatalog items, Session session, Bag bag)
{
    public CraftingCatalog Catalog => catalog;

    public Bag Bag => bag;

    /// <summary>这件装备现在能不能合成这一档；能就返回 <c>null</c>。</summary>
    public string? TargetProblem(string tier, string? target)
    {
        var entry = target is null ? null : bag.Get(target);
        if (entry is null || !bag.Entries.Any(item => item.Id == target)) return CraftingCatalog.Notice.Target;

        var definition = items.Get(entry.ItemId);
        var kind = definition.PriceKind;
        if (entry.Numbers is null || definition.Equipment is null ||
            (kind != "weapon" && kind != "armor"))
            return CraftingCatalog.Notice.Incompatible;

        var chaos = entry.Numbers.Chaos;
        if (tier == CraftingCatalog.ChaosTier)
        {
            if (chaos?.Tier == CraftingCatalog.EpicChaosTier) return CraftingCatalog.Notice.Epic;
            if ((chaos?.ValuePercent ?? 100) >= catalog.Operations[CraftingCatalog.ChaosTier].Rule.ValueBounds[1])
                return CraftingCatalog.Notice.Max;
        }
        else
        {
            if (chaos is null) return CraftingCatalog.Notice.NeedsChaos;
            if (chaos.Tier == CraftingCatalog.EpicChaosTier &&
                chaos.ValuePercent >= catalog.Operations[CraftingCatalog.EpicChaosTier].Rule.ValueBounds[1])
                return CraftingCatalog.Notice.Max;
        }

        return null;
    }

    public CraftingQuote Quote(string tier, string? target)
    {
        if (target is null) throw new CraftingError(CraftingCatalog.Notice.Target);
        if (TargetProblem(tier, target) is { } problem) throw new CraftingError(problem);

        var entry = bag.Get(target)!;
        var operation = catalog.Operations[tier];
        var cost = items.Get(entry.ItemId).Level * operation.CostPerLevel;

        if (operation.Materials.Any(material => bag.Count(material.Item) < material.Quantity))
            throw new CraftingError(CraftingCatalog.Notice.Materials);
        if (session.Money < cost) throw new CraftingError(CraftingCatalog.Notice.Money);

        return new CraftingQuote(tier, target, bag.Revision, cost);
    }

    public void Commit(CraftingQuote quote, Func<double> random)
    {
        if (bag.Revision != quote.Revision) throw new CraftingError(CraftingCatalog.Notice.Target);

        var current = Quote(quote.Tier, quote.Target);
        var entry = bag.Get(current.Target)!;
        var operation = catalog.Operations[current.Tier];
        var definition = items.Get(entry.ItemId);

        var chaos = ChaosEquipment.RollChaos(definition.Level, entry, current.Tier,
            new ChaosRule(operation.Rule.ValueDelta, operation.Rule.ValueBounds,
                operation.Rule.LevelDiceDivisor, operation.Rule.LevelClampDivisor),
            random);

        if (bag.Revision != current.Revision) throw new CraftingError(CraftingCatalog.Notice.Target);
        Quote(current.Tier, current.Target);

        session.Transaction(() =>
        {
            bag.ApplyChaos(current.Target, chaos, current.Revision);
            foreach (var material in operation.Materials) bag.Remove(material.Item, material.Quantity);
            if (current.Cost > 0) session.SpendMoney(current.Cost);
        });
    }
}
