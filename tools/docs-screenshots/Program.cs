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

        var item = new SidebarItem
        {
            Name = metadata ? "Midnight Cartography" : "Audio CD (D:)",
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

        var activity = vm.AddActivity("Sending “Road Trip” to OrgZ iPod");
        activity.Status = ActivityStatus.Completed;
        activity.Detail = "14 tracks written";
        vm.UpdateActivityBadge();
        vm.IsActivityPanelVisible = true;
        return window;
    }

    /// <summary>A MainWindow mid-rip: CD view with the LCD showing rip progress.</summary>
    private static MainWindow SeededRip()
    {
        var window = SeededCd(metadata: true);
        var vm = window.ViewModel;
        vm.RipTitle = "Importing “Dead Reckoning”";
        vm.RipDetail = "Track 3 of 8 — Time remaining: 1:12 (8.4×)";
        vm.RipPercent = 0.36;
        vm.IsRipping = true;
        return window;
    }

    /// <summary>Fake CD TOC. With metadata = post-MusicBrainz; without = raw "Track NN".</summary>
    private static List<MediaItem> CdTracks(string driveId, bool withMetadata)
    {
        var titles = new[] { "Compass Rose", "Latitude", "Dead Reckoning", "Meridian", "Cartographer", "The Open Sea", "Sextant", "Landfall" };
        var durations = new[] { (4, 3), (3, 51), (5, 12), (4, 28), (3, 9), (6, 2), (4, 44), (5, 20) };

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
                Artist = withMetadata ? "Atlas Bloom" : null,
                Album = withMetadata ? "Midnight Cartography" : null,
                Year = withMetadata ? 2022u : null,
                Genre = withMetadata ? "Electronic" : null,
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
        lib.First(t => t.Title == "Afterglow").IsPlaying = true;
        vm.SetItems(lib);
        vm.RefreshView();
        vm.UpdateData();
        ApplyNowPlaying(vm, FakeAlbumArt(2), "Afterglow", "Glass Harbor — Neon Tides", 101_000, 281_000);
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
        ApplyNowPlaying(vm, FakeAlbumArt(3), "Cartography", "The Slow Mornings — Paper Lanterns", 78_000, 242_000);
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

    // -- Fake data ---------------------------------------------------------

    private static readonly (string Bg, string[] Accents)[] _palettes =
    {
        ("#1B2A4A", new[] { "#FF6B6B", "#4ECDC4", "#FFE66D" }),
        ("#2D1B3D", new[] { "#E84855", "#F9DC5C", "#3185FC" }),
        ("#0B3D2E", new[] { "#F4D35E", "#EE964B", "#F95738" }),
        ("#3A0CA3", new[] { "#F72585", "#4CC9F0", "#B5179E" }),
    };

    /// <summary>
    /// Generates abstract album art: solid-color shapes at random positions, some
    /// bleeding off the clipped edges. Deterministic per seed; no real artwork.
    /// </summary>
    private static Bitmap FakeAlbumArt(int seed)
    {
        var rng = new Random(seed);
        var pal = _palettes[((seed % _palettes.Length) + _palettes.Length) % _palettes.Length];

        var canvas = new Canvas { Width = 320, Height = 320, ClipToBounds = true, Background = Brush.Parse(pal.Bg) };
        int shapes = 3 + rng.Next(2);
        for (int i = 0; i < shapes; i++)
        {
            var color = Color.Parse(pal.Accents[rng.Next(pal.Accents.Length)]);
            double size = 120 + rng.Next(170);
            Control shape = rng.Next(2) == 0
                ? new Avalonia.Controls.Shapes.Ellipse { Width = size, Height = size, Fill = new SolidColorBrush(color, 0.82) }
                : new Avalonia.Controls.Shapes.Rectangle { Width = size, Height = size * (0.5 + rng.NextDouble()), Fill = new SolidColorBrush(color, 0.82), RenderTransform = new RotateTransform(rng.Next(-35, 35)) };
            Canvas.SetLeft(shape, rng.Next(-90, 290));   // negative / large = bleed off the edge
            Canvas.SetTop(shape, rng.Next(-90, 290));
            canvas.Children.Add(shape);
        }

        var rtb = new RenderTargetBitmap(new PixelSize(320, 320), new Vector(96, 96));
        canvas.Measure(new Size(320, 320));
        canvas.Arrange(new Rect(0, 0, 320, 320));
        rtb.Render(canvas);
        return rtb;
    }

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

    /// <summary>Fictional albums/tracks — realistic shape, no personal or real metadata.</summary>
    private static List<MediaItem> SampleLibrary()
    {
        var items = new List<MediaItem>();

        void Album(string artist, string album, uint year, string genre, params (string Title, int Mins, int Secs, int Rating)[] tracks)
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
                    HasAlbumArt = true,
                    IsAnalyzed = true,
                });
            }
        }

        Album("Glass Harbor", "Neon Tides", 2019, "Electronic",
            ("Low Light", 3, 52, 5), ("Undertow", 4, 17, 0), ("Signal Drift", 3, 28, 4),
            ("Harbor Lights", 5, 3, 0), ("Afterglow", 4, 41, 5));

        Album("The Slow Mornings", "Paper Lanterns", 2021, "Indie",
            ("First Light", 3, 9, 0), ("Cartography", 4, 2, 4), ("Paper Lanterns", 3, 47, 5),
            ("Tin Roof", 2, 58, 0), ("The Long Way Home", 5, 22, 4));

        Album("Foxglove Choir", "Granite & Gold", 2023, "Folk",
            ("Quarry Song", 4, 11, 0), ("Goldenrod", 3, 33, 5), ("Granite", 4, 55, 0),
            ("Embers", 3, 20, 4));

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
