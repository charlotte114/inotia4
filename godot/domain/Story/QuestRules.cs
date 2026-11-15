using System.Collections.Generic;
using Inotia4.Domain.Character;

namespace Inotia4.Domain.Story;

public static class QuestRules
{
    public static bool Accept(CampaignContent campaign, QuestJournal journal, Bag? inventory, string questId) =>
        journal.Accept(questId, "interaction");

    public static IReadOnlyList<QuestHandover> OnAcceptItems(CampaignContent campaign, string questId) =>
        campaign.FindQuest(questId)?.OnAcceptItems ?? [];
}
