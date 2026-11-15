using System;
using System.Collections.Generic;
using System.Linq;

namespace Inotia4.Domain.Story;

public static class QuestTextLayout
{
    public enum Tone
    {
        Base,
        Title,
        Target,
        Person,
        Muted,
    }

    /// <summary>各语气的显示色；任务窗口的底色是白色，所以基础语气用白色。</summary>
    public static string ColorOf(Tone tone) => tone switch
    {
        Tone.Title => "#bf0000",
        Tone.Target => "#ff0000",
        Tone.Person => "#00b4ff",
        Tone.Muted => "#808080",
        _ => "#ffffff",
    };

    public sealed record Run(string Text, Tone Tone, float X);

    public sealed record Line(IReadOnlyList<Run> Runs, float Width);

    /// <summary>一个片段：<see cref="IsBreak"/> 为真表示断行，否则是带语气的文本。</summary>
    public readonly record struct Part(bool IsBreak, Tone Tone, string Text);

    private static readonly HashSet<char> Opening = new("（([【《「『“‘〈〔｛{".ToCharArray());

    private static readonly HashSet<char> Closing = new("，。！？；：、）)]】》」』”’〉〕｝}…,.!?;:".ToCharArray());

    public static List<Part> Parts(string value)
    {
        var parts = new List<Part>();
        var tone = Tone.Base;
        var locked = false;
        var lockNextColor = false;

        for (var index = 0; index < value.Length;)
        {
            if (index + 1 < value.Length && value[index] == '$' && "ABRSG".Contains(value[index + 1]))
            {
                if (!locked)
                    tone = value[index + 1] switch
                    {
                        'A' => Tone.Title,
                        'R' => Tone.Target,
                        'S' => Tone.Person,
                        'G' => Tone.Muted,
                        _ => Tone.Base,
                    };
                if (lockNextColor)
                {
                    locked = true;
                    lockNextColor = false;
                }

                index += 2;
                continue;
            }

            if (index + 1 < value.Length && value[index] == '&' && "NPC".Contains(value[index + 1]))
            {
                if (value[index + 1] == 'C')
                {
                    if (locked)
                    {
                        locked = false;
                        tone = Tone.Base;
                    }
                    else lockNextColor = true;
                }
                else parts.Add(new Part(true, tone, string.Empty)); // &N / &P：断行

                index += 2;
                continue;
            }

            if (value[index] == '\n')
            {
                parts.Add(new Part(true, tone, string.Empty));
                index += 1;
                continue;
            }

            parts.Add(new Part(false, tone, value[index].ToString()));
            index += 1;
        }

        return parts;
    }

    public static List<Line> Layout(IReadOnlyList<Part> parts, float width, Func<string, float> measure)
    {
        var paragraphs = new List<List<(char Text, Tone Tone)>> { new() };
        foreach (var part in parts)
        {
            if (part.IsBreak) paragraphs.Add([]);
            else foreach (var character in part.Text) paragraphs[^1].Add((character, part.Tone));
        }

        var lines = new List<Line>();
        foreach (var characters in paragraphs)
        {
            if (characters.Count == 0)
            {
                lines.Add(new Line([], 0f));
                continue;
            }

            var start = 0;
            while (start < characters.Count)
            {
                string Text(int end) => new(characters.Skip(start).Take(end - start).Select(c => c.Text).ToArray());
                var end = start;
                while (end < characters.Count &&
                       (end == start || measure(Text(end + 1)) <= width)) end += 1;

                if (end < characters.Count)
                {
                    // 合法断点：不能把起始标点留在行尾、也不能把收尾标点推到下一行行首。
                    var legalEnd = end;
                    while (legalEnd > start && (Opening.Contains(characters[legalEnd - 1].Text) ||
                                                 Closing.Contains(characters[legalEnd].Text)))
                        legalEnd -= 1;
                    if (legalEnd > start) end = legalEnd;
                    else while (end < characters.Count && Closing.Contains(characters[end].Text)) end += 1;
                }

                var runs = new List<Run>();
                for (var index = start; index < end; index += 1)
                {
                    var character = characters[index];
                    if (runs.Count > 0 && runs[^1].Tone == character.Tone)
                        runs[^1] = runs[^1] with { Text = runs[^1].Text + character.Text };
                    else runs.Add(new Run(character.Text.ToString(), character.Tone, measure(Text(index))));
                }

                lines.Add(new Line(runs, measure(Text(end))));
                start = end;
            }
        }

        return lines;
    }
}
