// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Models;

/// <summary>
/// A single playlist discovered on a connected device. Distinct from <see cref="Playlist"/>
/// (which is the user's library-side SQLite-backed playlist) — device playlists are read
/// from the device itself: MHYP/MHIP chunks inside a stock iPod's iTunesDB, or
/// <c>Playlists/*.m3u</c> files on a Rockbox player.
///
/// Identity is the mount path plus a source-local identifier (iTunesDB playlist ID for
/// stock, file name for Rockbox M3U). <see cref="TrackIds"/> holds the ordered list of
/// <see cref="MediaItem.Id"/> strings that make up the playlist — the same ID format the
/// device scanner produced, so the playlist view config can filter directly by set
/// membership without needing an additional lookup.
/// </summary>
public class DevicePlaylist
{
    /// <summary>
    /// Display name shown in the sidebar and playlist header.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Stable, per-device identifier. For stock iPods: the iTunesDB playlist PlaylistId
    /// rendered as string ("MHYP:42"). For Rockbox: the M3U filename stem. Used to build
    /// the sidebar ViewConfigKey "Device:{mount}:Playlist:{Key}" and to detect same-name
    /// playlists across reloads.
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    /// Ordered list of <see cref="MediaItem.Id"/> strings that belong to this playlist.
    /// Order is authoritative — the playlist view preserves it via a custom Sorter in
    /// the view config, so "Track 1, Track 2, Track 3" on the iPod screen matches OrgZ.
    /// </summary>
    public required List<string> TrackIds { get; init; }

    public int TrackCount => TrackIds.Count;
}
