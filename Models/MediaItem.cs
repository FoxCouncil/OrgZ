// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Models;

public partial class MediaItem : ObservableObject
{
    // -- Identity --

    public required string Id { get; init; }

    public required MediaKind Kind { get; init; }

    // -- Shared --

    [ObservableProperty]
    private string? _title;

    [ObservableProperty]
    private string? _artist;

    [ObservableProperty]
    private string? _album;

    [ObservableProperty]
    private TimeSpan? _duration;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private bool _isFavorite;

    [ObservableProperty]
    private DateTime? _lastPlayed;

    [ObservableProperty]
    private int? _rating;

    [ObservableProperty]
    private int _playCount;

    public DateTime DateAdded { get; init; } = DateTime.UtcNow;

    // -- Music-only (nullable for radio) --

    public string? FilePath { get; init; }

    public string? FileName { get; init; }

    public string? Extension { get; init; }

    public long? FileSize { get; init; }

    public DateTime? LastModified { get; init; }

    [ObservableProperty]
    private uint? _year;

    [ObservableProperty]
    private uint? _track;

    [ObservableProperty]
    private uint? _totalTracks;

    [ObservableProperty]
    private uint? _disc;

    [ObservableProperty]
    private uint? _totalDiscs;

    [ObservableProperty]
    private bool? _hasAlbumArt;

    [ObservableProperty]
    private bool? _fileNameMatchesHeaders;

    [ObservableProperty]
    private string? _mimeType;

    [ObservableProperty]
    private bool _isAnalyzed;

    public List<string> Issues { get; init; } = [];

    // -- Radio-only (nullable for music) --

    public string? StreamUrl { get; init; }

    public string? Source { get; init; }

    public string SourceLabel => Source switch
    {
        "radiobrowser" => "RB",
        "shoutcast" => "SC",
        "user" => "UG",
        _ => Source ?? ""
    };

    public string? SourceId { get; init; }

    public string? HomepageUrl { get; init; }

    public string? FaviconUrl { get; init; }

    public string? Country { get; init; }

    public string? CountryCode { get; init; }

    public string? Tags { get; init; }

    public string? Codec { get; init; }

    public string CodecLabel => Codec?.ToUpperInvariant() switch
    {
        null or "" => "-",
        "AUDIO/MPEG" => "MP3",
        "AUDIO/AACP" or "AAC+" => "AAC+",
        "AUDIO/AAC" => "AAC",
        "AUDIO/OGG" => "OGG",
        "AUDIO/FLAC" => "FLAC",
        var c => c
    };

    public int? Bitrate { get; init; }

    public string BitrateLabel => Bitrate is > 0 ? $"{Bitrate} kbps" : "";

    public int? Votes { get; init; }

    public int? ClickCount { get; init; }

    public int? ListenerCount { get; init; }

    public string ListenerCountLabel => ListenerCount is > 0 ? $"{ListenerCount:N0}" : "";

    public bool IsHls { get; init; }
}
