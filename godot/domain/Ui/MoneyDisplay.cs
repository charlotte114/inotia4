namespace Inotia4.Domain.Ui;

public readonly record struct MoneyParts(int Gold, int Silver, int Copper);

public static class MoneyDisplay
{
    /// <summary>
    /// 金额以铜币计；货币控件最多显示 9999 金 99 银 99 铜，
    /// 上限夹的是「金」那一档的四位数上限（99 999 999 铜 = 9999 金 99 银 99 铜），
    /// 所以这里先夹 99 999 999 再拆。
    /// </summary>
    public static MoneyParts SplitMoney(int amount)
    {
        var value = Math.Min(99_999_999, Math.Max(0, amount));
        return new MoneyParts(value / 10_000, value / 100 % 100, value % 100);
    }
}
