// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.ViewModels;

internal partial class StatusBarViewModel : ObservableObject
{
    [ObservableProperty]
    private string _mainStatus = "Ready";

    [ObservableProperty]
    private string _totalArtists = "0";

    [ObservableProperty]
    private string _totalAlbums = "0";

    [ObservableProperty]
    private string _totalSongs = "0";

    [ObservableProperty]
    private string _totalDuration = "0";

    [ObservableProperty]
    private MediaKind? _activeKind;

    [ObservableProperty]
    private string _stationCount = "0";

    [ObservableProperty]
    private string _syncStatus = string.Empty;

    [ObservableProperty]
    private int _errorCount;

    // Generic stats for non-Music/Radio views (Favorites, Playlists, Ignored, Bad Format)
    [ObservableProperty]
    private string _itemCount = "0";

    [ObservableProperty]
    private string _itemLabel = "items";

    [ObservableProperty]
    private string _itemDuration = "";

    [ObservableProperty]
    private bool _hasGenericStats;
}
