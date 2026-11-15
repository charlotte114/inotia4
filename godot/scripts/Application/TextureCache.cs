using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace Inotia4.Application;

public sealed class TextureCache
{
    private sealed class Entry
    {
        public string Name = string.Empty;
        public Texture2D? Texture;
        public Image? Image;
        public bool KeepImage;
        public int References;

        public long Bytes;
    }

    private readonly Dictionary<string, Entry> _entries = new();

    public int Count => _entries.Count;

    public long Bytes
    {
        get
        {
            var total = 0L;
            foreach (var entry in _entries.Values) total += entry.Bytes;
            return total;
        }
    }

    public long PeakBytes { get; private set; }

    public IReadOnlyList<(string Name, int Width, int Height, long Bytes)> Largest(int take)
    {
        var list = new List<(string Name, int Width, int Height, long Bytes)>();
        foreach (var entry in _entries.Values)
            if (entry.Texture is { } texture)
                list.Add((entry.Name, texture.GetWidth(), texture.GetHeight(), entry.Bytes));

        list.Sort((left, right) => right.Bytes.CompareTo(left.Bytes));
        return list.Count <= take ? list : list.GetRange(0, take);
    }

    public int ImageCount
    {
        get
        {
            var count = 0;
            foreach (var entry in _entries.Values)
                if (entry.Image is not null) count++;
            return count;
        }
    }

    public TextureLease Lease() => new(this);

    internal void Acquire(string relative)
    {
        var entry = GetOrCreate(relative);
        entry.References++;
    }

    internal void Release(string relative)
    {
        if (!_entries.TryGetValue(relative, out var entry)) return;
        if (--entry.References > 0) return;

        entry.Texture?.Dispose();
        entry.Image?.Dispose();
        _entries.Remove(relative);
    }

    internal Texture2D Texture(string relative)
    {
        var entry = GetOrCreate(relative);
        if (entry.Texture is not null) return entry.Texture;

        var image = entry.Image ?? Decode(relative);
        entry.Texture = ImageTexture.CreateFromImage(image);
        entry.Bytes += (long)image.GetWidth() * image.GetHeight() * 4;
        NotePeak();

        // 只有被明确要过像素的图才留着（那份内存在 Image() 里已经记过账）；
        // 否则建完贴图就把 CPU 侧还掉，省一半内存。
        if (entry.Image is null) image.Dispose();

        return entry.Texture;
    }

    internal Texture2D Texture(string relative, float scale)
    {
        if (scale >= 0.999f) return Texture(relative);

        var key = ScaledKey(relative, scale);
        var entry = GetOrCreate(key);
        if (entry.Texture is not null) return entry.Texture;

        var image = Decode(relative);
        var width = Math.Max(1, (int)MathF.Round(image.GetWidth() * scale));
        var height = Math.Max(1, (int)MathF.Round(image.GetHeight() * scale));
        image.Resize(width, height, Godot.Image.Interpolation.Lanczos);

        entry.Texture = ImageTexture.CreateFromImage(image);
        entry.Bytes += (long)width * height * 4;
        NotePeak();
        image.Dispose();
        return entry.Texture;
    }

    internal static string ScaledKey(string relative, float scale) =>
        relative + "@" + scale.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    internal Image Image(string relative)
    {
        var entry = GetOrCreate(relative);
        entry.KeepImage = true;
        if (entry.Image is null)
        {
            entry.Image = Decode(relative);
            entry.Bytes += (long)entry.Image.GetWidth() * entry.Image.GetHeight() * 4;
            NotePeak();
        }

        return entry.Image;
    }

    private void NotePeak()
    {
        var current = Bytes;
        if (current > PeakBytes) PeakBytes = current;
    }

    private Entry GetOrCreate(string relative)
    {
        if (_entries.TryGetValue(relative, out var entry)) return entry;

        entry = new Entry { Name = relative };
        _entries[relative] = entry;
        return entry;
    }

    private static Image Decode(string relative)
    {
        var path = ContentRoot.Resolve(relative);
        if (!File.Exists(path)) throw new FileNotFoundException($"找不到素材：{relative}", path);

        var image = Godot.Image.LoadFromFile(path)
            ?? throw new InvalidDataException($"素材解码失败：{relative}");

        if (image.GetFormat() != Godot.Image.Format.Rgba8) image.Convert(Godot.Image.Format.Rgba8);
        return image;
    }
}

/// <summary>
/// 一次使用期内的贴图租约。用完必须 <see cref="Dispose"/>，否则贴图不会释放。
/// 重复取同一张图只记一次引用，释放时也只还一次。
/// </summary>
public sealed class TextureLease : IDisposable
{
    private readonly TextureCache _cache;
    private readonly HashSet<string> _held = [];
    private bool _disposed;

    internal TextureLease(TextureCache cache) => _cache = cache;

    public int Count => _held.Count;

    public Texture2D Texture(string relative)
    {
        Hold(relative);
        return _cache.Texture(relative);
    }

    public Texture2D Texture(string relative, float scale)
    {
        if (scale >= 0.999f) return Texture(relative);

        Hold(TextureCache.ScaledKey(relative, scale));
        return _cache.Texture(relative, scale);
    }

    public Image Image(string relative)
    {
        Hold(relative);
        return _cache.Image(relative);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var relative in _held) _cache.Release(relative);
        _held.Clear();
    }

    private void Hold(string relative)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(TextureLease), "租约已归还，不能再取贴图");
        if (_held.Add(relative)) _cache.Acquire(relative);
    }
}
