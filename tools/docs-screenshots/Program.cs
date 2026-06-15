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

        BuildAvaloniaApp().SetupWithoutStarting();

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
            ("device-sync", 1280, 800, SeededDeviceSync),
            ("now-playing", 1280, 800, SeededNowPlaying),
            ("radio-browser", 1280, 800, SeededRadio),
            ("favorites", 1280, 800, SeededFavorites),
            ("settings", 620, 0, () => new SettingsDialog()),
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
        vm.RefreshView();
        vm.UpdateData();
        return window;
    }

    /// <summary>A MainWindow showing an inserted audio CD — before (generic tracks) or
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

        // After MusicBrainz resolves the disc, the info bar shows the matched release —
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

    /// <summary>A MainWindow showing the result of sending a playlist to a device.</summary>
    private static MainWindow SeededDeviceSync()
    {
        var window = new MainWindow(screenshotMode: true);
        var vm = window.ViewModel;
        vm.SetItems(SampleLibrary());
        vm.RefreshView();
        vm.UpdateData();

        vm.DeviceItems.Add(new SidebarItem
        {
            Name = "OrgZ iPod",
            Icon = "fa-solid fa-music",
            Category = "DEVICES",
            IsEnabled = true,
            ViewConfigKey = "Device:screenshot",
        });

        return window;
    }

    /// <summary>A MainWindow mid-rip: CD view with the LCD showing rip progress.</summary>
    private static MainWindow SeededRip()
    {
        var window = SeededCd(metadata: true);
        var vm = window.ViewModel;
        vm.RipTitle = "Importing “Boom Boom Love Me (Acappella)”";
        vm.RipDetail = "Track 3 of 5 — Time remaining: 0:48 (9.1×)";
        vm.RipPercent = 0.52;
        vm.IsRipping = true;
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

    /// <summary>Favorites view — the sample library, all starred.</summary>
    private static MainWindow SeededFavorites()
    {
        var window = new MainWindow(screenshotMode: true);
        var vm = window.ViewModel;
        vm.SetItems(SampleFavorites());
        vm.SelectedSidebarItem = vm.PlaylistItems.First(i => i.ViewConfigKey == "Favorites");
        vm.UpdateData();
        return window;
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
    // artists' OWN work — Mandie NRG / DJ Nine digital singles — used with their
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
            ("Nightdrive FM", "synthwave", "Germany", "DE", 128, 5210),
            ("KEXP", "alternative rock", "United States", "US", 256, 9120),
            ("Jazz24", "jazz", "United States", "US", 128, 7640),
            ("FIP", "eclectic", "France", "FR", 192, 8830),
            ("SomaFM Groove Salad", "ambient", "United States", "US", 128, 11200),
            ("BBC Radio 6 Music", "alternative", "United Kingdom", "GB", 128, 9950),
            ("Radio Paradise", "rock", "United States", "US", 320, 12400),
            ("Classic FM", "classical", "United Kingdom", "GB", 128, 6310),
            ("Lofi Girl Radio", "lofi hip hop", "France", "FR", 128, 13800),
            ("Triple J", "alternative", "Australia", "AU", 128, 4520),
            ("WWOZ New Orleans", "jazz", "United States", "US", 128, 3990),
            ("NTS Radio 1", "electronic", "United Kingdom", "GB", 192, 5870),
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
                    HasAlbumArt = false,
                    IsAnalyzed = true,
                });
            }
        }

        // -- Mandie NRG / DJ Nine — their own releases (cover art cleared for display) --
        Album("Mandie NRG feat. DJ Nine", "The Beat Online", 2023, "Eurobeat", hasArt: true,
            ("The Beat Online (Extended Version)", 5, 48, 5),
            ("The Beat Online (Instrumental Version)", 5, 48, 0),
            ("The Beat Online (Acappella Version)", 4, 30, 0),
            ("The Beat Online (DJ Nine's Radio Edit)", 3, 42, 4));

        // -- Japanese-script titles — DataGrid CJK showcase (album name in JP too).
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

    /// <summary>Wraps a bare control in a transparent host window so it can be rendered.</summary>
    private static Window Host(Control content)
        => new() { Content = content, Background = Brushes.Transparent };

    /// <summary>A fake but realistic iPod for the device info bar — no personal data.</summary>
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

        // Pump layout/render: run queued jobs, force a render tick, run again so
        // the Skia frame is actually composed before we grab it.
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
