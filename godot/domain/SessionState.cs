using System;
using System.Collections.Generic;
using Inotia4.Domain.Character;
using Inotia4.Domain.Party;
using Inotia4.Domain.Story;
using Inotia4.Domain.World;

namespace Inotia4.Domain;

public sealed class SessionState
{
    /// <summary>会话的职业：谁在操控就记谁——有过角色卡就取角色卡的，否则取主角的。</summary>
    public string? ClassId { get; set; }

    public QuestBookState QuestBook { get; set; } = new();

    public int Money { get; set; }

    public long GameTimeMs { get; set; }

    public BagState Bag { get; set; } = new();

    public BattleCharacterState? Hero { get; set; }

    public SkillConfigurationState? Skills { get; set; }

    public PartyState? Party { get; set; }

    public ShopVisitsState Shops { get; set; } = new();

    public GroundLootStore.GroundLootState? GroundLoot { get; set; }

    public LootPity.LootPityState? LootPity { get; set; }

    public ChestVisitsState? Chests { get; set; }

    public SpringVisitsState? Springs { get; set; }

    public PropVisitsState? Props { get; set; }

    public RecoveryItemsState? Recovery { get; set; }

    public QuestItemGenerationState? QuestItemGeneration { get; set; }

    public Combat.CombatClockState? CombatClock { get; set; }

    public Combat.CombatCooldownState? Cooldowns { get; set; }

    public Combat.FrenzyAnimationState? FrenzyAnimations { get; set; }

    public Combat.CombatResourceRecoveryState? ResourceRecovery { get; set; }

    public StoryWorldSnapshot? StoryWorld { get; set; }

    public StoryEventState? StoryEvents { get; set; }

    public StoryCursorState? StoryCursor { get; set; }

    public Dictionary<string, EnemyState> Enemies { get; set; } = new();

    public List<QuestReceiptState> Receipts { get; set; } = [];

    public Story.StoryRunSnapshot? Runtime { get; set; }
}

public sealed class QuestReceiptState
{
    public string Quest { get; set; } = string.Empty;

    public int Cycle { get; set; }

    public int Experience { get; set; }

    public int Money { get; set; }

    public List<QuestReceiptItemState> Items { get; set; } = [];
}

public sealed class QuestReceiptItemState
{
    public string Item { get; set; } = string.Empty;

    public int Quantity { get; set; }
}
