using System;
using System.Collections.Generic;
using Inotia4.Domain.Combat;
using Inotia4.Domain.Content;

namespace Inotia4.Domain.Character;

public sealed class CharacterPanel
{
    private const int PerMille = 1000;

    private const int StrengthRateTwoHanded = 700;
    private const int StrengthRateMartial = 600;
    private const int StrengthRateOther = 500;

    private const int IntelligenceRateStaff = 600;
    private const int IntelligenceRateOrb = 700;

    private const int MentalityRateCasting = 600;

    private const int DexterityAttackDivisor = 2;

    private const int BaseHealth = 640;
    private const int HealthPerLevel = 72;
    private const int HealthPerVitality = 80;
    private const int BaseMaxMana = 200;

    private const int HitBasePerPoint = 4;

    private const int RateCap = 750;

    private const int DefaultCriticalRate = 30;
    private const int DefaultCriticalDamage = 1000;
    private const int DefaultWeaponBlock = 30;

    private const int HitNumerator = 20000;
    private const int HitLevelOffset = 35;
    private const int HitDisplayBonus = 100;

    private const int DefenseWeightPerLevel = 20;
    private const int DefenseLevelOffset = 3;

    private const int EvasionLevelFactor = 5000;
    private const int EvasionConstant = 1000000;
    private const int EvasionLevelDivisor = 5;
    private const int EvasionDexterityOffset = 1200;

    public required PrimaryAttributes Attributes { get; init; }

    public int Level { get; init; }

    public int MaxHealth { get; init; }

    public int MaxMana { get; init; }

    public int PhysicalAttack { get; init; }

    public int MagicAttack { get; init; }

    public int BaseDefense { get; init; }

    public int Defense { get; init; }

    public int DefenseRate { get; init; }

    public int PhysicalResistance { get; init; }

    public int MagicResistance { get; init; }

    public int BaseMagicResistance { get; init; }

    public int HitBase { get; init; }

    public int HitBonus { get; init; }

    public int HitRate { get; init; }

    public int EvasionBase { get; init; }

    public int EvasionBonus { get; init; }

    public int EvasionRate { get; init; }

    public int CriticalRate { get; init; }

    public int CriticalResistance { get; init; }

    public int CriticalDamage { get; init; }

    public int CriticalDamageResistance { get; init; }

    public int WeaponBlock { get; init; }

    public int ShieldBlock { get; init; }

    public bool OffHandShield { get; init; }

    public string WeaponKind { get; init; } = "unarmed";

    public static CharacterPanel Calculate(
        CharacterStatsCatalog catalog,
        ItemCatalog items,
        string classId,
        int level,
        PrimaryAttributes? allocated = null,
        IReadOnlyDictionary<string, ItemInstance>? equipped = null)
    {
        if (!catalog.Classes.TryGetValue(classId, out var baseAttributes))
            throw new KeyNotFoundException($"职业数值不存在：{classId}");

        var equipment = equipped ?? new Dictionary<string, ItemInstance>();
        var bonus = allocated ?? new PrimaryAttributes();

        var strength = baseAttributes.Strength + bonus.Strength;
        var dexterity = baseAttributes.Dexterity + bonus.Dexterity;
        var vitality = baseAttributes.Vitality + bonus.Vitality;
        var intelligence = baseAttributes.Intelligence + bonus.Intelligence;
        var mentality = baseAttributes.Mentality + bonus.Mentality;

        var bonuses = new Dictionary<string, int>();
        var attack = 0;
        var magic = 0;
        var defense = 0;

        foreach (var (slot, entry) in equipment)
        {
            if (!catalog.Equipment.TryGetValue(entry.ItemId, out var numbers))
                throw new KeyNotFoundException($"装备数值不存在：{entry.ItemId}");

            var power = EquipmentPowerCalculator.Power(numbers, entry);

            if (slot == "main-hand")
            {
                attack += power.Attack;
                magic += power.MagicAttack;
            }

            defense += power.Defense;

            Add(bonuses, "criticalRate", numbers.CriticalRate);
            Add(bonuses, "hitBonus", numbers.HitBonus);
            Add(bonuses, "magicAttackRate", numbers.MagicAttackRate);
            Add(bonuses, "shieldBlock", numbers.ShieldBlock);

            if (entry.Numbers is null) continue;

            foreach (var modifier in entry.Numbers.Modifiers)
                ApplyModifier(modifier, bonuses, ref strength, ref dexterity, ref vitality,
                    ref intelligence, ref mentality);

            foreach (var modifier in entry.Numbers.GemModifiers)
                ApplyModifier(modifier, bonuses, ref strength, ref dexterity, ref vitality,
                    ref intelligence, ref mentality);
        }

        var weapon = equipment.TryGetValue("main-hand", out var mainHand)
            ? items.Get(mainHand.ItemId).Kind
            : "unarmed";
        var strengthRate = weapon switch
        {
            "two-handed" => StrengthRateTwoHanded,
            "longsword" or "axe" or "mace" => StrengthRateMartial,
            _ => StrengthRateOther,
        };

        var intelligenceRate = weapon switch
        {
            "unarmed" => 0,
            "staff" => IntelligenceRateStaff,
            "orb" => IntelligenceRateOrb,
            _ => PerMille,
        };

        var mentalityRate = weapon switch
        {
            "unarmed" => 0,
            "staff" or "orb" => MentalityRateCasting,
            _ => PerMille,
        };

        var physicalAttackRate = BonusOf(bonuses, "physicalAttackRate");
        var magicAttackRate = BonusOf(bonuses, "magicAttackRate");

        var physicalAttack = Math.Max(0, Truncate(
            (attack + Truncate(strength * (double)strengthRate / PerMille) +
             Truncate(dexterity / (double)DexterityAttackDivisor)) *
            (PerMille + physicalAttackRate) / (double)PerMille));

        var magicAttack = Math.Max(0, Truncate(
            (magic + Truncate(intelligence * (double)intelligenceRate / PerMille) +
             Truncate(mentality * (double)mentalityRate / PerMille)) *
            (PerMille + magicAttackRate) / (double)PerMille));

        var baseDefense = defense + vitality;
        var defenseRate = BonusOf(bonuses, "defenseRate");
        var effectiveDefense = Fighter.EffectiveDefense(baseDefense, defenseRate);

        var hitBase = HitBasePerPoint * (dexterity + mentality);
        var hitBonus = Math.Max(0, BonusOf(bonuses, "hitBonus"));
        var evasionBonus = Math.Max(0, Math.Min(RateCap, BonusOf(bonuses, "evasionBonus")));
        var baseMagicResistance = BonusOf(bonuses, "magicResistance");

        return new CharacterPanel
        {
            Level = level,
            Attributes = new PrimaryAttributes
            {
                Strength = strength,
                Dexterity = dexterity,
                Vitality = vitality,
                Intelligence = intelligence,
                Mentality = mentality,
            },
            MaxHealth = BaseHealth + HealthPerLevel * level + HealthPerVitality * vitality,
            MaxMana = BaseMaxMana + BonusOf(bonuses, "maxMana"),
            PhysicalAttack = physicalAttack,
            MagicAttack = magicAttack,
            BaseDefense = baseDefense,
            Defense = effectiveDefense,
            DefenseRate = defenseRate,
            PhysicalResistance = PhysicalResistanceFor(effectiveDefense, level),
            MagicResistance = Fighter.MagicResistanceLimit(baseMagicResistance),
            BaseMagicResistance = baseMagicResistance,
            HitBase = hitBase,
            HitBonus = hitBonus,
            HitRate = HitRateFor(hitBase, hitBonus, level),
            EvasionBase = dexterity,
            EvasionBonus = evasionBonus,
            EvasionRate = EvasionRateFor(evasionBonus, dexterity, level),
            CriticalRate = Math.Max(0, DefaultCriticalRate + BonusOf(bonuses, "criticalRate")),
            CriticalResistance = Math.Max(0, BonusOf(bonuses, "criticalResistance")),
            CriticalDamage = Math.Max(0, DefaultCriticalDamage + BonusOf(bonuses, "criticalDamage")),
            CriticalDamageResistance = Math.Max(0, BonusOf(bonuses, "criticalDamageResistance")),
            WeaponBlock = Math.Max(0, DefaultWeaponBlock + BonusOf(bonuses, "weaponBlock")),
            ShieldBlock = Math.Max(0, BonusOf(bonuses, "shieldBlock")),
            OffHandShield = equipment.TryGetValue("off-hand", out var offHand) &&
                            items.Get(offHand.ItemId).Kind == "shield",
            WeaponKind = weapon,
        };
    }

    public Fighter ToFighter() => new Fighter
    {
        Level = Level,
        PhysicalAttack = PhysicalAttack,
        MagicAttack = MagicAttack,
        Defense = BaseDefense,
        DefenseRate = DefenseRate,
        PhysicalResistanceBonus = 0,
        MagicResistance = MagicResistance,
        MaxHealth = MaxHealth,
        HitBase = HitBase,
        HitBonus = HitBonus,
        EvasionBase = EvasionBase,
        EvasionBonus = EvasionBonus,
        CriticalRate = CriticalRate,
        CriticalResistance = CriticalResistance,
        CriticalDamage = CriticalDamage,
        CriticalDamageResistance = CriticalDamageResistance,
        WeaponBlock = WeaponBlock,
        ShieldBlock = ShieldBlock,
    }.Resolved();

    public CharacterPanel WithPassives(int level, IReadOnlyList<AttributeModifier> modifiers)
    {
        static int Value(IReadOnlyList<AttributeModifier> list, string key, int value) =>
            AttributeModifier.Apply(key, value, list);

        var defense = Math.Max(0, Value(modifiers, "defense", BaseDefense));
        var defenseRate = Math.Max(0, Value(modifiers, "defenseRate", DefenseRate));
        var effectiveDefense = Fighter.EffectiveDefense(defense, defenseRate);
        var hitBase = Value(modifiers, "hitBase", HitBase);
        var hitBonus = Math.Max(0, Value(modifiers, "hitBonus", HitBonus));
        var evasionBonus = Math.Max(0, Math.Min(RateCap, Value(modifiers, "evasionBonus", EvasionBonus)));

        return new CharacterPanel
        {
            Attributes = Attributes,
            Level = Level,
            MaxHealth = Value(modifiers, "maxHealth", MaxHealth),
            MaxMana = Value(modifiers, "maxMana", MaxMana),
            PhysicalAttack = Math.Max(0, Value(modifiers, "physicalAttack", PhysicalAttack)),
            MagicAttack = Math.Max(0, Value(modifiers, "magicAttack", MagicAttack)),
            BaseDefense = BaseDefense,
            Defense = effectiveDefense,
            DefenseRate = defenseRate,
            PhysicalResistance = PhysicalResistanceFor(effectiveDefense, level) +
                                 Value(modifiers, "physicalResistanceBonus", 0),
            MagicResistance = Fighter.MagicResistanceLimit(Value(modifiers, "magicResistance", BaseMagicResistance)),
            HitBase = hitBase,
            HitBonus = hitBonus,
            HitRate = HitRateFor(hitBase, hitBonus, level),
            EvasionBase = EvasionBase,
            EvasionBonus = evasionBonus,
            EvasionRate = EvasionRateFor(evasionBonus, Attributes.Dexterity, level),
            CriticalRate = Math.Max(0, Value(modifiers, "criticalRate", CriticalRate)),
            CriticalResistance = Math.Max(0, Value(modifiers, "criticalResistance", CriticalResistance)),
            CriticalDamage = Math.Max(0, Value(modifiers, "criticalDamage", CriticalDamage)),
            CriticalDamageResistance = Math.Max(0,
                Value(modifiers, "criticalDamageResistance", CriticalDamageResistance)),
            WeaponBlock = Math.Max(0, Value(modifiers, "weaponBlock", WeaponBlock)),
            ShieldBlock = Math.Max(0, Value(modifiers, "shieldBlock", ShieldBlock)),
            OffHandShield = OffHandShield,
            WeaponKind = WeaponKind,
        };
    }

    private static void ApplyModifier(
        Modifier modifier,
        Dictionary<string, int> bonuses,
        ref int strength,
        ref int dexterity,
        ref int vitality,
        ref int intelligence,
        ref int mentality)
    {
        switch (modifier.Stat)
        {
            case "strength": strength += modifier.Value; return;
            case "dexterity": dexterity += modifier.Value; return;
            case "vitality": vitality += modifier.Value; return;
            case "intelligence": intelligence += modifier.Value; return;
            case "mentality": mentality += modifier.Value; return;
            default: Add(bonuses, modifier.Stat, modifier.Value); return;
        }
    }

    private static void Add(Dictionary<string, int> bonuses, string key, int value) =>
        bonuses[key] = bonuses.GetValueOrDefault(key) + value;

    private static int BonusOf(Dictionary<string, int> bonuses, string key) =>
        bonuses.GetValueOrDefault(key);

    private static int Truncate(double value) => (int)Math.Truncate(value);

    private static int HitRateFor(int hitBase, int hitBonus, int level) =>
        Truncate((hitBase * PerMille + HitNumerator) / (double)(hitBase + level + HitLevelOffset)) +
        Math.Max(0, hitBonus) + HitDisplayBonus;

    private static int PhysicalResistanceFor(int defense, int level) =>
        Truncate(PerMille * (double)defense / (defense + DefenseWeightPerLevel * (level + DefenseLevelOffset)));

    private static int EvasionRateFor(int evasionBonus, int dexterity, int level) =>
        Math.Min(RateCap, PerMille + Math.Max(0, Math.Min(RateCap, evasionBonus)) -
                         Truncate((EvasionLevelFactor * (double)level + EvasionConstant) /
                                  (EvasionLevelDivisor * level + dexterity + EvasionDexterityOffset)));
}
