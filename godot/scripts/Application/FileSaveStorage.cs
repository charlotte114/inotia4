using System;
using System.IO;
using Godot;
using Inotia4.Domain;

public sealed class FileSaveStorage : ISaveStorage
{
    private readonly string _directory;

    public FileSaveStorage(string directory = "saves")
    {
        _directory = Path.Combine(ProjectSettings.GlobalizePath("user://"), directory);
        Directory.CreateDirectory(_directory);
    }

    private string PathOf(string key)
    {
        foreach (var character in key)
        {
            var allowed = char.IsAsciiLetterOrDigit(character) || character == '.' || character == '-';
            if (!allowed) throw new InvalidOperationException($"存档键含非法字符：{key}");
        }

        return Path.Combine(_directory, $"{key}.json");
    }

    public string? GetItem(string key)
    {
        var path = PathOf(key);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    public void SetItem(string key, string value)
    {
        var path = PathOf(key);
        File.WriteAllText(path, value);
    }

    public void RemoveItem(string key)
    {
        var path = PathOf(key);
        if (File.Exists(path)) File.Delete(path);
    }
}
