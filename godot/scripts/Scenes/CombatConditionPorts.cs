using System;
using Godot;
using Inotia4.Domain.Combat;

namespace Inotia4.Scenes;

public sealed class SceneBuffResourcePort(WorldScene scene) : IBuffResourcePort
{
    public ResourceCapacities CaptureResourceCapacities(string actorId) =>
        new(scene.ResourceCapacityOf(actorId, "maxHealth"), scene.ResourceCapacityOf(actorId, "maxMana"));

    public void OnApply(string actorId, BattleBuffEffect effect, ResourceCapacities entry)
    {
        foreach (var application in effect.OnApply ?? [])
        {
            switch (application)
            {
                case RestoreHealthFromMaxHealthOnApply restore:
                {
                    var amount = CombatOperations.Apply(entry.MaxHealth, restore.Operation) - entry.MaxHealth;
                    if (amount > 0) scene.RestoreHealth(actorId, amount);
                    break;
                }

                case AddManaOnApply add:
                {
                    var amount = Math.Max(0, add.Value);
                    if (amount > 0) scene.RestoreMana(actorId, amount);
                    break;
                }
            }
        }
    }
}

public sealed class SceneInvincibilityPort(WorldScene scene) : IInvincibilityPort
{
    public bool Has(string actorId) => scene.IsInvincible(actorId);

    public void Add(string actorId) => scene.SetInvincible(actorId, true);

    public void Delete(string actorId) => scene.SetInvincible(actorId, false);
}
