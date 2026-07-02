// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Models;

/// <summary>
/// One book in the owned-books view - a whole book, not a file. It collapses a book's chapter
/// files (a single m4b, or a LibriVox MP3 set) into one card, and also stands in for a book that's
/// been acquired but whose download is gone (re-downloadable). Identity is the book directory
/// (<c>{root}/.audiobooks/{Author}/{Title}</c>), the same join key the record layer uses.
/// </summary>
public sealed class OwnedBook
{
    /// <summary>The book's folder - identity, and the key shared with its acquisition record.</summary>
    public required string BookFolder { get; init; }

    public string? Title  { get; init; }
    public string? Author { get; init; }

    /// <summary>Number of chapter files on disk; 0 for an acquired-but-not-downloaded book.</summary>
    public int ChapterCount { get; init; }

    /// <summary>Sum of the chapters' durations; zero when nothing is downloaded.</summary>
    public TimeSpan TotalDuration { get; init; }

    public bool IsDownloaded   { get; init; }
    public bool IsUserProvided { get; init; }

    /// <summary>The acquisition key, when this book has a record (for re-download / removal).</summary>
    public string? SourceKey { get; init; }

    /// <summary>The record's stored cover URL - the fallback the card shows when there's no local art.</summary>
    public string? RemoteCoverUrl { get; init; }

    /// <summary>The chapter files in play order; empty for a not-downloaded book.</summary>
    public IReadOnlyList<MediaItem> Chapters { get; init; } = [];

    /// <summary>A missing store download can be re-fetched; a user-provided one (no source) cannot.</summary>
    public bool CanReDownload => !IsDownloaded && !IsUserProvided;

    /// <summary>The local file whose embedded art the card prefers (first chapter); null when not downloaded.</summary>
    public string? CoverPath => Chapters.Count > 0 ? Chapters[0].FilePath : null;
}
