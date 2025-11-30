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
}
