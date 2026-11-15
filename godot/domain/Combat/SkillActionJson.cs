using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inotia4.Domain.Combat;

/// <summary>
/// 技能动作里的几个联合类型在 JSON 里靠一个判别字段分支（<c>kind</c> 或 <c>effect</c>）。
/// 动作分支在数据里是一组字符串判别值，C# 这边用抽象基类 + 每支一个派生类，
/// 所以需要这几个转换器把判别字段读出来再派发。
///
/// 认不出的判别值直接抛错——原版内容校验也在这一步就把问题暴露出来，
/// 不静默丢掉一个单元或一个事件。
/// </summary>
public static class SkillActionJson
{
    public static readonly JsonSerializerOptions Options = Build();

    public static void Configure(JsonSerializerOptions options)
    {
        options.Converters.Add(new CombatOperationConverter());
        options.Converters.Add(new CombatUnitConverter());
        options.Converters.Add(new SkillEventConverter());
        options.Converters.Add(new BuffReactionConverter());
        options.Converters.Add(new BuffPeriodicEffectConverter());
        options.Converters.Add(new BuffOnApplyConverter());
        options.Converters.Add(new SkillReactionConverter());
    }

    private static JsonSerializerOptions Build()
    {
        var options = new JsonSerializerOptions
        {
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
        };
        Configure(options);
        return options;
    }

    internal static string Discriminator(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new JsonException($"技能动作片段必须是对象：{element.ValueKind}");
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
            throw new JsonException($"技能动作片段缺少 {property} 字段");
        return value.GetString()!;
    }
}

public sealed class CombatOperationConverter : JsonConverter<CombatOperation>
{
    public override CombatOperation Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var operation = document.RootElement.Deserialize<CombatOperationJson>(options)
            ?? throw new JsonException("运算片段为空");
        return operation.ToOperation();
    }

    public override void Write(Utf8JsonWriter writer, CombatOperation value, JsonSerializerOptions options) =>
        throw new NotSupportedException("内容只读，不回写运算");
}

public sealed class CombatUnitConverter : JsonConverter<ICombatUnit>
{
    public override ICombatUnit Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var effect = SkillActionJson.Discriminator(root, "effect");
        return effect switch
        {
            "physical" or "magic" or "heal" or "drain" or "self-damage" =>
                root.Deserialize<NumericUnit>(options) ?? throw new JsonException("数值单元为空"),
            "state" => root.Deserialize<StateCombatUnit>(options)!,
            "buff" => root.Deserialize<BuffCombatUnit>(options)!,
            "summon" => root.Deserialize<SummonCombatUnit>(options)!,
            "set-attribute" => root.Deserialize<SetAttributeUnit>(options)!,
            "clear-attribute" => root.Deserialize<ClearAttributeUnit>(options)!,
            "action" or "taunt" or "visual" => root.Deserialize<SimpleCombatUnit>(options)!,
            _ => throw new JsonException($"未知的结算单元类型：{effect}"),
        };
    }

    public override void Write(Utf8JsonWriter writer, ICombatUnit value, JsonSerializerOptions options) =>
        throw new NotSupportedException("内容只读，不回写结算单元");
}

public sealed class SkillEventConverter : JsonConverter<SkillEvent>
{
    public override SkillEvent Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var kind = SkillActionJson.Discriminator(root, "kind");
        return kind switch
        {
            "feedback" => root.Deserialize<FeedbackEvent>(options)!,
            "capture-targets" => root.Deserialize<CaptureTargetsEvent>(options)!,
            "captured-effect" => root.Deserialize<CapturedEffectEvent>(options)!,
            "summon" => root.Deserialize<SummonEvent>(options)!,
            "cast" => root.Deserialize<CastEvent>(options)!,
            "units" => root.Deserialize<UnitsEvent>(options)!,
            "caster" => root.Deserialize<CasterEvent>(options)!,
            "swing" => root.Deserialize<SwingEvent>(options)!,
            "impact" => root.Deserialize<ImpactEvent>(options)!,
            "spawn" => root.Deserialize<SpawnEvent>(options)!,
            "screen" => root.Deserialize<ScreenEvent>(options)!,
            _ => throw new JsonException($"未知的技能事件类型：{kind}"),
        };
    }

    public override void Write(Utf8JsonWriter writer, SkillEvent value, JsonSerializerOptions options) =>
        throw new NotSupportedException("内容只读，不回写技能事件");
}

public sealed class BuffReactionConverter : JsonConverter<BuffReaction>
{
    public override BuffReaction Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var kind = SkillActionJson.Discriminator(root, "kind");
        return kind switch
        {
            "physical-avoidance" => root.Deserialize<PhysicalAvoidanceReaction>(options)!,
            "physical-success-party" => root.Deserialize<PhysicalSuccessPartyReaction>(options)!,
            "weapon-block-counter" => root.Deserialize<WeaponBlockCounterReaction>(options)!,
            "mana-on-physical-success" => root.Deserialize<ManaOnPhysicalSuccessReaction>(options)!,
            "weapon-block-stun" => root.Deserialize<WeaponBlockStunReaction>(options)!,
            "physical-success-threat" => root.Deserialize<PhysicalSuccessThreatReaction>(options)!,
            _ => throw new JsonException($"未知的增益反应类型：{kind}"),
        };
    }

    public override void Write(Utf8JsonWriter writer, BuffReaction value, JsonSerializerOptions options) =>
        throw new NotSupportedException("内容只读，不回写增益反应");
}

public sealed class BuffPeriodicEffectConverter : JsonConverter<BuffPeriodicEffect>
{
    public override BuffPeriodicEffect Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var kind = SkillActionJson.Discriminator(root, "kind");
        return kind switch
        {
            "fixed-damage" => root.Deserialize<FixedDamageTick>(options)!,
            "magic-curse" => root.Deserialize<MagicCurseTick>(options)!,
            "normal-damage" => root.Deserialize<NormalDamageTick>(options)!,
            "attack-damage" => root.Deserialize<AttackDamageTick>(options)!,
            "healing" => root.Deserialize<HealingTick>(options)!,
            "attribute-damage" => root.Deserialize<AttributeDamageTick>(options)!,
            "visual" => root.Deserialize<VisualTick>(options)!,
            _ => throw new JsonException($"未知的增益周期效果：{kind}"),
        };
    }

    public override void Write(Utf8JsonWriter writer, BuffPeriodicEffect value, JsonSerializerOptions options) =>
        throw new NotSupportedException("内容只读，不回写周期效果");
}

public sealed class BuffOnApplyConverter : JsonConverter<BuffOnApply>
{
    public override BuffOnApply Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var kind = SkillActionJson.Discriminator(root, "kind");
        return kind switch
        {
            "restore-health-from-max-health" => root.Deserialize<RestoreHealthFromMaxHealthOnApply>(options)!,
            "add-mana" => root.Deserialize<AddManaOnApply>(options)!,
            _ => throw new JsonException($"未知的增益挂载动作：{kind}"),
        };
    }

    public override void Write(Utf8JsonWriter writer, BuffOnApply value, JsonSerializerOptions options) =>
        throw new NotSupportedException("内容只读，不回写增益挂载动作");
}
