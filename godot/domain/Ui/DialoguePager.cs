using System;
using System.Collections.Generic;
using System.Text;

namespace Inotia4.Domain.Ui;

/// <summary>
/// 一页对白：要显示的那段**原文**，以及这一页在原文里结束的位置。
/// <para>
/// 存原文而不是剥掉控制码的文本——逐字显示与翻页都按原文下标记账，页首的控制码
/// 决定这一页首段的字色，必须跟着这一页走。
/// </para>
/// </summary>
/// <param name="Text">本页原文子串；含页首控制码，不含页尾那个由分页承担的换行。</param>
/// <param name="End">本页在整句原文里的结束下标；下一页从这里开始。</param>
public sealed record DialoguePage(string Text, int End)
{
    public int TypedLength { get; init; }
}

public static class DialoguePager
{
    public static (string Text, List<int> Offsets) VisibleIndex(string? value)
    {
        var text = new StringBuilder();
        var offsets = new List<int>();
        var raw = 0;
        var source = value ?? string.Empty;

        for (var index = 0; index < source.Length; index++)
        {
            if (IsCodeAt(source, index, out var length))
            {
                if (IsLineBreakAt(source, index, out var breakLength))
                {
                    text.Append('\n');
                    offsets.Add(index);
                    index += breakLength - 1;
                    raw = index + 1;
                    continue;
                }

                index += length - 1;
                raw = index + 1;
                continue;
            }

            text.Append(source[index]);
            offsets.Add(index);
            raw = index + 1;
        }

        offsets.Add(raw);
        return (text.ToString(), offsets);
    }

    /// <summary>
    /// 逐字显示用的文本与下标：显式换行**不计**进度。
    /// <para>
    /// 换行是排版断行，从画第一笔起就摆好了位置，不该像打字一样敲出来，
    /// 也不该让打字机在换行处停一拍。
    /// </para>
    /// </summary>
    public static (string Text, List<int> Offsets) TypedIndex(string? value)
    {
        var text = new StringBuilder();
        var offsets = new List<int>();
        var source = value ?? string.Empty;

        for (var index = 0; index < source.Length; index++)
        {
            if (IsLineBreakAt(source, index, out var breakLength))
            {
                index += breakLength - 1;
                continue;
            }

            if (IsCodeAt(source, index, out var length))
            {
                index += length - 1;
                continue;
            }

            text.Append(source[index]);
            offsets.Add(index);
        }

        offsets.Add(source.Length);
        return (text.ToString(), offsets);
    }

    public static int TypedLength(string? value) => TypedIndex(value).Text.Length;

    public static List<string> WrapText(string text, float width, Func<string, float> measure)
    {
        var lines = new List<string>();
        var line = new StringBuilder();

        foreach (var character in text)
        {
            if (character == '\n')
            {
                lines.Add(line.ToString());
                line.Clear();
                continue;
            }

            line.Append(character);
            // 只有一个字符时不再比较：放不下也得先占一行，否则会空转。
            if (line.Length > 1 && measure(line.ToString()) > width)
            {
                line.Length -= 1;
                lines.Add(line.ToString());
                line.Clear();
                line.Append(character);
            }
        }

        lines.Add(line.ToString());
        return lines;
    }

    public static List<DialoguePage> Pages(string? value, float width, int lines,
        Func<string, float> measure)
    {
        var source = value ?? string.Empty;
        var (text, offsets) = VisibleIndex(source);
        var pages = new List<DialoguePage>();
        var pageLines = Math.Max(1, lines);

        var start = 0;   // 可见文本下标
        var raw = 0;     // 本页在原文里的起点

        while (start < text.Length || pages.Count == 0)
        {
            while (start < text.Length && text[start] == '\n')
            {
                raw = offsets[start] + (offsets[start] < source.Length && source[offsets[start]] == '\n' ? 1 : 2);
                start++;
            }

            var end = start;
            while (end < text.Length)
            {
                var next = end + 1;
                if (next > start && WrapText(text[start..next], width, measure).Count > pageLines) break;
                end = next;
            }

            // 单个字符也放不下时至少推进一格，否则会原地打转。
            if (end == start) end = Math.Min(text.Length, start + 1);

            // 页尾落在换行上时收在换行之前：那个断行正是分页本身，不该再占一行。
            var last = end > start && text[end - 1] == '\n' ? end - 1 : end;
            var pageText = source[raw..offsets[last]];
            pages.Add(new DialoguePage(pageText, offsets[end]) { TypedLength = TypedLength(pageText) });

            raw = offsets[end];
            start = end;
        }

        return pages;
    }

    public static int[] TypedToVisibleMap(string? page)
    {
        var (visibleText, visibleOffsets) = VisibleIndex(page);
        var (typedText, typedOffsets) = TypedIndex(page);
        var map = new int[typedText.Length + 1];

        var cursor = 0;
        for (var count = 0; count <= typedText.Length; count++)
        {
            var raw = typedOffsets[count];
            while (cursor < visibleText.Length && visibleOffsets[cursor] < raw) cursor++;
            map[count] = cursor;
        }

        return map;
    }

    private static bool IsCodeAt(string source, int index, out int length)
    {
        length = 0;
        if (index + 1 >= source.Length) return false;

        var head = source[index];
        var next = source[index + 1];

        if (head == '$' && (next == 'B' || IsColorCode(next)))
        {
            length = 2;
            return true;
        }

        if (head == '&' && next is 'N' or 'P' or 'C')
        {
            length = 2;
            return true;
        }

        return false;
    }

    private static bool IsLineBreakAt(string source, int index, out int length)
    {
        length = 0;
        if (source[index] == '\n')
        {
            length = 1;
            return true;
        }

        if (source[index] == '&' && index + 1 < source.Length && source[index + 1] is 'N' or 'P')
        {
            length = 2;
            return true;
        }

        return false;
    }

    private static bool IsColorCode(char code) => code is >= 'A' and <= 'Y';
}
