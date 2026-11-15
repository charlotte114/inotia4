namespace Inotia4.Domain.Combat;

public static class CombatTempo
{
    public static int AttackCycleFrames(int baseFrames, int attackSpeedRate, int strikeFrame)
    {
        var adjusted = baseFrames +
                       (int)Math.Truncate(baseFrames * (1000 - Math.Max(0, attackSpeedRate)) / 1000.0);
        return Math.Max(adjusted, strikeFrame + 1);
    }
}
