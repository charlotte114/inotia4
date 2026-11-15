using System;
using System.Collections.Generic;
using Godot;
using Inotia4.Domain.Character;
using Inotia4.Domain.Combat;
using Inotia4.Domain.Content;
using Inotia4.Domain.Party;

namespace Inotia4.Ui;

public sealed record PartyMemberView(
    string Id,
    bool IsPlayer,
    int Level,
    CharacterPanel Panel,
    PlayerVitals Player,
    string ClassId,
    string ClassName,
    string ActorName,

    string Source,

    string? PortraitImage,

    string? PartyIcon,

    IAllocationOwner? Attributes,

    SkillConfiguration? Skills,

    CompanionMember? Companion,

    int StatPoints)
{
    public string Owner => Id;
}

public static class PartyMembers
{
    public static IReadOnlyList<string> ActiveIds(PartyRoster? party) =>
        party is null ? ["player"] : party.ActiveIds;

    public static PartyMemberView? Of(PartyRoster? party, BattleCharacter? hero,
        string playerActorId, WorldIndex world, StoryClassCatalog classes,
        MenuPanelsCatalog panels, string id)
    {
        if (id == "player")
        {
            if (hero is null) return null;
            var startActor = world.Actors.GetValueOrDefault(playerActorId);
            var portrait = PortraitInfo(panels, startActor?.Portrait ?? "kiyan");
            return new PartyMemberView(
                "player", true, hero.Level, hero.Panel, hero.Player,
                hero.ClassId, classes.NameOf(hero.ClassId),
                startActor?.Name ?? id,
                "player",
                portrait?.Image, portrait?.PartyIcon,
                hero, null, null, hero.StatPoints);
        }

        var member = party?.Get(id);
        if (member is null) return null;
        var actor = world.Actors.GetValueOrDefault(member.Definition.Actor);
        var info = PortraitInfo(panels, actor?.Portrait ?? "kiyan");
        var classIcon = panels.Asset($"party-class-{member.ClassId}");
        var isMercenary = member.Source == "mercenary";
        return new PartyMemberView(
            id, false, member.Level, member.Panel, member.Player,
            member.ClassId, classes.NameOf(member.ClassId),
            member.Definition.Name,
            member.Source,
            isMercenary ? classIcon : info?.Image,
            isMercenary ? classIcon : info?.PartyIcon,
            null, member.Skills, member, 0);
    }

    public static string? IconOf(PartyRoster? party, string playerActorId, WorldIndex world,
        MenuPanelsCatalog panels, string id)
    {
        if (id == "player")
        {
            var actor = world.Actors.GetValueOrDefault(playerActorId);
            return PortraitInfo(panels, actor?.Portrait ?? "kiyan")?.PartyIcon;
        }

        var member = party?.Get(id);
        if (member is null) return null;
        if (member.Source == "mercenary") return panels.Asset($"party-class-{member.ClassId}");
        var companionActor = world.Actors.GetValueOrDefault(member.Definition.Actor);
        return PortraitInfo(panels, companionActor?.Portrait ?? "kiyan")?.PartyIcon;
    }

    private static CharacterPortraitInfo? PortraitInfo(MenuPanelsCatalog panels, string key) =>
        panels.Character.Portraits.GetValueOrDefault(key) ??
        panels.Character.Portraits.GetValueOrDefault("kiyan");
}

public static class MenuMemberColumn
{
    public static void Draw(Control content, Func<string, Texture2D?> texture,
        Func<string, string?> asset, IReadOnlyList<string> ids, Func<string, string?> iconOf,
        string selectedId, Action<string>? select = null)
    {
        for (var index = 0; index < 3; index += 1)
        {
            var id = index < ids.Count ? ids[index] : null;
            var top = 49f + index * 113f;

            PanelGfx.Image(texture, asset("character-party-frame"), 8f, top, 104f, 114f, content);
            if (id is null) continue;

            var memberId = id;
            var button = PanelGfx.Hit(8f, top, 104f, 113f,
                () => { if (memberId != selectedId) select?.Invoke(memberId); }, content);
            PanelGfx.Image(texture, iconOf(memberId), 9f, 9f, 86f, 96f, button);
        }
    }
}
