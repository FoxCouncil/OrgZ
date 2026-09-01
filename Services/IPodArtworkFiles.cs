// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Services;

/// <summary>
/// Naming and size rules for the artwork thumbnail files (iPod_Control/Artwork/F{format}_{n}.ithmb).
///
/// An iPod is a FAT32 volume, and FAT32 caps a single file at 4 GiB minus one byte. A 320x320 RGB565
/// thumbnail is 200 KB, so a large library's thumbnails for one format reach that ceiling - and when
/// they did, every cover after it was silently lost while the file sat at exactly 4,294,860,800 bytes.
/// iTunes handles it by starting F{format}_2.ithmb; this is the same rule, with the ceiling held a little
/// short of the true limit so the database's 32-bit offsets and a last append never touch it.
/// </summary>
public static class IPodArtworkFiles
{
    /// <summary>The largest a single .ithmb is allowed to grow before the next index opens. Held below
    /// the FAT32 ceiling (4 GiB - 1) with headroom for one more thumbnail of any supported size.</summary>
    public const long DefaultMaxFileBytes = 4L * 1024 * 1024 * 1024 - 8L * 1024 * 1024;

    /// <summary>Settable so tests can force a rollover with kilobytes instead of gigabytes.</summary>
    internal static long MaxFileBytes { get; set; } = DefaultMaxFileBytes;

    public static string FileName(int formatId, int fileIndex) => $"F{formatId}_{fileIndex}.ithmb";

    /// <summary>True when appending <paramref name="bytes"/> at <paramref name="currentEnd"/> would cross the ceiling.
    /// An empty file always accepts its first thumbnail, whatever the ceiling is set to.</summary>
    public static bool WouldOverflow(long currentEnd, long bytes) => currentEnd > 0 && currentEnd + bytes > MaxFileBytes;

    /// <summary>
    /// The highest F{format}_N.ithmb index present in <paramref name="artDir"/>, or 1 when the format
    /// has no file yet. Appends always go to the newest file; older ones are full by definition.
    /// </summary>
    public static int NewestFileIndex(string artDir, int formatId)
    {
        if (!Directory.Exists(artDir))
        {
            return 1;
        }

        int newest = 1;
        var prefix = $"F{formatId}_";
        foreach (var path in Directory.EnumerateFiles(artDir, prefix + "*.ithmb"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (int.TryParse(name.AsSpan(prefix.Length), out var index) && index > newest)
            {
                newest = index;
            }
        }
        return newest;
    }

    /// <summary>Every thumbnail file on the device with its size - the input for the file-size limit check.</summary>
    public static IReadOnlyList<(string FileName, long Bytes)> ListFiles(string mountPath)
    {
        var artDir = Path.Combine(mountPath, "iPod_Control", "Artwork");
        if (!Directory.Exists(artDir))
        {
            return [];
        }
        return Directory.EnumerateFiles(artDir, "F*_*.ithmb")
            .Select(p => (Path.GetFileName(p), new FileInfo(p).Length))
            .OrderBy(f => f.Item1, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
