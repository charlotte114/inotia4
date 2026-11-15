using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inotia4.Domain.Combat;

public sealed class SkillReactionConverter : JsonConverter<SkillReaction>
{
    public override SkillReaction Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var kind = SkillActionJson.Discriminator(root, "kind");
        return kind switch
        {
            "soul-sword" => root.Deserialize<SoulSwordReaction>(options)!,
            "blood-sword" => root.Deserialize<BloodSwordReaction>(options)!,
            "shield-of-rage" => root.Deserialize<ShieldOfRageReaction>(options)!,
            "flares-of-pain" => root.Deserialize<FlaresOfPainReaction>(options)!,
            "wrath-of-darkness" => root.Deserialize<WrathOfDarknessReaction>(options)!,
            "dark-sense" => root.Deserialize<DarkSenseReaction>(options)!,
            "armor-penetration" => root.Deserialize<ArmorPenetrationReaction>(options)!,
            "revenge-incarnate" => root.Deserialize<RevengeIncarnateReaction>(options)!,
            "blood-thirst" => root.Deserialize<BloodThirstReaction>(options)!,
            "flare" => root.Deserialize<FlareReaction>(options)!,
            "clever-fellow" => root.Deserialize<CleverFellowReaction>(options)!,
            "owner-mana-restore" => root.Deserialize<OwnerManaRestoreReaction>(options)!,
            "shield-of-trust" => root.Deserialize<ShieldOfTrustReaction>(options)!,
            "blood-revenge" => root.Deserialize<BloodRevengeReaction>(options)!,
            "bard-of-destruction" => root.Deserialize<BardOfDestructionReaction>(options)!,
            "deep-breath" => root.Deserialize<DeepBreathReaction>(options)!,
            "sniping" => root.Deserialize<SnipingReaction>(options)!,
            "ordination-prayer" => root.Deserialize<OrdinationPrayerReaction>(options)!,
            "magic-insight" => root.Deserialize<MagicInsightReaction>(options)!,
            "disturbance" => root.Deserialize<DisturbanceReaction>(options)!,
            "lightning-of-hell" => root.Deserialize<LightningOfHellReaction>(options)!,
            "god-of-justice" => root.Deserialize<GodOfJusticeReaction>(options)!,
            _ => throw new JsonException($"未知的技能反应：{kind}"),
        };
    }

    public override void Write(Utf8JsonWriter writer, SkillReaction value, JsonSerializerOptions options) =>
        throw new NotSupportedException("内容只读，不回写技能反应");
}
