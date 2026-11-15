namespace Inotia4.Domain.Combat;

public static class StableSeed
{
    private const int HashMultiplier = 31;

    public static uint Of(string id)
    {
        uint hash = 0;
        foreach (var character in id) hash = unchecked(hash * HashMultiplier + character);
        return hash;
    }
}
