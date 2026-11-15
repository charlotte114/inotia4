namespace Inotia4.Domain.Combat;

public static class ActionHealing
{
    public static int Apply(int amount, int healingPowerRate)
    {
        var rate = Math.Max(0, healingPowerRate);
        return rate > 0 ? (int)Math.Truncate(amount * (double)rate / 1000) : amount;
    }
}
