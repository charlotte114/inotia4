using System;
using System.Linq;
using Inotia4.Domain.Content;
using Inotia4.Domain.World;

namespace Inotia4.Domain.Story;

public sealed class QuestEnemyWatch : IQuestEnemyProvider
{
    private readonly QuestJournal _journal;
    private readonly IReadOnlyDictionary<string, QuestDefinition> _definitions;
    private readonly IReadOnlyDictionary<string, IReadOnlySet<string>> _itemSources;

    private int _revision = -1;
    private IReadOnlySet<string> _ids = new HashSet<string>();

    public QuestEnemyWatch(QuestJournal journal, IReadOnlyList<QuestDefinition> quests,
        IReadOnlyDictionary<string, Drops.QuestItemDropRule[]>? questItems)
    {
        _journal = journal;
        _definitions = quests.GroupBy(quest => quest.Id).ToDictionary(group => group.Key, group => group.First());

        var sources = new Dictionary<string, HashSet<string>>();
        foreach (var (enemyId, rules) in questItems ?? new Dictionary<string, Drops.QuestItemDropRule[]>())
        foreach (var rule in rules)
        {
            if (!sources.TryGetValue(rule.Item, out var enemies))
                sources[rule.Item] = enemies = [];
            enemies.Add(enemyId);
        }

        _itemSources = sources.ToDictionary(
            pair => pair.Key, pair => (IReadOnlySet<string>)pair.Value);
    }

    public QuestEnemySnapshot Snapshot()
    {
        var book = _journal.Book;
        if (book.Version == _revision) return new QuestEnemySnapshot(_revision, _ids);

        var ids = new HashSet<string>();
        foreach (var id in book.RunningQuestIds)
        {
            if (book.Status(id) != QuestStatus.Active) continue;
            if (!_definitions.TryGetValue(id, out var quest)) continue;

            foreach (var goal in quest.Goals)
            {
                if (goal.Kind == "defeat")
                {
                    ids.Add(goal.Target);
                }
                else if (goal.Kind == "item" &&
                         _itemSources.TryGetValue(goal.Target, out var enemies))
                {
                    foreach (var enemyId in enemies) ids.Add(enemyId);
                }
            }

            foreach (var change in quest.TargetChanges ?? []) ids.Add(change.EnemyId);
        }

        _revision = book.Version;
        _ids = ids;
        return new QuestEnemySnapshot(_revision, _ids);
    }
}
