// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Models;

namespace OrgZ.Services;

/// <summary>
/// Two-way sync between playlists and .m3u8 files in the music folder.
///
/// Track paths are written relative to the music folder. Favorites is write-only - it is a
/// per-track flag, not a list, so <see cref="Discover"/> excludes it.
/// </summary>
public static class PlaylistFolderSync
{
    public const string FavoritesName = "Favorites";

    public const string Extension = ".m3u8";

    /// <summary>Staging name for the write-then-move; on the folder watcher's ignore list.</summary>
    private const string TempExtension = ".orgztmp";

    /// <summary>
    /// Paths written by OrgZ itself, so the folder watcher can ignore the change it caused.
    /// Without this, saving a playlist edit triggers a rescan of the whole library.
    /// </summary>
    private static readonly Dictionary<string, DateTime> _selfWritten = new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan SelfWriteWindow = TimeSpan.FromSeconds(10);

    /// <summary>
    /// True if OrgZ wrote this path within the last few seconds. Does NOT consume the record:
    /// one write raises several watcher events (a move-with-overwrite reports the delete and
    /// the create), and a single-use record let the second event through - which triggered a
    /// rescan, which rewrote the file, which raised more events.
    /// </summary>
    public static bool WasSelfWritten(string path)
    {
        lock (_selfWritten)
        {
            var now = DateTime.UtcNow;

            foreach (var stale in _selfWritten.Where(e => now - e.Value > SelfWriteWindow).Select(e => e.Key).ToList())
            {
                _selfWritten.Remove(stale);
            }

            return _selfWritten.ContainsKey(path);
        }
    }

    private static void MarkSelfWritten(string path)
    {
        lock (_selfWritten)
        {
            _selfWritten[path] = DateTime.UtcNow;
        }
    }

    public static bool IsFavoritesFile(string path) =>
        string.Equals(Path.GetFileNameWithoutExtension(path), FavoritesName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Maps invalid characters to '-' instead of removing them, so distinct names stay distinct.</summary>
    public static string SanitizeFileName(string playlistName)
    {
        if (string.IsNullOrWhiteSpace(playlistName))
        {
            return "Playlist";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(playlistName.Select(c => invalid.Contains(c) ? '-' : c).ToArray());

        // Trailing dots and spaces cannot be opened on Windows.
        cleaned = cleaned.Trim().TrimEnd('.', ' ');

        return string.IsNullOrWhiteSpace(cleaned) ? "Playlist" : cleaned;
    }

    /// <summary>Playlists OrgZ writes always live in the root, whatever depth a discovered one came from.</summary>
    public static string PathFor(string musicRoot, string playlistName) =>
        Path.Combine(musicRoot, SanitizeFileName(playlistName) + Extension);

    public static void Write(string musicRoot, string playlistName, IReadOnlyList<MediaItem> tracks) =>
        WriteTo(PathFor(musicRoot, playlistName), musicRoot, playlistName, tracks);

    /// <summary>
    /// Writes to an explicit path, so a discovered playlist is rewritten where it was found
    /// rather than duplicated into the root.
    /// </summary>
    public static void WriteTo(string filePath, string musicRoot, string playlistName, IReadOnlyList<MediaItem> tracks)
    {
        if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(musicRoot) || !Directory.Exists(musicRoot))
        {
            return;
        }

        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return;
        }

        var temp = filePath + TempExtension;

        PlaylistExporter.ExportM3U8(temp, playlistName, tracks, relativeTo: musicRoot);

        // Identical content is not worth a write. Favorites is regenerated on every library
        // scan, and rewriting it unchanged was enough to keep the watcher and the scanner
        // feeding each other.
        if (File.Exists(filePath) && File.ReadAllBytes(temp).AsSpan().SequenceEqual(File.ReadAllBytes(filePath)))
        {
            File.Delete(temp);
            return;
        }

        MarkSelfWritten(filePath);
        File.Move(temp, filePath, overwrite: true);
    }

    public static void Delete(string musicRoot, string playlistName)
    {
        if (string.IsNullOrEmpty(musicRoot))
        {
            return;
        }

        var path = PathFor(musicRoot, playlistName);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Every .m3u8 under the music folder, Favorites excluded. Dot-directories are skipped -
    /// .podcasts, .audiobooks and .disc-images are other subsystems' storage.
    /// </summary>
    public static List<string> Discover(string musicRoot)
    {
        if (string.IsNullOrEmpty(musicRoot) || !Directory.Exists(musicRoot))
        {
            return [];
        }

        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.System,
            };

            return Directory
                .EnumerateFiles(musicRoot, "*" + Extension, options)
                .Where(p => !IsFavoritesFile(p) && !IsInDotDirectory(musicRoot, p))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static bool IsInDotDirectory(string musicRoot, string path)
    {
        var relative = Path.GetRelativePath(musicRoot, path);

        return relative
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .SkipLast(1)
            .Any(segment => segment.StartsWith('.'));
    }
}
