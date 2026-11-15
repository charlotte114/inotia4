using System.Globalization;
using Inotia4.Domain.Character;
using Inotia4.Domain.Content;

namespace Inotia4.Domain.Ui;

public readonly record struct DescriptionMember(string ClassId, int Level);

public readonly record struct DescriptionSkill(string Name, string ClassName);

public sealed class ItemDescriptionRow
{
    public string? Text { get; init; }

    public List<DescriptionTextPart>? Parts { get; init; }

    public static ItemDescriptionRow Plain(string text) => new() { Text = text };

    public static ItemDescriptionRow OfParts(List<DescriptionTextPart> parts) => new() { Parts = parts };
}

public sealed class ItemDescriptionBody
{
    public string Title { get; init; } = string.Empty;

    public string? Color { get; init; }

    public List<ItemDescriptionRow> Rows { get; init; } = [];

    public int Sockets { get; init; }
}

public static class ItemDetailLines
{
    private const string ModifierColor = "#7878ff";

    private const string GemColor = "#bf0000";

    private const string NormalColor = "#ffffff";

    private const string WarnColor = "#ff0000";

    private const string DescriptionColor = "#00b4ff";

    private const string ConcealedColor = "#808080";

    private static List<DescriptionTextPart> EquipmentAttributeLines(
        CharacterStatsCatalog catalog, ItemInstance entry, bool standaloneGem = false)
    {
        var result = new List<DescriptionTextPart>();

        void Add(IEnumerable<Modifier> modifiers, string color)
        {
            foreach (var modifier in modifiers)
            {
                var descriptor = catalog.Modifiers.TryGetValue(modifier.Stat, out var found)
                    ? found
                    : new ModifierDescriptor { Label = modifier.Stat };

                var value = descriptor.Percent
                    ? (modifier.Value / 10.0).ToString("0.0", CultureInfo.InvariantCulture) + "%"
                    : modifier.Value.ToString(CultureInfo.InvariantCulture);

                if (result.Count > 0) result.Add(DescriptionTextPart.Of("\n"));
                result.Add(DescriptionTextPart.Of($"{descriptor.Label}: {value}", color));
            }
        }

        Add(entry.Numbers?.Modifiers ?? [], standaloneGem ? GemColor : ModifierColor);
        Add(entry.Numbers?.GemModifiers ?? [], GemColor);
        return result;
    }

    public static ItemDescriptionBody Build(
        ItemDefinition item,
        ItemInstance entry,
        EquipmentNumbers? numbers,
        CharacterStatsCatalog catalog,
        IReadOnlyList<ShopGrade> grades,
        DescriptionMember? member = null,
        IReadOnlyDictionary<string, string>? classNames = null,
        DescriptionSkill? skill = null,
        bool concealed = false)
    {
        if (concealed)
            return new ItemDescriptionBody
            {
                Title = $"{item.TypeName}（未鉴定）",
                Color = ConcealedColor,
                Rows = [],
                Sockets = 0,
            };

        var equipment = item.Equipment;
        var grade = entry.Numbers?.Grade ?? numbers?.DefaultGrade ?? EquipmentRoller.DefaultGrade;

        var gradeEntry = grade >= 0 && grade < grades.Count ? grades[grade] : null;
        var prefix = equipment is not null && grade != EquipmentRoller.DefaultGrade
            ? item.PriceKind == "weapon" ? gradeEntry?.Name : gradeEntry?.ArmorName ?? gradeEntry?.Name
            : string.Empty;

        var title = $"{(string.IsNullOrEmpty(prefix) ? string.Empty : prefix + " ")}{item.Name}" +
                    (equipment is not null && entry.Enhancement != 0 ? "+" + entry.Enhancement : string.Empty);
        var color = equipment is not null
            ? ItemNameColor.Of(gradeEntry?.Rarity, entry.Numbers?.Chaos?.Tier)
            : null;

        var parts = new List<DescriptionTextPart>();
        var sockets = 0;

        if (equipment is not null)
        {
            var wrongClass = member is { } who && !equipment.Allows(who.ClassId);
            parts.Add(DescriptionTextPart.Of(item.TypeName, wrongClass ? WarnColor : NormalColor));

            var level = ChaosEquipment.EquipmentLevel(item.Level, entry);
            parts.Add(DescriptionTextPart.Of("\n等级 "));
            parts.Add(DescriptionTextPart.Of(
                level.ToString(CultureInfo.InvariantCulture),
                member is { } low && low.Level < level ? WarnColor : NormalColor));

            if (classNames is not null)
            {
                var all = classNames.Keys.ToList();
                var universal = all.Count > 0 && equipment.Classes.Length == all.Count &&
                                all.All(id => equipment.Allows(id));

                parts.Add(DescriptionTextPart.Of(
                    $"\n适合职业：{(universal ? "全部职业" : string.Join("、", equipment.Classes.Select(id => classNames.GetValueOrDefault(id) ?? id)))}"));
            }

            if (equipment.Description?.ShowEnhancementUses ??
                (item.PriceKind == "weapon" || item.PriceKind == "armor"))
                parts.Add(DescriptionTextPart.Of($"\n强化耐久度 {entry.Numbers?.EnhancementUses ?? 0}"));

            sockets = entry.Numbers?.Sockets ?? 0;
            parts.Add(DescriptionTextPart.Of($"\n宝石孔数量 {sockets}"));

            if (!string.IsNullOrEmpty(equipment.Description?.AttackSpeed))
                parts.Add(DescriptionTextPart.Of($"\n{equipment.Description!.AttackSpeed}"));

            if (numbers is not null)
            {
                var power = EquipmentPowerCalculator.Power(numbers, entry);
                if (item.PriceKind == "weapon")
                    parts.Add(DescriptionTextPart.Of($"\n\n物理攻击 {power.Attack}\n魔法攻击 {power.MagicAttack}"));
                else if (item.PriceKind == "armor")
                    parts.Add(DescriptionTextPart.Of($"\n\n防御力 {power.Defense}"));
            }

            if (equipment.Description?.ClassAttributes.Length > 0)
            {
                parts.Add(DescriptionTextPart.Of("\n"));
                parts.AddRange(equipment.Description.ClassAttributes);
            }
        }

        var attributes = EquipmentAttributeLines(catalog, entry, item.Use?.Kind == "socket");
        if (attributes.Count > 0)
        {
            if (parts.Count > 0) parts.Add(DescriptionTextPart.Of("\n\n"));
            parts.AddRange(attributes);
        }

        if (skill is { } info)
        {
            if (parts.Count > 0) parts.Add(DescriptionTextPart.Of("\n"));
            parts.Add(DescriptionTextPart.Of($"{info.Name}({info.ClassName})"));
        }

        List<DescriptionTextPart> description;
        if (item.DescriptionParts is { } declared)
            description = [.. declared];
        else if (!string.IsNullOrEmpty(item.Description))
            description = [DescriptionTextPart.Of(item.Description, DescriptionColor)];
        else
            description = [];

        if (description.Count > 0)
        {
            if (parts.Count > 0) parts.Add(DescriptionTextPart.Of("\n\n"));
            parts.AddRange(description);
        }

        return new ItemDescriptionBody
        {
            Title = title,
            Color = color,
            Rows = parts.Count > 0 ? [ItemDescriptionRow.OfParts(parts)] : [],
            Sockets = sockets,
        };
    }

    public static List<string> ResultDetails(
        ItemResult? result, ItemCatalog catalog,
        Func<string, string> skillName, Func<string, string> className)
    {
        var lines = new List<string>();
        if (result is null) return lines;

        if (result.Message.Length > 0) lines.Add(result.Message);

        foreach (var reward in result.Rewards)
            lines.Add($"{catalog.Get(reward.Item).Name} × {reward.Quantity}");
        if (result.Skill is { } skillId) lines.Add($"技能：{skillName(skillId)}");
        if (result.ClassId is { } classId) lines.Add($"职业：{className(classId)}");
        if (result.Recipe is { } recipeId && catalog.Recipes.TryGetValue(recipeId, out var recipe))
        {
            lines.Add($"配方：{recipe.Name}");
            lines.Add($"产物：{catalog.Get(recipe.Result).Name}");
            lines.Add("所需材料：");
            foreach (var material in recipe.Materials)
                lines.Add($"{catalog.Get(material.Item).Name} × {material.Quantity}");
        }

        return lines;
    }
}
