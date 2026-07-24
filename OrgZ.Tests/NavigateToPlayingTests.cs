// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.ViewModels;

namespace OrgZ.Tests;

/// <summary>
/// "Go to current song" target resolution: the view playback started from wins
/// (song in its context, like iTunes); kind-based homes only when that view is
/// gone. Verification cases prove the contract, adversarial cases attack the
/// fallbacks, stale origins, and collection precedence.
/// </summary>
public class NavigateToPlayingTests
{
    private static readonly SidebarItem MusicTab = new() { Name = "Music", ViewConfigKey = "Music", Kind = MediaKind.Music };
    private static readonly SidebarItem RadioTab = new() { Name = "Radio", ViewConfigKey = "Radio", Kind = MediaKind.Radio };
    private static readonly SidebarItem Favorites = new() { Name = "Favorites", ViewConfigKey = "Favorites", IsFavorites = true };
    private static readonly SidebarItem Playlist7 = new() { Name = "Burn Test 1", ViewConfigKey = "Playlist:7", PlaylistId = 7 };
    private static readonly SidebarItem CdView = new() { Name = "Audio CD (I:)", ViewConfigKey = "CdAudio" };
    private static readonly SidebarItem FoxPod = new() { Name = "FOXPOD", ViewConfigKey = "Device:E" };

    private static List<SidebarItem> Library => [MusicTab, RadioTab, Favorites];
    private static List<SidebarItem> Playlists => [Playlist7];
    private static List<SidebarItem> Devices => [CdView, FoxPod];

    private static MediaItem Song(string? source = null, MediaKind kind = MediaKind.Music) => new()
    {
        Id = "test:1",
        Kind = kind,
        Title = "Stop",
        Source = source,
    };

    // ── Verification: the origin view wins ───────────────────────────────

    [Fact]
    public void Origin_playlist_wins_over_music_tab()
    {
        var target = MainWindowViewModel.ResolveNavigationTarget("Playlist:7", Song(), Library, Playlists, Devices);
        Assert.Same(Playlist7, target);
    }

    [Fact]
    public void Origin_favorites_wins_over_music_tab()
    {
        var target = MainWindowViewModel.ResolveNavigationTarget("Favorites", Song(), Library, Playlists, Devices);
        Assert.Same(Favorites, target);
    }

    [Fact]
    public void Origin_device_view_wins_for_device_track()
    {
        var target = MainWindowViewModel.ResolveNavigationTarget("Device:E", Song(source: "device:E"), Library, Playlists, Devices);
        Assert.Same(FoxPod, target);
    }

    [Fact]
    public void Origin_cd_view_wins_for_cd_track()
    {
        var target = MainWindowViewModel.ResolveNavigationTarget("CdAudio", Song(source: "cdda"), Library, Playlists, Devices);
        Assert.Same(CdView, target);
    }

    // ── Adversarial: stale/missing origins fall back correctly ───────────

    [Fact]
    public void Deleted_playlist_origin_falls_back_to_music_tab()
    {
        var target = MainWindowViewModel.ResolveNavigationTarget("Playlist:99", Song(), Library, Playlists, Devices);
        Assert.Same(MusicTab, target);
    }

    [Fact]
    public void Deleted_origin_with_device_track_falls_back_to_the_device_entry()
    {
        var target = MainWindowViewModel.ResolveNavigationTarget("Playlist:99", Song(source: "device:E"), Library, Playlists, Devices);
        Assert.Same(FoxPod, target);
    }

    [Fact]
    public void Deleted_origin_with_cd_track_falls_back_to_the_cd_view()
    {
        var target = MainWindowViewModel.ResolveNavigationTarget("Playlist:99", Song(source: "cdda"), Library, Playlists, Devices);
        Assert.Same(CdView, target);
    }

    [Fact]
    public void Null_origin_uses_kind_fallback_for_music_and_radio()
    {
        Assert.Same(MusicTab, MainWindowViewModel.ResolveNavigationTarget(null, Song(), Library, Playlists, Devices));
        Assert.Same(RadioTab, MainWindowViewModel.ResolveNavigationTarget(null, Song(kind: MediaKind.Radio), Library, Playlists, Devices));
    }

    [Fact]
    public void Empty_origin_never_matches_an_empty_view_key()
    {
        // SidebarItem.ViewConfigKey defaults to "" - an unset origin ("") must not
        // "match" such an item and hijack the navigation.
        var blankKeyItem = new SidebarItem { Name = "Header", ViewConfigKey = "" };
        var target = MainWindowViewModel.ResolveNavigationTarget("", Song(), [blankKeyItem, MusicTab], Playlists, Devices);
        Assert.Same(MusicTab, target);
    }

    [Fact]
    public void Ejected_device_with_no_origin_resolves_to_nothing()
    {
        var target = MainWindowViewModel.ResolveNavigationTarget(null, Song(source: "device:Z"), Library, Playlists, Devices);
        Assert.Null(target);
    }

    [Fact]
    public void Kinds_without_a_home_resolve_to_nothing()
    {
        Assert.Null(MainWindowViewModel.ResolveNavigationTarget(null, Song(kind: MediaKind.Podcast), Library, Playlists, Devices));
        Assert.Null(MainWindowViewModel.ResolveNavigationTarget(null, Song(kind: MediaKind.Audiobook), Library, Playlists, Devices));
    }

    [Fact]
    public void Origin_key_collisions_resolve_library_first_then_playlists_then_devices()
    {
        var libDupe = new SidebarItem { Name = "lib", ViewConfigKey = "Dupe" };
        var plDupe = new SidebarItem { Name = "pl", ViewConfigKey = "Dupe" };
        var devDupe = new SidebarItem { Name = "dev", ViewConfigKey = "Dupe" };

        Assert.Same(libDupe, MainWindowViewModel.ResolveNavigationTarget("Dupe", Song(), [libDupe], [plDupe], [devDupe]));
        Assert.Same(plDupe, MainWindowViewModel.ResolveNavigationTarget("Dupe", Song(), [], [plDupe], [devDupe]));
        Assert.Same(devDupe, MainWindowViewModel.ResolveNavigationTarget("Dupe", Song(), [], [], [devDupe]));
    }
}
