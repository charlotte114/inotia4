using System.Text.Json.Serialization;
using Inotia4.Domain.Character;
using Inotia4.Domain.Combat;
using Inotia4.Domain.Content;

namespace Inotia4.Domain.Party;

public static class CompanionGroupEffects
{
    public static readonly string[] All =
    [
        "low-health-resistance", "sword-damage", "plate-defense", "ancient-legendary-chance",
        "sell-price", "critical-stun-chance", "leather-physical-damage", "chest-quality",
        "magic-damage", "cloth-magic-damage", "magic-resistance", "scroll-drop",
        "buy-discount", "blunt-stun-chance", "experience", "rare-recruit-chance",
        "potion-recovery", "bow-damage", "critical-damage", "material-drop",
        "status-resistance", "two-hand-damage", "potion-cooldown-ms", "equipment-drop",
    ];
}

public sealed class CompanionGroupSkill
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;

    [JsonPropertyName("effect")] public string Effect { get; init; } = string.Empty;

    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;

    [JsonPropertyName("description")] public string Description { get; init; } = string.Empty;

    [JsonPropertyName("icon")] public string Icon { get; init; } = string.Empty;

    [JsonPropertyName("value")] public int Value { get; init; }

    [JsonPropertyName("unit")] public string Unit { get; init; } = string.Empty;
}

public sealed class CompanionTraining
{
    [JsonPropertyName("classId")] public string ClassId { get; init; } = string.Empty;

    [JsonPropertyName("position")] public int Position { get; init; }

    [JsonPropertyName("maxLevel")] public int MaxLevel { get; init; }

    [JsonPropertyName("requiredLevels")] public int[] RequiredLevels { get; init; } = [];
}

public sealed class CompanionEmblemRule
{
    [JsonPropertyName("classId")] public string? ClassId { get; init; }

    [JsonPropertyName("qualityWeights")] public int[] QualityWeights { get; init; } = [];

    [JsonPropertyName("guaranteedRare")] public bool GuaranteedRare { get; init; }
}

public sealed class CompanionStatDie
{
    [JsonPropertyName("sides")] public int Sides { get; init; }

    [JsonPropertyName("count")] public int Count { get; init; }
}

public sealed class CompanionStatDice
{
    [JsonPropertyName("ordinary")] public CompanionStatDie[] Ordinary { get; init; } = [];

    [JsonPropertyName("rare")] public CompanionStatDie[] Rare { get; init; } = [];
}

public sealed class CompanionGenerationRules
{
    [JsonPropertyName("emblems")] public Dictionary<string, CompanionEmblemRule> Emblems { get; init; } = new();

    [JsonPropertyName("training")] public Dictionary<string, CompanionTraining> Training { get; init; } = new();

    [JsonPropertyName("skillChoices")] public int[][] SkillChoices { get; init; } = [];

    [JsonPropertyName("skillCountWeights")] public int[][][] SkillCountWeights { get; init; } = [];

    [JsonPropertyName("statDice")] public Dictionary<string, CompanionStatDice> StatDice { get; init; } = new();

    [JsonPropertyName("groupPools")] public Dictionary<string, string[]> GroupPools { get; init; } = new();
}

public sealed class GeneratedCompanion
{
    public int Level { get; set; } = 1;

    public PrimaryAttributes BaseAttributes { get; set; } = new();

    public Dictionary<string, int> SkillCaps { get; set; } = new();

    public List<string> GroupSkills { get; set; } = [];

    public GeneratedCompanion Clone() => new()
    {
        Level = Level,
        BaseAttributes = new PrimaryAttributes
        {
            Strength = BaseAttributes.Strength,
            Dexterity = BaseAttributes.Dexterity,
            Vitality = BaseAttributes.Vitality,
            Intelligence = BaseAttributes.Intelligence,
            Mentality = BaseAttributes.Mentality,
        },
        SkillCaps = new Dictionary<string, int>(SkillCaps),
        GroupSkills = [.. GroupSkills],
    };
}

/// <summary>
/// 招募掷骰与伙伴技能成长。
///
/// 掷骰全部走调用方给的 <c>random</c>（返回 [0,1) 的一个数），
/// 所以同一串随机值在任何机器上都得到同一只伙伴——测试靠这一点钉住数值。
/// </summary>
public static class CompanionProgression
{
    public static readonly string[] MercenaryClassOrder =
        ["dark-knight", "assassin", "warlock", "priest", "ranger", "warrior"];

    /// <summary>在 [minimum, maximum] 里取一个整数：随机值必须落在 [0,1)，越界直接拒绝。</summary>
    public static int RandomInteger(Func<double> random, int minimum, int maximum)
    {
        var value = random();
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0 || value >= 1)
            throw new InvalidOperationException("招募随机值无效");
        return minimum + (int)Math.Floor(value * (maximum - minimum + 1));
    }

    public static int WeightedIndex(Func<double> random, IReadOnlyList<int> weights)
    {
        var roll = RandomInteger(random, 0, 99);
        for (var index = 0; index < weights.Count; index++)
        {
            roll -= weights[index];
            if (roll < 0) return index;
        }

        return weights.Count;
    }

    private static T DrawWithoutReplacement<T>(List<T> pool, Func<double> random)
    {
        var index = RandomInteger(random, 0, pool.Count - 1);
        var result = pool[index];
        pool[index] = pool[^1];
        pool.RemoveAt(pool.Count - 1);
        return result;
    }

    public static GeneratedCompanion Generate(CompanionGenerationRules rules,
        string classId, int? quality, IReadOnlyList<string> fixedSkills, int heroLevel, Func<double> random)
    {
        var tier = quality ?? 0;
        var level = Math.Max(1, RandomInteger(random, heroLevel - 3, heroLevel));

        var training = rules.Training
            .Where(pair => pair.Value.ClassId == classId)
            .ToList();

        var selected = new List<string>();
        if (tier < 2)
        {
            for (var index = 0; index < rules.SkillChoices.Length; index++)
            {
                var positions = rules.SkillChoices[index];
                var count = WeightedIndex(random, rules.SkillCountWeights[tier][index]) + 1;
                if (count > 3) continue;

                var pool = training
                    .Where(pair => positions.Contains(pair.Value.Position))
                    .Select(pair => pair.Key)
                    .ToList();
                for (var drawn = 0; drawn < count && pool.Count > 0; drawn++)
                    selected.Add(DrawWithoutReplacement(pool, random));
            }
        }
        else
        {
            selected.AddRange(fixedSkills);
        }

        var caps = new Dictionary<string, int>();
        foreach (var id in selected) caps[id] = rules.Training[id].MaxLevel;

        var fullCount = RandomInteger(random, 4, 7);
        var reducedCount = RandomInteger(random, 2, 6);
        var remaining = new List<string>(selected);
        for (var index = 0; remaining.Count > 0; index++)
        {
            var id = DrawWithoutReplacement(remaining, random);
            var cut = index < fullCount ? 0 : index < fullCount + reducedCount ? 1 : 2;
            caps[id] = Math.Max(1, caps[id] - cut);
        }

        var groupPool = new List<string>(rules.GroupPools.GetValueOrDefault(classId) ?? []);
        var groupSkills = new List<string>();
        for (var index = 0; index < (tier < 2 ? 1 : 2) && groupPool.Count > 0; index++)
            groupSkills.Add(DrawWithoutReplacement(groupPool, random));

        var dice = rules.StatDice[classId];
        var rows = tier < 2 ? dice.Ordinary : dice.Rare;
        var values = new int[PrimaryStats.All.Length];
        for (var index = 0; index < PrimaryStats.All.Length; index++)
        {
            var die = rows[index];
            var value = 0;
            for (var roll = 0; roll < die.Count; roll++) value += RandomInteger(random, 1, die.Sides);
            values[index] = value;
        }

        return new GeneratedCompanion
        {
            Level = level,
            BaseAttributes = new PrimaryAttributes
            {
                Strength = values[0],
                Dexterity = values[1],
                Vitality = values[2],
                Intelligence = values[3],
                Mentality = values[4],
            },
            SkillCaps = caps,
            GroupSkills = groupSkills,
        };
    }
}

public sealed class CompanionSkillProgression : SkillConfiguration
{
    private readonly Dictionary<string, int> _levels = [];

    private readonly Dictionary<string, CompanionTraining> _training;

    public CompanionSkillProgression(Dictionary<string, int> caps, Dictionary<string, CompanionTraining> training)
    {
        Caps = new Dictionary<string, int>(caps);
        _training = training;
    }

    public Dictionary<string, int> Caps { get; }

    public void UpdateLevel(int level)
    {
        _levels.Clear();
        foreach (var (id, cap) in Caps)
        {
            var required = _training.TryGetValue(id, out var training) ? training.RequiredLevels : [];
            _levels[id] = required.Take(Math.Min(cap, required.Length)).Count(value => level >= value);
        }

        MarkChanged();
    }

    public Dictionary<string, int> SkillLevels() => new(_levels);

    public override SkillLoadout Draft(SkillCatalog catalog, string classId)
    {
        var character = catalog.Class(classId) ?? throw new ArgumentException("职业不存在");
        var levels = new Dictionary<string, int>();
        foreach (var id in character.SkillIds) levels[id] = _levels.GetValueOrDefault(id);
        return new SkillLoadout { Levels = levels };
    }

    public override void Save(SkillCatalog catalog, string classId, SkillLoadout draft)
    {
        var character = catalog.Class(classId);
        if (character is null) throw new ArgumentException("技能配置无效");

        var expected = Draft(catalog, classId).Levels;
        foreach (var id in draft.Levels.Keys)
            if (!character.SkillIds.Contains(id)) throw new ArgumentException("佣兵技能随角色等级成长");
        foreach (var id in character.SkillIds)
            if (draft.Levels.GetValueOrDefault(id) != expected.GetValueOrDefault(id))
                throw new ArgumentException("佣兵技能随角色等级成长");

        Loadouts[classId] = draft.Clone();
        MarkChanged();
    }
}
