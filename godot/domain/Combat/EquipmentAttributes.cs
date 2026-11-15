using System.Collections.Generic;
using Inotia4.Domain.Character;

namespace Inotia4.Domain.Combat;

public static class EquipmentAttributes
{
    private static readonly Dictionary<string, string> OptionAttributes = new()
    {
        ["fire"] = "fireSplashChance",
        ["wind"] = "windStrikeChance",
        ["freeze"] = "frostStrikeChance",
        ["divine"] = "divineStrikeChance",
        ["darkness"] = "darknessStrikeChance",
        ["poison"] = "poisonStrikeChance",
    };

    public readonly record struct AttackProcRates(
        int FireSplashChance,
        int WindStrikeChance,
        int FrostStrikeChance,
        int DivineStrikeChance,
        int DarknessStrikeChance,
        int PoisonStrikeChance)
    {
        public int Of(string key) => key switch
        {
            "fireSplashChance" => FireSplashChance,
            "windStrikeChance" => WindStrikeChance,
            "frostStrikeChance" => FrostStrikeChance,
            "divineStrikeChance" => DivineStrikeChance,
            "darknessStrikeChance" => DarknessStrikeChance,
            "poisonStrikeChance" => PoisonStrikeChance,
            _ => 0,
        };
    }

    public static AttackProcRates AttackProcs(IReadOnlyList<ItemInstance> equipped)
    {
        var fire = 0;
        var wind = 0;
        var frost = 0;
        var divine = 0;
        var darkness = 0;
        var poison = 0;

        foreach (var item in equipped)
        {
            if (item.Numbers is null) continue;
            foreach (var modifier in item.Numbers.Modifiers)
                if (OptionAttributes.TryGetValue(modifier.Stat, out var key)) Apply(key, modifier.Value);
            foreach (var modifier in item.Numbers.GemModifiers)
                if (OptionAttributes.TryGetValue(modifier.Stat, out var key)) Apply(key, modifier.Value);
        }

        return new AttackProcRates(fire, wind, frost, divine, darkness, poison);

        void Apply(string key, int value)
        {
            switch (key)
            {
                case "fireSplashChance": fire += value; break;
                case "windStrikeChance": wind += value; break;
                case "frostStrikeChance": frost += value; break;
                case "divineStrikeChance": divine += value; break;
                case "darknessStrikeChance": darkness += value; break;
                case "poisonStrikeChance": poison += value; break;
            }
        }
    }

    public static int ThreatReduction(IReadOnlyList<ItemInstance> equipped)
    {
        var reduction = 0;
        foreach (var item in equipped)
        {
            if (item.Numbers is null) continue;
            foreach (var modifier in item.Numbers.Modifiers)
                if (modifier.Stat == "aggroReduction") reduction += modifier.Value;
            foreach (var modifier in item.Numbers.GemModifiers)
                if (modifier.Stat == "aggroReduction") reduction += modifier.Value;
        }

        return reduction;
    }

    public static int BaseOffhandAttackChance(string classId, string offhandKind, string armorKind)
    {
        if (classId != "assassin" && classId != "warrior") return 0;
        var weapon = offhandKind switch
        {
            "dagger" => 140,
            "longsword" => 100,
            "axe" or "mace" => 90,
            _ => 0,
        };
        return weapon + (armorKind == "leather" ? 50 : 0);
    }

    /// <summary>
    /// 有资格的攻击在读取当前副手率**之前**就掷骰，哪怕率是零也要消耗这次随机
    /// （掷点序列因此稳定，同一场战斗里后续的随机不会因为率变零而错位）。没资格时**连骰子都不取**——
    /// 所以随机与率都按回调传进来，由调用方在正确的时机取。
    /// </summary>
    public static bool ChooseDualWield(string classId, bool hasOffhandWeapon,
        Func<double> random, Func<int> chance)
    {
        if ((classId != "assassin" && classId != "warrior") || !hasOffhandWeapon) return false;
        var roll = (int)Math.Floor(random() * 999) + 1;
        return roll < Math.Max(0, chance());
    }

    public static int OffhandPhysicalAttack(int physicalAttack, int mainHandPower, int offhandPower) =>
        (int)Math.Floor((physicalAttack - (double)mainHandPower) / 2) + offhandPower;

    public static int StateImmunity(IReadOnlyList<ItemInstance> equipped, string state)
    {
        var option = state switch
        {
            "sleep" => "sleepResistance",
            "stun" => "stunResistance",
            _ => null,
        };
        if (option is null) return 0;

        var value = 0;
        foreach (var item in equipped)
        {
            if (item.Numbers is null) continue;
            foreach (var modifier in item.Numbers.Modifiers)
                if (modifier.Stat == option) value += modifier.Value;
            foreach (var modifier in item.Numbers.GemModifiers)
                if (modifier.Stat == option) value += modifier.Value;
        }

        return value;
    }

    public readonly record struct RecoveryModifiers(
        int LifeDrainPerThousand,
        int ManaRecoveryPerPulse,
        int ManaCostReductionRate);

    public static RecoveryModifiers Recovery(IReadOnlyList<ItemInstance> equipped)
    {
        var lifeDrain = 0;
        var manaRecovery = 0;
        var manaCostReduction = 0;

        foreach (var item in equipped)
        {
            if (item.Numbers is null) continue;
            foreach (var modifier in item.Numbers.Modifiers) Apply(modifier.Stat, modifier.Value);
            foreach (var modifier in item.Numbers.GemModifiers) Apply(modifier.Stat, modifier.Value);
        }

        return new RecoveryModifiers(Math.Max(0, lifeDrain), manaRecovery, Math.Max(0, manaCostReduction));

        void Apply(string stat, int value)
        {
            switch (stat)
            {
                case "lifeSteal": lifeDrain += value; break;
                case "manaRegen": manaRecovery += value; break;
                case "spellExpert": manaCostReduction += value; break;
            }
        }
    }
}
