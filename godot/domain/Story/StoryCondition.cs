using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Inotia4.Domain.Story;

public interface IStoryConditionContext
{
    QuestStatus QuestStatus(string id);

    int QuestCompletedCycles(string id);

    string EventStatus(string id);

    string? Choice(string id);

    bool Flag(string id);

    object? World(string key);
}

/// <summary>
/// 剧情条件求值。内容里的每个分支条件、每条任务可见条件都走这里。
/// 遇到不认识的条件**判为不成立并记下来**——宁可走「否则」分支，也不要把剧情带错。
/// </summary>
public static class StoryCondition
{
    public static bool IsEmpty(JsonElement condition) =>
        condition.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null;

    public static bool Evaluate(JsonElement condition, IStoryConditionContext context, Action<string>? unknown = null)
    {
        if (IsEmpty(condition)) return true;

        switch (StoryType.Of(condition))
        {
            case "always":
                return condition.TryGetProperty("value", out var always) &&
                       always.ValueKind == JsonValueKind.True;

            case "all":
                foreach (var child in Children(condition))
                    if (!Evaluate(child, context, unknown)) return false;
                return true;

            case "any":
                foreach (var child in Children(condition))
                    if (Evaluate(child, context, unknown)) return true;
                return false;

            case "not":
                return condition.TryGetProperty("condition", out var inner) &&
                       !Evaluate(inner, context, unknown);

            case "quest":
            {
                var questId = StoryType.Text(condition, "quest");
                var status = context.QuestStatus(questId);
                return StoryType.Text(condition, "status") switch
                {
                    "accepted" => status is QuestStatus.Active or QuestStatus.Ready or QuestStatus.Completed,
                    "settled" => status == QuestStatus.Completed || context.QuestCompletedCycles(questId) > 0,
                    "locked" => status == QuestStatus.Locked,
                    "available" => status == QuestStatus.Available,
                    "active" => status == QuestStatus.Active,
                    "ready" => status == QuestStatus.Ready,
                    "completed" => status == QuestStatus.Completed,
                    var other => Unknown($"quest 状态 {other}", unknown),
                };
            }

            case "event":
                return context.EventStatus(StoryType.Text(condition, "event")) ==
                       StoryType.Text(condition, "status");

            case "choice":
                return context.Choice(StoryType.Text(condition, "choice")) ==
                       StoryType.Text(condition, "value");

            case "flag":
            {
                var wanted = !condition.TryGetProperty("value", out var value) ||
                             value.ValueKind == JsonValueKind.True;
                return context.Flag(StoryType.Text(condition, "flag")) == wanted;
            }

            case "world":
            {
                var key = StoryType.Text(condition, "key");
                var actual = context.World(key);
                if (!condition.TryGetProperty("value", out var wanted)) return actual is null;

                return wanted.ValueKind switch
                {
                    JsonValueKind.String => actual is string text && text == wanted.GetString(),
                    JsonValueKind.Number => actual is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal &&
                                            Convert.ToDouble(actual) == wanted.GetDouble(),
                    JsonValueKind.True => actual is true,
                    JsonValueKind.False => actual is false,
                    _ => false,
                };
            }

            default:
                return Unknown(StoryType.Of(condition), unknown);
        }
    }

    public static void CollectQuestRefs(JsonElement condition, ISet<string> quests)
    {
        if (IsEmpty(condition)) return;

        switch (StoryType.Of(condition))
        {
            case "all":
            case "any":
                foreach (var child in Children(condition)) CollectQuestRefs(child, quests);
                break;
            case "not":
                if (condition.TryGetProperty("condition", out var inner)) CollectQuestRefs(inner, quests);
                break;
            case "quest":
                quests.Add(StoryType.Text(condition, "quest"));
                break;
        }
    }

    private static bool Unknown(string name, Action<string>? unknown)
    {
        unknown?.Invoke(name);
        return false;
    }

    private static IEnumerable<JsonElement> Children(JsonElement condition)
    {
        if (!condition.TryGetProperty("conditions", out var list) || list.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var child in list.EnumerateArray()) yield return child;
    }
}
