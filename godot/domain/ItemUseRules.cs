using System.Collections.Generic;
using Inotia4.Domain.Content;

namespace Inotia4.Domain;

public enum UseHandling
{
    Bag,

    Ui,

    Pending,
}

/// <summary>
/// 物品用途的静态规则：分类标签、由谁结算、哪些消耗品真的能用。
///
/// 这张表是「漏接一种 kind」的守门人：<see cref="Bag.Use"/> 的 <c>switch</c>
/// 末尾有兜底抛错，十六种用途必须在这里全数列出，否则对不上。
/// </summary>
public static class ItemUseRules
{
    public const string RecoverHealth = "recover-health";
    public const string Consume = "consume";
    public const string Enchant = "enchant";
    public const string Socket = "socket";
    public const string Purify = "purify";
    public const string Pack = "pack";
    public const string Box = "box";
    public const string Unseal = "unseal";
    public const string Learn = "learn";
    public const string Regenerate = "regenerate";
    public const string ResetSkills = "reset-skills";
    public const string Recruit = "recruit";
    public const string Merchant = "merchant";
    public const string Revive = "revive";
    public const string Recipe = "recipe";
    public const string Dungeon = "dungeon";

    public static readonly string[] Kinds =
    [
        RecoverHealth, Consume, Enchant, Socket, Purify, Pack, Box, Unseal, Learn, Regenerate,
        ResetSkills, Recruit, Merchant, Revive, Recipe, Dungeon,
    ];

    private static readonly Dictionary<string, string> Labels = new()
    {
        [RecoverHealth] = "使用",
        [Consume] = "使用",
        [Enchant] = "强化",
        [Socket] = "镶嵌",
        [Purify] = "净化",
        [Pack] = "打开礼盒",
        [Box] = "打开宝箱",
        [Unseal] = "解除封印",
        [Learn] = "使用技能书",
        [Regenerate] = "使用",
        [ResetSkills] = "初始化技能",
        [Recruit] = "使用徽章",
        [Merchant] = "召唤神灯商人",
        [Revive] = "复活",
        [Recipe] = "打开合成套装",
        [Dungeon] = "使用符文",
    };

    private static readonly Dictionary<string, UseHandling> Handling = new()
    {
        [RecoverHealth] = UseHandling.Ui,
        [Regenerate] = UseHandling.Ui,
        [Recruit] = UseHandling.Ui,
        [Revive] = UseHandling.Ui,
        // 消耗由背包完成，界面在它返回后接着开商店，所以不算界面层独有。
        [Merchant] = UseHandling.Bag,
        [Consume] = UseHandling.Bag,
        [Dungeon] = UseHandling.Pending,
        [Enchant] = UseHandling.Bag,
        [Socket] = UseHandling.Bag,
        [Purify] = UseHandling.Bag,
        [Pack] = UseHandling.Bag,
        [Box] = UseHandling.Bag,
        [Unseal] = UseHandling.Bag,
        [Recipe] = UseHandling.Bag,
        [Learn] = UseHandling.Bag,
        [ResetSkills] = UseHandling.Bag,
    };

    public const string SkillPointItem = "supreme-elixir";

    public static readonly string[] ImplementedConsumables = [SkillPointItem];

    public const string UseNotImplemented = "此物品的用途尚未实现，暂不能使用";

    /// <summary>回血、招募、复活由队伍界面的专门入口结算，背包的直接使用不该受理。</summary>
    public const string UseInterfaceOnly = "此物品要在队伍或商店界面中使用";

    public static string LabelOf(string kind) => Labels.TryGetValue(kind, out var label) ? label : kind;

    public static UseHandling HandlingOf(string kind) =>
        Handling.TryGetValue(kind, out var handling) ? handling : UseHandling.Pending;

    public static string? UnimplementedItemUse(string itemId, ItemDefinition definition)
    {
        var use = definition.Use;
        if (use is null) return null;
        if (HandlingOf(use.Kind) == UseHandling.Pending) return UseNotImplemented;
        if (use.Kind == Consume && System.Array.IndexOf(ImplementedConsumables, itemId) < 0)
            return UseNotImplemented;
        return null;
    }
}
