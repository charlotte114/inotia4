using System.Collections.Generic;

namespace Inotia4.Domain.Content;

/// <summary>
/// 开局装配里与职业有关的那一段。
///
/// 职业只提供战斗动作（idle / walk / attack 四向）；主角定义里的剧情姿态
/// （<c>lie-side</c>、<c>cry</c>、<c>look-around</c> 等）必须保留，否则剧情演出会报
/// 「角色动作缺少资源」。所以合并方向是「主角为底，职业片段覆盖同名片段」。
/// </summary>
public static class StoryClassAppearance
{
    public static ActorDefinition Merge(ActorDefinition hero, StoryClassInfo? selected)
    {
        if (selected is null || selected.PlayerActor.Clips.Count == 0) return hero;

        var clips = new Dictionary<string, ActorClip>(hero.Clips);
        foreach (var (name, clip) in selected.PlayerActor.Clips) clips[name] = clip;

        return new ActorDefinition
        {
            Id = hero.Id,
            Name = hero.Name,
            Portrait = hero.Portrait,
            Description = hero.Description,
            Service = hero.Service,
            Chest = hero.Chest,
            Collision = hero.Collision,
            EffectAnchor = selected.PlayerActor.EffectAnchor ?? hero.EffectAnchor,
            HeadAnchor = hero.HeadAnchor,
            Clips = clips,
        };
    }
}
