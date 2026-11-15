using System;
using System.Collections.Generic;
using System.Linq;

namespace Inotia4.Domain.Story;

public static class QuestJournalView
{
    private static readonly QuestStatus[] AcceptedStatuses = [QuestStatus.Active, QuestStatus.Ready, QuestStatus.Completed];

    public sealed record Entry(QuestDefinition Quest, QuestProgress State);

    public sealed record Group(QuestGroupDefinition Definition, bool Ready, IReadOnlyList<Entry> Entries);

    public static IReadOnlyList<Group> Groups(CampaignContent campaign,
        IReadOnlyDictionary<string, QuestProgress> states, string? focus = null)
    {
        var progression = new Dictionary<string, int>();
        for (var index = 0; index < campaign.Quests.Length; index += 1)
            progression.TryAdd(campaign.Quests[index].Id, index);

        var selected = campaign.GroupTable.Select(definition =>
        {
            var entries = definition.QuestIds
                .Select(id => (id, quest: campaign.FindQuest(id), state: states.GetValueOrDefault(id)))
                .Where(pair => pair.quest is not null && pair.state is not null)
                .Select(pair => new Entry(pair.quest!, pair.state!))
                .Where(entry => focus is not null
                    ? entry.Quest.Id == focus && entry.State.Status != QuestStatus.Locked
                    : IsVisible(entry.Quest) && AcceptedStatuses.Contains(entry.State.Status))
                .Reverse()
                .ToList();

            return (definition, entries);
        }).Where(pair => pair.entries.Count > 0).ToList();

        if (focus is null)
        {
            // 没有调试焦点时，一组里必须还有进行中或待交付的一段才整组显示。
            selected.RemoveAll(pair => !pair.entries.Any(entry =>
                entry.State.Status is QuestStatus.Active or QuestStatus.Ready));
        }

        return selected
            .Select(pair => new Group(pair.definition,
                pair.entries.Any(entry => entry.State.Status == QuestStatus.Ready), pair.entries))
            .OrderByDescending(group => group.Definition.Kind == "main")
            .ThenBy(group => group.Definition.QuestIds
                .Select(id => progression.GetValueOrDefault(id, int.MaxValue)).DefaultIfEmpty(int.MaxValue).Min())
            .ToList();
    }

    private static bool IsVisible(QuestDefinition quest) => !quest.Hidden && quest.Completion != "internal";

    public static string Text(Group group, Func<string, string> actorName)
    {
        var blocks = group.Entries.Select(entry =>
        {
            var quest = entry.Quest;
            var state = entry.State;
            if (state.Status == QuestStatus.Completed) return $"&C$G[完成]&N{quest.Description}&C";

            if (quest.Completion == "talk")
            {
                var targets = quest.DescriptionTargets?.Length > 0
                    ? string.Join("、", quest.DescriptionTargets.Select(target => target.Label ?? actorName(target.Target)))
                    : null;
                var delivery = quest.Delivery ?? quest.Giver;
                var description = targets is { Length: > 0 } ? targets
                    : delivery is not null ? actorName(delivery) : null;
                return $"{quest.Description}{(state.Status == QuestStatus.Ready ? "$S" : "$R")}&N" +
                       (description is not null ? $"与{description}进行对话" : quest.Guidance?.Active ?? string.Empty) + "$B";
            }

            if (quest.Goals.Length == 0)
                return $"{quest.Description}{(state.Status == QuestStatus.Ready ? "$S" : "$R")}&N{quest.Guidance?.Active}$B";

            var progress = string.Join(string.Empty, quest.Goals.Select(goal =>
            {
                var count = Math.Min(state.GoalCount(goal.Id), goal.Quantity);
                var complete = count >= goal.Quantity;
                var label = goal.Label ?? quest.Guidance?.CounterLabel ?? (goal.Kind == "talk" ? actorName(goal.Target) : goal.Target);
                return $"{(complete ? "$S" : "$R")}&N{label}: {count}/{goal.Quantity}$B";
            }));
            return quest.Description + progress;
        });

        return string.Join("&N$G-----------------------------------------$B&N", blocks);
    }
}
