using System;
using System.Collections.Generic;

namespace Inotia4.Domain.Combat;

public sealed class StateApplicationRules
{
    public bool ImmuneToAllStates { get; init; }

    public IReadOnlyList<string>? ImmuneStates { get; init; }

    public double DurationScale { get; init; } = 1;

    public IReadOnlyDictionary<string, double>? BuffDurationScaleByState { get; init; }

    public double FrameDurationScale { get; init; } = 1;
}

public sealed class StateApplicationRequest
{
    public required string State { get; init; }

    public required string SourceRelation { get; init; }

    public required string Kind { get; init; }

    public StateApplicationRules? Rules { get; init; }

    public int? SpecificImmunityValue { get; init; }

    public int? ResistanceThreshold { get; init; }

    public int ChanceThreshold { get; init; }

    public bool IsAction => Kind == "action";
}

public sealed record StateApplicationDecision(
    bool Allowed,
    string? Reason,
    double DurationScale,
    double BuffDurationScale,
    double FrameDurationScale)
{
    public static StateApplicationDecision Reject(string reason) => new(false, reason, 1, 1, 1);

    public static StateApplicationDecision Accept(double durationScale, double buffDurationScale,
        double frameDurationScale) => new(true, null, durationScale, buffDurationScale, frameDurationScale);
}

public static class StateApplication
{
    public static StateApplicationDecision Resolve(StateApplicationRequest request, Func<double> random)
    {
        if (request.IsAction)
        {
            if (request.SourceRelation == "none") return StateApplicationDecision.Reject("missing-source");
            if (!ActionStateRules.PassesChanceThreshold(request.ChanceThreshold, random))
                return StateApplicationDecision.Reject("chance");
        }

        var rules = request.Rules;
        if (rules?.ImmuneToAllStates == true ||
            rules?.ImmuneStates is { } immune && Contains(immune, request.State) ||
            (request.SpecificImmunityValue ?? 0) > 0)
            return StateApplicationDecision.Reject("immune");

        var resistance = request.ResistanceThreshold ?? 0;
        if (request.SourceRelation == "hostile" && resistance > 0 &&
            1 + (int)Math.Floor(random() * 999) < resistance)
            return StateApplicationDecision.Reject("resisted");

        return StateApplicationDecision.Accept(
            rules?.DurationScale ?? 1,
            request.Kind == "buff"
                ? rules?.BuffDurationScaleByState?.GetValueOrDefault(request.State) ?? 1
                : 1,
            rules?.FrameDurationScale ?? 1);
    }

    public static StateDurationProfile FrameStateDurationProfile(int raw)
    {
        static int Age(int value) => (int)Math.Truncate(value * 15.0 / 10);
        return new StateDurationProfile
        {
            Normal = new StateDurationBound { LastActiveFrame = Age(raw) },
            TargetShortened = new StateDurationBound { LastActiveFrame = Age(raw >> 1) },
        };
    }

    public static StateDurationBound SelectStateDurationProfile(StateDurationProfile profile,
        StateApplicationDecision decision)
    {
        var branch = decision.BuffDurationScale == 0.5
            ? decision.DurationScale == 0.5 ? "buffAndTargetShortened" : "buffShortened"
            : decision.DurationScale == 0.5 ? "targetShortened" : "normal";

        var timing = branch switch
        {
            "buffAndTargetShortened" => profile.BuffAndTargetShortened,
            "buffShortened" => profile.BuffShortened,
            "targetShortened" => profile.TargetShortened,
            _ => profile.Normal,
        };

        if (timing is null) throw new InvalidOperationException($"状态持续方案缺少 {branch}");
        return new StateDurationBound
        {
            LastActiveFrame = (int)Math.Truncate(timing.LastActiveFrame * decision.FrameDurationScale),
        };
    }

    private static bool Contains(IReadOnlyList<string> values, string state)
    {
        for (var index = 0; index < values.Count; index += 1)
            if (values[index] == state) return true;
        return false;
    }
}
