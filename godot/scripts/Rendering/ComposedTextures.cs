using System;
using System.Collections.Generic;
using Godot;

namespace Inotia4.Rendering;

/// <summary>
/// 合成出来的外观贴图登记表。
///
/// 合成外观的目前不落文件：片段图层只带 key，像素在内存里现合；
/// Godot 这边合成出来的是内存贴图，所以按 key 记在这里，<see cref="CharacterVisual"/> 取图层时
/// 先看文件、为空就看这张表。
/// </summary>
public static class ComposedTextures
{
    private static readonly Dictionary<string, Texture2D> Registry = new();

    private static readonly Dictionary<string, Func<Texture2D?>> Pending = new();

    private static readonly Dictionary<string, Texture2D> Retired = new();

    public static void Register(string key, Texture2D texture) => Registry[key] = texture;

    /// <summary>
    /// 登记一张「迟些再合」的贴图：只记下 key 与合成函数，像素等第一次 <see cref="Find"/> 时才算。
    ///
    /// 一套外观五十来段，进图那一刻只用得到待机与行走；进攻与技能的片段留到真播到时再合，
    /// 一段 3–7 ms，看不出来。合成必须在主线程（走 <c>ImageTexture.CreateFromImage</c>），
    /// 而 <see cref="Find"/> 只在画面换动作时被调用，天然是主线程。
    /// </summary>
    public static void RegisterLazy(string key, Func<Texture2D?> render) => Pending[key] = render;

    public static bool Has(string key) => Registry.ContainsKey(key) || Pending.ContainsKey(key);

    public static Texture2D? Find(string key)
    {
        if (Registry.TryGetValue(key, out var texture)) return texture;
        if (!Pending.Remove(key, out var render)) return null;

        var built = render();
        if (built is not null) Registry[key] = built;
        return built;
    }

    public static void Retire(string key)
    {
        Pending.Remove(key);
        if (!Registry.Remove(key, out var texture)) return;
        Retired[key] = texture;
    }

    public static void Destroy(string key)
    {
        if (!Retired.Remove(key, out var texture)) return;
        texture.Dispose();
    }

    public static void Remove(string key)
    {
        Pending.Remove(key);
        if (Registry.Remove(key, out var texture)) texture.Dispose();
        if (Retired.Remove(key, out var retired)) retired.Dispose();
    }

    public static int Count => Registry.Count;
}
