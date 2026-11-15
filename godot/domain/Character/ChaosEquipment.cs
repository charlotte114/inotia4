using System;

namespace Inotia4.Domain.Character;

public sealed record EquipmentChaos(string Tier, int ValuePercent, int LevelOffset)
{
    public const string ChaosTier = "chaos";
    public const string EpicChaosTier = "epic-chaos";
}

public sealed record ChaosRule(
    int[] ValueDelta, int[] ValueBounds, int LevelDiceDivisor, int LevelClampDivisor);

public static class ChaosEquipment
{
    public static int EquipmentLevel(int itemLevel, ItemInstance entry)
    {
        var limit = itemLevel <= 100 ? 100 : 105;
        return Math.Max(0, Math.Min(limit, itemLevel + (entry.Numbers?.Chaos?.LevelOffset ?? 0)));
    }

    public static EquipmentChaos RollChaos(int itemLevel, ItemInstance entry, string tier, ChaosRule rule,
        Func<double> random)
    {
        int Die(int min, int max)
        {
            var value = random();
            if (double.IsNaN(value) || value < 0 || value >= 1) throw new InvalidOperationException("随机结果无效");
            return min + (int)Math.Floor(value * (max - min + 1));
        }

        int Average(int min, int max) => (int)((Die(min, max) + Die(min, max)) / 2.0);

        var delta = Average(rule.ValueDelta[0], rule.ValueDelta[1]);
        var radius = (int)(itemLevel / (double)rule.LevelDiceDivisor);
        var limit = (int)(itemLevel / (double)rule.LevelClampDivisor);

        var valuePercent = Math.Max(rule.ValueBounds[0],
            Math.Min(rule.ValueBounds[1], (entry.Numbers?.Chaos?.ValuePercent ?? 100) + delta));

        return new EquipmentChaos(tier, valuePercent,
            Math.Max(-limit, Math.Min(limit, Average(-radius, radius))));
    }
}
