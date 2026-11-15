using System;
using System.Collections.Generic;
using Godot;
using Inotia4.Application;
using Inotia4.Domain.Combat;
using Inotia4.Domain.Content;
using Inotia4.Domain.World;
using Inotia4.Rendering;

namespace Inotia4.Scenes;

public sealed class SceneActionPort(WorldScene scene) : ICombatActionPort
{
    public CombatRuntime Runtime => scene.CombatRuntime;

    public BattleContent Catalog => scene.BattleContent;

    public Combatant? Actor(string id) => scene.FindCombatant(id);

    public IReadOnlyList<Combatant> Candidates() => scene.AllCombatants();

    public Func<double> Random => scene.RandomSource;

    public (int Width, int Height) Viewport()
    {
        var size = scene.GetViewportRect().Size;
        return ((int)size.X, (int)size.Y);
    }

    public void Pose(CombatAction action, int index) => scene.PoseCombatFrame(action, index);

    public void Effect(string name, string variant, Vec2 position, string? placement = null, string? recipientId = null) =>
        scene.SpawnAttackEffect(name, variant, position, placement, recipientId);

    public void Flight(FlightEvent flight) => scene.ApplyFlightEvent(flight);

    public void Screen(float transparency) => scene.FlashScreen(transparency);

    public void Notify(string name, IReadOnlyDictionary<string, object?> data) => scene.NotifyCombat(name, data);

    public bool CanSummonAt(Vec2 position, CollisionBox bounds) => scene.CanSummonAt(position, bounds);

    public Vec2? NearestSummonSpot(Vec2 position, CollisionBox bounds, Vec2? preferred = null) =>
        scene.NearestSummonSpot(position, bounds, preferred);

    public void BeginSummons(string ownerId) => scene.BeginSummons(ownerId);

    public string? Summon(string ownerId, string enemyId, Vec2 position) =>
        scene.SummonCreature(ownerId, enemyId, position);
}
