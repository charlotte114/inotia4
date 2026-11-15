namespace Inotia4.Domain;

public static class Economy
{
    public const int MoneyGainMultiplier = 10;

    public static int MoneyGain(int basis) => basis * MoneyGainMultiplier;
}
