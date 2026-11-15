namespace Inotia4.Domain.Combat;

public sealed class PathBudget
{
    private int _remaining;

    public PathBudget(int perFrame)
    {
        PerFrame = perFrame;
        _remaining = perFrame;
    }

    public int PerFrame { get; }

    public int Left => _remaining;

    public void Reset() => _remaining = PerFrame;

    public int Take(int limit)
    {
        var granted = System.Math.Max(0, System.Math.Min(limit, _remaining));
        _remaining -= granted;
        return granted;
    }
}
