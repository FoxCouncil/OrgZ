// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Models;

/// <summary>Per-track CD-rip status shown in the CD view's play column.</summary>
public enum RipState
{
    None,
    Pending,   // queued - grey static spinner
    Ripping,   // in progress - black spinning spinner
    Ripped,    // done - green check
}

public partial class MediaItem : ObservableObject
{
    // -- Identity --

    public required string Id { get; init; }

    /// <summary>
    /// Settable (not init) for exactly one sanctioned mutation: analysis refining Music →
    /// Audiobook when the file's tags say so (iTunes stik atom / audiobook genre) - the scan pass
    /// constructs items before any tags are read. Everything else treats Kind as immutable.
    /// </summary>
    public required MediaKind Kind { get; set; }

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
    [NotifyPropertyChangedFor(nameof(ShowRipPending), nameof(ShowRipping), nameof(ShowRipDone))]
    private bool _isPlaying;

    [ObservableProperty]
    private bool _isFavorite;

    /// <summary>
    /// CD-rip status surfaced in the play column. The play indicator takes priority,
    /// so the rip glyphs hide while a row is actively playing.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRipPending), nameof(ShowRipping), nameof(ShowRipDone))]
    private RipState _ripStatus;

    public bool ShowRipPending => RipStatus == RipState.Pending && !IsPlaying;
    public bool ShowRipping => RipStatus == RipState.Ripping && !IsPlaying;
    public bool ShowRipDone => RipStatus == RipState.Ripped && !IsPlaying;

    /// <summary>
    /// Storage for the iTunes-style row checkbox. Persisted (column name predates the
    /// rename) but always read through <see cref="IsChecked"/> - the UI concept is
    /// "checked to include", not "ignored".
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChecked))]
    private bool _isIgnored;

    /// <summary>
    /// iTunes' per-row checkbox: ticked (the default) means this track takes part -
    /// it plays when the list plays through, and syncs with its playlist. Unticked
    /// leaves it visible and individually playable but skipped by everything automatic.
    /// </summary>
    public bool IsChecked
    {
        get => !IsIgnored;
        set => IsIgnored = !value;
    }

    [ObservableProperty]
    private DateTime? _lastPlayed;

    [ObservableProperty]
    private int? _rating;

    [ObservableProperty]
    private int _playCount;

    // Settable (not init) so a rescan's replacement item can adopt the original added date -
    // otherwise every metadata change resets the row to "added just now" until restart.
    public DateTime DateAdded { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Carries the user's own state from the item being replaced onto this freshly-scanned one.
    /// A rescan rebuilds file facts (tags, size, duration) but must never reset what the user
    /// did: rating, play history, favorites, the row tick, and the Options-tab playback
    /// settings. The DB upsert preserves some of these on conflict; this keeps the in-memory
    /// row honest too, and covers the columns the upsert legitimately overwrites.
    /// </summary>
    public void AdoptUserStateFrom(MediaItem existing)
    {
        IsFavorite = existing.IsFavorite;
        IsIgnored = existing.IsIgnored;
        LastPlayed = existing.LastPlayed;
        DateAdded = existing.DateAdded;
        Rating = existing.Rating;
        PlayCount = existing.PlayCount;
        VolumeAdjustment = existing.VolumeAdjustment;
        EqPreset = existing.EqPreset;
        StartTime = existing.StartTime;
        StopTime = existing.StopTime;
        UseStartTime = existing.UseStartTime;
        UseStopTime = existing.UseStopTime;
        LastPositionMs = existing.LastPositionMs;
    }

    // -- Music-only (nullable for radio) --

    public string? FilePath { get; init; }

    public string? FileName { get; init; }

    public string? Extension { get; init; }

    public long? FileSize { get; init; }

    public DateTime? LastModified { get; init; }

    [ObservableProperty]
    private uint? _year;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrackDisplay))]
    private uint? _track;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrackDisplay))]
    private uint? _totalTracks;

    /// <summary>
    /// Human display for the track column: "1 of 12" when both Track and TotalTracks
    /// are known, just the track number when TotalTracks is missing, empty when Track
    /// is missing altogether. Matches the iTunes / music-player convention.
    /// </summary>
    public string TrackDisplay
    {
        get
        {
            if (!Track.HasValue)
            {
                return string.Empty;
            }
            return TotalTracks.HasValue && TotalTracks.Value > 0
                ? $"{Track.Value} of {TotalTracks.Value}"
                : Track.Value.ToString();
        }
    }

    [ObservableProperty]
    private uint? _disc;

    [ObservableProperty]
    private uint? _totalDiscs;

    /// <summary>
    /// MusicBrainz DiscID of the CD this track was ripped from, read from the file's
    /// MUSICBRAINZ_DISCID tag. Lets the CD view show a green check when that disc is
    /// re-inserted (the file is the source of truth - no side database).
    /// </summary>
    [ObservableProperty]
    private string? _discId;

    /// <summary>
    /// For device (iPod) tracks: the iTunesDB 64-bit persistent id (dbid) used to look
    /// up the track's artwork in the iPod's ArtworkDB. 0/null for library tracks.
    /// </summary>
    public ulong? Dbid { get; init; }

    [ObservableProperty]
    private bool? _hasAlbumArt;

    [ObservableProperty]
    private bool? _fileNameMatchesHeaders;

    [ObservableProperty]
    private string? _mimeType;

    [ObservableProperty]
    private string? _genre;

    [ObservableProperty]
    private string? _composer;

    [ObservableProperty]
    private string? _comment;

    [ObservableProperty]
    private uint? _bpm;

    public int? AudioBitrate { get; set; }

    public int? SampleRate { get; set; }

    /// <summary>Bits per sample for lossless sources (16/24/32); null for lossy codecs where bit depth isn't meaningful.</summary>
    public int? BitDepth { get; set; }

    public int? AudioChannels { get; set; }

    public string? EncoderSettings { get; set; }

    public string? CodecDescription { get; set; }

    /// <summary>
    /// ReplayGain track gain (dB) read from the file's tags, or null when the file carries none.
    /// When present, playback lets VLC apply it (the precise, one-time-computed "Sound Check"
    /// value); when null and normalization is on, VLC's real-time normvol handles it and a
    /// background pass computes + tags the value so next time it's precise.
    /// </summary>
    public double? ReplayGainTrackGainDb { get; set; }

    public bool HasReplayGain => ReplayGainTrackGainDb.HasValue;

    public string ChannelsLabel => AudioChannels switch
    {
        1 => "Mono",
        2 => "Stereo",
        var n when n > 2 => $"{n} channels",
        _ => ""
    };

    public string KindLabel => Extension?.ToUpperInvariant() switch
    {
        ".MP3" => "MPEG audio file",
        ".FLAC" => "FLAC audio file",
        ".M4A" => "AAC audio file",
        ".M4B" => "AAC audiobook file",
        ".AAC" => "AAC audio file",
        ".OGG" => "OGG Vorbis file",
        ".WAV" => "WAV audio file",
        ".WMA" => "WMA audio file",
        ".APE" => "APE audio file",
        ".OPUS" => "Opus audio file",
        _ => MimeType ?? "Audio file"
    };

    [ObservableProperty]
    private bool _isAnalyzed;

    public List<string> Issues { get; init; } = [];

    private static readonly HashSet<string> LossyExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wma", ".aac", ".ogg", ".m4a"
    };

    /// <summary>
    /// Returns a comma-joined list of format quality issues based on the active Settings criteria.
    /// Empty string means the track passes all checks. Used as both the BaseFilter test for the
    /// Bad Format view and displayed in a "Reason" column.
    /// </summary>
    public string FormatIssues
    {
        get
        {
            if (Kind != MediaKind.Music)
            {
                return "";
            }

            var criteria = BadFormatCriteria.Current;
            var issues = new List<string>();

            if (criteria.NoTitle && string.IsNullOrWhiteSpace(Title))
            {
                issues.Add("No Title");
            }

            if (criteria.NoArtist && string.IsNullOrWhiteSpace(Artist))
            {
                issues.Add("No Artist");
            }

            if (criteria.NoYear && (Year == null || Year == 0))
            {
                issues.Add("No Year");
            }

            if (criteria.NoAlbumArt && HasAlbumArt != true)
            {
                issues.Add("No Album Art");
            }

            if (criteria.LossyFormats && Extension != null && LossyExtensions.Contains(Extension))
            {
                issues.Add($"Lossy Format ({Extension.ToLowerInvariant()})");
            }

            return string.Join(", ", issues);
        }
    }

    /// <summary>
    /// The Bad Format view's criteria, read from Settings ONCE per change rather than five
    /// times per item. <see cref="FormatIssues"/> is the view's BaseFilter *and* SearchFilter,
    /// so a keystroke over a 30k-track library used to take ~150,000 lock-and-deserialize
    /// trips through Settings. <see cref="Settings.Save"/> invalidates this.
    /// </summary>
    internal sealed record BadFormatCriteria(bool NoTitle, bool NoArtist, bool NoYear, bool NoAlbumArt, bool LossyFormats)
    {
        private static BadFormatCriteria? _current;

        public static BadFormatCriteria Current => _current ??= new(
            Settings.Get("OrgZ.BadFormat.NoTitle", true),
            Settings.Get("OrgZ.BadFormat.NoArtist", true),
            Settings.Get("OrgZ.BadFormat.NoYear", true),
            Settings.Get("OrgZ.BadFormat.NoAlbumArt", true),
            Settings.Get("OrgZ.BadFormat.LossyFormats", true));

        public static void Invalidate() => _current = null;
    }

    // -- Radio-only (nullable for music) --

    public string? StreamUrl { get; init; }

    public string? Source { get; init; }

    public string? SourceId { get; init; }

    public string? HomepageUrl { get; init; }

    public string? FaviconUrl { get; init; }

    public string? Country { get; init; }

    public string? CountryCode { get; init; }

    /// <summary>
    /// "XX - Country Name" pairing for the Radio Country column tooltip.
    /// Returns just the code when no long form is known, just the name when
    /// no code is known, or null when neither is set (so the tooltip vanishes).
    /// </summary>
    public string? CountryTooltip
    {
        get
        {
            var code = CountryCode?.Trim().ToUpperInvariant();
            var name = Country?.Trim();
            var hasCode = !string.IsNullOrEmpty(code);
            var hasName = !string.IsNullOrEmpty(name);
            return (hasCode, hasName) switch
            {
                (true, true)   => $"{code} — {name}",
                (true, false)  => code,
                (false, true)  => name,
                _              => null,
            };
        }
    }

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

    public bool IsHls { get; init; }

    // -- Per-track playback options (DB only) --

    [ObservableProperty]
    private int _volumeAdjustment;

    [ObservableProperty]
    private string? _eqPreset;

    [ObservableProperty]
    private TimeSpan? _startTime;

    [ObservableProperty]
    private TimeSpan? _stopTime;

    [ObservableProperty]
    private bool _useStartTime;

    [ObservableProperty]
    private bool _useStopTime;

    /// <summary>
    /// Audiobook resume point (ms). Saved throttled during playback, applied on the next play,
    /// reset to 0 when the book finishes. Meaningless (always 0) for other kinds.
    /// </summary>
    public long LastPositionMs { get; set; }
}
