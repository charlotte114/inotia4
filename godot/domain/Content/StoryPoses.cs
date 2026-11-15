namespace Inotia4.Domain.Content;

public static class StoryPoses
{
    public static readonly string[] Names =
    [
        "stand", "fall-down", "heavy-blow", "recovery", "lie-down", "lie-side",
        "sway", "cry", "look-around", "pick-up", "give", "demon-lord-attack",
        "demon-lord-skill", "skill-2",
    ];

    /// <summary>
    /// 这个动作名是不是某个姿态的定向片段（形如 <c>cry-west</c>）。
    /// 判据是「以某个姿态名加连字符开头」，所以光有姿态名、没有朝向的不算。
    /// </summary>
    public static bool IsPose(string clipName)
    {
        foreach (var pose in Names)
            if (clipName.StartsWith(pose + "-", StringComparison.Ordinal)) return true;
        return false;
    }
}
