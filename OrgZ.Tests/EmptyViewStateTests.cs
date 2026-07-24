// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.ViewModels;

namespace OrgZ.Tests;

/// <summary>
/// The line an empty view shows. "Nothing here" is unhelpful when the reason differs -
/// an empty iPod wants a different sentence than an empty playlist - so each view's
/// wording is asserted rather than eyeballed, including the fallbacks.
/// </summary>
public class EmptyViewStateTests
{
    private static SidebarItem View(string key, bool favorites = false)
        => new() { Name = key, ViewConfigKey = key, IsFavorites = favorites };

    [Fact]
    public void Device_views_name_the_thing_that_is_missing()
    {
        Assert.Contains("No music on this device", MainWindowViewModel.DescribeEmptyView(View(@"Device:E:\")));
        Assert.Contains("No podcasts on this device", MainWindowViewModel.DescribeEmptyView(View($@"Device:E:\:{MediaKind.Podcast}")));
        Assert.Contains("No audiobooks on this device", MainWindowViewModel.DescribeEmptyView(View($@"Device:E:\:{MediaKind.Audiobook}")));
    }

    [Fact]
    public void Empty_device_music_suggests_the_way_out()
    {
        // An empty view that doesn't say what to do next is just a shrug.
        var text = MainWindowViewModel.DescribeEmptyView(View(@"Device:E:\"));
        Assert.Contains("Sync", text);
    }

    [Fact]
    public void Kind_sub_views_win_over_the_generic_device_wording()
    {
        // Both keys start with "Device:" - the more specific suffix must be checked first.
        var podcasts = MainWindowViewModel.DescribeEmptyView(View($@"Device:E:\:{MediaKind.Podcast}"));
        Assert.DoesNotContain("No music", podcasts);
    }

    [Fact]
    public void Library_playlists_favorites_and_cd_each_read_differently()
    {
        Assert.Contains("library is empty", MainWindowViewModel.DescribeEmptyView(View("Music")));
        Assert.Contains("playlist is empty", MainWindowViewModel.DescribeEmptyView(View("Playlist:7")));
        Assert.Contains("favorites", MainWindowViewModel.DescribeEmptyView(View("Favorites", favorites: true)));
        Assert.Contains("No audio tracks", MainWindowViewModel.DescribeEmptyView(View("CdAudio")));
        Assert.Contains("stations", MainWindowViewModel.DescribeEmptyView(View("Radio")));
    }

    [Fact]
    public void A_shared_library_says_so_rather_than_blaming_the_local_library()
    {
        var text = MainWindowViewModel.DescribeEmptyView(View("Share:192.168.1.50:7391"));

        Assert.Contains("shared library", text);
        Assert.DoesNotContain("Settings", text);   // nothing local for the user to fix
    }

    [Fact]
    public void Unknown_and_null_views_still_say_something()
    {
        Assert.Equal("Nothing to show here.", MainWindowViewModel.DescribeEmptyView(null));
        Assert.Equal("Nothing to show here.", MainWindowViewModel.DescribeEmptyView(View("SomeFutureView")));
        Assert.Equal("Nothing to show here.", MainWindowViewModel.DescribeEmptyView(new SidebarItem()));   // blank key
    }
}
