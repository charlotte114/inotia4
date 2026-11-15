using Inotia4.Domain.Character;

namespace Inotia4.Domain.Ui;

public static class ItemNameColor
{
    private static readonly string[] RarityColors = ["#4eff00", "#00b4ff", "#ffe600", "#9c00ff"];

    public static string? Of(int? rarity, string? chaosTier = null)
    {
        if (chaosTier == EquipmentChaos.ChaosTier) return "#0000ff";
        if (chaosTier == EquipmentChaos.EpicChaosTier) return "#ff6c00";
        return rarity is null or < 1 or > 4 ? null : RarityColors[rarity.Value - 1];
    }
}
