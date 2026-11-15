namespace Inotia4.Domain.Combat;

public readonly record struct ManaDrainResult(int TargetMana, int CasterMana, int Deducted);

public static class ManaDrain
{
    public static ManaDrainResult Drain(int amount, int targetMana, int casterMana, int casterMaxMana)
    {
        var gain = Math.Max(0, (int)Math.Truncate((double)amount));
        return new ManaDrainResult(
            Math.Max(0, targetMana - gain),
            Math.Min(casterMaxMana, casterMana + gain),
            Math.Min(targetMana, gain));
    }
}
