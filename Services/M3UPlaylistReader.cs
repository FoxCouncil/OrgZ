// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Serilog;

namespace OrgZ.Services;

/// <summary>
/// Reads Rockbox-style M3U / M3U8 playlists from a connected device. Rockbox stores
/// playlists as plain-text files under <c>/Playlists/</c> at the mount root (some users
/// put them elsewhere, but we scan the conventional folder). Each file is a newline-
/// separated list of track paths with optional <c>#EXT…</c> directives.
///
/// Path resolution:
/// - Absolute paths relative to the filesystem root (Rockbox convention, e.g.
///   <c>/Music/Rush/Signals/01 Subdivisions.mp3</c>) are prefixed with the mount path.
/// - Relative paths resolve against the playlist file's directory.
/// - Backslashes are normalized to forward slashes then mapped to the native separator.
/// </summary>
public static class M3UPlaylistReader
{
    private const string PlaylistsFolder = "Playlists";
    private static readonly ILogger _log = Logging.For("M3UPlaylistReader");

    /// <summary>
    /// Scans the device's <c>Playlists/</c> folder and returns one <see cref="Models.DevicePlaylist"/>
    /// per <c>.m3u</c> / <c>.m3u8</c> file. The track IDs match <see cref="Models.MediaItem.Id"/>
    /// values the Rockbox scanner produces (full absolute file path), so the view config
    /// can filter by set membership without additional lookups.
    ///
    /// Returns an empty list if the Playlists folder is missing or unreadable — this is
    /// the common case on devices that have never had playlists synced.
    /// </summary>
    public static List<Models.DevicePlaylist> Read(string mountPath)
    {
        var playlists = new List<Models.DevicePlaylist>();
        var playlistsDir = Path.Combine(mountPath, PlaylistsFolder);

        if (!Directory.Exists(playlistsDir))
        {
            return playlists;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(playlistsDir))
            {
                var ext = Path.GetExtension(file);
                if (!ext.Equals(".m3u", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".m3u8", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var playlist = ReadOne(file, mountPath);
                if (playlist != null)
                {
                    playlists.Add(playlist);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to enumerate Rockbox playlists at {Path}", playlistsDir);
        }

        return playlists;
    }

    /// <summary>
    /// Parses a single M3U/M3U8 file. Rules:
    /// - Lines starting with <c>#</c> are directives; only <c>#PLAYLIST:</c> is captured
    ///   (overrides the filename-derived name).
    /// - Blank lines are skipped.
    /// - Everything else is a track reference; absolute paths get the mount root,
    ///   relative paths resolve against the playlist directory.
    /// Returns null on read error. Always sets Name (falls back to filename stem).
    /// </summary>
    public static Models.DevicePlaylist? ReadOne(string playlistPath, string mountPath)
    {
        try
        {
            var lines = File.ReadAllLines(playlistPath);
            var nameFromFile = Path.GetFileNameWithoutExtension(playlistPath);
            var playlistDir = Path.GetDirectoryName(playlistPath) ?? mountPath;

            string name = nameFromFile;
            var trackIds = new List<string>();

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                if (line.StartsWith('#'))
                {
                    // #PLAYLIST: directive (non-standard but widely supported) sets the
                    // display name. Everything else (#EXTINF, #EXTM3U) we skip.
                    const string playlistTag = "#PLAYLIST:";
                    if (line.StartsWith(playlistTag, StringComparison.OrdinalIgnoreCase))
                    {
                        var candidate = line[playlistTag.Length..].Trim();
                        if (!string.IsNullOrEmpty(candidate))
                        {
                            name = candidate;
                        }
                    }
                    continue;
                }

                var resolved = ResolvePath(line, playlistDir, mountPath);
                if (!string.IsNullOrEmpty(resolved))
                {
                    trackIds.Add(resolved);
                }
            }

            return new Models.DevicePlaylist
            {
                Name = name,
                Key = nameFromFile,
                TrackIds = trackIds,
            };
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to read M3U playlist {Path}", playlistPath);
            return null;
        }
    }

    /// <summary>
    /// Resolves a single track reference. Normalizes backslashes, then decides between
    /// three cases:
    /// 1. Absolute filesystem path (starts with '/' or Windows drive letter) — prefix
    ///    with mount path if Unix-absolute (Rockbox convention).
    /// 2. Looks like an already-absolute host path that matches the mount — leave alone.
    /// 3. Relative — combine with the playlist's directory.
    /// </summary>
    private static string ResolvePath(string reference, string playlistDir, string mountPath)
    {
        // Normalize separators — Rockbox emits forward slashes, some tools use backslashes
        var normalized = reference.Replace('\\', '/');

        // Case 1: Rockbox-style absolute path "/Music/..." — relative to mount root
        if (normalized.StartsWith('/'))
        {
            // Trim the leading slash and combine with mount root
            var rel = normalized.TrimStart('/');
            return Path.GetFullPath(Path.Combine(mountPath, rel));
        }

        // Case 2: already-qualified host path (e.g. "L:\Music\..." or "/run/media/.../Music/...")
        if (IsAlreadyAbsoluteHostPath(normalized))
        {
            return Path.GetFullPath(normalized);
        }

        // Case 3: relative to the playlist directory
        return Path.GetFullPath(Path.Combine(playlistDir, normalized));
    }

    private static bool IsAlreadyAbsoluteHostPath(string path)
    {
        // Windows drive letter form: "L:\" or "L:/"
        if (path.Length >= 2 && path[1] == ':' && char.IsLetter(path[0]))
        {
            return true;
        }
        // Rooted Unix path that resembles a host mount ("/run/media/..." or "/Volumes/...")
        if (path.StartsWith("/run/media/", StringComparison.Ordinal) ||
            path.StartsWith("/media/", StringComparison.Ordinal) ||
            path.StartsWith("/Volumes/", StringComparison.Ordinal))
        {
            return true;
        }
        return false;
    }
}
