using System;
using System.Collections.Generic;
using System.Linq;
using Inotia4.Domain.Character;

namespace Inotia4.Domain.Party;

public sealed record HealerRevivalQuote(int Cost, IReadOnlyList<string> MemberIds);

public static class PartyRevival
{
    public static HealerRevivalQuote Quote(Session session)
    {
        var party = session.Party;
        if (party is null) return new HealerRevivalQuote(0, []);

        var fallen = new List<(string Id, int Level)>();
        foreach (var id in party.ActiveIds)
        {
            var health = HealthOf(session, id);
            if (health is null || health.Value > 0) continue;
            fallen.Add((id, LevelOf(session, id)));
        }

        return fallen.Count == 0
            ? new HealerRevivalQuote(0, [])
            : new HealerRevivalQuote(10 + 2 * fallen.Sum(entry => entry.Level),
                [.. fallen.Select(entry => entry.Id)]);
    }

    public static HealerRevivalQuote ReviveAtHealer(Session session)
    {
        var quote = Quote(session);
        var party = session.Party;
        if (party is null || quote.MemberIds.Count == 0)
            throw new InvalidOperationException("没有需要复活的队员");
        if (party.LivingIds.Count == 0)
            throw new InvalidOperationException("全队阵亡时请使用复活圣文或返回检查点");
        if (session.Money < quote.Cost)
            throw new InvalidOperationException("金币不足");

        session.Transaction(() =>
        {
            session.SpendMoney(quote.Cost);
            foreach (var id in quote.MemberIds)
            {
                if (id == "player")
                {
                    var hero = session.Hero;
                    if (hero is null) continue;
                    hero.ClearCombatResourceLimits();
                    hero.Recover();
                }
                else if (session.Party?.Get(id) is { } member)
                {
                    member.ClearCombatResourceLimits();
                    member.Recover();
                }
            }

            party.SwitchControl("player");
        });

        return quote;
    }

    private static int? HealthOf(Session session, string id) =>
        id == "player" ? session.Hero?.Player.Health : session.Party?.Get(id)?.Player.Health;

    private static int LevelOf(Session session, string id) =>
        id == "player" ? session.Hero?.Player.Level ?? 0 : session.Party?.Get(id)?.Player.Level ?? 0;
}
