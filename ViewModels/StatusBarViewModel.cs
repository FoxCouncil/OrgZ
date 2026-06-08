// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Helpers;

namespace OrgZ.ViewModels;

internal partial class StatusBarViewModel : ObservableObject
{
    [ObservableProperty]
    private string _mainStatus = "Ready";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MusicSummary))]
    private int _totalSongs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MusicSummary))]
    private TimeSpan _totalDuration;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MusicSummary))]
    private long _totalFileSize;

    [ObservableProperty]
    private MediaKind? _activeKind;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RadioSummary))]
    private int _stationCount;

    [ObservableProperty]
    private int _errorCount;

    // Generic stats for non-Music/Radio views (CD, Favorites, Playlists,
    // Ignored, Bad Format). Label is the noun ("tracks", "favorites", …)
    // appended to the count.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GenericSummary))]
    private int _itemCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GenericSummary))]
    private string _itemLabel = "items";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GenericSummary))]
    private TimeSpan _itemDuration;

    [ObservableProperty]
    private bool _hasGenericStats;

    /// <summary>"23,300 songs, 77:00:48:00, 123.4 GB" for the Music view.</summary>
    public string MusicSummary =>
        $"{TotalSongs:N0} songs, {TotalDuration:d\\:hh\\:mm\\:ss}, {FormatHelper.FormatFileSize(TotalFileSize)}";

    /// <summary>"1,246 stations" for the Radio view.</summary>
    public string RadioSummary => $"{StationCount:N0} stations";

    /// <summary>
    /// "12 tracks" / "12 tracks, 1:13:42" — the second clause appears only
    /// when ItemDuration is non-zero. Used by every non-Music/Radio view
    /// (CD = tracks, Favorites = favorites, Playlists = tracks, etc).
    /// </summary>
    public string GenericSummary
    {
        get
        {
            var head = $"{ItemCount:N0} {ItemLabel}";
            if (ItemDuration.TotalSeconds > 0)
            {
                return $"{head}, {ItemDuration:d\\:hh\\:mm\\:ss}";
            }
            return head;
        }
    }
}
