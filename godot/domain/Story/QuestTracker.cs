using System;
using System.Collections.Generic;
using System.Linq;

namespace Inotia4.Domain.Story;

public sealed class QuestTracker
{
    private readonly CampaignContent _campaign;
    private readonly QuestJournal _journal;

    public QuestTracker(CampaignContent campaign, QuestJournal journal)
    {
        _campaign = campaign;
        _journal = journal;
    }

    public int CountOf(string questId, string goalId) => _journal.GoalCount(questId, goalId);

    public List<string> ReportDefeat(string enemyId) =>
        Emit("defeat", enemyId, "combat");

    public List<string> ReportInteract(string target, string? characterId = null) =>
        Emit("interact", target, "interaction", characterId);

    public List<string> ReportTalk(string target, string? characterId = null) =>
        Emit("talk", target, "talk", characterId);

    public List<string> ReportRecruit(string item) =>
        Emit("recruit", item, "recruit");

    /// <summary>
    /// 按背包重新数一遍 <c>item</c> 目标（151 个任务用它）。任务物品在接取时发放、
    /// 交付时收走，数量会随剧情变化，所以要在背包变动后调用。
    /// </summary>
    public List<string> RefreshItemGoals(Character.Bag inventory)
    {
        var before = SnapshotRunning();
        _journal.Refresh();
        return CollectReady(before);
    }

    public QuestDefinition? Guidance()
    {
        foreach (var id in _journal.RunningQuestIds)
            if (_journal.StatusOf(id) is QuestStatus.Active or QuestStatus.Ready)
                return _campaign.FindQuest(id);

        return _campaign.Quests.FirstOrDefault(quest =>
            _journal.StatusOf(quest.Id) is QuestStatus.Active or QuestStatus.Ready);
    }

    public string GoalText(QuestDefinition quest)
    {
        if (quest.Goals.Length == 0) return string.Empty;

        var parts = quest.Goals
            .Select(goal => $"{goal.Label ?? goal.Target} " +
                            $"{CountOf(quest.Id, goal.Id)}/{goal.Quantity}")
            .ToList();

        var text = string.Join("，", parts);
        return _journal.StatusOf(quest.Id) == QuestStatus.Ready ? $"{text}（可交付）" : text;
    }

    private List<string> Emit(string kind, string target, string source, string? characterId = null)
    {
        var before = SnapshotRunning();
        _journal.EmitFact(kind, target, source, characterId);
        return CollectReady(before);
    }

    private Dictionary<string, QuestStatus> SnapshotRunning() =>
        _journal.RunningQuestIds.Distinct().ToDictionary(id => id, _journal.StatusOf);

    private List<string> CollectReady(Dictionary<string, QuestStatus> before) =>
        _journal.RunningQuestIds
            .Where(id => _journal.StatusOf(id) == QuestStatus.Ready &&
                         before.GetValueOrDefault(id) != QuestStatus.Ready)
            .Distinct()
            .ToList();
}
