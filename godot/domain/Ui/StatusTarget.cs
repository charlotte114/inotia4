using Inotia4.Domain.Content;

namespace Inotia4.Domain.Ui;

public sealed record TargetStatus(string Id, string Name, int Level, int Health, int MaxHealth, bool SceneLevel);

public static class StatusTargetRules
{
    /// <summary>
    /// 名字颜色：按关卡带出等级的一律白色；否则从高到低找第一条
    /// 「目标等级 - 主角等级 &gt;= 阈值」的颜色，找不到就用白色。
    /// 原版最低档的灰色在明暗不一的地图上读不清，这里同样回退白色。
    /// <c>null</c> 表示白色。
    /// </summary>
    public static string? NameColor(StatusHudSkin skin, int playerLevel, TargetStatus target)
    {
        if (target.SceneLevel) return null;
        foreach (var entry in skin.LevelColors)
            if (target.Level - playerLevel >= entry.MinDifference)
                return entry.Color;
        return null;
    }

    public static (float X, float Y) NameAnchor(StatusHudSkin skin) =>
        (skin.Target.Name.X + skin.Target.Name.Width / 2f, skin.Target.Name.Y);
}
