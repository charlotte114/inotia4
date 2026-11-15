using System.Globalization;
using Inotia4.Domain.Character;

namespace Inotia4.Domain.Ui;

public static class CharacterValues
{
    public static string[] CombatValues(CharacterPanel panel) =>
    [
        Percentage(panel.PhysicalAttack, false),
        Percentage(panel.MagicAttack, false),
        Percentage(panel.CriticalRate),
        Percentage(panel.HitRate),
        Percentage(panel.CriticalDamage),
        Percentage(panel.Defense, false),
        Percentage(panel.PhysicalResistance),
        Percentage(panel.MagicResistance),
        Percentage(panel.EvasionRate),
        Percentage(panel.WeaponBlock),
        Percentage(panel.ShieldBlock),
    ];

    public static string Percentage(int thousandths) =>
        (thousandths / 10.0).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    private static string Percentage(int value, bool percent) =>
        percent ? Percentage(value) : value.ToString(CultureInfo.InvariantCulture);
}
