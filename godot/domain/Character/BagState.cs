using System.Collections.Generic;

namespace Inotia4.Domain.Character;

public sealed class BagState
{
    public List<ItemInstance> Instances { get; set; } = [];

    public Dictionary<string, List<string?>> Pouches { get; set; } = [];

    public List<string?> AllCells { get; set; } = [];

    public Dictionary<string, string> Worn { get; set; } = [];

    public List<BagOwnerEquipment> CompanionEquipment { get; set; } = [];

    public int Serial { get; set; }

    public List<BagOwnerShortcuts> Shortcuts { get; set; } = [];

    public List<string> Recipes { get; set; } = [];
}

public sealed record BagOwnerEquipment(string Owner, Dictionary<string, string> Equipment);

public sealed record BagOwnerShortcuts(string Owner, List<ShortcutEntry?> Row);
