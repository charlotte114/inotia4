using System;
using System.Collections.Generic;
using System.Linq;
using Inotia4.Domain.Content;

namespace Inotia4.Domain.Character;

public sealed class GradeProfile
{
    public required IReadOnlyList<int> Weights { get; init; }

    public IReadOnlyList<int>? RarityScale { get; init; }

    public int? MinRarity { get; init; }

    public int? MaxGrade { get; init; }

    public int? Luck { get; init; }
}

public sealed record RolledGrade(
    int Grade, int Rarity, string GradeName, int Attack, int Defense, int OptionCount, int SocketLimit)
    : EquipmentGradeSeed(Grade, GradeName, Attack, Defense, OptionCount, SocketLimit);

public static class EquipmentGrade
{
    public static int[] RarityWeights(GradeProfile profile)
    {
        var scale = profile.RarityScale ?? [];
        var weights = profile.Weights
            .Select((value, tier) => value * (tier < scale.Count ? scale[tier] : 1000) / 1000)
            .ToArray();

        if (profile.MinRarity is { } minRarity)
            for (var tier = 0; tier < weights.Length; tier++)
                if (tier < minRarity) weights[tier] = 0;

        var luck = Math.Clamp(profile.Luck ?? 0, 0, 50);
        if (luck > 0 && weights.Length >= 3 && weights[0] > 0)
        {
            var move = Math.Min(weights[0] / 2, luck * 2);
            weights[0] -= move;
            weights[2] += move;
        }

        return weights;
    }

    public static RolledGrade? RollGrade(CharacterStatsCatalog stats, EquipmentNumbers equipment,
        GradeProfile profile, Func<double> random)
    {
        var grades = stats.Grades;
        var variants = equipment.GradeVariants;
        if (grades.Length == 0 || variants.Length == 0) return null;

        var weights = RarityWeights(profile);
        var tier = PickWeighted(weights, random);
        if (tier < 0)
        {
            tier = weights.Length - 1;
            while (tier > 0 && weights[tier] == 0) tier -= 1;
        }

        var maxGrade = profile.MaxGrade ?? 9;
        var candidates = grades
            .Where(grade => grade.Rarity == tier && grade.Index <= maxGrade && grade.Index < variants.Length)
            .ToList();
        var pool = candidates.Count > 0
            ? candidates
            : grades.Where(grade => grade.Index <= maxGrade && grade.Index < variants.Length).ToList();
        if (pool.Count == 0) return null;

        var index = PickWeighted(pool.Select(grade => grade.Weight).ToArray(), random);
        if (index < 0) return null;

        var chosen = pool[index];
        var variant = variants[chosen.Index];
        return new RolledGrade(chosen.Index, chosen.Rarity, variant.Name, variant.Attack, variant.Defense,
            variant.OptionCount, variant.SocketLimit);
    }

    public static RolledGrade? StaticGrade(CharacterStatsCatalog stats, EquipmentNumbers equipment)
    {
        var index = equipment.DefaultGrade ?? EquipmentRoller.DefaultGrade;
        if (index < 0 || index >= equipment.GradeVariants.Length) return null;

        var variant = equipment.GradeVariants[index];
        var rarity = stats.Grades.FirstOrDefault(grade => grade.Index == index)?.Rarity ?? variant.Rarity;        return new RolledGrade(index, rarity, variant.Name, variant.Attack, variant.Defense,
            variant.OptionCount, variant.SocketLimit);
    }

    private static int Draw(Func<double> random, int max)
    {
        var value = random();
        if (double.IsNaN(value) || value < 0 || value >= 1) throw new InvalidOperationException("品级随机值无效");
        return (int)Math.Floor(value * max);
    }

    private static int PickWeighted(IReadOnlyList<int> weights, Func<double> random)
    {
        var total = 0;
        foreach (var weight in weights) total += weight;
        if (total <= 0) return -1;

        var roll = Draw(random, total);
        var sum = 0;
        for (var index = 0; index < weights.Count; index++)
        {
            sum += weights[index];
            if (roll < sum) return index;
        }

        return weights.Count - 1;
    }
}
