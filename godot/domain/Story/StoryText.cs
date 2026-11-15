using System.Collections.Generic;
using System.Text;

namespace Inotia4.Domain.Story;

/// <summary>
/// 剧情文本里控制码的转换。剧情对白、任务说明、物品描述共用同一套标记：
/// <list type="bullet">
/// <item><c>$A</c>…<c>$Y</c>：切换字色，色表与原生 <c>X_TEXTCTRL_GetColorFromCode</c> 的映射一致；</item>
/// <item><c>$B</c>：结束当前色段，回到控件基础色（不是换行）；</item>
/// <item><c>&amp;N</c> / <c>&amp;P</c>：换行；<c>&amp;C</c>：锁定下一个色段，直到再次出现该标记。</item>
/// </list>
/// 这里转成 Godot 的 BBCode。裸 <c>[</c> / <c>]</c> 要转义，否则文本里出现方括号会被当成标签。
/// </summary>
public static class StoryText
{
    private static readonly Dictionary<char, string> Colors = new()
    {
        ['A'] = "#bf0000", ['C'] = "#00ff00", ['D'] = "#000000", ['E'] = "#000000",
        ['F'] = "#000000", ['G'] = "#808080", ['H'] = "#000000", ['I'] = "#000000",
        ['J'] = "#000000", ['K'] = "#000000", ['L'] = "#0012c8", ['M'] = "#4eff00",
        ['N'] = "#000000", ['O'] = "#ff6c00", ['P'] = "#ff00c0", ['Q'] = "#bd9e6b",
        ['R'] = "#ff0000", ['S'] = "#00b4ff", ['T'] = "#7878ff", ['U'] = "#000000",
        ['V'] = "#9c00ff", ['W'] = "#ffffff", ['X'] = "#000000", ['Y'] = "#fcff00",
    };

    public static string Bbcode(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;

        var builder = new StringBuilder(raw.Length + 16);
        var open = false;
        var locked = false;
        var lockNextColor = false;

        for (var index = 0; index < raw.Length; index++)
        {
            var character = raw[index];

            if (character == '$' && index + 1 < raw.Length)
            {
                var code = raw[index + 1];

                if (locked && (code == 'B' || Colors.ContainsKey(code)))
                {
                    index++;
                    continue;
                }

                if (code == 'B')
                {
                    if (open) builder.Append("[/color]");
                    open = false;
                    if (lockNextColor) { locked = true; lockNextColor = false; }
                    index++;
                    continue;
                }

                if (Colors.TryGetValue(code, out var color))
                {
                    if (open) builder.Append("[/color]");
                    builder.Append("[color=").Append(color).Append(']');
                    open = true;
                    if (lockNextColor) { locked = true; lockNextColor = false; }
                    index++;
                    continue;
                }
            }

            if (character == '&' && index + 1 < raw.Length && raw[index + 1] is 'N' or 'P')
            {
                builder.Append('\n');
                index++;
                continue;
            }

            if (character == '&' && index + 1 < raw.Length && raw[index + 1] == 'C')
            {
                if (locked)
                {
                    locked = false;
                    if (open) builder.Append("[/color]");
                    open = false;
                }
                else lockNextColor = true;
                index++;
                continue;
            }

            switch (character)
            {
                case '[': builder.Append("[lb]"); break;
                case ']': builder.Append("[rb]"); break;
                default: builder.Append(character); break;
            }
        }

        if (open) builder.Append("[/color]");
        return builder.ToString();
    }

    public static string Plain(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;

        var builder = new StringBuilder(raw.Length);
        for (var index = 0; index < raw.Length; index++)
        {
            var character = raw[index];

            if (character == '$' && index + 1 < raw.Length &&
                (raw[index + 1] == 'B' || Colors.ContainsKey(raw[index + 1])))
            {
                index++;
                continue;
            }

            if (character == '&' && index + 1 < raw.Length && raw[index + 1] is 'N' or 'P' or 'C')
            {
                builder.Append(raw[index + 1] == 'C' ? string.Empty : "\n");
                index++;
                continue;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }
}
