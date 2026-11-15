using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Godot;

namespace Inotia4.Application;

internal sealed class PackedContent : IDisposable
{
    private const uint EndOfCentralDirectory = 0x06054b50;
    private const uint CentralDirectoryEntry = 0x02014b50;
    private const uint LocalFileHeader = 0x04034b50;

    private readonly record struct Entry(ushort Method, long LocalOffset, uint CompressedSize, uint Size);

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Godot.FileAccess _file;

    private PackedContent(Godot.FileAccess file) => _file = file;

    public int Count => _entries.Count;

    public static PackedContent? Open(string resPath)
    {
        var file = Godot.FileAccess.Open(resPath, Godot.FileAccess.ModeFlags.Read);
        if (file is null) return null;

        var length = (long)file.GetLength();
        if (length < 22)
        {
            file.Dispose();
            return null;
        }

        var tailSize = (int)Math.Min(length, 65557);
        file.Seek((ulong)(length - tailSize));
        var tail = file.GetBuffer(tailSize);
        var eocd = LastSignature(tail, EndOfCentralDirectory);
        if (eocd < 0)
        {
            file.Dispose();
            return null;
        }

        var total = BitConverter.ToUInt16(tail, eocd + 10);
        var directorySize = BitConverter.ToUInt32(tail, eocd + 12);
        var directoryOffset = BitConverter.ToUInt32(tail, eocd + 16);

        // 0xFFFF／0xFFFFFFFF 是 ZIP64 的占位值。这个包不会到那儿，碰到了就明说。
        if (total == ushort.MaxValue || directoryOffset == uint.MaxValue)
        {
            GD.PushWarning("[content] 内容包是 ZIP64，当前读取器不支持");
            file.Dispose();
            return null;
        }

        var content = new PackedContent(file);
        file.Seek(directoryOffset);
        var directory = file.GetBuffer((int)directorySize);

        var cursor = 0;
        for (var i = 0; i < total; i++)
        {
            if (cursor + 46 > directory.Length) break;
            if (BitConverter.ToUInt32(directory, cursor) != CentralDirectoryEntry) break;

            var method = BitConverter.ToUInt16(directory, cursor + 10);
            var compressed = BitConverter.ToUInt32(directory, cursor + 20);
            var size = BitConverter.ToUInt32(directory, cursor + 24);
            var nameLength = BitConverter.ToUInt16(directory, cursor + 28);
            var extraLength = BitConverter.ToUInt16(directory, cursor + 30);
            var commentLength = BitConverter.ToUInt16(directory, cursor + 32);
            var localOffset = BitConverter.ToUInt32(directory, cursor + 42);
            if (cursor + 46 + nameLength > directory.Length) break;

            var name = System.Text.Encoding.UTF8.GetString(directory, cursor + 46, nameLength);
            content._entries[name] = new Entry(method, localOffset, compressed, size);
            cursor += 46 + nameLength + extraLength + commentLength;
        }

        return content;
    }

    public bool Contains(string name) => _entries.ContainsKey(name);

    public byte[]? Read(string name)
    {
        if (!_entries.TryGetValue(name, out var entry)) return null;

        // 本地头里的名字／扩展区长度可能与中央目录不一致，必须读一次本地头才知道数据从哪开始。
        _file.Seek((ulong)entry.LocalOffset);
        var header = _file.GetBuffer(30);
        if (header.Length < 30 || BitConverter.ToUInt32(header, 0) != LocalFileHeader) return null;

        var nameLength = BitConverter.ToUInt16(header, 26);
        var extraLength = BitConverter.ToUInt16(header, 28);
        _file.Seek((ulong)(entry.LocalOffset + 30 + nameLength + extraLength));

        var raw = _file.GetBuffer((int)entry.CompressedSize);
        if (entry.Method == 0) return raw;
        if (entry.Method != 8) return null;

        using var input = new MemoryStream(raw, writable: false);
        using var inflater = new DeflateStream(input, CompressionMode.Decompress);
        var output = new byte[entry.Size];
        var read = 0;
        while (read < output.Length)
        {
            var step = inflater.Read(output, read, output.Length - read);
            if (step <= 0) break;
            read += step;
        }

        return output;
    }

    public void Dispose() => _file.Dispose();

    private static int LastSignature(byte[] buffer, uint signature)
    {
        for (var i = buffer.Length - 22; i >= 0; i -= 1)
            if (BitConverter.ToUInt32(buffer, i) == signature)
                return i;
        return -1;
    }
}
