using System;
using System.Collections.Generic;

namespace Inotia4.Domain.Audio;

/// <summary>
/// 一次打中之后该放哪条音效，五个分支依次判定：
/// 落空 → 该武器的落空声；打中的敌人自带受击声 → 用它；暴击 → 暴击声；
/// 被格挡 → 格挡声；其余按权重从普通命中里抽一条。
///
/// 顺序是规则的一部分（先看敌人受击声，再看暴击），所以这里单独成一处、
/// 由调用方把随机值传进来，方便逐条验。
/// </summary>
public static class AudioCueRules
{
    public static string? PlayerHit(AudioCatalog catalog, string weaponKind, string hitKind, string? targetEnemy,
        Func<double> random)
    {
        var entry = catalog.Combat.Weapons.TryGetValue(weaponKind, out var found)
            ? found
            : catalog.Combat.Weapons.GetValueOrDefault("unarmed");
        if (entry is null) return null;

        if (hitKind == "miss") return entry.Miss;

        if (targetEnemy is not null &&
            catalog.Enemies.TryGetValue(targetEnemy, out var enemy) &&
            enemy.Struck is { Length: > 0 } struck)
            return struck;

        if (hitKind == "critical") return entry.Critical;

        if (hitKind != "hit") return entry.Blocked;

        var total = 0;
        foreach (var cue in entry.Normal) total += cue.Weight;
        if (total <= 0) return null;

        var value = random();
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0 || value >= 1)
            throw new InvalidOperationException("音频随机值无效");

        var roll = value * total;
        foreach (var cue in entry.Normal)
        {
            roll -= cue.Weight;
            if (roll < 0) return cue.Sound;
        }

        return null;
    }

    public static List<string?> PrepareCombatNames(AudioCatalog catalog,
        IEnumerable<string?> skillIds, IEnumerable<string?> actionSounds)
    {
        var names = new List<string?>
        {
            LevelUpEffect,
            catalog.Combat.ShieldBlock,
            catalog.Combat.PeriodicDamage,
        };

        foreach (var weapon in catalog.Combat.Weapons.Values)
        {
            names.Add(weapon.Critical);
            names.Add(weapon.Blocked);
            foreach (var cue in weapon.Normal) names.Add(cue.Sound);
            names.Add(weapon.Miss);
        }

        foreach (var enemy in catalog.Enemies.Values)
        {
            names.Add(enemy.Attack);
            names.Add(enemy.Death);
            names.Add(enemy.Struck);
        }

        foreach (var id in skillIds)
            names.Add(id is not null && catalog.Skills.TryGetValue(id, out var sound) ? sound : null);

        names.AddRange(actionSounds);
        return names;
    }

    public static List<string?> PrepareUiNames(AudioCatalog catalog) =>
    [
        catalog.Ui.Open, catalog.Ui.Move, catalog.Ui.Select, catalog.Ui.QuestStart,
        catalog.Ui.QuestComplete, catalog.Ui.Heal, catalog.Ui.Loot, catalog.Ui.ItemMove,
        catalog.Ui.ChestOpen, catalog.Ui.Enchant, catalog.Ui.Craft,
    ];

    public const string LevelUpEffect = "Common_LevelUp";
}
