// Copyright (c) 2025 Fox Diller

namespace OrgZ.Models;

/// <summary>
/// Represents an audio file with metadata and validation information
/// </summary>
public partial class AudioFileInfo : ObservableObject
{
    /// <summary>
    /// Full path to the file
    /// </summary>
    public required string FilePath { get; init; }

    /// <summary>
    /// File name without path
    /// </summary>
    public required string FileName { get; init; }

    /// <summary>
    /// File extension (e.g., .flac, .mp3)
    /// </summary>
    public required string Extension { get; init; }

    /// <summary>
    /// File size in bytes
    /// </summary>
    public long FileSize { get; init; }

    /// <summary>
    /// Last modified time (UTC) from filesystem
    /// </summary>
    public DateTime LastModified { get; init; }

    /// <summary>
    /// Whether the file has been analyzed with TagLib
    /// </summary>
    [ObservableProperty]
    private bool _isAnalyzed;

    /// <summary>
    /// Whether the filename matches the file's internal headers
    /// </summary>
    [ObservableProperty]
    private bool? _fileNameMatchesHeaders;

    /// <summary>
    /// Whether the file has album art embedded
    /// </summary>
    [ObservableProperty]
    private bool? _hasAlbumArt;

    /// <summary>
    /// What TagLib identifies the MIME type as
    /// </summary>
    [ObservableProperty]
    private string? _mimeType;

    /// <summary>
    /// Artist name from metadata
    /// </summary>
    [ObservableProperty]
    private string? _artist;

    /// <summary>
    /// Year from metadata
    /// </summary>
    [ObservableProperty]
    private uint? _year;

    /// <summary>
    /// Album name from metadata
    /// </summary>
    [ObservableProperty]
    private string? _album;

    /// <summary>
    /// Track title from metadata
    /// </summary>
    [ObservableProperty]
    private string? _title;

    /// <summary>
    /// Track duration from metadata
    /// </summary>
    [ObservableProperty]
    private TimeSpan? _duration;

    /// <summary>
    /// Track number from metadata
    /// </summary>
    [ObservableProperty]
    private uint? _track;

    /// <summary>
    /// Total tracks in album from metadata
    /// </summary>
    [ObservableProperty]
    private uint? _totalTracks;

    /// <summary>
    /// Disc number from metadata
    /// </summary>
    [ObservableProperty]
    private uint? _disc;

    /// <summary>
    /// Total discs in album from metadata
    /// </summary>
    [ObservableProperty]
    private uint? _totalDiscs;

    /// <summary>
    /// Any issues found with the file
    /// </summary>
    public List<string> Issues { get; init; } = [];
}
