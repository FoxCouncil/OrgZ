// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Media.Imaging;

namespace OrgZ.Models;

/// <summary>
/// Snapshot of the inserted audio CD's metadata, shown in the CD view's info bar.
/// Built once at detect from the CdDiscInfo + its MusicBrainz lookup. Missing fields
/// surface as "-" so the layout stays stable.
/// </summary>
public sealed class CdInfo
{
    public Bitmap? CoverArt { get; init; }
    public string? Album { get; init; }
    public string? Artist { get; init; }
    public uint? Year { get; init; }
    public string? Genre { get; init; }
    public int TrackCount { get; init; }
    public TimeSpan TotalDuration { get; init; }
    public string? DiscId { get; init; }
    public string? ReleaseMbid { get; init; }

    public bool HasCoverArt => CoverArt is not null;

    public string AlbumDisplay => string.IsNullOrWhiteSpace(Album) ? "Audio CD" : Album!;
    public string ArtistDisplay => string.IsNullOrWhiteSpace(Artist) ? "—" : Artist!;
    public string YearDisplay => Year is > 0 ? Year.Value.ToString() : "—";
    public string GenreDisplay => string.IsNullOrWhiteSpace(Genre) ? "—" : Genre!;
    public string TrackCountDisplay => TrackCount == 1 ? "1 track" : $"{TrackCount} tracks";
    public string DiscIdDisplay => string.IsNullOrWhiteSpace(DiscId) ? "—" : DiscId!;
    public string ReleaseMbidDisplay => string.IsNullOrWhiteSpace(ReleaseMbid) ? "—" : ReleaseMbid!;

    public string TotalTimeDisplay =>
        TotalDuration.TotalHours >= 1
            ? $"{(int)TotalDuration.TotalHours}:{TotalDuration.Minutes:D2}:{TotalDuration.Seconds:D2}"
            : $"{TotalDuration.Minutes}:{TotalDuration.Seconds:D2}";
}
