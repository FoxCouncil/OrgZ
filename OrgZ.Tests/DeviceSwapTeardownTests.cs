// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.ViewModels;

namespace OrgZ.Tests;

/// <summary>
/// Guards the device-disconnect teardown that makes iPod hot-swapping safe. The regression it locks
/// out: two iPods swapped at one drive letter share the view key ("Device:E:\"), and since nothing
/// invalidated the per-view cache on disconnect, clicking the swapped-in iPod served the DEPARTED
/// iPod's cached row list verbatim. Teardown now evicts caches and view configs by key family; these
/// tests pin the two pure pieces of that - which keys belong to a device
/// (<see cref="MainWindowViewModel.IsDeviceViewKeyFor"/>, also the selection-fallback predicate) and
/// the registry's family removal (<see cref="ListViewConfigs.RemoveWithSubViews"/>).
/// </summary>
public class DeviceSwapTeardownTests
{
    // ===== IsDeviceViewKeyFor - "does this view/cache key belong to that device?" =====

    [Theory]
    [InlineData(@"Device:E:\")]                    // the device's own root view
    [InlineData(@"Device:E:\:Podcast")]            // kind sub-views
    [InlineData(@"Device:E:\:Audiobook")]
    [InlineData(@"Device:E:\:Playlists")]          // the navigation-container node
    [InlineData(@"Device:E:\:Playlist:MHYP:2")]    // a per-playlist view
    public void Keys_of_the_device_family_match(string key)
    {
        Assert.True(MainWindowViewModel.IsDeviceViewKeyFor(key, @"E:\"));
    }

    [Theory]
    [InlineData(@"Device:F:\")]              // another device's root
    [InlineData(@"Device:F:\:Podcast")]      // another device's sub-view
    [InlineData("Music")]                    // library views
    [InlineData("Radio")]
    [InlineData("Favorites")]
    [InlineData(null)]
    public void Foreign_and_library_keys_do_not_match(string? key)
    {
        Assert.False(MainWindowViewModel.IsDeviceViewKeyFor(key, @"E:\"));
    }

    [Fact]
    public void A_mount_that_string_prefixes_another_does_not_claim_the_siblings_keys()
    {
        // Two Linux mounts where one is a string prefix of the other: tearing down /media/ipod must
        // not evict /media/ipod-red's views - the boundary is the ":" after the mount, not the prefix.
        Assert.False(MainWindowViewModel.IsDeviceViewKeyFor("Device:/media/ipod-red", "/media/ipod"));
        Assert.False(MainWindowViewModel.IsDeviceViewKeyFor("Device:/media/ipod-red:Podcast", "/media/ipod"));
        Assert.True(MainWindowViewModel.IsDeviceViewKeyFor("Device:/media/ipod:Podcast", "/media/ipod"));
    }

    [Fact]
    public void Mount_path_casing_differences_still_match()
    {
        // WMI arrival/removal events aren't guaranteed to agree on drive-letter casing; the
        // connected-devices map is OrdinalIgnoreCase and the teardown predicate matches it.
        Assert.True(MainWindowViewModel.IsDeviceViewKeyFor(@"Device:e:\:Podcast", @"E:\"));
    }

    // ===== ListViewConfigs.RemoveWithSubViews - the registry side of the same teardown =====

    [Fact]
    public void RemoveWithSubViews_removes_the_whole_family_and_nothing_else()
    {
        var mount = @"Q:\" + Guid.NewGuid();     // unique so parallel tests can't collide in the static registry
        var root = $"Device:{mount}";
        var foreignRoot = $"Device:{mount}-red"; // shares the string prefix, but is a different device

        ListViewConfigs.Register(root, ListViewConfigs.BuildDeviceConfig(mount));
        ListViewConfigs.Register($"{root}:Podcast", ListViewConfigs.BuildDeviceKindConfig(mount, MediaKind.Podcast));
        ListViewConfigs.Register($"{root}:Audiobook", ListViewConfigs.BuildDeviceKindConfig(mount, MediaKind.Audiobook));
        ListViewConfigs.Register($"{root}:Playlist:MHYP:2", ListViewConfigs.BuildDevicePlaylistConfig($"{root}:Playlist:MHYP:2", []));
        ListViewConfigs.Register(foreignRoot, ListViewConfigs.BuildDeviceConfig($"{mount}-red"));
        try
        {
            ListViewConfigs.RemoveWithSubViews(root);

            Assert.Null(ListViewConfigs.Get(root));
            Assert.Null(ListViewConfigs.Get($"{root}:Podcast"));
            Assert.Null(ListViewConfigs.Get($"{root}:Audiobook"));
            Assert.Null(ListViewConfigs.Get($"{root}:Playlist:MHYP:2"));

            Assert.NotNull(ListViewConfigs.Get(foreignRoot));   // the prefix-sharing sibling survives
            Assert.NotNull(ListViewConfigs.Get("Music"));       // library views untouched
        }
        finally
        {
            ListViewConfigs.Remove(foreignRoot);
        }
    }

    [Fact]
    public void RemoveWithSubViews_on_an_unknown_key_is_a_no_op()
    {
        ListViewConfigs.RemoveWithSubViews("Device:NotThere:" + Guid.NewGuid());
    }
}
