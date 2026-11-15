using Inotia4.Domain.Content;
using Inotia4.Domain.Ui;

namespace Inotia4.Domain.Character;

public sealed class SkillLoadout
{
    public Dictionary<string, int> Levels { get; init; } = new();

    public SkillLoadout Clone() => new() { Levels = new Dictionary<string, int>(Levels) };

    public int LevelOf(string id) => Levels.GetValueOrDefault(id);
}

public sealed class SkillConfigurationState
{
    public List<KeyValuePair<string, SkillLoadout>> Loadouts { get; init; } = [];

    public List<KeyValuePair<string, int>> Pages { get; init; } = [];

    public SkillConfigurationState Clone()
    {
        var loadouts = new List<KeyValuePair<string, SkillLoadout>>(Loadouts.Count);
        foreach (var (classId, loadout) in Loadouts)
            loadouts.Add(new KeyValuePair<string, SkillLoadout>(classId, loadout.Clone()));

        return new SkillConfigurationState { Loadouts = loadouts, Pages = [.. Pages] };
    }
}

public class SkillConfiguration
{
    protected readonly Dictionary<string, SkillLoadout> Loadouts = new();

    private readonly Dictionary<string, int> _pages = new();

    public int Version { get; private set; }

    protected void MarkChanged() => Version += 1;

    public SkillConfigurationState CaptureState()
    {
        var loadouts = new List<KeyValuePair<string, SkillLoadout>>(Loadouts.Count);
        foreach (var (classId, loadout) in Loadouts)
            loadouts.Add(new KeyValuePair<string, SkillLoadout>(classId, loadout.Clone()));
        return new SkillConfigurationState { Loadouts = loadouts, Pages = [.. _pages] };
    }

    public void RestoreState(SkillConfigurationState state)
    {
        var restored = state.Clone();
        Loadouts.Clear();
        foreach (var (classId, loadout) in restored.Loadouts) Loadouts[classId] = loadout.Clone();
        _pages.Clear();
        foreach (var (classId, page) in restored.Pages) _pages[classId] = page;
        MarkChanged();
    }

    public virtual SkillLoadout Draft(SkillCatalog catalog, string classId)
    {
        if (Loadouts.TryGetValue(classId, out var existing)) return existing.Clone();

        var character = catalog.Class(classId) ?? throw new ArgumentException("职业不存在");
        var levels = new Dictionary<string, int>();
        foreach (var id in character.SkillIds) levels[id] = 1;
        return new SkillLoadout { Levels = levels };
    }

    public List<string> ActiveSkills(SkillCatalog catalog, string classId)
    {
        var character = catalog.Class(classId);
        if (character is null) return [];

        var levels = Draft(catalog, classId).Levels;
        return character.SkillIds
            .Where(id => catalog.Skills.GetValueOrDefault(id)?.IsActive == true && levels.GetValueOrDefault(id) > 0)
            .ToList();
    }

    public virtual void Save(SkillCatalog catalog, string classId, SkillLoadout draft)
    {
        var character = catalog.Class(classId) ?? throw new ArgumentException("技能配置无效");
        var current = Draft(catalog, classId).Levels;
        foreach (var (id, level) in draft.Levels)
        {
            if (!character.SkillIds.Contains(id)) throw new ArgumentException("技能配置无效");
            if (current.GetValueOrDefault(id) != level) throw new ArgumentException("技能等级只能通过学习改变");
        }

        foreach (var id in character.SkillIds)
        {
            if (!draft.Levels.TryGetValue(id, out var level) ||
                level != current.GetValueOrDefault(id))
                throw new ArgumentException("技能等级只能通过学习改变");
        }

        Loadouts[classId] = draft.Clone();
        MarkChanged();
    }

    public int ShortcutPage(string classId) => _pages.GetValueOrDefault(classId);

    public void DebugInstallLevels(string classId, IReadOnlyDictionary<string, int> levels)
    {
        Loadouts[classId] = new SkillLoadout { Levels = new Dictionary<string, int>(levels) };
        MarkChanged();
    }

    public void SetShortcutPage(string classId, int page)
    {
        if (page < 0 || page >= ShortcutLayout.PageCount) throw new ArgumentException("技能组无效");
        _pages[classId] = page;
        MarkChanged();
    }

    public int NextShortcutPage(string classId)
    {
        var page = (ShortcutPage(classId) + 1) % ShortcutLayout.PageCount;
        SetShortcutPage(classId, page);
        return page;
    }
}

/// <summary>
/// 正式游戏的已学技能与快捷栏，菜单与战斗读同一份状态。
///
/// 技能点不单独记：它由角色卡上的「(等级−1)×每级技能点 ＋ 药水加的 − 已花掉的」推出来；
/// 等级、背包也都在角色卡上，所以这里直接持有那张卡，不再自己记一份——
/// 两处各记一份就会分叉。
/// </summary>
public sealed class StorySkillProgression : SkillConfiguration
{
    private readonly BattleCharacter? _hero;

    private readonly SkillCatalog? _catalog;

    public StorySkillProgression(BattleCharacter? hero = null, SkillCatalog? catalog = null)
    {
        _hero = hero;
        _catalog = catalog;
    }

    public int ResetVersion { get; private set; }

    public string ClassId => _hero?.ClassId ?? string.Empty;

    public Bag Bag => RequireHero().Bag;

    public int Points => _hero?.SkillPoints ?? 0;

    private BattleCharacter RequireHero() =>
        _hero ?? throw new InvalidOperationException("技能进度不可用");

    private SkillCatalog RequireCatalog() =>
        _catalog ?? throw new InvalidOperationException("技能进度不可用");

    /// <summary>
    /// 主角的初始配置：第一个技能是职业的初始主动技能（1 级），其余从 0 级开始——
    /// 首个技能不是主动技能就当场报错，因为这职业根本没有可用的起手技能。
    /// </summary>
    public override SkillLoadout Draft(SkillCatalog catalog, string classId)
    {
        if (Loadouts.TryGetValue(classId, out var existing)) return existing.Clone();

        var character = catalog.Class(classId) ?? throw new ArgumentException("职业不存在");
        var starter = character.SkillIds.Length > 0 ? character.SkillIds[0] : null;
        if (starter is null || catalog.Skills.GetValueOrDefault(starter)?.IsActive != true)
            throw new ArgumentException("职业缺少初始主动技能");

        var levels = new Dictionary<string, int>();
        foreach (var id in character.SkillIds) levels[id] = id == starter ? 1 : 0;
        return new SkillLoadout { Levels = levels };
    }

    /// <summary>等级只能通过学习改变；职业还必须与角色卡一致，写别人的职业一律拒绝。</summary>
    public override void Save(SkillCatalog catalog, string classId, SkillLoadout draft)
    {
        var character = catalog.Class(classId);
        if (character is null || classId != _hero?.ClassId) throw new ArgumentException("技能配置无效");

        var current = Draft(catalog, classId).Levels;
        foreach (var id in character.SkillIds)
            if (draft.Levels.GetValueOrDefault(id) != current.GetValueOrDefault(id))
                throw new ArgumentException("技能等级只能通过学习改变");
        foreach (var id in draft.Levels.Keys)
            if (!character.SkillIds.Contains(id)) throw new ArgumentException("技能等级只能通过学习改变");

        Loadouts[classId] = draft.Clone();
        MarkChanged();
    }

    public ShortcutBar Bar()
    {
        var hero = RequireHero();
        return ShortcutBar.For(hero.Bag, "player", ActiveSkills(RequireCatalog(), hero.ClassId),
            hero.Bag.Catalog.StartingShortcut);
    }

    private SkillDefinition RequireSkill(string id)
    {
        var catalog = RequireCatalog();
        var skill = catalog.Skills.GetValueOrDefault(id);
        if (skill is null || !catalog.SkillsOf(_hero?.ClassId).Contains(id))
            throw new ArgumentException("请选择本职业的技能");
        return skill;
    }

    public int SkillLevel(string id)
    {
        RequireSkill(id);
        return Draft(RequireCatalog(), RequireHero().ClassId).LevelOf(id);
    }

    public int InvestedPoints()
    {
        var catalog = RequireCatalog();
        var character = catalog.Class(_hero?.ClassId);
        if (character is null) return 0;

        var total = 0;
        foreach (var id in character.SkillIds)
        {
            var rank = SkillLevel(id);
            if (rank <= 0) continue;
            var ranks = catalog.Skills[id].Ranks;
            total += ranks[rank - 1].InvestedSkillPoints ?? ranks.Take(rank).Sum(value => value.PointCost);
        }

        return total;
    }

    public SkillLearningRequirements LearningRequirements(string id)
    {
        var skill = RequireSkill(id);
        var level = SkillLevel(id);
        var cost = level < skill.Ranks.Count ? skill.Ranks[level].PointCost : 0;
        var invested = InvestedPoints();
        var required = skill.RequiredInvestedSkillPoints;
        return new SkillLearningRequirements(
            level,
            cost,
            invested,
            required,
            AtLimit: level >= skill.NormalMaxLevel,
            ConditionMet: invested >= required,
            PointsAvailable: Points >= cost);
    }

    public string? LearningProblem(string id)
    {
        var state = LearningRequirements(id);
        if (state.AtLimit) return "已达到普通学习上限";
        if (!state.ConditionMet) return $"条件：投入{state.RequiredInvested}技能点";
        if (!state.PointsAvailable) return $"需要 {state.Cost} 个技能点";
        return null;
    }

    public int Learn(string id)
    {
        var problem = LearningProblem(id);
        if (problem is not null) throw new InvalidOperationException(problem);

        var skill = RequireSkill(id);
        var next = SkillLevel(id) + 1;
        var draft = Draft(RequireCatalog(), RequireHero().ClassId);
        draft.Levels[id] = next;
        Loadouts[RequireHero().ClassId] = draft;
        MarkChanged();
        RequireHero().SpendSkillPoints(skill.Ranks[next - 1].PointCost);
        return next;
    }

    public string? BookProblem(string id)
    {
        var skill = RequireSkill(id);
        var rank = SkillLevel(id);
        if (rank < skill.NormalMaxLevel) return $"技能达到 {skill.NormalMaxLevel} 级后才能使用技能书";
        if (rank >= skill.Ranks.Count) return "技能已达到最高等级";
        return null;
    }

    public int Book(string id)
    {
        var problem = BookProblem(id);
        if (problem is not null) throw new InvalidOperationException(problem);

        var draft = Draft(RequireCatalog(), RequireHero().ClassId);
        var next = draft.Levels[id] + 1;
        draft.Levels[id] = next;
        Loadouts[RequireHero().ClassId] = draft;
        MarkChanged();
        return next;
    }

    public void Reset()
    {
        var hero = RequireHero();
        var catalog = RequireCatalog();

        ResetVersion += 1;
        Loadouts.Remove(hero.ClassId);
        MarkChanged();
        SetShortcutPage(hero.ClassId, 0);
        hero.Bag.SetShortcuts("player",
            ShortcutLayout.DefaultRow(ActiveSkills(catalog, hero.ClassId), hero.Bag.Catalog.StartingShortcut));
        hero.ResetSkillPoints();
    }

    public bool IsEquipped(string id) => SkillLevel(id) > 0 && Bar().Assigned(ShortcutEntry.SkillKind, id);

    public string? ApplyItem(string itemId, string? skillId = null)
    {
        var hero = RequireHero();
        if (!hero.Bag.Catalog.Items.TryGetValue(itemId, out var item)) return null;

        if (item.Use?.Kind == ItemUseRules.Learn)
        {
            if (skillId is null) throw new InvalidOperationException("请选择本职业的技能");
            var rank = Book(skillId);
            return $"{RequireSkill(skillId).Name}提升至 {rank} 级";
        }

        if (item.Use?.Kind == ItemUseRules.ResetSkills)
        {
            Reset();
            return "已初始化技能与技能点";
        }

        if (itemId == ItemUseRules.SkillPointItem)
        {
            hero.GrantSkillPoint();
            return "获得 1 个技能点";
        }

        return null;
    }
}
