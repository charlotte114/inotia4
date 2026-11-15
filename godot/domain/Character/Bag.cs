using System;
using System.Collections.Generic;
using System.Linq;
using Inotia4.Domain.Content;

namespace Inotia4.Domain.Character;

public sealed class ItemResult
{
    public ItemNotice? Notice { get; set; }

    public required string Kind { get; init; }

    public required string Title { get; init; }

    public string Message { get; set; } = string.Empty;

    public List<ItemReward> Rewards { get; } = [];

    public string? Target { get; init; }

    public string? ClassId { get; set; }

    public string? Skill { get; set; }

    public string? Recipe { get; set; }

    public string? Item { get; init; }
}

/// <summary>
/// 背包：散装物品、格位、穿在身上的装备、伙伴装备、快捷栏与配方本。
///
/// 三套状态必须同步维护：物品实例表、五个分类袋的格位、全部页面的格位。
/// 举一个具体的耦合：一件物品同时存在于「实例表」和「它那一袋的第 N 格」，
/// 丢弃时两处都要清，只在一边删就会留下指向空实例的格位。
/// <see cref="VerifyConsistency"/> 是这套手工纪律的唯一机器检查。
/// </summary>
public sealed class Bag
{
    private readonly Dictionary<string, ItemInstance> _instances = [];
    private readonly Dictionary<InventoryPouch, List<string?>> _pouches = CreatePouches();
    private readonly Dictionary<string, Dictionary<string, string>> _companionEquipment = [];
    private readonly Dictionary<string, ShortcutEntry?[]> _shortcuts = [];
    private readonly HashSet<string> _recipeBook = [];
    private readonly Action _changed;
    private readonly BagRules? _rules;
    private List<string?> _all = [];
    private Dictionary<string, string> _worn = [];
    private int _serial;
    private int _countsRevision = -1;
    private Dictionary<string, int>? _countsCache;

    private static readonly ItemCatalog EmptyCatalog = new();

    public Bag(Action? changed = null, ItemCatalog? catalog = null, string classId = "", bool seed = false,
        BagRules? rules = null)
    {
        Catalog = catalog ?? EmptyCatalog;
        ClassId = classId;
        _changed = changed ?? (() => { });
        _rules = rules;

        if (!seed) return;

        foreach (var entry in Catalog.StartingInventory) Add(entry.Item, entry.Quantity);
        _all = OrderedIds().Select(id => (string?)id).ToList();

        foreach (var (slot, itemId) in Catalog.StartingEquipmentOf(classId))
        {
            var entry = Create(itemId);
            _worn[slot] = entry.Id;
        }
    }

    public ItemCatalog Catalog { get; }

    public string ClassId { get; }

    public int Revision { get; private set; }

    public IReadOnlyCollection<string> LearnedRecipes => _recipeBook;

    public ItemResult? LastResult { get; private set; }

    private static Dictionary<InventoryPouch, List<string?>> CreatePouches() => new()
    {
        [InventoryPouch.Weapon] = [],
        [InventoryPouch.Armor] = [],
        [InventoryPouch.Consumable] = [],
        [InventoryPouch.Material] = [],
        [InventoryPouch.Quest] = [],
    };

    public void VerifyConsistency() => VerifyState(CaptureState());

    public BagState CaptureState()
    {
        var state = Snapshot();
        // 出站自检：坏状态不该离开背包，否则要等到下一次恢复检查点才暴露。
        VerifyState(state);
        return state;
    }

    public void RestoreUnchecked(BagState state) => WriteState(CloneState(state));

    public void RestoreState(BagState state)
    {
        var restored = CloneState(state);
        VerifyState(restored);
        WriteState(restored);
    }

    private BagState Snapshot()
    {
        var state = new BagState
        {
            Serial = _serial,
            Worn = new Dictionary<string, string>(_worn),
            Recipes = [.. _recipeBook],
        };

        state.Instances.AddRange(_instances.Values.Select(CloneInstance));
        foreach (var pouch in InventoryViews.Pouches) state.Pouches[PouchKey(pouch)] = [.. _pouches[pouch]];
        state.AllCells.AddRange(_all);
        foreach (var (owner, equipment) in _companionEquipment)
            state.CompanionEquipment.Add(new BagOwnerEquipment(owner, new Dictionary<string, string>(equipment)));
        foreach (var (owner, row) in _shortcuts)
            state.Shortcuts.Add(new BagOwnerShortcuts(owner, [.. row]));

        return state;
    }

    private void WriteState(BagState restored)
    {
        _instances.Clear();
        foreach (var entry in restored.Instances) _instances[entry.Id] = CloneInstance(entry);

        foreach (var pouch in InventoryViews.Pouches)
            _pouches[pouch] = restored.Pouches.TryGetValue(PouchKey(pouch), out var cells) ? [.. cells] : [];

        _all = [.. restored.AllCells];
        _worn = new Dictionary<string, string>(restored.Worn);
        _serial = restored.Serial;

        _companionEquipment.Clear();
        foreach (var owned in restored.CompanionEquipment)
            _companionEquipment[owned.Owner] = new Dictionary<string, string>(owned.Equipment);

        _shortcuts.Clear();
        foreach (var owned in restored.Shortcuts)
            _shortcuts[owned.Owner] = ShortcutLayout.DedupeSkills(owned.Row);

        _recipeBook.Clear();
        foreach (var recipe in restored.Recipes) _recipeBook.Add(recipe);

        LastResult = null;
        Revision++;
        _countsCache = null;
    }

    private static BagState CloneState(BagState state)
    {
        var copy = new BagState
        {
            Serial = state.Serial,
            Worn = new Dictionary<string, string>(state.Worn),
            AllCells = [.. state.AllCells],
            Recipes = [.. state.Recipes],
        };
        copy.Instances.AddRange(state.Instances.Select(CloneInstance));
        foreach (var (key, cells) in state.Pouches) copy.Pouches[key] = [.. cells];
        foreach (var owned in state.CompanionEquipment)
            copy.CompanionEquipment.Add(new BagOwnerEquipment(owned.Owner, new Dictionary<string, string>(owned.Equipment)));
        foreach (var owned in state.Shortcuts)
            copy.Shortcuts.Add(new BagOwnerShortcuts(owned.Owner, [.. owned.Row]));
        return copy;
    }

    private static ItemInstance CloneInstance(ItemInstance entry)
    {
        var copy = new ItemInstance
        {
            Id = entry.Id,
            ItemId = entry.ItemId,
            Quantity = entry.Quantity,
            Enhancement = entry.Enhancement,
            Skill = entry.Skill,
            Numbers = entry.Numbers?.Clone(),
        };
        copy.Gems.AddRange(entry.Gems);
        return copy;
    }

    private static string PouchKey(InventoryPouch pouch) => pouch switch
    {
        InventoryPouch.Weapon => "weapon",
        InventoryPouch.Armor => "armor",
        InventoryPouch.Consumable => "consumable",
        InventoryPouch.Material => "material",
        _ => "quest",
    };

    /// <summary>
    /// 一份背包状态是否自洽。格位、全部页、装备三处引用必须刚好覆盖实例表，
    /// 不能有重复、悬空或无归属的实例。
    /// </summary>
    public void VerifyState(BagState state)
    {
        var knownItems = Catalog.HasDefinitions;
        if (state.Serial < 0) throw new InvalidOperationException("背包物品序号无效");

        var ids = new HashSet<string>();
        foreach (var entry in state.Instances)
        {
            if (string.IsNullOrEmpty(entry.Id) || !ids.Add(entry.Id) || string.IsNullOrEmpty(entry.ItemId) ||
                (knownItems && !Catalog.Has(entry.ItemId)) || entry.Quantity <= 0)
                throw new InvalidOperationException("背包物品引用或数量无效");

            foreach (var gem in entry.Gems)
                if (knownItems && !Catalog.Has(gem)) throw new InvalidOperationException("背包宝石引用无效");
        }

        var loose = new HashSet<string>();
        foreach (var cells in state.Pouches.Values)
        foreach (var id in cells)
        {
            if (id is null) continue;
            if (!ids.Contains(id) || !loose.Add(id)) throw new InvalidOperationException("背包格位引用无效");
        }

        var all = state.AllCells.Where(id => id is not null).Select(id => id!).ToList();
        if (all.Distinct().Count() != all.Count || all.Any(id => !loose.Contains(id)) || all.Count != loose.Count)
            throw new InvalidOperationException("全部页与分类袋的物品不一致");

        var equipped = new HashSet<string>();
        var equipmentSets = new List<IReadOnlyDictionary<string, string>> { state.Worn };
        equipmentSets.AddRange(state.CompanionEquipment.Select(owned => owned.Equipment));
        foreach (var equipment in equipmentSets)
        foreach (var id in equipment.Values)
        {
            if (string.IsNullOrEmpty(id)) continue;
            if (!ids.Contains(id) || loose.Contains(id) || !equipped.Add(id))
                throw new InvalidOperationException("背包装备引用无效");
        }

        if (ids.Count != loose.Count + equipped.Count) throw new InvalidOperationException("背包存在无归属的物品");

        foreach (var recipe in state.Recipes)
            if (knownItems && !Catalog.Recipes.ContainsKey(recipe))
                throw new InvalidOperationException("背包配方引用不存在");
    }

    private List<string?>? CellsOf(string id)
    {
        if (!_instances.TryGetValue(id, out var entry)) return null;
        return _pouches[InventoryViews.PouchFor(Catalog.Items.GetValueOrDefault(entry.ItemId))];
    }

    private int CellOf(string id) => CellsOf(id)?.IndexOf(id) ?? -1;

    private static void Trim(List<string?> cells)
    {
        while (cells.Count > 0 && cells[^1] is null) cells.RemoveAt(cells.Count - 1);
    }

    private void Put(string id)
    {
        var cellsOfItem = CellsOf(id) ?? throw new InvalidOperationException("物品不在背包里");
        foreach (var cells in new[] { _all, cellsOfItem })
        {
            var free = cells.IndexOf(null);
            if (free < 0) cells.Add(id);
            else cells[free] = id;
        }
    }

    private void ClearCells(string id)
    {
        foreach (var cells in new[] { _all, CellsOf(id) })
        {
            if (cells is null) continue;
            var cell = cells.IndexOf(id);
            if (cell < 0) continue;
            cells[cell] = null;
            Trim(cells);
        }
    }

    private List<string> OrderedIds() =>
        InventoryViews.Pouches
            .SelectMany(pouch => _pouches[pouch].Where(id => id is not null).Select(id => id!))
            .ToList();

    public List<ItemInstance> Entries =>
        OrderedIds().Select(id => CloneInstance(_instances[id])).ToList();

    public List<ItemInstance?> AllCells =>
        _all.Select(id => id is null ? null : CloneInstance(_instances[id])).ToList();

    public Dictionary<InventoryPouch, List<ItemInstance?>> PouchCells
    {
        get
        {
            var view = new Dictionary<InventoryPouch, List<ItemInstance?>>();
            foreach (var pouch in InventoryViews.Pouches)
                view[pouch] = _pouches[pouch]
                    .Select(id => id is null ? null : CloneInstance(_instances[id]))
                    .ToList();
            return view;
        }
    }

    public IReadOnlyDictionary<string, string> WornIds => _worn;

    public Dictionary<string, ItemInstance> WornInstances
    {
        get
        {
            var worn = new Dictionary<string, ItemInstance>();
            foreach (var (slot, id) in _worn)
                if (_instances.TryGetValue(id, out var entry)) worn[slot] = entry;
            return worn;
        }
    }

    public Dictionary<string, string> EquipmentFor(string owner = "player") =>
        owner == "player" ? new Dictionary<string, string>(_worn) : new Dictionary<string, string>(
            _companionEquipment.TryGetValue(owner, out var equipment) ? equipment : []);

    public string AddEquipped(string item, string slot, string owner)
    {
        if (!Catalog.Items.TryGetValue(item, out var definition) || definition.Equipment is null)
            throw new InvalidOperationException("初始装备不存在");

        var entry = Create(item);
        var next = EquipmentFor(owner);
        next[slot] = entry.Id;
        if (owner == "player") _worn = next;
        else _companionEquipment[owner] = next;
        Notify();
        return entry.Id;
    }

    public void RemoveOwner(string owner, bool returnEquipment)
    {
        if (owner == "player") throw new InvalidOperationException("主角不能移出队伍");

        _shortcuts.Remove(owner);
        if (!_companionEquipment.TryGetValue(owner, out var equipment))
        {
            Notify();
            return;
        }

        _companionEquipment.Remove(owner);
        foreach (var id in equipment.Values)
        {
            if (string.IsNullOrEmpty(id)) continue;
            if (returnEquipment) Put(id);
            else _instances.Remove(id);
        }

        Notify();
    }

    /// <summary>
    /// 该角色的十二格快捷栏。这一行还不存在时用 <paramref name="defaults"/> 铺开并记下来——
    /// 铺开前后的可见内容完全一样，所以这里不通知订阅者。
    /// </summary>
    public ShortcutEntry?[] EnsureShortcuts(string owner, IReadOnlyList<ShortcutEntry?> defaults)
    {
        if (_shortcuts.TryGetValue(owner, out var existing)) return [.. existing];

        var row = defaults.ToArray();
        _shortcuts[owner] = row;
        return [.. row];
    }

    public void SetShortcuts(string owner, IReadOnlyList<ShortcutEntry?> row)
    {
        if (row.Count != ShortcutLayout.EntryCount) throw new ArgumentException("快捷栏格数无效");

        _shortcuts[owner] = ShortcutLayout.DedupeSkills(row);
        Notify();
    }

    public IReadOnlyDictionary<string, int> Counts
    {
        get
        {
            if (_countsCache is not null && _countsRevision == Revision) return _countsCache;

            var value = new Dictionary<string, int>();
            foreach (var id in OrderedIds())
            {
                var entry = _instances[id];
                value[entry.ItemId] = value.GetValueOrDefault(entry.ItemId) + entry.Quantity;
            }

            _countsCache = value;
            _countsRevision = Revision;
            return value;
        }
    }

    public int Count(string item) => Counts.GetValueOrDefault(item);

    public ItemInstance? Get(string id) => _instances.TryGetValue(id, out var entry) ? CloneInstance(entry) : null;

    private ItemInstance Create(string item, string? skill = null)
    {
        if (Catalog.Items.TryGetValue(item, out var definition) && definition.IsPickup)
            throw new InvalidOperationException("该物品在地面拾取时生效");

        if (skill is null && definition?.Use?.Kind == ItemUseRules.Learn)
            skill = _rules?.RollLearnSkill();

        var entry = new ItemInstance
        {
            Id = $"owned-{++_serial}",
            ItemId = item,
            Quantity = 1,
            Enhancement = 0,
            Skill = skill,
        };

        _rules?.Initialize(entry);
        _instances[entry.Id] = entry;
        return entry;
    }

    public void Add(string item, int quantity = 1, string? skill = null)
    {
        if (quantity <= 0) throw new ArgumentException("物品数量必须为正整数");

        var definition = Catalog.Items.GetValueOrDefault(item);
        if (Catalog.HasDefinitions && definition is null) throw new InvalidOperationException("物品定义不存在");
        if (definition?.IsPickup == true) throw new InvalidOperationException("该物品在地面拾取时生效");

        Revision++;
        var stackable = definition?.Stackable ?? true;

        if (stackable && skill is null)
        {
            var existing = OrderedIds()
                .Select(id => _instances[id])
                .FirstOrDefault(entry => entry.ItemId == item && entry.Skill is null);
            if (existing is not null)
            {
                existing.Quantity += quantity;
                return;
            }

            var created = Create(item);
            created.Quantity = quantity;
            Put(created.Id);
            return;
        }

        for (var index = 0; index < quantity; index++) Put(Create(item, skill).Id);
    }

    public ItemInstance PrepareDrop(string item, DropRoll? drop = null)
    {
        if (!Catalog.Items.ContainsKey(item)) throw new InvalidOperationException("物品定义不存在");

        var entry = new ItemInstance
        {
            Id = $"ground-{++_serial}",
            ItemId = item,
            Quantity = 1,
            Enhancement = 0,
        };

        _rules?.Initialize(entry, drop);
        return CloneInstance(entry);
    }

    public void ReceiveDrop(ItemInstance prepared)
    {
        var definition = Catalog.Items.GetValueOrDefault(prepared.ItemId);
        if (definition is null || definition.IsPickup || prepared.Quantity != 1)
            throw new InvalidOperationException("地面物品无效");

        if (definition.Stackable && prepared.Numbers is null)
        {
            Add(prepared.ItemId);
            return;
        }

        var entry = CloneInstance(prepared);
        var copy = new ItemInstance
        {
            Id = $"owned-{++_serial}",
            ItemId = entry.ItemId,
            Quantity = entry.Quantity,
            Enhancement = entry.Enhancement,
            Skill = entry.Skill,
            Numbers = entry.Numbers?.Clone(),
        };
        copy.Gems.AddRange(entry.Gems);

        _instances[copy.Id] = copy;
        Put(copy.Id);
        Revision++;
    }

    public bool Remove(string item, int quantity)
    {
        if (quantity <= 0 || Count(item) < quantity) return false;

        foreach (var entry in Entries.Where(entry => entry.ItemId == item))
        {
            var used = Math.Min(quantity, entry.Quantity);
            Consume(entry.Id, used);
            quantity -= used;
            if (quantity == 0) break;
        }

        return true;
    }

    private void Consume(string id, int quantity = 1)
    {
        Revision++;
        var entry = _instances[id];
        entry.Quantity -= quantity;

        if (entry.Quantity <= 0)
        {
            ClearCells(id);
            _instances.Remove(id);
        }

        if (Count(entry.ItemId) != 0) return;

        foreach (var row in _shortcuts.Values)
            for (var index = 0; index < row.Length; index += 1)
                if (row[index] is { Kind: ShortcutEntry.ItemKind } slot && slot.Id == entry.ItemId)
                    row[index] = null;
    }

    private void Notify()
    {
        Revision++;
        _changed();
    }

    public void Organize()
    {
        var ranks = new Dictionary<string, int>();
        var ordered = InventoryViews.Organized(Counts, Catalog.Items);
        for (var index = 0; index < ordered.Count; index += 1) ranks[ordered[index]] = index;

        var changed = false;
        foreach (var target in InventoryViews.SideFilters)
        {
            var cells = target == InventoryViewKind.All ? _all : _pouches[PouchOf(target)];
            var current = cells.Where(id => id is not null).Select(id => id!).ToList();

            // 排序必须是稳定的：比较器打平时保持原格位次序。
            // List.Sort 不保证稳定，带上原下标做最后一层平局判定补回同一语义。
            var indexed = current.Select((id, index) => (Id: id, Index: index)).ToList();
            indexed.Sort((left, right) =>
            {
                var leftItem = _instances[left.Id].ItemId;
                var rightItem = _instances[right.Id].ItemId;
                var byPouch = Array.IndexOf(InventoryViews.Pouches,
                                  InventoryViews.PouchFor(Catalog.Items.GetValueOrDefault(leftItem))) -
                              Array.IndexOf(InventoryViews.Pouches,
                                  InventoryViews.PouchFor(Catalog.Items.GetValueOrDefault(rightItem)));
                return byPouch != 0 ? byPouch
                    : ranks[leftItem] != ranks[rightItem] ? ranks[leftItem] - ranks[rightItem]
                    : left.Index - right.Index;
            });
            var sorted = indexed.Select(entry => entry.Id).ToList();

            // 有空格时即使序已排好也要压缩，否则「整理」对空位不起作用。
            if (current.Count == cells.Count && sorted.SequenceEqual(current)) continue;

            if (target == InventoryViewKind.All) _all = [.. sorted];
            else _pouches[PouchOf(target)] = [.. sorted];
            changed = true;
        }

        if (changed) Notify();
    }

    private static InventoryPouch PouchOf(InventoryViewKind view) => view switch
    {
        InventoryViewKind.Weapon => InventoryPouch.Weapon,
        InventoryViewKind.Armor => InventoryPouch.Armor,
        InventoryViewKind.Consumable => InventoryPouch.Consumable,
        InventoryViewKind.Material => InventoryPouch.Material,
        _ => InventoryPouch.Quest,
    };

    public bool PlaceInCell(string id, InventoryViewKind view, int cell)
    {
        if (!_instances.TryGetValue(id, out var entry)) return false;

        var own = InventoryViews.PouchFor(Catalog.Items.GetValueOrDefault(entry.ItemId));
        if (view != InventoryViewKind.All && InventoryViews.ViewOf(own) != view) return false;

        var cells = view == InventoryViewKind.All ? _all : _pouches[PouchOf(view)];
        var from = cells.IndexOf(id);
        if (from < 0 || cell < 0) return false;
        if (from == cell) return false;

        while (cells.Count <= cell) cells.Add(null);
        var occupant = cells[cell];
        cells[cell] = id;
        cells[from] = occupant;
        Trim(cells);

        Notify();
        return true;
    }

    public List<string> SlotsFor(string id, string? classId = null)
    {
        var targetClass = classId ?? ClassId;
        if (!_instances.TryGetValue(id, out var entry)) return [];
        if (!Catalog.Items.TryGetValue(entry.ItemId, out var definition)) return [];

        var rule = definition.Equipment;
        if (rule is null || rule.Locked || !rule.Allows(targetClass)) return [];

        var slots = new List<string> { rule.Slot };
        if (rule.DualWield && Array.IndexOf(Catalog.DualWieldClasses, targetClass) >= 0) slots.Add("off-hand");
        if (rule.Slot == "ring") slots.Add("accessory");
        return slots;
    }

    public string? EmptyEquipSlot(string id, string owner = "player", string? classId = null, int? level = null)
    {
        var equipment = EquipmentFor(owner);
        return SlotsFor(id, classId).FirstOrDefault(slot =>
            !equipment.ContainsKey(slot) && PlanEquipStep(id, slot, owner, classId, level).Problem is null);
    }

    public List<string> EquippableSlots(string id, string owner = "player", string? classId = null, int? level = null) =>
        SlotsFor(id, classId)
            .Where(slot => PlanEquipStep(id, slot, owner, classId, level).Problem is null)
            .ToList();

    /// <summary>
    /// 只读的装备判定：可以装备时返回 <c>null</c>，否则返回不能装备的原因。
    /// 界面用它禁用按钮并显示原因，不复制规则。
    /// </summary>
    public string? EquipProblem(string id, string slot, string owner = "player", string? classId = null, int? level = null) =>
        PlanEquipStep(id, slot, owner, classId, level).Problem;

    private (Dictionary<string, string>? Next, string? Problem) PlanEquipStep(
        string id, string slot, string owner, string? classId, int? level)
    {
        if (!_instances.TryGetValue(id, out var entry) || CellOf(id) < 0 ||
            !SlotsFor(id, classId).Contains(slot))
            return (null, "该物品不能装备在此位置");

        var problem = owner == "player"
            ? _rules?.EquipProblem(entry)
            : level is not null && Catalog.Items.TryGetValue(entry.ItemId, out var definition) &&
              ChaosEquipment.EquipmentLevel(definition.Level, entry) > level
                ? $"需要等级 {ChaosEquipment.EquipmentLevel(definition.Level, entry)}"
                : null;
        if (problem is not null) return (null, problem);

        var rule = Catalog.Get(entry.ItemId).Equipment!;
        var next = EquipmentFor(owner);
        next[slot] = id;

        if (slot == "main-hand" && rule.TwoHanded) next.Remove("off-hand");
        if (slot == "off-hand" && rule.DualWield && !next.ContainsKey("main-hand"))
            return (null, "请先装备一件单手主武器");

        if (slot == "off-hand" && next.TryGetValue("main-hand", out var mainId))
        {
            var main = Catalog.Get(_instances[mainId].ItemId);
            if (main.Equipment?.TwoHanded == true) next.Remove("main-hand");
            if (rule.DualWield && !next.ContainsKey("main-hand")) return (null, "请先装备一件单手主武器");
        }

        return (next, null);
    }

    public Dictionary<string, string> PlanEquip(string id, string slot, string owner = "player",
        string? classId = null, int? level = null)
    {
        var (next, problem) = PlanEquipStep(id, slot, owner, classId, level);
        if (problem is not null) throw new InvalidOperationException(problem);
        return next!;
    }

    public Dictionary<string, string> PlanUnequip(string slot, string owner = "player")
    {
        var current = EquipmentFor(owner);
        if (!current.ContainsKey(slot)) throw new InvalidOperationException("此装备槽为空");

        var next = new Dictionary<string, string>(current);
        next.Remove(slot);

        if (slot == "main-hand" && next.TryGetValue("off-hand", out var offId))
        {
            var off = Catalog.Get(_instances[offId].ItemId);
            if (off.Equipment?.DualWield == true) next.Remove("off-hand");
        }

        return next;
    }

    public void Equip(string id, string slot, string owner = "player", string? classId = null, int? level = null) =>
        CommitEquipment(PlanEquip(id, slot, owner, classId, level), owner);

    public void Unequip(string slot, string owner = "player") => CommitEquipment(PlanUnequip(slot, owner), owner);

    private void CommitEquipment(Dictionary<string, string> next, string owner = "player")
    {
        var oldIds = EquipmentFor(owner).Values.ToHashSet();
        var newIds = next.Values.ToHashSet();

        foreach (var id in newIds) ClearCells(id);
        foreach (var id in oldIds)
            if (!newIds.Contains(id)) Put(id);

        if (owner == "player") _worn = next;
        else _companionEquipment[owner] = next;
        Notify();
    }

    /// <summary>先查这对物品能不能应用，再决定是应用还是交换；耐久与孔数属于应用之后的结果。</summary>
    public bool CanApply(string sourceId, string targetId)
    {
        if (!_instances.TryGetValue(sourceId, out var source) || CellOf(sourceId) < 0 ||
            !_instances.TryGetValue(targetId, out var target) || sourceId == targetId)
            return false;

        var use = Catalog.Items.GetValueOrDefault(source.ItemId)?.Use;
        var definition = Catalog.Items.GetValueOrDefault(target.ItemId);
        var equipment = definition?.Equipment;
        if (use is null || definition is null || equipment is null) return false;

        return use.Kind switch
        {
            ItemUseRules.Enchant => equipment.Enchantable &&
                                     (use.Target == "weapon") ==
                                     (definition.PriceKind == "weapon" || equipment.Slot == "main-hand"),
            ItemUseRules.Socket => !equipment.Locked,
            ItemUseRules.Purify => target.Numbers?.Chaos is not null,
            _ => false,
        };
    }

    public ItemNotice? ApplicationProblem(string sourceId, string targetId)
    {
        var source = _instances[sourceId];
        var target = _instances[targetId];
        var use = Catalog.Get(source.ItemId).Use!;
        var definition = Catalog.Get(target.ItemId);

        if (use.Kind == ItemUseRules.Enchant)
        {
            if (use.MaxLevel is { } maxLevel && maxLevel != 0 && definition.Level > maxLevel)
                return ItemNotice.EnchantLevel;
            if (use.RequiresEnhanced && target.Enhancement == 0) return ItemNotice.EnchantFirst;
        }

        if (use.Kind == ItemUseRules.Socket &&
            target.Gems.Count >= (target.Numbers?.Sockets ?? definition.Equipment!.SocketCapacity))
            return ItemNotice.SocketFull;

        return _rules?.TargetProblem(source, target);
    }

    public string? TargetProblem(string sourceId, string targetId)
    {
        if (!CanApply(sourceId, targetId)) return "请选择可应用的装备";

        var problem = ApplicationProblem(sourceId, targetId);
        return problem is null ? null : ItemNoticeText.Of(problem.Value);
    }

    public void ApplyChaos(string id, EquipmentChaos chaos, int expectedRevision)
    {
        if (Revision != expectedRevision || !_instances.TryGetValue(id, out var entry) || entry.Numbers is null ||
            CellOf(id) < 0)
            throw new InvalidOperationException("合成装备已变化");

        var definition = Catalog.Get(entry.ItemId);
        if (definition.Equipment is null || definition.PriceKind is not ("weapon" or "armor"))
            throw new InvalidOperationException("合成装备已变化");

        if (chaos.Tier is not (EquipmentChaos.ChaosTier or EquipmentChaos.EpicChaosTier) ||
            chaos.ValuePercent is < 0 or > 200 || Math.Abs(chaos.LevelOffset) > 127)
            throw new InvalidOperationException("混沌数值无效");

        entry.Numbers.Chaos = chaos;
        Notify();
    }

    public void ApplySocket(string id, int expectedRevision)
    {
        if (Revision != expectedRevision || !_instances.TryGetValue(id, out var entry) || entry.Numbers is null ||
            CellOf(id) < 0)
            throw new InvalidOperationException("合成装备已变化");

        var definition = Catalog.Get(entry.ItemId);
        if (definition.Equipment is null || definition.PriceKind is not ("weapon" or "armor") ||
            entry.Numbers.Sockets >= definition.Equipment.SocketCapacity)
            throw new InvalidOperationException("合成装备已变化");

        entry.Numbers.Sockets += 1;
        Notify();
    }

    public ItemResult Use(string id, string? target = null, string? skill = null, Func<double>? random = null)
    {
        var roll = random ?? Random.Shared.NextDouble;
        if (!_instances.TryGetValue(id, out var source) || CellOf(id) < 0)
            throw new InvalidOperationException("物品已不在背包中");

        var definition = Catalog.Get(source.ItemId);
        var use = definition.Use ?? throw new InvalidOperationException("此物品不能直接使用");

        if (ItemUseRules.HandlingOf(use.Kind) == UseHandling.Ui)
            throw new InvalidOperationException(ItemUseRules.UseInterfaceOnly);

        var unimplemented = ItemUseRules.UnimplementedItemUse(source.ItemId, definition);
        if (unimplemented is not null) throw new InvalidOperationException(unimplemented);

        if (use.Kind is ItemUseRules.Enchant or ItemUseRules.Socket or ItemUseRules.Purify)
        {
            if (target is null || !CanApply(id, target)) throw new InvalidOperationException("请选择可应用的装备");

            var problem = ApplicationProblem(id, target);
            if (problem is not null) throw new ItemApplicationException(problem.Value);
        }

        var learnedSkill = source.Skill ?? skill;
        if (use.Kind == ItemUseRules.Learn && learnedSkill is null)
        {
            learnedSkill = _rules?.RollLearnSkill();
            source.Skill = learnedSkill;
        }

        if (use.Kind == ItemUseRules.Learn &&
            (learnedSkill is null || !Catalog.ClassSkillsOf(ClassId).Contains(learnedSkill)))
            throw new InvalidOperationException("请选择本职业的技能");

        var result = new ItemResult
        {
            Kind = use.Kind,
            Title = ItemUseRules.LabelOf(use.Kind),
            Message = $"已使用{definition.Name}",
            Target = target,
            Item = source.ItemId,
        };

        var enchantOutcome = use.Kind == ItemUseRules.Enchant
            ? _rules?.Enchant(source, _instances[target!], roll)
            : null;
        var useMessage = _rules?.ApplyUse(source, target is null ? null : _instances[target], learnedSkill);
        if (useMessage is not null) result.Message = useMessage;

        string Pick(IReadOnlyList<string> choices)
        {
            var value = roll();
            if (choices.Count == 0 || !double.IsFinite(value) || value < 0 || value >= 1)
                throw new InvalidOperationException("随机结果无效");
            return choices[(int)Math.Floor(value * choices.Count)];
        }

        ItemReward PickReward(IReadOnlyList<ItemReward> choices)
        {
            var value = roll();
            if (choices.Count == 0 || !double.IsFinite(value) || value < 0 || value >= 1)
                throw new InvalidOperationException("随机结果无效");
            return choices[(int)Math.Floor(value * choices.Count)];
        }

        switch (use.Kind)
        {
            case ItemUseRules.Consume:
            case ItemUseRules.ResetSkills:
            case ItemUseRules.Merchant:
                break;

            case ItemUseRules.Enchant:
            {
                var entry = _instances[target!];
                if (enchantOutcome is not null)
                {
                    entry.Enhancement = enchantOutcome.Level;
                    result.Message = enchantOutcome.Message;
                    result.Notice = enchantOutcome.Notice;
                }
                else
                {
                    entry.Enhancement++;
                    result.Message = $"{Catalog.Get(entry.ItemId).Name} 强化 +{entry.Enhancement}";
                }

                break;
            }

            case ItemUseRules.Socket:
            {
                var entry = _instances[target!];
                entry.Gems.Add(source.ItemId);
                result.Message = $"{Catalog.Get(entry.ItemId).Name}已镶嵌{definition.Name}";
                break;
            }

            case ItemUseRules.Purify:
            {
                var entry = _instances[target!];
                if (entry.Numbers is not null) entry.Numbers.Chaos = null;
                result.Message = string.Empty;
                break;
            }

            case ItemUseRules.Pack:
                result.Rewards.AddRange(use.Rewards.Select(reward =>
                    new ItemReward { Item = reward.Item, Quantity = reward.Quantity }));
                break;

            case ItemUseRules.Box:
            case ItemUseRules.Unseal:
            {
                var reward = PickReward(use.Rewards);
                result.Rewards.Add(new ItemReward { Item = reward.Item, Quantity = reward.Quantity });
                if (use.Kind == ItemUseRules.Unseal)
                {
                    result.ClassId = use.ClassId;
                    result.Skill = Pick(Catalog.ClassSkillsOf(use.ClassId ?? string.Empty));
                }

                break;
            }

            case ItemUseRules.Recipe:
                result.Recipe = Pick(use.Recipes);
                break;

            case ItemUseRules.Learn:
                result.Skill = learnedSkill;
                break;

            case ItemUseRules.RecoverHealth:
            case ItemUseRules.Regenerate:
            case ItemUseRules.Recruit:
            case ItemUseRules.Revive:
                throw new InvalidOperationException(ItemUseRules.UseInterfaceOnly);

            case ItemUseRules.Dungeon:
                throw new InvalidOperationException(ItemUseRules.UseNotImplemented);

            default:
                throw new InvalidOperationException($"未处理的物品用途：{use.Kind}");
        }

        Consume(id);
        foreach (var reward in result.Rewards)
            Add(reward.Item, reward.Quantity,
                use.Kind == ItemUseRules.Unseal ? result.Skill : null);
        if (result.Recipe is not null) _recipeBook.Add(result.Recipe);

        LastResult = result;
        Notify();
        return result;
    }

    public void Discard(string id, int quantity)
    {
        if (!_instances.TryGetValue(id, out var entry) || CellOf(id) < 0 ||
            !Catalog.Get(entry.ItemId).Discardable)
            throw new InvalidOperationException("此物品不能丢弃");

        if (quantity <= 0 || quantity > entry.Quantity) throw new InvalidOperationException("丢弃数量无效");

        Consume(id, quantity);
        Notify();
    }

    public void Take(string id, int quantity, bool notify = true)
    {
        if (!_instances.TryGetValue(id, out var entry) || CellOf(id) < 0 || quantity < 1 ||
            quantity > entry.Quantity)
            throw new InvalidOperationException("物品数量已变化");

        Consume(id, quantity);
        if (notify) Notify();
    }

    public Dictionary<string, string> EquipmentItems(IReadOnlyDictionary<string, string>? equipment = null)
    {
        var source = equipment ?? _worn;
        var items = new Dictionary<string, string>();
        foreach (var slot in EquipmentSetSlotNames)
            if (source.TryGetValue(slot, out var id) && _instances.TryGetValue(id, out var entry))
                items[slot] = entry.ItemId;
        return items;
    }

    private static readonly string[] EquipmentSetSlotNames =
        ["head", "hands", "back", "body", "feet", "main-hand", "off-hand", "neck", "ring", "accessory"];
}
