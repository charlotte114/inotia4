using System;
using System.Collections.Generic;
using System.Linq;
using Inotia4.Domain.Character;
using Inotia4.Domain.Content;

namespace Inotia4.Domain;

/// <summary>
/// 商店的价格口径：一件东西值多少钱、卖回给店家能拿回多少。
///
/// 装备的价值按「掷定后的攻防」算，不是按定义里的基础值——同一件装备洗出好词条就更值钱，
/// 这也是商店报价与说明面板必须共用同一份数值的原因。
/// </summary>
public static class ShopValues
{
    public static int Whole(double value) => Math.Max(0, (int)Math.Truncate(value));

    public static int ItemValue(ItemCatalog items, CharacterStatsCatalog stats, string id, ItemInstance? instance = null)
    {
        if (!items.Items.TryGetValue(id, out var item)) throw new InvalidOperationException("物品不存在");
        if (item.Equipment is null) return item.BasePrice;

        var source = stats.Equipment.TryGetValue(id, out var found)
            ? found
            : throw new InvalidOperationException("装备数值不存在");

        var entry = instance ?? new ItemInstance { Id = "quote", ItemId = id };
        var power = EquipmentPowerCalculator.Power(source, entry);
        var level = item.Level;
        var kind = item.PriceKind ?? (source.Attack != 0 ? "weapon" : source.Defense != 0 ? "armor" : "accessory");
        var options = instance?.Numbers?.Modifiers.Count ?? (source.OptionCount + source.FixedOptions.Length);

        var value = kind switch
        {
            "weapon" => Whole(power.Attack * (level + 140) / 10.0),
            "armor" => Whole(power.Defense * (level + 140) / 50.0),
            _ => Whole((level + 5) * (level + 140 + options * 50) / 40.0),
        };

        return item.PricePremium ? value * 8 + 30000 : value;
    }

    public static int SaleValue(int value) => value == 0 ? 0 : (int)Math.Floor(value * 2 / 11.0) + 1;
}

public sealed class ShopOffer
{
    public required string Id { get; init; }

    public required ItemInstance Instance { get; init; }

    public required bool Limited { get; init; }

    public required bool Concealed { get; init; }

    public bool Sold { get; set; }

    public ShopOffer DeepClone() => new()
    {
        Id = Id,
        Instance = Instance.DeepClone(),
        Limited = Limited,
        Concealed = Concealed,
        Sold = Sold,
    };
}

public sealed class ShopVisitsState
{
    public string? MapId { get; init; } = null;

    public List<KeyValuePair<string, List<ShopOffer>>> Stock { get; init; } = [];
}

public sealed class ShopVisits
{
    private const int Capacity = 16;

    public string? MapId { get; private set; }

    private readonly Dictionary<string, List<ShopOffer>> _stock = new();
    private int _serial;

    public ShopVisitsState CaptureState() => new()
    {
        MapId = MapId,
        Stock = _stock.Select(pair => KeyValuePair.Create(pair.Key, pair.Value.Select(offer => offer.DeepClone()).ToList())).ToList(),
    };

    public void RestoreState(ShopVisitsState state)
    {
        MapId = state.MapId;
        _stock.Clear();
        foreach (var (shopId, offers) in state.Stock)
            _stock[shopId] = offers.Select(offer => offer.DeepClone()).ToList();
    }

    public void EnterMap(string map)
    {
        if (map == MapId) return;
        _stock.Clear();
        MapId = map;
    }

    public string Identity() => $"merchandise-{++_serial}";

    public List<ShopOffer> Open(string id, Func<int, List<ShopOffer>> create)
    {
        if (_stock.TryGetValue(id, out var offers)) return offers;

        var occupied = _stock.Values.Sum(entries => entries.Count);
        offers = create(Math.Max(0, Capacity - occupied));
        _stock[id] = offers;
        return offers;
    }
}

public sealed class Storefront
{
    private readonly ShopCatalog _catalog;
    private readonly ItemCatalog _items;
    private readonly CharacterStatsCatalog _stats;
    private readonly Session _session;
    private readonly Func<double> _random;
    private List<ShopOffer> _stock = [];

    public ShopDefinition Shop { get; }

    public string? Category { get; private set; }

    public Storefront(ShopCatalog catalog, ItemCatalog items, CharacterStatsCatalog stats, Session session,
        string shopId, string mapId, Func<double>? random = null)
    {
        if (!catalog.Shops.TryGetValue(shopId, out var shop)) throw new InvalidOperationException("商店不存在");

        Shop = shop;
        _catalog = catalog;
        _items = items;
        _stats = stats;
        _session = session;
        _random = random ?? Random.Shared.NextDouble;

        var map = !string.IsNullOrEmpty(mapId) ? mapId
            : !string.IsNullOrEmpty(session.Shops.MapId) ? session.Shops.MapId!
            : session.StartMap;
        session.Shops.EnterMap(map);

        if (shop.Categories.Length > 0)
        {
            SelectCategory(shop.Categories[0]);
            return;
        }

        _stock = shop.FixedStock
            .Where(id => items.Items.TryGetValue(id, out var item) && !item.IsPickup)
            .Select(id => CreateOffer(id, false))
            .ToList();

        var rules = shop.StockRules.Where(rule => !rule.IsLevelRange).ToList();
        if (rules.Count > 0)
            _stock.AddRange(session.Shops.Open(shopId, capacity =>
            {
                var offers = new List<ShopOffer>();
                foreach (var rule in rules)
                {
                    var range = rule.IsRareEquipment ? RareRange() : default;
                    for (var attempt = 0; attempt < rule.Attempts && offers.Count < capacity; attempt++)
                    {
                        var offer = rule.IsRareEquipment ? RareOffer(range) : RandomOffer(rule);
                        if (offer is not null) offers.Add(offer);
                    }
                }

                return offers;
            }));

        _stock = _stock.Take(48).ToList();
    }

    public IReadOnlyList<ShopOffer> Offers => _stock.Take(16).ToList();

    public CharacterStatsCatalog CharacterStats => _stats;

    /// <summary>按 id 取物品定义。界面问「这件能不能叠加」时用它，别自己再查一遍目录。</summary>
    public ItemDefinition? Item(string id) => _items.Items.GetValueOrDefault(id);

    public void SelectCategory(string category)
    {
        if (Array.IndexOf(Shop.Categories, category) < 0) throw new InvalidOperationException("商品分类不存在");
        if (Category == category) return;

        Category = category;
        _stock = [];

        foreach (var rule in Shop.StockRules)
        {
            if (!rule.IsLevelRange) continue;

            foreach (var (id, item) in _items.Items)
            {
                if (item.Kind == category && item.StoreListed && item.Level >= rule.MinimumLevel &&
                    item.Level <= rule.MaximumLevel)
                    _stock.Add(CreateOffer(id, false));

                if (_stock.Count >= 48) break;
            }
        }
    }

    private int Roll(int min, int max)
    {
        var value = _random();
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0 || value >= 1)
            throw new InvalidOperationException("随机结果无效");

        return min + (int)Math.Floor(value * (max - min + 1));
    }

    private int Weighted(IReadOnlyList<int> weights, int? total = null)
    {
        var sum = total ?? weights.Sum();
        if (sum == 0) return -1;

        var draw = Roll(0, sum - 1);
        for (var index = 0; index < weights.Count; index++)
        {
            draw -= weights[index];
            if (draw < 0) return index;
        }

        return -1;
    }

    private int Grade(string mode)
    {
        var rarity = Weighted(mode == "rare" ? _catalog.RarityWeights.Rare : _catalog.RarityWeights.Ordinary);
        return Weighted(_catalog.Grades.Select(grade => grade.Rarity == rarity ? grade.Weight : 0).ToArray());
    }

    /// <summary>
    /// 上架一件商品。装备要按档掷数值：档位同时改攻防、词条数与孔上限，
    /// 所以先把选中档写进装备数值定义，再掷骰，最后把攻防写回实例。
    /// </summary>
    private ShopOffer CreateOffer(string item, bool limited, int? grade = null, bool concealed = false,
        bool withSockets = true)
    {
        var id = _session.Shops.Identity();
        var instance = new ItemInstance { Id = id, ItemId = item };

        if (_catalog.Equipment.TryGetValue(item, out var template))
        {
            var selectedGrade = grade ?? (limited && template.RandomGrade ? Grade("ordinary") : template.DefaultGrade);
            var variant = template.Variants![selectedGrade];

            var definition = _stats.GetEquipment(item) with
            {
                Attack = variant.Attack,
                Defense = variant.Defense,
                GradeName = variant.GradeName,
                DefaultGrade = selectedGrade,
                OptionCount = limited ? variant.OptionCount : 0,
                SocketLimit = variant.SocketLimit,
            };

            var numbers = EquipmentRoller.Roll(definition, (min, max) => Roll(min, max), limited && withSockets);
            numbers.BaseAttack = variant.Attack;
            numbers.BaseDefense = variant.Defense;
            instance.Numbers = numbers;
        }
        else
        {
            var prepared = _session.Bag?.PrepareDrop(item);
            if (prepared?.Numbers is not null) instance.Numbers = prepared.Numbers;
        }

        return new ShopOffer { Id = id, Instance = instance, Limited = limited, Concealed = concealed };
    }

    private ShopOffer? EquipmentOffer(int minimum, int maximum, int? grade = null, bool concealed = false)
    {
        var draw = Roll(0, 99);
        var group = draw < 20 ? "weapon" : draw < 70 ? "armor" : "accessory";
        var candidates = _catalog.Equipment
            .Where(pair => pair.Value.RandomListed && pair.Value.RandomGroup == group &&
                           pair.Value.DropLevel >= minimum && pair.Value.DropLevel <= maximum)
            .Select(pair => pair.Key)
            .Take(64)
            .ToList();

        if (candidates.Count == 0) return null;
        return CreateOffer(candidates[Roll(0, candidates.Count - 1)], true, grade, concealed);
    }

    private ShopOffer? UniqueOffer(int level, bool concealed = false)
    {
        var candidates = _catalog.Equipment
            .Where(pair => pair.Value.UniqueListed && _items.Items[pair.Key].Level <= level)
            .Select(pair => pair.Key)
            .ToList();

        if (candidates.Count == 0) return null;
        return CreateOffer(candidates[Roll(0, candidates.Count - 1)], true, 10, concealed, false);
    }

    private (int Minimum, int Maximum) RareRange()
    {
        var level = _session.PlayerLevel;
        var lower = Math.Max(level, 6) - 5;
        var average = (Roll(lower, level + 5) + Roll(lower, level + 5)) / 2;
        var minimum = Math.Max(average, 6) - 5;
        return (minimum, Math.Max(average + 2, minimum));
    }

    private ShopOffer? RareOffer((int Minimum, int Maximum) range)
    {
        var grade = Grade("rare");
        return grade == 10 ? UniqueOffer(range.Maximum, true) : EquipmentOffer(range.Minimum, range.Maximum, grade, true);
    }

    private ShopOffer? RandomOffer(ShopStockRule rule)
    {
        var profile = _catalog.Profiles[rule.Profile!];
        var level = rule.Profile == "lamp"
            ? _session.PlayerLevel
            : _catalog.MapShopLevels.GetValueOrDefault(_session.Shops.MapId ?? string.Empty);

        var minimum = Math.Max(1, level - rule.LevelRadius);
        var maximum = level + rule.LevelRadius;

        foreach (var category in ShopProfile.PoolCategories)
        {
            if (Roll(0, 9999) >= profile.Chances.GetValueOrDefault(category)) continue;

            for (var attempt = 0; attempt < 100; attempt++)
            {
                if (category == "equipment")
                {
                    var offer = EquipmentOffer(minimum, maximum);
                    if (offer is not null) return offer;
                    continue;
                }

                var pool = profile.Pools[category];
                var index = Weighted(pool.Select(entry => entry.Weight).ToArray(), 10000);
                var choice = index >= 0 && index < pool.Length ? pool[index] : null;
                var id = choice?.Tiers is not null
                    ? choice.Tiers.FirstOrDefault(tier => tier.Level <= level)?.Item
                    : choice?.Item;
                if (id is not null && _items.Items.TryGetValue(id, out var definition) && !definition.IsPickup)
                    return CreateOffer(id, true);
            }
        }

        if (Roll(0, 9999) < profile.UniqueChanceByLevel[Math.Min(127, level)]) return UniqueOffer(level);
        return null;
    }

    private ShopOffer Offer(string id) =>
        Offers.FirstOrDefault(offer => offer.Id == id) is { Sold: false } offer
            ? offer
            : throw new InvalidOperationException("这件商品已不在货架上");

    public int BuyPrice(string id)
    {
        var offer = Offer(id);
        var price = offer.Concealed
            ? _catalog.Equipment[offer.Instance.ItemId].ConcealedPrices[Math.Min(127, _session.PlayerLevel)]
            : ShopValues.Whole(ShopValues.ItemValue(_items, _stats, offer.Instance.ItemId, offer.Instance) *
                               (double)Shop.PriceRatePercent / 100);

        var discount = _session.GroupModifiers().GetValueOrDefault("buy-discount");
        return price - ShopValues.Whole(price * (double)discount / 1000);
    }

    public int SellPrice(ItemInstance entry)
    {
        if (!_items.Items.TryGetValue(entry.ItemId, out var item) || item.Category == "quest" || !item.Sellable)
            return 0;

        var price = ShopValues.SaleValue(ShopValues.ItemValue(_items, _stats, entry.ItemId, entry));
        var bonus = _session.GroupModifiers().GetValueOrDefault("sell-price");
        return price + ShopValues.Whole(price * (double)bonus / 1000);
    }

    /// <summary>
    /// 买。限购商品一次只能买一件；常驻堆叠商品最多一组九十九。
    /// 先入包再扣钱——入包失败就不该扣钱，而扣钱本身不会失败。
    /// </summary>
    public int Buy(string id, int quantity)
    {
        var offer = Offer(id);
        var item = _items.Get(offer.Instance.ItemId);
        var maximum = !offer.Limited && item.Stackable ? 99 : 1;
        if (quantity < 1 || quantity > maximum) throw new InvalidOperationException("购买数量无效");

        var total = (long)BuyPrice(id) * quantity;
        if (total < 1 || total > int.MaxValue) throw new InvalidOperationException("商品价格无效");
        if (_session.Money < total) throw new InvalidOperationException("金币不足");

        var bag = RequireBag();
        for (var index = 0; index < quantity; index++) bag.ReceiveDrop(offer.Instance);
        if (offer.Limited) offer.Sold = true;

        _session.SpendMoney((int)total);
        return (int)total;
    }

    /// <summary>卖。穿在身上的东西不能卖，得先卸下来。</summary>
    public int Sell(string entryId, int quantity)
    {
        var bag = RequireBag();
        var entry = bag.Get(entryId);
        if (entry is null || !bag.Entries.Any(item => item.Id == entryId))
            throw new InvalidOperationException("请先卸下装备，或重新选择物品");
        if (quantity < 1 || quantity > entry.Quantity) throw new InvalidOperationException("出售数量无效");

        var total = (long)SellPrice(entry) * quantity;
        if (total < 1 || _session.Money + total > int.MaxValue) throw new InvalidOperationException("此物品无法出售");

        bag.Take(entryId, quantity, false);
        _session.GainMoney((int)total);
        return (int)total;
    }

    private Character.Bag RequireBag() =>
        _session.Bag ?? throw new InvalidOperationException("会话没有背包，无法交易");
}
