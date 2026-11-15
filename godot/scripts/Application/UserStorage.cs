using Godot;

namespace Inotia4.Application;

public static class UserStorage
{
    public static string? Read(string key)
    {
        using var file = FileAccess.Open($"user://{key}", FileAccess.ModeFlags.Read);
        return file is null ? null : file.GetAsText();
    }

    public static void Write(string key, string content)
    {
        using var file = FileAccess.Open($"user://{key}", FileAccess.ModeFlags.Write);
        file?.StoreString(content);
    }
}
