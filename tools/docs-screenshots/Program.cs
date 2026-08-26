// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using OrgZ;
using OrgZ.Controls;
using OrgZ.Models;
using OrgZ.Services;
using OrgZ.ViewModels;
using OrgZ.Views;

namespace OrgZ.DocsScreenshots;

/// <summary>
/// Renders self-contained OrgZ views (dialogs, panels) to PNG for the manual,
/// seeded with fake data so nothing personal leaks and shots regenerate
/// deterministically. Bootstraps the real <see cref="App"/> so styles, fonts,
/// and the FontAwesome icon provider match the running app.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var outDir = args.Length > 0
            ? args[0]
            : Path.Combine(FindRepoRoot(), "docs", "assets", "screenshots");
        Directory.CreateDirectory(outDir);
        Console.WriteLine($"Output: {outDir}");

        // Isolate from the user's real settings: a throwaway directory plus an
        // empty in-memory map. This keeps the harness hermetic (no reading the
        // user's library path) and, crucially, leaves WindowSizeTracker with no
        // saved sizes so dialogs honor their own SizeToContent instead of being
        // stretched to whatever the user last dragged them to.
        Settings.OverrideSettingsDirectory(Path.Combine(Path.GetTempPath(), "orgz-docs-screenshots"));
        Settings.Clear();

        // The library database too, or anything that reads it renders the developer's own
        // library into a published screenshot - playlist names, favourites, device history.
        var scratch = Path.Combine(Path.GetTempPath(), "orgz-docs-screenshots", "db");
        if (Directory.Exists(scratch))
        {
            Directory.Delete(scratch, recursive: true);
        }

        Directory.CreateDirectory(scratch);
        LibraryDb.OverrideDirectory(scratch);

        // Same three stores App opens at startup. Without them the empty scratch database has
        // no schema and anything that queries it throws instead of returning nothing.
        MediaCache.EnsureCreated();
        OrgZ.Services.Podcast.PodcastCache.EnsureCreated();
        OrgZ.Services.Media.AcquisitionStore.EnsureCreated();

        // Also the music folder: RemoteImage keys its disk cache off it, so leaving it empty
        // would read and write the developer's real image cache.
        App.FolderPath = scratch;

        // The share name defaults to "<MACHINENAME> Library", which puts the developer's
        // computer name in a published screenshot.
        Settings.Set("OrgZ.Services.Sharing.Name", "OrgZ Library");

        // The device helper listens on a machine-wide pipe, so an unguarded harness reaches the
        // developer's own running service and renders their live share into the Services shot.
        OrgZ.Services.DeviceHelper.DeviceHelperClient.OfflineForScreenshots = true;

        BuildAvaloniaApp().SetupWithoutStarting();

        SeedRemoteImageCache(scratch);

        // Width pins the window so wrapping help text wraps as designed; Height > 0
        // forces a fixed-size window (the full MainWindow), otherwise height is
        // automatic (SizeToContent) for dialogs and panels.
        var shots = new (string Name, double Width, double Height, Func<Window> Factory)[]
        {
            ("cd-rip-options", 440, 0, () => new RipOptionsDialog(CdRipOptions.Default)),
            ("device-ipod", 920, 0, () => Host(new DeviceInfoBar { DataContext = SampleIPod() })),
            ("library-overview", 1280, 800, SeededMainWindow),
            ("cd-detected", 1280, 800, () => SeededCd(metadata: false)),
            ("cd-metadata", 1280, 800, () => SeededCd(metadata: true)),
            ("cd-rip-progress", 1280, 800, SeededRip),

            ("now-playing", 1280, 800, SeededNowPlaying),
            ("radio-browser", 1280, 800, SeededRadio),
            ("favorites", 1280, 800, SeededFavorites),
            ("playlists", 1280, 800, SeededPlaylists),
            ("podcasts", 1280, 800, SeededPodcasts),
            ("audiobooks", 1280, 800, SeededAudiobooks),
            ("burn-disc", 480, 0, SeededBurnDialog),
            ("media-info", 620, 0, SeededMediaInfo),
            ("sync-settings", 520, 0, SeededSyncSettings),
            ("device-picker", 420, 0, () => new DevicePickerDialog(
                ["OrgZ iPod (Classic 6G)", "NIGHTDRIVE (Rockbox)", "Shuffle 2G"],
                "Send to Device", "Choose where these tracks should go.")),
            ("playlist-name", 400, 0, () => new PlaylistNameDialog("Night Drive")),
            ("airplay-password", 420, 0, () => new AirPlayPasswordDialog("Living Room")),
            ("first-launch", 1280, 800, SeededFirstLaunch),
            ("settings", 620, 0, () => SettingsTab("General")),
            ("settings-playback", 620, 0, () => SettingsTab("Playback")),
            ("settings-burning", 620, 0, () => SettingsTab("Burning")),
            ("settings-services", 620, 0, () => SettingsTab("Services")),
            ("settings-podcasts", 620, 0, () => SettingsTab("Podcasts")),
            ("settings-stats", 620, 0, () => SettingsTab("Stats")),
            ("settings-advanced", 620, 0, () => SettingsTab("Advanced")),
            ("queue", 1280, 800, SeededQueue),
            ("sharing", 1280, 800, SeededSharing),
            ("airplay-picker", 460, 0, SeededOutputPicker),
            ("device-library", 1280, 800, SeededDeviceLibrary),
            ("device-sync", 1280, 800, SeededDeviceSync),
            ("radio-filters", 1280, 800, SeededRadioFilters),
            ("search-results", 1280, 800, SeededSearch),
            ("confirm-remove", 460, 0, () => new ConfirmDialog(
                "Remove From Library",
                "Delete 3 tracks from disk?" + Environment.NewLine + Environment.NewLine
                    + "This cannot be undone and nothing goes to the recycle bin.",
                "Delete")),
            ("mini-player", 0, 0, SeededMiniPlayer),
        };

        int ok = 0;
        foreach (var (name, width, height, factory) in shots)
        {
            var path = Path.Combine(outDir, name + ".png");
            try
            {
                Capture(factory(), width, height, path);
                Console.WriteLine($"  ok  {name}.png");
                ok++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  FAIL {name}: {ex}");
            }
        }

        Console.WriteLine($"Rendered {ok}/{shots.Length}.");
        return ok == shots.Length ? 0 : 1;
    }

    /// <summary>A MainWindow in screenshot mode, seeded with a sample music library.</summary>
    private static MainWindow SeededMainWindow()
    {
        var window = new MainWindow(screenshotMode: true);
        var vm = window.ViewModel;
        vm.SetItems(SampleLibrary());
        vm.SelectedSidebarItem = vm.LibraryItems.First(i => i.ViewConfigKey == "Music");
        vm.RefreshView();
        vm.UpdateData();
        return window;
    }

    /// <summary>A MainWindow showing an inserted audio CD - before (generic tracks) or
    /// after (MusicBrainz titles/artist/album/year) metadata lookup.</summary>
    private static MainWindow SeededCd(bool metadata)
    {
        var window = new MainWindow(screenshotMode: true);
        var vm = window.ViewModel;
        var tracks = CdTracks("D:", metadata);

        vm.SetItems(tracks);
        foreach (var t in tracks)
        {
            vm.CdTrackList.Add(t);
        }

        // After MusicBrainz resolves the disc, the info bar shows the matched release -
        // here a Mandie NRG single, with HER cover paired to HER title (never borrowed).
        if (metadata)
        {
            vm.CurrentCdInfo = new CdInfo
            {
                CoverArt = EurobeatCover("boom-boom-love-me.png"),
                Album = "Boom Boom Love Me",
                Artist = "Mandie NRG",
                Year = 2023,
                Genre = "Eurobeat",
                TrackCount = tracks.Count,
                TotalDuration = tracks.Aggregate(TimeSpan.Zero, (sum, t) => sum + (t.Duration ?? TimeSpan.Zero)),
                DiscId = "kA0p9eQh7s.Hd2bN",
                ReleaseMbid = "8f3a1c92-eurobeat",
            };
        }

        var item = new SidebarItem
        {
            Name = metadata ? "Boom Boom Love Me" : "Audio CD (D:)",
            Icon = "fa-solid fa-compact-disc",
            Category = "DEVICES",
            IsEnabled = true,
            ViewConfigKey = "CdAudio",
        };
        vm.DeviceItems.Add(item);
        vm.SelectedSidebarItem = item;   // selection re-runs the filter
        return window;
    }

    /// <summary>
    /// A sync in progress. The LCD's busy state is the whole point of this shot - the previous
    /// version only added a sidebar row, which made it identical to device-library.
    /// </summary>
    private static MainWindow SeededDeviceSync()
    {
        var window = SeededDeviceLibrary();
        var vm = window.ViewModel;

        vm.BusyTitle = "Syncing OrgZ iPod";
        vm.BusyDetail = "Copying \u201cDriver's High (Extended)\u201d - 7 of 26";
        vm.BusyPercent = 0.27;
        vm.IsBusy = true;
        return window;
    }

    /// <summary>A MainWindow mid-rip: CD view with the LCD showing rip progress.</summary>
    private static MainWindow SeededRip()
    {
        var window = SeededCd(metadata: true);
        var vm = window.ViewModel;
        vm.BusyTitle = "Importing “Boom Boom Love Me (Acappella)”";
        vm.BusyDetail = "Track 3 of 5 — Time remaining: 0:48 (9.1×)";
        vm.BusyPercent = 0.52;
        vm.IsBusy = true;
        return window;
    }

    /// <summary>Fake CD TOC. With metadata = post-MusicBrainz; without = raw "Track NN".</summary>
    private static List<MediaItem> CdTracks(string driveId, bool withMetadata)
    {
        var titles = new[] { "Boom Boom Love Me (Extended)", "Boom Boom Love Me (Instrumental)", "Boom Boom Love Me (Acappella)", "Boom Boom Love Me (Karaoke)", "Boom Boom Love Me (Mini Mix)" };
        var durations = new[] { (5, 42), (5, 42), (4, 18), (4, 30), (2, 55) };

        var list = new List<MediaItem>();
        for (int i = 0; i < durations.Length; i++)
        {
            var n = i + 1;
            list.Add(new MediaItem
            {
                Id = $"cd:{driveId}:{n}",
                Kind = MediaKind.Music,
                Source = "cdda",
                Title = withMetadata ? titles[i] : $"Track {n:D2}",
                Artist = withMetadata ? "Mandie NRG" : null,
                Album = withMetadata ? "Boom Boom Love Me" : null,
                Year = withMetadata ? 2023u : null,
                Genre = withMetadata ? "Eurobeat" : null,
                Track = (uint)n,
                TotalTracks = (uint)durations.Length,
                Duration = new TimeSpan(0, durations[i].Item1, durations[i].Item2),
            });
        }
        return list;
    }

    /// <summary>Now-playing LCD with a loaded track + generated album art.</summary>
    private static MainWindow SeededNowPlaying()
    {
        var window = new MainWindow(screenshotMode: true);
        var vm = window.ViewModel;
        var lib = SampleLibrary();
        lib.First(t => t.Title == "The Beat Online (Extended Version)").IsPlaying = true;
        vm.SetItems(lib);
        vm.RefreshView();
        vm.UpdateData();
        ApplyNowPlaying(vm, EurobeatCover("the-beat-online.png"),
            "The Beat Online (Extended Version)", "Mandie NRG feat. DJ Nine — The Beat Online", 138_000, 348_000);
        return window;
    }

    /// <summary>Radio view with seeded stations.</summary>
    private static MainWindow SeededRadio()
    {
        var window = new MainWindow(screenshotMode: true);
        var vm = window.ViewModel;
        vm.SetItems(SampleRadio());
        vm.SelectedSidebarItem = vm.LibraryItems.First(i => i.ViewConfigKey == "Radio");
        return window;
    }

    /// <summary>
    /// The audio output picker, populated from a fixed device list rather than whatever
    /// hardware and AirPlay receivers are on the machine taking the shot. The flyout's panel
    /// is hosted directly - a flyout renders nothing until it is opened.
    /// </summary>
    private static Window SeededOutputPicker()
    {
        var manager = new OrgZ.Services.AudioOutput.AudioOutputManager();
        manager.UseOnlyProvidersForScreenshots(
            new SampleOutputProvider("system", "System Audio",
                ("local-default", "Speakers (Realtek High Definition Audio)", true),
                ("local-optical", "Digital Output (S/PDIF)", false)),
            new SampleOutputProvider("airplay", "AirPlay",
                ("airplay-living", "Living Room", false),
                ("airplay-kitchen", "Kitchen", false),
                ("airplay-office", "Office HomePod", false)));

        var panel = new StackPanel { Spacing = 4, Margin = new Thickness(6, 6, 18, 6) };
        OrgZ.Services.AudioOutput.AudioOutputFlyoutHelper.Populate(manager, panel);

        return Host(panel);
    }

    /// <summary>Another OrgZ library mounted from the network, with its playlists.</summary>
    private static MainWindow SeededSharing()
    {
        var window = new MainWindow(screenshotMode: true);
        var vm = window.ViewModel;

        var share = new Services.Sharing.DiscoveredShare("Studio Library", "studio.local", 7391, "10.0.0.24");
        var source = $"share:{share.Key}";

        // Share tracks are namespaced by source, which is what the share view filters on.
        var tracks = SampleLibrary()
            .Where(i => i.Kind == MediaKind.Music)
            .Select(t => new MediaItem
            {
                Id = $"{source}/{t.Id}",
                Kind = MediaKind.Music,
                Title = t.Title,
                Artist = t.Artist,
                Album = t.Album,
                Year = t.Year,
                Genre = t.Genre,
                Track = t.Track,
                TotalTracks = t.TotalTracks,
                Duration = t.Duration,
                Extension = t.Extension,
                Source = source,
                StreamUrl = $"{share.BaseUrl}/stream",
                IsAnalyzed = true,
            })
            .ToList();

        var playlists = new List<Services.Sharing.ShareDiscovery.SharePlaylist>
        {
            new("Favorites", tracks.Take(9).Select(t => t.Id).ToList(), "favorites"),
            new("Night Drive", tracks.Skip(4).Take(7).Select(t => t.Id).ToList()),
        };

        vm.SetItems([]);
        vm.MountShareForScreenshots(share, tracks, playlists);
        vm.SelectedSidebarItem = vm.ShareItems.First();
        vm.UpdateData();
        return window;
    }

    /// <summary>
    /// A connected iPod SELECTED, so the grid shows the device's own tracks. Device rows are
    /// namespaced by source, which is what the device view filters on - without that the grid
    /// renders the local library and the shot is indistinguishable from library-overview.
    /// </summary>
    private static MainWindow SeededDeviceLibrary()
    {
        var window = new MainWindow(screenshotMode: true);
        var vm = window.ViewModel;

        const string mount = "/Volumes/ORGZ IPOD";
        var source = $"device:{mount}";

        var deviceTracks = SampleLibrary()
            .Where(i => i.Kind == MediaKind.Music)
            .Select(t => new MediaItem
            {
                Id = $"{source}/{t.Id}",
                Kind = MediaKind.Music,
                Title = t.Title,
                Artist = t.Artist,
                Album = t.Album,
                Year = t.Year,
                Genre = t.Genre,
                Track = t.Track,
                TotalTracks = t.TotalTracks,
                Duration = t.Duration,
                Extension = t.Extension,
                Source = source,
                FilePath = $"{mount}/iPod_Control/Music/F00/{t.Track:00}.mp3",
                IsAnalyzed = true,
            })
            .ToList();

        ListViewConfigs.Register($"Device:{mount}", ListViewConfigs.BuildDeviceConfig(mount));
        vm.SetItems(deviceTracks);

        var item = new SidebarItem
        {
            Name = "OrgZ iPod",
            Icon = "fa-solid fa-music",
            Category = "DEVICES",
            IsEnabled = true,
            ViewConfigKey = $"Device:{mount}",
        };

        vm.DeviceItems.Add(item);
        vm.SelectedSidebarItem = item;
        vm.UpdateData();
        return window;
    }

    /// <summary>The Settings dialog opened on a named tab.</summary>
    private static Window SettingsTab(string header)
    {
        var dialog = new SettingsDialog();
        dialog.SelectTabForScreenshots(header);
        return dialog;
    }

    /// <summary>The play queue open beside the library.</summary>
    private static MainWindow SeededQueue()
    {
        var window = new MainWindow(screenshotMode: true);
        var vm = window.ViewModel;
        var library = SampleLibrary();

        vm.SetItems(library);
        vm.SelectedSidebarItem = vm.LibraryItems.First(i => i.ViewConfigKey == "Music");
        vm.RefreshView();

        var queue = library.Where(i => i.Kind == MediaKind.Music).ToList();
        vm.SeedQueueForScreenshots(queue, queue[0]);
        ApplyNowPlaying(vm, EurobeatCover("the-beat-online.png"),
            "The Beat Online (Extended Version)", "Mandie NRG feat. DJ Nine", 128_000, 348_000);
        vm.IsQueueVisible = true;
        vm.UpdateData();
        return window;
    }

    /// <summary>Radio view with the filter panel open.</summary>
    private static MainWindow SeededRadioFilters()
    {
        var window = new MainWindow(screenshotMode: true);
        var vm = window.ViewModel;
        vm.SetItems(SampleRadio());
        vm.SelectedSidebarItem = vm.LibraryItems.First(i => i.ViewConfigKey == "Radio");
        vm.UpdateData();
        return window;
    }

    /// <summary>The library filtered by a search term.</summary>
    private static MainWindow SeededSearch()
    {
        var window = new MainWindow(screenshotMode: true);
        var vm = window.ViewModel;
        vm.SetItems(SampleLibrary());
        vm.SelectedSidebarItem = vm.LibraryItems.First(i => i.ViewConfigKey == "Music");
        vm.SearchText = "driver";
        vm.RefreshView();
        vm.UpdateData();
        return window;
    }

    /// <summary>The burn confirmation, sized for an audio CD's worth of tracks.</summary>
    private static Window SeededBurnDialog()
        => new BurnDiscDialog(
            ["I:", "F:"],
            trackCount: 14,
            totalLength: TimeSpan.FromMinutes(58) + TimeSpan.FromSeconds(12),
            totalDataBytes: 612_000_000,
            initialTitle: "Night Drive",
            probeAsync: null);

    /// <summary>Get Info for one track of the sample library.</summary>
    private static Window SeededMediaInfo()
    {
        var library = SampleLibrary();
        var track = library.First(i => i.Album == "Driver's High");
        return new MediaInfoDialog(track, library);
    }

    /// <summary>Sync settings for a device that supports everything.</summary>
    private static Window SeededSyncSettings()
        => new SyncSettingsDialog(
            "OrgZ iPod",
            supportsPodcasts: true,
            supportsAudiobooks: true,
            supportsPlaylists: true,
            playlists: [(1, "Night Drive"), (2, "Eurobeat"), (3, "Workout")],
            current: new SyncPlan());

    /// <summary>The empty library - what a first run looks like before a folder is chosen.</summary>
    private static MainWindow SeededFirstLaunch()
    {
        var window = new MainWindow(screenshotMode: true);
        var vm = window.ViewModel;
        vm.SetItems([]);

        // Explicit: the window restores the last view from settings, and an earlier shot in the
        // run leaves that pointing at whatever it selected - which also puts the audiobook store
        // on the network mid-capture.
        vm.SelectedSidebarItem = vm.LibraryItems.First(i => i.ViewConfigKey == "Music");
        vm.RefreshView();
        vm.UpdateData();
        return window;
    }

    /// <summary>Podcasts view, showing subscribed shows. Invented feeds - nothing real is named.</summary>
    private static MainWindow SeededPodcasts()
    {
        var window = new MainWindow(screenshotMode: true);
        var vm = window.ViewModel;

        vm.Podcasts.Subscriptions.Clear();
        var shows = new (string Title, string Author, string Description)[]
        {
            ("Eurobeat After Dark", "Mandie NRG", "Long-form mixes and the stories behind them, every Friday night."),
            ("Akina Pass Weekly", "Kaiju Red Alarm", "Two hosts argue about downhill anthems and BPM."),
            ("The 175 Club", "DJ Nine", "One track, one episode, taken apart bar by bar."),
            ("Para Para Practice", "Neon Expressway", "Choreography breakdowns for the Tokyo club circuit."),
            ("Super Euro History", "Velocity 175", "How a genre built in Italy conquered Japanese car culture."),
        };

        for (var i = 0; i < shows.Length; i++)
        {
            vm.Podcasts.Subscriptions.Add(new PodcastSubscription
            {
                FeedId = 500 + i,
                Title = shows[i].Title,
                Author = shows[i].Author,
                Description = shows[i].Description,
                ImageUrl = PodcastArtwork[i].Url,
                SubscribedAt = new DateTime(2026, 1, 12 + i, 9, 0, 0, DateTimeKind.Utc),
                LastCheckedAt = new DateTime(2026, 8, 24, 18, 30, 0, DateTimeKind.Utc),
            });
        }

        vm.Podcasts.CurrentView = PodcastsView.Subscriptions;
        vm.SelectedSidebarItem = vm.LibraryItems.First(i => i.ViewConfigKey == "Podcasts");
        return window;
    }

    /// <summary>Audiobooks view, owned shelf. Invented titles in the same theme as the music.</summary>
    private static MainWindow SeededAudiobooks()
    {
        var window = new MainWindow(screenshotMode: true);
        var vm = window.ViewModel;

        // Selection first: it runs RefreshOwned, which reads the (empty) store and would
        // clear anything seeded before it.
        vm.SelectedSidebarItem = vm.LibraryItems.First(i => i.ViewConfigKey == "Audiobooks");
        Dispatcher.UIThread.RunJobs();

        vm.Audiobooks.OwnedBooks.Clear();
        var books = new (string Title, string Author, int Chapters, int Hours, int Mins)[]
        {
            ("Night Of Fire: An Oral History", "Mandie NRG", 14, 9, 12),
            ("Downhill: Notes From Akina Pass", "DJ Nine", 22, 13, 40),
            ("The Beat Online", "Kaiju Red Alarm", 9, 5, 55),
            ("Three Minutes At 175 BPM", "Velocity 175", 18, 11, 6),
        };

        // Also as library items, so the status bar's count agrees with the shelf.
        var media = new List<MediaItem>();

        foreach (var b in books)
        {
            vm.Audiobooks.OwnedBooks.Add(new OwnedBook
            {
                BookFolder = SamplePath(b.Author, "Audiobooks", 1, b.Title),
                Title = b.Title,
                Author = b.Author,
                ChapterCount = b.Chapters,
                TotalDuration = new TimeSpan(b.Hours, b.Mins, 0),
                IsDownloaded = true,
            });

            media.Add(new MediaItem
            {
                Id = $"audiobook:{b.Title}",
                Kind = MediaKind.Audiobook,
                Title = b.Title,
                Artist = b.Author,
                Album = b.Title,
                Duration = new TimeSpan(b.Hours, b.Mins, 0),
                Extension = ".m4b",
                FilePath = SamplePath(b.Author, "Audiobooks", 1, b.Title),
                FileName = $"{b.Title}.m4b",
                FileSize = SampleSize(b.Hours * 60 + b.Mins, 0) / 12,
                IsAnalyzed = true,
            });
        }

        vm.SetItems(media);
        vm.UpdateData();

        vm.Audiobooks.CurrentView = AudiobooksView.Owned;
        return window;
    }

    /// <summary>Favorites view - the sample library, all starred.</summary>
    private static MainWindow SeededFavorites()
    {
        var window = new MainWindow(screenshotMode: true);
        var vm = window.ViewModel;
        vm.SetItems(SampleFavorites());
        vm.SelectedSidebarItem = vm.PlaylistItems.First(i => i.ViewConfigKey == "Favorites");
        vm.UpdateData();
        return window;
    }

    /// <summary>A playlist selected in the sidebar, showing its tracks in playlist order.</summary>
    private static MainWindow SeededPlaylists()
    {
        var window = new MainWindow(screenshotMode: true);
        var vm = window.ViewModel;

        var tracks = SampleFavorites();
        vm.SetItems(tracks);

        // Written to the scratch database so the header, the track list and the sidebar all
        // read through the real code path.
        MediaCache.UpsertMusicBatch(tracks);
        SeedPlaylist("Night Drive", tracks.Take(6));
        SeedPlaylist("Eurobeat", tracks);
        SeedPlaylist("Workout", tracks.Skip(2).Take(5));

        vm.ReloadPlaylistsForScreenshots();
        vm.SelectedSidebarItem = vm.PlaylistItems.First(i => i.Name == "Eurobeat");
        vm.UpdateData();
        return window;

        static void SeedPlaylist(string name, IEnumerable<MediaItem> items)
        {
            var id = MediaCache.CreatePlaylist(name, "M3U8", $@"D:\Music\{name}.m3u8");
            MediaCache.ReplacePlaylistTracks(id, items.Select(t => t.Id));
        }
    }

    /// <summary>The mini-player window, bound to a now-playing view model.</summary>
    private static Window SeededMiniPlayer()
    {
        var main = new MainWindow(screenshotMode: true);
        var vm = main.ViewModel;
        vm.SetItems(SampleLibrary());
        vm.RefreshView();
        ApplyNowPlaying(vm, EurobeatCover("drivers-high.png"),
            "Driver's High (Extended)", "Mandie NRG — Driver's High", 96_000, 330_000);
        return new MiniPlayerWindow { DataContext = vm };
    }

    /// <summary>Drives the now-playing LCD/transport properties (all public on the VM).</summary>
    private static void ApplyNowPlaying(MainWindowViewModel vm, Bitmap art, string line1, string line2, long timeMs, long durationMs)
    {
        vm.CurrentAlbumArt = art;
        vm.CurrentTrackLine1 = line1;
        vm.CurrentTrackLine2 = line2;
        vm.CurrentTrackDurationNumber = durationMs;
        vm.CurrentTrackTimeNumber = timeMs;
        vm.CurrentTrackTime = TimeSpan.FromMilliseconds(timeMs).ToString(@"mm\:ss");
        vm.CurrentTrackDuration = TimeSpan.FromMilliseconds(durationMs).ToString(@"mm\:ss");
        vm.IsSeekEnabled = true;
        vm.ShowPlayingState();
    }

    // -- Sample data -------------------------------------------------------
    //
    // The sample library is real Eurobeat. Cover art shown in the screenshots is the
    // artists' OWN work - Mandie NRG / DJ Nine digital singles - used with their
    // permission, and only ever paired with their own track/title (never borrowed under
    // another artist's name). The wider track-list sampling is Eurobeat song metadata
    // from eurobeat.online ("The Super Euro Database"), used under CC BY 4.0
    // ("Data provided by eurobeat.online"); no third-party cover art is displayed.

    /// <summary>Loads one of the bundled Eurobeat cover PNGs (the artists' own releases).</summary>
    private static Bitmap EurobeatCover(string file)
        => new(Path.Combine(FindRepoRoot(), "tools", "docs-screenshots", "assets", "eurobeat", file));

    private static List<MediaItem> SampleRadio()
    {
        var data = new (string Name, string Genre, string Country, string Cc, int Bitrate, int Votes)[]
        {
            ("Nightdrive FM", "eurobeat", "Germany", "DE", 128, 5210),
            ("Hyper Techno Tokyo", "hyper techno", "Japan", "JP", 256, 9120),
            ("Kaiju Red Alert Radio", "eurobeat", "Japan", "JP", 128, 7640),
            ("Autobahn Beat", "eurobeat", "Germany", "DE", 192, 8830),
            ("Super Euro Channel", "eurobeat", "Italy", "IT", 128, 11200),
            ("Akina Pass FM", "eurodance", "Japan", "JP", 128, 9950),
            ("Velocity 175", "eurobeat", "Italy", "IT", 320, 12400),
            ("Neon Expressway", "synthwave", "Canada", "CA", 128, 6310),
            ("Para Para Station", "parapara", "Japan", "JP", 128, 13800),
            ("Rising Sun Energy", "eurobeat", "Australia", "AU", 128, 4520),
            ("Midnight Drift", "eurobeat", "United Kingdom", "GB", 128, 3990),
            ("Bassline Overdrive", "hardcore", "Netherlands", "NL", 192, 5870),
        };

        var list = new List<MediaItem>();
        foreach (var s in data)
        {
            list.Add(new MediaItem
            {
                Id = $"radio:{s.Name}",
                Kind = MediaKind.Radio,
                Title = s.Name,
                Tags = s.Genre,
                Country = s.Country,
                CountryCode = s.Cc,
                Bitrate = s.Bitrate,
                Codec = "audio/mpeg",
                Votes = s.Votes,
                StreamUrl = "https://example.invalid/stream",
            });
        }
        return list;
    }

    private static List<MediaItem> SampleFavorites()
    {
        var lib = SampleLibrary();
        foreach (var t in lib)
        {
            t.IsFavorite = true;
        }
        return lib;
    }

    /// <summary>
    /// Real Eurobeat. Mandie NRG / DJ Nine albums carry their own cover art (shown in the
    /// now-playing / CD / mini-player surfaces); the "Super Eurobeat" and Japanese sampler
    /// albums fill the grid with popular titles (metadata via eurobeat.online, CC BY 4.0)
    /// and exercise the DataGrid's CJK rendering. No borrowed cover art is displayed.
    /// </summary>
    private static List<MediaItem> SampleLibrary()
    {
        var items = new List<MediaItem>();

        // Single artist across the album; HasAlbumArt true only where we actually hold the cover.
        void Album(string artist, string album, uint year, string genre, bool hasArt, params (string Title, int Mins, int Secs, int Rating)[] tracks)
        {
            for (int i = 0; i < tracks.Length; i++)
            {
                var t = tracks[i];
                items.Add(new MediaItem
                {
                    Id = $"{artist}/{album}/{i + 1}",
                    Kind = MediaKind.Music,
                    Title = t.Title,
                    Artist = artist,
                    Album = album,
                    Year = year,
                    Genre = genre,
                    Track = (uint)(i + 1),
                    TotalTracks = (uint)tracks.Length,
                    Duration = new TimeSpan(0, t.Mins, t.Secs),
                    Rating = t.Rating > 0 ? t.Rating : null,
                    Extension = ".flac",
                    // A track with no path is not a library file - it is dropped by the
                    // playlist/favorites lookups, which read counts and sizes off real files.
                    FilePath = SamplePath(artist, album, i + 1, t.Title),
                    FileName = $"{i + 1:00} - {t.Title}.flac",
                    FileSize = SampleSize(t.Mins, t.Secs),
                    HasAlbumArt = hasArt,
                    IsAnalyzed = true,
                });
            }
        }

        // Per-track artist (compilation-style). No cover art is shown for these in the grid.
        void Various(string album, uint year, string genre, params (string Artist, string Title, int Mins, int Secs, int Rating)[] tracks)
        {
            for (int i = 0; i < tracks.Length; i++)
            {
                var t = tracks[i];
                items.Add(new MediaItem
                {
                    Id = $"{album}/{i + 1}",
                    Kind = MediaKind.Music,
                    Title = t.Title,
                    Artist = t.Artist,
                    Album = album,
                    Year = year,
                    Genre = genre,
                    Track = (uint)(i + 1),
                    TotalTracks = (uint)tracks.Length,
                    Duration = new TimeSpan(0, t.Mins, t.Secs),
                    Rating = t.Rating > 0 ? t.Rating : null,
                    Extension = ".flac",
                    FilePath = SamplePath(t.Artist, album, i + 1, t.Title),
                    FileName = $"{i + 1:00} - {t.Title}.flac",
                    FileSize = SampleSize(t.Mins, t.Secs),
                    HasAlbumArt = false,
                    IsAnalyzed = true,
                });
            }
        }

        // -- Mandie NRG / DJ Nine - their own releases (cover art cleared for display) --
        Album("Mandie NRG feat. DJ Nine", "The Beat Online", 2023, "Eurobeat", hasArt: true,
            ("The Beat Online (Extended Version)", 5, 48, 5),
            ("The Beat Online (Instrumental Version)", 5, 48, 0),
            ("The Beat Online (Acappella Version)", 4, 30, 0),
            ("The Beat Online (DJ Nine's Radio Edit)", 3, 42, 4));

        // -- Japanese-script titles - DataGrid CJK showcase (album name in JP too).
        //    Placed high so it's visible in the library screenshot. --
        Various("ユーロビート・ベスト", 2019, "Eurobeat",
            ("Key-A-Kiss", "デラックス", 4, 2, 4),
            ("越田Rute隆人, あき", "Scream Out!", 3, 58, 5),
            ("Queue", "愛してる", 4, 14, 0),
            ("MAX", "あの夏へと", 4, 7, 0),
            ("橘花音", "劫火の華", 4, 22, 5));

        Album("Mandie NRG", "Driver's High", 2024, "Eurobeat", hasArt: true,
            ("Driver's High (Extended)", 5, 30, 5),
            ("Driver's High (Instrumental)", 5, 30, 0),
            ("Driver's High (Acappella)", 4, 12, 0),
            ("Driver's High (Last Version)", 4, 5, 0));

        Album("Mandie NRG", "Tokyo Clash (Kaiju Red Alarm)", 2026, "Eurobeat", hasArt: true,
            ("Tokyo Clash (Kaiju Red Alarm) (Extended)", 5, 55, 4),
            ("Tokyo Clash (Kaiju Red Alarm) (Instrumental)", 5, 55, 0),
            ("Tokyo Clash (Kaiju Red Alarm) (Acappella)", 4, 20, 0));

        // -- Popular Super Eurobeat (metadata via eurobeat.online, CC BY 4.0) --
        Various("Super Eurobeat", 2001, "Eurobeat",
            ("Niko", "Night Of Fire", 5, 1, 5),
            ("Dave Rodgers", "Deja Vu", 4, 36, 5),
            ("Domino", "Tora Tora Tora", 4, 12, 4),
            ("Lolita", "Try Me (I Need To Be Needed)", 4, 48, 0),
            ("Mega NRG Man", "Seventies", 4, 20, 0),
            ("Go Go Girls", "One Night In Arabia", 4, 5, 4),
            ("Cherry", "Yesterday", 4, 31, 0),
            ("Virginelle", "Fantasy", 4, 16, 0),
            ("DJ NRG", "Kamikaze", 4, 50, 0),
            ("Edo Boys", "No One Sleep In Tokyo", 4, 9, 5));

        return items;
    }

    /// <summary>
    /// Pre-fills RemoteImage's disk cache so podcast and store artwork renders without a
    /// network fetch. The cache is checked before any request and keyed on a SHA-1 of the URL,
    /// so writing the licensed covers under those names makes the shots offline-deterministic.
    /// </summary>
    private static void SeedRemoteImageCache(string root)
    {
        var dir = Path.Combine(root, ".podcasts", "images");
        Directory.CreateDirectory(dir);

        foreach (var (url, file) in PodcastArtwork)
        {
            var hash = Convert.ToHexString(
                System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(url))).ToLowerInvariant();

            File.Copy(
                Path.Combine(FindRepoRoot(), "tools", "docs-screenshots", "assets", "eurobeat", file),
                Path.Combine(dir, hash + ".png"),
                overwrite: true);
        }
    }

    /// <summary>Fake artwork URLs paired with the covers we actually hold the rights to.</summary>
    private static readonly (string Url, string File)[] PodcastArtwork =
    [
        ("https://art.invalid/eurobeat-after-dark.png", "the-beat-online.png"),
        ("https://art.invalid/akina-pass-weekly.png", "tokyo-clash.png"),
        ("https://art.invalid/the-175-club.png", "drivers-high.png"),
        ("https://art.invalid/para-para-practice.png", "boom-boom-love-me.png"),
        ("https://art.invalid/super-euro-history.png", "the-beat-online.png"),
    ];

    /// <summary>A plausible library path for a mock track. Nothing reads the file - the path
    /// is what marks the item as a library file rather than a stream.</summary>
    private static string SamplePath(string artist, string album, int track, string title)
    {
        var root = OperatingSystem.IsWindows() ? @"D:\Music" : "/home/orgz/Music";
        var safe = new string(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c).ToArray());
        return Path.Combine(root, artist, album, $"{track:00} - {safe}.flac");
    }

    /// <summary>Roughly what a FLAC of that length weighs, so size totals read sensibly.</summary>
    private static long SampleSize(int mins, int secs) => (long)((mins * 60 + secs) * 900_000L / 8);

    /// <summary>Wraps a bare control in a transparent host window so it can be rendered.</summary>
    private static Window Host(Control content)
        => new() { Content = content, Background = Brushes.Transparent };

    /// <summary>A fake but realistic iPod for the device info bar - no personal data.</summary>
    private static ConnectedDevice SampleIPod()
    {
        var d = new ConnectedDevice
        {
            MountPath = OperatingSystem.IsWindows() ? "E:\\" : "/media/orgz/IPOD",
            DeviceType = DeviceType.StockIPod,
            Name = "OrgZ iPod",
        };
        d.Model = "iPod Classic (6th gen)";
        d.IpodGeneration = "Classic 6G";          // has a bundled product image
        d.Serial = "9X930ABC2QX";
        d.AppleFirmwareVersion = "iPod OS 1.1.2";
        d.Format = "FAT32";
        d.TotalSpace = 80_000_000_000;
        d.AudioSpace = 52_400_000_000;
        d.FreeSpace = 26_100_000_000;
        return d;
    }

    private static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont()
            .WithIcons();

    private static void Capture(Window window, double width, double height, string outPath)
    {
        if (height > 0)
        {
            window.SizeToContent = SizeToContent.Manual;
            window.Width = width;
            window.Height = height;
        }
        else if (width > 0)
        {
            window.SizeToContent = SizeToContent.Height;
            window.Width = width;
        }

        window.Show();

        // Drop focus before rendering. A focused TextBox draws a BLINKING caret, so a dialog
        // with an input in it produced a different PNG depending on which half of the blink
        // the capture landed in - the Burn Disc shot churned by one 1x17px column every run.
        // No ClearFocus in this Avalonia; focusing the window itself moves it off the input.
        window.FocusManager?.Focus(null);

        // Pump layout/render until it settles. A single pass is not enough for anything that
        // loads on a background thread and posts its result back - the playlist header reads
        // its counts and cover tiles through Task.Run, and capturing after one RunJobs caught
        // it before the continuation had even been queued.
        for (var pass = 0; pass < 12; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(40);
        }

        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();

        var frame = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("CaptureRenderedFrame returned null");
        frame.Save(outPath);

        window.Close();
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "mkdocs.yml")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        return dir ?? Directory.GetCurrentDirectory();
    }
}

/// <summary>Fixed speakers for the output-picker shot - no real hardware, no LAN scan.</summary>
internal sealed class SampleOutputProvider(
    string providerId,
    string providerName,
    params (string Id, string Name, bool IsDefault)[] devices) : OrgZ.Services.AudioOutput.IAudioSinkProvider
{
    public string ProviderId => providerId;

    public string ProviderName => providerName;

    public bool IsSupported => true;

    public event EventHandler? DevicesChanged { add { } remove { } }

    public IReadOnlyList<OrgZ.Services.AudioOutput.AudioDeviceInfo> EnumerateDevices() =>
        [.. devices.Select(d => new OrgZ.Services.AudioOutput.AudioDeviceInfo
        {
            DeviceId = d.Id,
            DisplayName = d.Name,
            ProviderId = providerId,
            ProviderName = providerName,
            IsDefault = d.IsDefault,
        })];

    public OrgZ.Services.AudioOutput.IAudioSink CreateSink(OrgZ.Services.AudioOutput.AudioDeviceInfo device)
        => throw new NotSupportedException("The screenshot harness never plays audio.");
}
