// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Security.Cryptography;
using System.Text;

namespace OrgZ.Services;

/// <summary>
/// How OrgZ decides that a track on a device *is* a track in the library.
///
/// The library's own identity is the file's full path - no two library tracks share one - so that is
/// what a device track carries: its 64-bit dbid is derived from the path it was copied from. A track
/// is "already on the device" when a device track has that dbid. Matching on artist + title, the old
/// rule, treated a live take and the studio version as the same song and never synced one of them.
///
/// Tracks written by older builds (random dbids) and by iTunes still need matching, so there is one
/// fallback: artist, title, album and length all agreeing. That is enough to keep a live version
/// distinct from a studio one, and nothing looser is used.
/// </summary>
public static class DeviceTrackIdentity
{
    /// <summary>The dbid a library file gets on a device. Deterministic: the same file always
    /// produces the same id, on every sync, so re-syncing recognises its own copies.</summary>
    public static ulong DbidFor(string libraryPath)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(NormalizePath(libraryPath)));
        ulong id = BitConverter.ToUInt64(hash, 0);
        return id == 0 ? 1 : id;   // 0 means "no dbid" throughout the iPod formats
    }

    /// <summary>Full path with one separator style, so the same file hashes the same however it was spelled.</summary>
    internal static string NormalizePath(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch
        {
            full = path;
        }
        return full.Replace('\\', '/');
    }

    /// <summary>
    /// The fallback key: artist, title, album and length to the second. Empty when there is no title,
    /// so an untagged file never matches anything by accident.
    /// </summary>
    public static string StrictKey(MediaItem item) => StrictKey(item.Artist, item.Title, item.Album, item.Duration);

    public static string StrictKey(string? artist, string? title, string? album, TimeSpan? duration)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }
        long seconds = duration is { } d ? (long)Math.Round(d.TotalSeconds) : -1;
        return $"{(artist ?? "").Trim()}|{title.Trim()}|{(album ?? "").Trim()}|{seconds}";
    }

    /// <summary>The same key with the length nudged by a second either way - transcoding and tag
    /// readers disagree about the last fraction of a second often enough to matter.</summary>
    private static IEnumerable<string> StrictKeyNeighbours(MediaItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Title))
        {
            yield break;
        }
        var d = item.Duration;
        yield return StrictKey(item.Artist, item.Title, item.Album, d);
        if (d is { } dur)
        {
            yield return StrictKey(item.Artist, item.Title, item.Album, dur + TimeSpan.FromSeconds(1));
            yield return StrictKey(item.Artist, item.Title, item.Album, dur - TimeSpan.FromSeconds(1));
        }
    }

    /// <summary>
    /// Answers "is this library track on the device?" for a set of device tracks. Build once per sync;
    /// lookups are dictionary hits.
    /// </summary>
    public sealed class DeviceMatcher
    {
        private readonly Dictionary<ulong, MediaItem> _byDbid = new();
        private readonly Dictionary<string, MediaItem> _byStrictKey = new(StringComparer.OrdinalIgnoreCase);

        public DeviceMatcher(IEnumerable<MediaItem> deviceTracks)
        {
            foreach (var track in deviceTracks)
            {
                if (track.Dbid is { } dbid && dbid != 0)
                {
                    _byDbid.TryAdd(dbid, track);
                }
                var key = StrictKey(track);
                if (key.Length > 0)
                {
                    _byStrictKey.TryAdd(key, track);
                }
            }
        }

        public int Count => _byDbid.Count + _byStrictKey.Count;

        /// <summary>The device's copy of <paramref name="libraryTrack"/>, or null when it isn't there.</summary>
        public MediaItem? Match(MediaItem libraryTrack)
        {
            if (!string.IsNullOrEmpty(libraryTrack.FilePath) && _byDbid.TryGetValue(DbidFor(libraryTrack.FilePath), out var byId))
            {
                return byId;
            }
            foreach (var key in StrictKeyNeighbours(libraryTrack))
            {
                if (_byStrictKey.TryGetValue(key, out var byKey))
                {
                    return byKey;
                }
            }
            return null;
        }

        public bool Contains(MediaItem libraryTrack) => Match(libraryTrack) is not null;
    }

    /// <summary>
    /// The other direction, for mirror removal: the library tracks the plan keeps, asked "is this
    /// device track one of yours?" A device track with no match is what the mirror removes.
    /// </summary>
    public sealed class KeepSet
    {
        private readonly HashSet<ulong> _dbids = new();
        private readonly HashSet<string> _strictKeys = new(StringComparer.OrdinalIgnoreCase);

        public void Add(MediaItem libraryTrack)
        {
            if (!string.IsNullOrEmpty(libraryTrack.FilePath))
            {
                _dbids.Add(DbidFor(libraryTrack.FilePath));
            }
            foreach (var key in StrictKeyNeighbours(libraryTrack))
            {
                _strictKeys.Add(key);
            }
        }

        public bool Covers(MediaItem deviceTrack)
        {
            if (deviceTrack.Dbid is { } dbid && _dbids.Contains(dbid))
            {
                return true;
            }
            var key = StrictKey(deviceTrack);
            return key.Length > 0 && _strictKeys.Contains(key);
        }

        /// <summary>A device track OrgZ can't identify at all (no title) is never removed - the mirror
        /// can't prove it was deselected.</summary>
        public static bool IsIdentifiable(MediaItem deviceTrack) => !string.IsNullOrWhiteSpace(deviceTrack.Title);
    }
}
