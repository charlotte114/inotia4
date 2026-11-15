using System;
using System.Collections.Generic;
using System.Linq;
using Inotia4.Domain.Character;
using Inotia4.Domain.Content;

namespace Inotia4.Domain.Combat;

public static class PassiveSkills
{
    private static readonly string[] ShortOrLongSwords = ["dagger", "shortsword", "longsword"];

    public static List<AttributeModifier> PassiveModifiers(SkillCatalog catalog, string classId,
        SkillLoadout loadout, string weaponKind, bool stealthed = false, bool companion = false)
    {
        var result = new List<AttributeModifier>();
        var definition = catalog.Class(classId);
        if (definition is null) return result;

        foreach (var id in definition.SkillIds)
        {
            if (!catalog.Skills.TryGetValue(id, out var skill)) continue;
            var effective = companion ? catalog.SkillForGrowth(id, true) : skill;
            if (effective.PassiveUnits is not { Count: > 0 } units) continue;

            var rank = loadout.LevelOf(id);
            if (rank == 0) continue;

            foreach (var unit in units)
            {
                var allowed = unit.Condition == "always" ||
                              unit.Condition == "stealthed" && stealthed ||
                              unit.Condition == "weapon-two-handed" && weaponKind == "two-handed" ||
                              unit.Condition == "weapon-short-or-long-sword" &&
                              ShortOrLongSwords.Contains(weaponKind) ||
                              unit.Condition == "weapon-mace" && weaponKind == "mace";
                if (!allowed || unit.Ranks.Count == 0) continue;

                var index = Math.Min(unit.Ranks.Count - 1, rank - 1);
                result.Add(new AttributeModifier { Attribute = unit.Attribute, Operation = unit.Ranks[index] });
            }
        }

        return result;
    }
}
