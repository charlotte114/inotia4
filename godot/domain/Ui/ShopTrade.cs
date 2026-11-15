using Inotia4.Domain.Character;
using Inotia4.Domain.Content;

namespace Inotia4.Domain.Ui;

public enum TradeKind
{
    Buy,
    Sell,
}

/// <summary>
/// 交易停在哪一步：先弹物品说明，再点「购买／出售」进数量或确认；
/// 不可出售的物品直接落到提示框。
/// </summary>
public enum TradeStep
{
    Description,
    Quantity,
    Confirm,
    Notice,
}

public sealed class ShopTrade
{
    private readonly Storefront _store;
    private readonly Session _session;

    public ShopTrade(Storefront store, Session session)
    {
        _store = store;
        _session = session;
    }

    public TradeKind Kind { get; private set; }

    public string? Id { get; private set; }

    public TradeStep Step { get; private set; }

    public int Quantity { get; private set; } = 1;

    public string? Error { get; private set; }

    public bool Active => Id is not null;

    /// <summary>
    /// 说明面板画在主画布上：它是主界面的一部分，只有数量框与确认框才占弹层画布。
    /// 所以停在说明步时不算「弹层打开」，主画布照常收输入。
    /// </summary>
    public bool ModalOpen => Active && Step != TradeStep.Description;

    public ShopOffer? Offer =>
        Kind == TradeKind.Buy && Id is not null
            ? _store.Offers.FirstOrDefault(offer => offer.Id == Id && !offer.Sold)
            : null;

    public ItemInstance? Entry =>
        Kind == TradeKind.Sell && Id is not null && _session.Bag is { } bag ? bag.Get(Id) : null;

    public ItemInstance? Instance => Kind == TradeKind.Buy ? Offer?.Instance : Entry;

    public bool Concealed => Kind == TradeKind.Buy && Offer is { Concealed: true };

    /// <summary>
    /// 点货架格买、点背包格卖都先弹物品说明面板；只有不可出售才跳过它直接提示。
    /// </summary>
    public bool Select(TradeKind kind, string id)
    {
        var entry = kind == TradeKind.Sell && _session.Bag is { } bag ? bag.Get(id) : null;
        if (kind == TradeKind.Sell && entry is null) return false;

        Kind = kind;
        Id = id;
        Quantity = 1;
        Error = null;
        Step = kind == TradeKind.Sell && _store.SellPrice(entry!) == 0
            ? TradeStep.Notice
            : TradeStep.Description;
        return true;
    }

    public void BeginTrade()
    {
        if (!Active) return;
        Step = Maximum() > 1 ? TradeStep.Quantity : TradeStep.Confirm;
    }

    public void Cancel()
    {
        Id = null;
        Error = null;
        Step = TradeStep.Description;
    }

    /// <summary>
    /// 普通堆叠商品可一次买 1–99；限量随机商品与不可堆叠商品一次一件；出售上限是持有数量。
    /// </summary>
    public int Maximum()
    {
        if (!Active) return 1;

        if (Kind == TradeKind.Sell) return Entry?.Quantity ?? 1;

        var offer = Offer;
        var item = offer is null ? null : _store.Item(offer.Instance.ItemId);
        return offer is not null && item is not null && !offer.Limited && item.Stackable ? 99 : 1;
    }

    public void Adjust(int delta)
    {
        if (Step != TradeStep.Quantity) return;
        Quantity = System.Math.Clamp(Quantity + delta, 1, Maximum());
    }

    public void ToConfirm()
    {
        if (Step == TradeStep.Quantity) Step = TradeStep.Confirm;
    }

    public int Total()
    {
        if (!Active) return 0;
        var price = Kind == TradeKind.Buy
            ? Id is null ? 0 : _store.BuyPrice(Id)
            : Entry is { } entry ? _store.SellPrice(entry) : 0;
        return price * Quantity;
    }

    /// <summary>确认框的「确定」是否置灰：买东西钱不够时不能按。</summary>
    public bool Insufficient() => Kind == TradeKind.Buy && _session.Money < Total();

    public string? Confirm()
    {
        if (!Active || Id is null) return null;

        var kind = Kind;
        var id = Id;
        var quantity = Quantity;

        try
        {
            if (kind == TradeKind.Buy) _store.Buy(id, quantity);
            else _store.Sell(id, quantity);
        }
        catch (System.Exception error)
        {
            Error = error.Message;
            return Error;
        }

        Id = null;
        Error = null;
        Step = TradeStep.Description;
        return null;
    }

    /// <summary>
    /// 面板调试入口：直接摆到某个交易步骤，跳过「点货架格买、点背包格卖」的点击链路。
    /// 只给调试场景用。可用堆叠商品的 <c>quantity</c> 步骤会自动挑一件能买多件的商品；
    /// <c>notice</c> 则挑第一件不可出售的物品。
    /// </summary>
    public bool DebugTrade(TradeStep step, TradeKind kind = TradeKind.Buy)
    {
        if (kind == TradeKind.Buy)
        {
            var offers = _store.Offers.Where(offer => !offer.Sold).ToList();
            var stackable = offers.FirstOrDefault(offer =>
            {
                var item = _store.Item(offer.Instance.ItemId);
                return item is not null && !offer.Limited && item.Stackable;
            });
            var offer = step == TradeStep.Quantity ? stackable ?? offers.FirstOrDefault() : offers.FirstOrDefault();
            if (offer is null) return false;
            Select(TradeKind.Buy, offer.Id);
        }
        else
        {
            var entries = _session.Bag?.Entries ?? [];
            var entry = step == TradeStep.Notice
                ? entries.FirstOrDefault(value => _store.SellPrice(value) == 0)
                : entries.FirstOrDefault(value => _store.SellPrice(value) > 0);
            if (entry is null) return false;
            Select(TradeKind.Sell, entry.Id);
        }

        if (step is TradeStep.Quantity or TradeStep.Confirm)
        {
            Step = step;
            if (step == TradeStep.Quantity) Quantity = System.Math.Clamp(2, 1, Maximum());
        }

        return true;
    }
}
