using System;
using System.Collections.Generic;
using Inotia4.Domain.Combat;
using Inotia4.Domain.Content;

namespace Inotia4.Domain.Party;

public sealed class MemberCombatModifierInput
{
    public required SkillCatalog Catalog { get; init; }

    public required string ClassId { get; init; }

    public required Character.SkillLoadout Loadout { get; init; }

    public required string WeaponKind { get; init; }

    public required string ArmorKind { get; init; }

    public required int Health { get; init; }

    public required int MaxHealth { get; init; }

    public required IReadOnlyDictionary<string, int> GroupEffects { get; init; }

    public bool? Stealthed { get; init; }

    public string? Growth { get; init; }
}

public static class PartyCombat
{
    public static List<AttributeModifier> MemberCombatModifiers(MemberCombatModifierInput input) =>
    [
        .. PassiveSkills.PassiveModifiers(input.Catalog, input.ClassId, input.Loadout, input.WeaponKind,
            input.Stealthed ?? false, companion: input.Growth == "companion"),
        .. PartyCombatModifiers(input.GroupEffects, input.WeaponKind, input.ArmorKind, input.Health,
            input.MaxHealth),
    ];

    public static List<AttributeModifier> PartyCombatModifiers(IReadOnlyDictionary<string, int> effects,
        string weapon, string armor, int health, int maxHealth)
    {
        var result = new List<AttributeModifier>();

        void Add(string attribute, int? value)
        {
            if (value is null or 0) return;
            result.Add(new AttributeModifier
            {
                Attribute = attribute,
                Operation = new CombatOperation { Kind = "add", Amount = value.Value },
            });
        }

        if (health <= (int)Math.Floor(maxHealth * 30 / 100.0))
        {
            Add("physicalResistanceBonus", effects.GetValueOrDefault("low-health-resistance"));
            Add("magicResistance", effects.GetValueOrDefault("low-health-resistance"));
        }

        if (weapon == "longsword")
        {
            Add("physicalDamageRate", effects.GetValueOrDefault("sword-damage"));
            Add("magicDamageRate", effects.GetValueOrDefault("sword-damage"));
        }

        if (armor == "plate") Add("defenseRate", effects.GetValueOrDefault("plate-defense"));
        if (armor == "leather") Add("physicalDamageRate", effects.GetValueOrDefault("leather-physical-damage"));
        if (armor == "cloth") Add("magicDamageRate", effects.GetValueOrDefault("cloth-magic-damage"));
        if (weapon is "bow" or "crossbow") Add("physicalDamageRate", effects.GetValueOrDefault("bow-damage"));
        if (weapon == "two-handed") Add("physicalDamageRate", effects.GetValueOrDefault("two-hand-damage"));

        Add("magicDamageRate", effects.GetValueOrDefault("magic-damage"));
        Add("magicResistance", effects.GetValueOrDefault("magic-resistance"));
        Add("criticalDamage", effects.GetValueOrDefault("critical-damage"));
        return result;
    }

    public static bool PartyProcSucceeds(int chance, Func<double> random) =>
        chance > 0 && 1 + (int)Math.Floor(random() * 999) < chance;
}
