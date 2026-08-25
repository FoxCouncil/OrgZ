// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Models;

public partial class Playlist : ObservableObject
{
    public int Id { get; init; }

    [ObservableProperty]
    private string _name = string.Empty;

    /// <summary>
    /// Where the playlist came from - "Library" for one built in OrgZ, or the imported
    /// format ("M3U", "M3U8", "PLS", "XSPF"). Shown in the playlist header.
    /// </summary>
    public string Source { get; set; } = "Library";

    /// <summary>
    /// The .m3u8 this playlist was discovered from, empty when it was made in OrgZ. When set,
    /// the file is authoritative: a rescan rewrites the tracks, and deleting it drops the playlist.
    /// </summary>
    public string SourcePath { get; set; } = string.Empty;

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
