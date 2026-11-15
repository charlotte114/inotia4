using System;
using Inotia4.Domain.Character;
using Inotia4.Domain.Content;
using Inotia4.Domain.World;

namespace Inotia4.Domain.Combat;

public interface ICombatFollowUpPort
{
    SkillCatalog Catalog { get; }

    CombatRuntime Runtime { get; }

    (string ClassId, SkillLoadout Skills)? Loadout(string id);

    Func<double> Random { get; }

    bool Active();

    Combatant? Actor(string id);

    void CasterStrikes(string casterId, CasterStrikes strikes);

    ITransitionPort Flight { get; }

    void Execute(string casterId, string? targetId, string skillId, int level, SkillAction action, Vec2? origin);
}

public static class CombatFollowUps
{
    public static void Run(string kind, string casterId, string? targetId, ICombatFollowUpPort port)
    {
        var learned = port.Loadout(casterId);
        if (learned is not { } loadout) return;

        var companion = casterId != "player";
        int chance = 0;
        foreach (var reaction in SkillReactions.LearnedReactions(port.Catalog, loadout.ClassId, loadout.Skills, companion))
        {
            if (reaction is LightningOfHellReaction lightning && kind == "lightning-of-hell")
            {
                chance = lightning.ChanceThreshold;
                break;
            }

            if (reaction is GodOfJusticeReaction justice && kind == "god-of-justice")
            {
                chance = justice.ChanceThreshold;
                break;
            }
        }

        if (!(chance > 0) || (int)Math.Floor(port.Random() * 999) + 1 >= chance) return;

        var skillId = kind == "god-of-justice" ? "celestial-lightning" : "lightning-of-hell";
        var level = kind == "god-of-justice" ? loadout.Skills.LevelOf(skillId) : 1;
        if (level < 1) return;

        var definition = port.Catalog.SkillForGrowth(skillId, companion);
        var action = kind == "god-of-justice" ? definition?.Action : definition?.FollowUpAction;
        if (action is null || (kind == "god-of-justice" && (action.CombatUnits?.Count ?? 0) == 0)) return;

        void Settle()
        {
            if (!port.Active()) return;
            var target = port.Actor(targetId ?? "");
            port.Execute(casterId, target?.Id, skillId, level, action, target?.Position);
        }

        if (kind == "god-of-justice")
        {
            if (action.Transition?.CasterStrikes is { } strikes) port.CasterStrikes(casterId, strikes);
            var delay = action.Transition?.SwitchFrame ?? 0;
            if (delay > 0) port.Runtime.Schedule(casterId, delay, Settle);
            else Settle();
        }
        else if (action.Transition is { } transition)
        {
            var caster = port.Actor(casterId);
            if (caster is not null)
                port.Runtime.Transition(casterId, transition, port.Actor(targetId ?? ""), caster.Position,
                    port.Flight, _ => Settle());
        }
        else
        {
            Settle();
        }
    }
}
