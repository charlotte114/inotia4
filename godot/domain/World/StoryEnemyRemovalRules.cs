namespace Inotia4.Domain.World;

public static class StoryEnemyRemovalRules
{
    public readonly record struct Command(string? Map, string EnemyId, bool All);

    public readonly record struct Placed(string Key, string EnemyId);

    /// <summary>
    /// 按命令顺序挑出要移除的场上键。三条口径：
    /// <list type="number">
    /// <item>命令指定了地图就只在那张图上生效；</item>
    /// <item>同一种怪里，<c>all</c> 为真时全取，否则只取第一只；</item>
    /// <item>前面命令已经移除过的不再重复计数。</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<string> Select(string map, IReadOnlyList<Command> commands,
        IReadOnlyList<Placed> enemies)
    {
        var removed = new HashSet<string>();
        var picked = new List<string>();

        foreach (var command in commands)
        {
            if (command.Map is { Length: > 0 } target && target != map) continue;

            var matches = enemies
                .Where(enemy => !removed.Contains(enemy.Key) && enemy.EnemyId == command.EnemyId)
                .ToList();

            foreach (var enemy in command.All ? matches : matches.Take(1).ToList())
            {
                picked.Add(enemy.Key);
                removed.Add(enemy.Key);
            }
        }

        return picked;
    }
}
