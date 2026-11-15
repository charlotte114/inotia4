using System;
using System.IO;
using Godot;

namespace Inotia4.Application;

/// <summary>
/// 内容与素材的根目录。开发时指向仓库的 <c>public/</c>，导出后指向内容包解下来的目录。
///
/// 之所以不把素材放进 Godot 的导入管线：11,556 张 PNG 全量预导入会把编辑器卡在扫描上、
/// 把首包撑大，而游戏实际是「进这张图才读这张图要的素材」。所以导出时把 public/ 打成一个
/// 普通 zip 随包分发（<c>godot/tools/pack_game_data.py</c>），解到应用私有目录（<c>user://</c>，
/// Android 上是真文件路径），之后所有读取照旧走文件系统。
///
/// **按需解包**：以前是首启把 12,000 个文件／171 MB 一次性写满，玩家对着加载页等十几秒，
/// 磁盘也一次性吃掉。现在只写一个版本标记，文件在第一次被读到时才从 zip 里解出来——
/// 大多数内容（没走到的地图、没触发的对话、没放过的特效）一辈子都不落到磁盘上。
/// </summary>
public static class ContentRoot
{
    private const string PackedPath = "res://game_data.zip";

    private const string ExtractedPath = "user://game_data";

    private const string ReadyMark = ".ready";

    private static string? _root;

    private static bool _lazy;

    private static PackedContent? _pack;

    public static string Root => _root ??= Detect();

    public static int ExtractedFiles { get; private set; }

    public static ulong ExtractMicroseconds { get; private set; }

    public static ulong ReadMicroseconds { get; private set; }

    public static ulong WriteMicroseconds { get; private set; }

    /// <summary>
    /// 把一个相对路径换成磁盘路径。导出后的按需解包就挂在这里：
    /// 文件不在就从内容包解这一个再返回——全仓读内容只有 ContentLoader、GameAudio、
    /// TextureCache 三个入口，都经过这里，所以一处就够。
    /// </summary>
    public static string Resolve(string relative)
    {
        var root = Root;
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

        // 开发时 root 是仓库里的 public/，那是源目录，缺文件就是真缺，不该往里写。
        if (_lazy && !File.Exists(path)) ExtractOne(relative, path);
        return path;
    }

    private static string Detect()
    {
        // 自检用：强制走「导出后」那条路，即使本机有 public/ 也照样从内容包按需解。
        // --unpackcheck 自己也带这个意思，不然它验的永远是开发路径。
        var arguments = OS.GetCmdlineUserArgs();
        var forcePacked = Array.IndexOf(arguments, "--packedcontent") >= 0
                          || Array.IndexOf(arguments, "--unpackcheck") >= 0;

        var projectDirectory = ProjectSettings.GlobalizePath("res://");
        string[] candidates =
        [
            projectDirectory,
            Path.GetFullPath(Path.Combine(projectDirectory, "..", "public")),
        ];
        if (!forcePacked)
            foreach (var candidate in candidates)
                if (HasWorld(candidate)) return candidate;

        if (!Godot.FileAccess.FileExists(PackedPath))
            throw new DirectoryNotFoundException(
                "找不到内容目录（需要 content/world.json），也没有内容包 " + PackedPath);

        var extracted = ProjectSettings.GlobalizePath(ExtractedPath);
        var signature = PackSignature();
        Prepare(extracted, signature);

        using (var probe = PackedContent.Open(PackedPath))
        {
            if (probe is null)
            {
                GD.PushWarning("[content] 自建读取器不可用，退回整包解包（首启会慢很多）");
                ExtractAll(extracted, signature);
            }
            else
            {
                _lazy = true;
            }
        }

        return extracted;
    }

    private static void Prepare(string directory, string signature)
    {
        var mark = Path.Combine(directory, ReadyMark);
        if (File.Exists(mark) && File.ReadAllText(mark).Trim() == signature) return;

        if (Directory.Exists(directory))
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                GD.PushWarning("[content] 旧解包目录删不干净，可能残留上一版素材：" + directory);
            }
        }

        Directory.CreateDirectory(directory);
        File.WriteAllText(mark, signature);
        GD.Print($"[content] 内容目录就绪（{signature} 字节），素材改为按需解包");
    }

    private static bool HasWorld(string directory) =>
        File.Exists(Path.Combine(directory, "content", "world.json"));

    private static string PackSignature()
    {
        using var file = Godot.FileAccess.Open(PackedPath, Godot.FileAccess.ModeFlags.Read);
        return file is null ? "" : file.GetLength().ToString();
    }

    private static void ExtractOne(string relative, string destination)
    {
        var startedAt = Time.GetTicksUsec();
        var reader = Pack();
        var entry = relative.Replace('\\', '/');
        if (reader is null)
            throw new InvalidDataException("内容包打不开：" + PackedPath);
        if (!reader.Contains(entry))
            throw new FileNotFoundException(
                $"内容包里没有这个文件：{entry}（磁盘上也没有）");

        var parent = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

        var readStarted = Time.GetTicksUsec();
        var bytes = reader.Read(entry);
        var writeStarted = Time.GetTicksUsec();
        if (bytes is null) throw new InvalidDataException("内容包读不出这个文件：" + entry);
        File.WriteAllBytes(destination, bytes);
        var finished = Time.GetTicksUsec();

        ExtractedFiles++;
        ReadMicroseconds += writeStarted - readStarted;
        WriteMicroseconds += finished - writeStarted;
        ExtractMicroseconds += finished - startedAt;

        if ((writeStarted - readStarted) / 1000 > 20
            && Array.IndexOf(OS.GetCmdlineUserArgs(), "--unpackcheck") >= 0)
            GD.Print($"[content] 读包慢：{entry} → {(writeStarted - readStarted) / 1000} 毫秒，{bytes.Length} 字节");
    }

    private static PackedContent? Pack() => _pack ??= PackedContent.Open(PackedPath);

    /// <summary>
    /// 兜底：整包解开。只在自建读取器不可用时走这条路（12,200 个文件，
    /// 引擎的 ZipReader 每个要 120 毫秒，实际是十分钟以上的首启）——慢，但至少能起来。
    /// </summary>
    private static void ExtractAll(string target, string signature)
    {
        GD.Print($"[content] 正在整包解包 {PackedPath} → {ExtractedPath}（会很久）");

        using var reader = new ZipReader();
        if (reader.Open(PackedPath) != Error.Ok)
            throw new InvalidDataException("内容包打不开：" + PackedPath);

        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        Directory.CreateDirectory(target);

        var entries = reader.GetFiles();
        var startedAt = Time.GetTicksMsec();
        var written = 0;
        foreach (var entry in entries)
        {
            if (entry.EndsWith('/')) continue;

            var destination = Path.Combine(target, entry.Replace('/', Path.DirectorySeparatorChar));
            var parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            File.WriteAllBytes(destination, reader.ReadFile(entry));

            if (++written % 2000 == 0) GD.Print($"[content] 已解开 {written}/{entries.Length}");
        }

        File.WriteAllText(Path.Combine(target, ReadyMark), signature);
        GD.Print($"[content] 整包解包完成：{written} 个文件，{Time.GetTicksMsec() - startedAt} 毫秒");
    }
}
