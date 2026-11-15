using System;
using System.Collections.Generic;

namespace Inotia4.Domain;

public enum ItemNotice
{
    EnchantDouble,
    EnchantRule,
    EnchantDurability,
    EnchantLevel,
    EnchantFirst,
    EnchantChaosDurability,
    EnchantMax,
    SocketIncompatible,
    SocketFull,
}

public static class ItemNoticeText
{
    private static readonly Dictionary<ItemNotice, string> Texts = new()
    {
        [ItemNotice.EnchantDouble] = "强化+2成功！",
        [ItemNotice.EnchantRule] = "与一般的强化卷轴不同。",
        [ItemNotice.EnchantDurability] = "耐久度不足。",
        [ItemNotice.EnchantLevel] = "等级不符。",
        [ItemNotice.EnchantFirst] = "至少需要强化+1以上。",
        [ItemNotice.EnchantChaosDurability] = "混沌强化卷轴只有在耐久度为0的状态下才可以使用。",
        [ItemNotice.EnchantMax] = "强化已达到上限。",
        [ItemNotice.SocketIncompatible] = "该道具不适合。",
        [ItemNotice.SocketFull] = "宝石孔不足。",
    };

    public static string Of(ItemNotice notice) => Texts[notice];
}

public sealed class ItemApplicationException : Exception
{
    public ItemApplicationException(ItemNotice notice) : base(ItemNoticeText.Of(notice))
    {
        Notice = notice;
    }

    public ItemNotice Notice { get; }
}
