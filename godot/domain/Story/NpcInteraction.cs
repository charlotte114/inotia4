using System.Collections.Generic;
using System.Linq;
using Inotia4.Domain.Character;

namespace Inotia4.Domain.Story;

public static class NpcInteraction
{
    public static bool MatchesActor(QuestDefinition quest, string actor, string? characterId, bool delivery)
    {
        var expectedActor = delivery ? quest.Delivery ?? quest.Giver : quest.Giver;
        var expectedCharacter = delivery ? quest.DeliveryCharacterId ?? quest.GiverCharacterId : quest.GiverCharacterId;

        return characterId is not null && expectedCharacter is not null
            ? characterId == expectedCharacter
            : actor == expectedActor;
    }

    public static QuestDefinition? QuestSlot(
        CampaignContent campaign, QuestJournal journal, string actor, string? characterId)
    {
        journal.Refresh();

        // 1. 待交付独占：按**运行池**顺序取第一条（接取顺序，不是内容顺序）。
        //    隐藏任务同样在运行池里，这一段不因为日志不可见而跳过。
        foreach (var id in journal.RunningQuestIds)
        {
            if (journal.StatusOf(id) != QuestStatus.Ready) continue;
            var quest = campaign.FindQuest(id);
            if (quest is not null && MatchesActor(quest, actor, characterId, delivery: true)) return quest;
        }

        var available = campaign.Quests
            .Where(quest => journal.StatusOf(quest.Id) == QuestStatus.Available &&
                            journal.Visible(quest.Id) &&
                            MatchesActor(quest, actor, characterId, delivery: false))
            .ToList();

        var main = available.FirstOrDefault(quest => campaign.KindOfQuest(quest.Id) == "main");
        if (main is not null) return main;
        if (available.Count > 0) return available[0];

        foreach (var id in journal.RunningQuestIds)
        {
            if (journal.StatusOf(id) != QuestStatus.Active) continue;
            var quest = campaign.FindQuest(id);
            if (quest is not null && MatchesActor(quest, actor, characterId, delivery: true)) return quest;
        }

        return null;
    }

    public static InteractionPlan Interact(
        CampaignContent campaign, QuestJournal journal, string actor, string? characterId,
        string? hint, string? description)
    {
        var quest = QuestSlot(campaign, journal, actor, characterId);
        if (quest is null) return InteractionPlan.Idle(hint is { Length: > 0 } ? hint : description ?? "一路小心。");

        return journal.StatusOf(quest.Id) switch
        {
            QuestStatus.Available => quest.OfferScript is { Length: > 0 }
                ? InteractionPlan.PlayScript(quest.OfferScript, quest.Id)
                : InteractionPlan.Accept(quest.Id, quest.Title),
            QuestStatus.Ready => quest.CompleteScript is { Length: > 0 }
                ? InteractionPlan.PlayScript(quest.CompleteScript, quest.Id)
                : InteractionPlan.Complete(quest.Id, quest.Title),
            _ => InteractionPlan.Progress(quest.Id, quest.Title, quest.ProgressText),
        };
    }

    public static RewardReceipt? Settle(CampaignContent campaign, QuestJournal journal, string questId)
    {
        var quest = campaign.FindQuest(questId);
        if (quest is null) return null;

        var held = journal.Bag;
        foreach (var handover in quest.HandoverItems)
        {
            var amount = handover.Removal == "all-matching" ? held?.Count(handover.Item) ?? 0 : handover.Quantity;
            if ((held?.Count(handover.Item) ?? 0) < amount) return null;
        }

        if (journal.StatusOf(questId) != QuestStatus.Ready) return null;

        var settlement = journal.Settle(questId);
        return settlement?.Reward;
    }
}

public sealed class InteractionPlan
{
    public enum PlanKind
    {
        Idle,

        PlayScript,

        Accept,

        Complete,

        Progress,
    }

    private InteractionPlan(PlanKind kind, string questId, string title, string? scriptId, string? text)
    {
        Kind = kind;
        QuestId = questId;
        Title = title;
        ScriptId = scriptId;
        Text = text;
    }

    public PlanKind Kind { get; }

    public string QuestId { get; }

    public string Title { get; }

    public string? ScriptId { get; }

    public string? Text { get; }

    public static InteractionPlan Idle(string text) => new(PlanKind.Idle, string.Empty, string.Empty, null, text);

    public static InteractionPlan PlayScript(string scriptId, string questId) =>
        new(PlanKind.PlayScript, questId, string.Empty, scriptId, null);

    public static InteractionPlan Accept(string questId, string title) =>
        new(PlanKind.Accept, questId, title, null, null);

    public static InteractionPlan Complete(string questId, string title) =>
        new(PlanKind.Complete, questId, title, null, null);

    public static InteractionPlan Progress(string questId, string title, string? text) =>
        new(PlanKind.Progress, questId, title, null, text);
}
