// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OrgZ.Services.Audiobooks;
using OrgZ.Services.DeviceHelper;
using OrgZ.Services.Podcast;
using OrgZ.ViewModels;

namespace OrgZ.Views;

public partial class SettingsDialog : Window
{
    private string _pendingFolderPath;
    private readonly List<MediaItem> _allItems;

    public bool FolderChanged { get; private set; }
    public bool SettingsReset { get; private set; }

    public SettingsDialog() : this([]) { }

    public SettingsDialog(List<MediaItem> allItems)
    {
        InitializeComponent();

        _pendingFolderPath = App.FolderPath;
        _allItems = allItems;

        WindowSizeTracker.Track(this, "Settings");

        LoadSettings();
        PopulateStats();
    }

    private void LoadSettings()
    {
        // General
        FolderPathText.Text = string.IsNullOrEmpty(App.FolderPath) ? "(No folder selected)" : App.FolderPath;

        // Same feature, per-platform vocabulary: Windows trays, macOS menu-bars,
        // Linux notification-areas.
        MinimizeToTrayCheck.Content = OperatingSystem.IsMacOS()
            ? "Keep running in the menu bar on close"
            : OperatingSystem.IsLinux()
                ? "Minimize to notification area on close"
                : "Minimize to system tray on close";
        MinimizeToTrayCheck.IsChecked = Settings.Get("OrgZ.MinimizeToTray", false);
        RememberLastTrackCheck.IsChecked = Settings.Get("OrgZ.RememberLastTrack", false);

        BadFormatShowCheck.IsChecked = Settings.Get("OrgZ.BadFormat.ShowInSidebar", false);
        BadFormatNoTitleCheck.IsChecked = Settings.Get("OrgZ.BadFormat.NoTitle", true);
        BadFormatNoArtistCheck.IsChecked = Settings.Get("OrgZ.BadFormat.NoArtist", true);
        BadFormatNoYearCheck.IsChecked = Settings.Get("OrgZ.BadFormat.NoYear", true);
        BadFormatNoAlbumArtCheck.IsChecked = Settings.Get("OrgZ.BadFormat.NoAlbumArt", true);
        BadFormatLossyCheck.IsChecked = Settings.Get("OrgZ.BadFormat.LossyFormats", true);

        // Playback
        var bufferSize = Settings.Get("OrgZ.StreamingBufferSize", "Medium");
        BufferSizeCombo.SelectedIndex = bufferSize switch
        {
            "Small" => 0,
            "Medium" => 1,
            "Large" => 2,
            "Extra Large" => 3,
            _ => 1
        };

        // NOT "OrgZ.ShuffleMode" - that key is the transport's Off/On toggle. This one is
        // how shuffle orders the queue when it IS on.
        var shuffleBy = Settings.Get("OrgZ.ShuffleBy", ShuffleBy.Song);
        ShuffleSongRadio.IsChecked = shuffleBy == ShuffleBy.Song;
        ShuffleAlbumRadio.IsChecked = shuffleBy == ShuffleBy.Album;

        AutoAdvanceCheck.IsChecked = Settings.Get("OrgZ.AutoAdvance", true);
        NormalizeVolumeCheck.IsChecked = Settings.Get("OrgZ.NormalizeVolume", false);

        // Playback → Mini-Player
        var miniMode = MainWindowViewModel.LoadMiniPlayerMode();
        MiniPlayerModeReplace.IsChecked = miniMode == MiniPlayerMode.Replace;
        MiniPlayerModeSideBySide.IsChecked = miniMode == MiniPlayerMode.SideBySide;

        // Burning
        BurnAudioGapCombo.SelectedIndex = Settings.Get("OrgZ.Burn.AudioGapSeconds", 2) == 0 ? 0 : 1;
        BurnCdTextCheck.IsChecked = Settings.Get("OrgZ.Burn.CdText", true);

        // Services
        ShareNameInput.Text = Settings.Get("OrgZ.Services.Sharing.Name", $"{Environment.MachineName} Library");
        ShareEnabledCheck.IsChecked = Settings.Get("OrgZ.Services.Sharing.Enabled", false);
        _ = RefreshShareStatusAsync();

        ServiceKeepBurningCheck.IsChecked = Settings.Get("OrgZ.Services.KeepAlive.Burning", false);
        ServiceKeepSyncCheck.IsChecked = Settings.Get("OrgZ.Services.KeepAlive.IPodSync", false);
        ServiceKeepSharingCheck.IsChecked = Settings.Get("OrgZ.Services.KeepAlive.Sharing", false);

        // Hidden in Release - and don't probe the service either, since nothing would
        // read the answer. The probe is #if'd rather than guarded on the const so the
        // Release compile doesn't warn about code it can prove unreachable.
        ServiceLifecycleCard.IsVisible = ShowServiceLifecycle;
#if DEBUG
        _ = RefreshServiceStatusAsync();
#endif

        BurnDataFormatCombo.SelectedIndex = Settings.Get("OrgZ.Burn.DataFormat", "original") switch
        {
            "original" => 0,
            "mp3"      => 1,
            "aac"      => 2,
            "alac"     => 3,
            "flac"     => 4,
            "wav"      => 5,
            _          => 0,
        };

        BurnLossyQualityCombo.SelectedIndex = Settings.Get("OrgZ.Burn.LossyQualityKbps", 256) switch
        {
            128 => 0,
            192 => 1,
            256 => 2,
            320 => 3,
            _   => 2,
        };

        // Disc images live inside the library at {library}/.disc-images - not user-configurable.
        BurnImageFolderText.Text = string.IsNullOrEmpty(_pendingFolderPath)
            ? "(Set a music library folder first)"
            : Path.Combine(_pendingFolderPath, ".disc-images");

        // Podcasts
        LoadPodcastSettings();
        LoadPodcastStorage();

        // Advanced
        var dbDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OrgZ");
        DatabasePathText.Text = Path.Combine(dbDir, "library.db");
        SettingsPathText.Text = Settings.GetSettingsFilePath();
    }

    private void LoadPodcastSettings()
    {
        var checkInterval = Settings.Get("OrgZ.Podcasts.CheckInterval", "day");
        PodcastCheckIntervalCombo.SelectedIndex = checkInterval switch
        {
            "hour"   => 0,
            "day"    => 1,
            "week"   => 2,
            "manual" => 3,
            _        => 1,
        };

        var newAction = Settings.Get("OrgZ.Podcasts.NewEpisodeAction", "recent");
        PodcastNewEpisodeActionCombo.SelectedIndex = newAction switch
        {
            "all"    => 0,
            "recent" => 1,
            "none"   => 2,
            _        => 1,
        };

        var keep = Settings.Get("OrgZ.Podcasts.Keep", "all");
        PodcastKeepCombo.SelectedIndex = keep switch
        {
            "all"      => 0,
            "unplayed" => 1,
            "last1"    => 2,
            "last2"    => 3,
            "last5"    => 4,
            "last10"   => 5,
            _          => 0,
        };

        UpdatePodcastNextCheckText();
        PodcastCheckIntervalCombo.SelectionChanged += (_, _) => UpdatePodcastNextCheckText();
    }

    private void UpdatePodcastNextCheckText()
    {
        if (PodcastCheckIntervalCombo.SelectedItem is not ComboBoxItem item)
        {
            PodcastNextCheckText.Text = "";
            return;
        }

        var tag = item.Tag as string ?? "";

        if (tag == "manual")
        {
            PodcastNextCheckText.Text = "Episodes are only fetched when you click Refresh.";
            return;
        }

        var lastCheckRaw = Settings.Get("OrgZ.Podcasts.LastCheck", "");
        var lastCheck = DateTime.TryParse(
            lastCheckRaw,
            null,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out var parsed) ? parsed : DateTime.MinValue;

        var span = tag switch
        {
            "hour" => TimeSpan.FromHours(1),
            "day"  => TimeSpan.FromDays(1),
            "week" => TimeSpan.FromDays(7),
            _      => TimeSpan.FromDays(1),
        };

        var next = lastCheck == DateTime.MinValue ? DateTime.Now : (lastCheck + span).ToLocalTime();
        PodcastNextCheckText.Text = $"Next check: {next:dddd, MMMM d, h:mm tt}";
    }

    private void LoadPodcastStorage()
    {
        var dir = PodcastSettings.DownloadDir(App.FolderPath);
        PodcastDownloadPathText.Text = dir ?? "(No library folder set)";

        // Sizing the folder walks the disk, so compute it off the UI thread.
        PodcastSpaceUsedText.Text = "Space used: calculating…";
        var root = App.FolderPath;
        _ = Task.Run(() =>
        {
            var bytes = PodcastSettings.DownloadBytes(root);
            Dispatcher.UIThread.Post(() =>
                PodcastSpaceUsedText.Text = $"Space used: {PodcastSettings.FormatBytes(bytes)}");
        });
    }

    private void OpenPodcastFolderButton_Click(object? sender, RoutedEventArgs e)
    {
        var dir = PodcastSettings.DownloadDir(App.FolderPath);
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
        }
        catch
        {
            // No file manager available / sandboxed - nothing actionable.
        }
    }

    private async void ClearPodcastDownloadsButton_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new ConfirmDialog(
            "Clear podcast downloads",
            "Delete all downloaded podcast episodes from disk? Your subscriptions and play history are kept.",
            "Delete");
        if (await dialog.ShowDialog<bool>(this) != true)
        {
            return;
        }

        var root = App.FolderPath;
        PodcastSpaceUsedText.Text = "Space used: clearing…";
        await Task.Run(() => PodcastSettings.ClearDownloads(root));
        LoadPodcastStorage();
    }

    private async void RefreshPodcastsButton_Click(object? sender, RoutedEventArgs e)
    {
        if (PodcastSubscriptionService.Instance.IsRefreshing)
        {
            return;
        }

        var original = PodcastRefreshButton.Content;
        PodcastRefreshButton.IsEnabled = false;
        PodcastRefreshButton.Content = "Refreshing…";
        try
        {
            await PodcastSubscriptionService.Instance.RefreshNowAsync(App.FolderPath);
        }
        finally
        {
            PodcastRefreshButton.Content = original;
            PodcastRefreshButton.IsEnabled = true;
            UpdatePodcastNextCheckText();
            LoadPodcastStorage();
        }
    }

    private void PopulateStats()
    {
        var musicItems = _allItems.Where(i => i.Kind == MediaKind.Music).ToList();
        var radioItems = _allItems.Where(i => i.Kind == MediaKind.Radio).ToList();

        // Library Overview
        StatMusicCount.Text = musicItems.Count.ToString("N0");
        StatRadioCount.Text = radioItems.Count.ToString("N0");

        // Music Stats
        var artists = musicItems.Where(f => !string.IsNullOrEmpty(f.Artist)).Select(f => f.Artist).Distinct().Count();
        var albums = musicItems.Where(f => !string.IsNullOrEmpty(f.Album)).Select(f => f.Album).Distinct().Count();
        var totalDuration = TimeSpan.FromTicks(musicItems.Sum(x => x.Duration?.Ticks ?? 0));
        var totalBytes = musicItems.Sum(x => x.FileSize ?? 0);

        StatArtistCount.Text = artists.ToString("N0");
        StatAlbumCount.Text = albums.ToString("N0");
        StatSongCount.Text = musicItems.Count.ToString("N0");
        StatMusicFavorites.Text = musicItems.Count(f => f.IsFavorite).ToString("N0");
        StatTotalDuration.Text = FormatHelper.FormatDurationHuman(totalDuration);
        StatTotalFileSize.Text = FormatHelper.FormatFileSize(totalBytes);

        // File Types (inside Music section)
        var extensionGroups = musicItems
            .Where(f => !string.IsNullOrEmpty(f.Extension))
            .GroupBy(f => f.Extension!.ToUpperInvariant())
            .OrderByDescending(g => g.Count())
            .ToList();

        foreach (var group in extensionGroups)
        {
            var totalSize = group.Sum(f => f.FileSize ?? 0);
            FileTypesPanel.Children.Add(CreateBreakdownRow(group.Key, $"{group.Count():N0} files", FormatHelper.FormatFileSize(totalSize)));
        }

        if (extensionGroups.Count == 0)
        {
            FileTypesPanel.Children.Add(CreateStatLabel("No music files found", 0.4));
        }

        // Radio Stats
        var favorites = radioItems.Count(s => s.IsFavorite);
        var countries = radioItems.Where(s => !string.IsNullOrEmpty(s.Country)).Select(s => s.Country).Distinct().Count();

        StatRadioStations.Text = radioItems.Count.ToString("N0");
        StatRadioFavorites.Text = favorites.ToString("N0");
        StatRadioCountries.Text = countries.ToString("N0");

        // Radio Genres - Tags carries the canonical taxonomy display name
        // (bundled loader and the Add Station dialog both write it).
        var genreGroups = radioItems
            .GroupBy(s => string.IsNullOrWhiteSpace(s.Tags) ? "—" : s.Tags!)
            .OrderByDescending(g => g.Count())
            .ToList();

        foreach (var group in genreGroups)
        {
            RadioSourcesPanel.Children.Add(CreateBreakdownRow(group.Key, $"{group.Count():N0} stations", null));
        }

        if (genreGroups.Count == 0)
        {
            RadioSourcesPanel.Children.Add(CreateStatLabel("No stations", 0.4));
        }

        // Radio Codecs
        var codecGroups = radioItems
            .Where(s => !string.IsNullOrEmpty(s.Codec))
            .GroupBy(s => s.CodecLabel)
            .OrderByDescending(g => g.Count())
            .ToList();

        foreach (var group in codecGroups)
        {
            RadioCodecsPanel.Children.Add(CreateBreakdownRow(group.Key, $"{group.Count():N0} stations", null));
        }

        if (codecGroups.Count == 0)
        {
            RadioCodecsPanel.Children.Add(CreateStatLabel("No codec data", 0.4));
        }

        PopulatePodcastStats();
        PopulateAudiobookStats();
    }

    /// <summary>
    /// Podcast section + overview row. Subscriptions come from the cache; downloaded
    /// episode count and size need a disk walk, so those fill in off the UI thread.
    /// The whole section stays hidden for a library with no podcast footprint.
    /// </summary>
    private void PopulatePodcastStats()
    {
        var subscriptions = PodcastCache.GetSubscriptions().Count;
        StatPodcastSubs.Text = subscriptions.ToString("N0");
        StatPodcastCount.Text = subscriptions.ToString("N0");

        var visible = subscriptions > 0;
        PodcastStatsSection.IsVisible = visible;
        OverviewPodcastLabel.IsVisible = visible;
        OverviewPodcastLink.IsVisible = visible;

        var root = App.FolderPath;
        _ = Task.Run(() =>
        {
            var count = 0;
            long bytes = 0;
            var dir = PodcastSettings.DownloadDir(root);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                try
                {
                    // Only audio counts as an episode - the download root also holds
                    // cached artwork (thousands of PNGs) and aborted .partial files.
                    // Space Used stays all-files: it's the folder's real disk footprint.
                    string[] episodeExtensions = [".mp3", ".m4a", ".ogg", ".flac"];
                    foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        if (episodeExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                        {
                            count++;
                        }

                        try
                        {
                            bytes += new FileInfo(file).Length;
                        }
                        catch (IOException) { }
                    }
                }
                catch (Exception)
                {
                    // Best-effort; the section just keeps its placeholder counts.
                }
            }

            Dispatcher.UIThread.Post(() =>
            {
                StatPodcastDownloaded.Text = count.ToString("N0");
                StatPodcastSpace.Text = PodcastSettings.FormatBytes(bytes);

                // Downloads with zero subscriptions still count as a podcast footprint.
                if (count > 0 && !PodcastStatsSection.IsVisible)
                {
                    PodcastStatsSection.IsVisible = true;
                    OverviewPodcastLabel.IsVisible = true;
                    OverviewPodcastLink.IsVisible = true;
                }
            });
        });
    }

    /// <summary>
    /// Audiobook section + overview row, grouped the same way as the Audiobooks shelf
    /// (whole books, not files - acquired-but-deleted books count too). Hidden for a
    /// library with no books.
    /// </summary>
    private void PopulateAudiobookStats()
    {
        var audiobookItems = _allItems.Where(i => i.Kind == MediaKind.Audiobook).ToList();
        var owned = AudiobookLibrary.AssembleOwned(App.FolderPath, audiobookItems);

        var visible = owned.Count > 0;
        AudiobookStatsSection.IsVisible = visible;
        OverviewAudiobookLabel.IsVisible = visible;
        OverviewAudiobookLink.IsVisible = visible;

        if (!visible)
        {
            return;
        }

        var authors = owned.Where(b => !string.IsNullOrEmpty(b.Author)).Select(b => b.Author).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var totalDuration = TimeSpan.FromTicks(owned.Sum(b => b.TotalDuration.Ticks));
        var totalBytes = audiobookItems.Sum(i => i.FileSize ?? 0);

        StatAudiobookCount.Text = owned.Count.ToString("N0");
        StatBookCount.Text = owned.Count.ToString("N0");
        StatBookAuthors.Text = authors.ToString("N0");
        StatBookChapters.Text = audiobookItems.Count.ToString("N0");
        StatBookDuration.Text = FormatHelper.FormatDurationHuman(totalDuration);
        StatBookSize.Text = FormatHelper.FormatFileSize(totalBytes);
    }

    private void ScrollToElement(Control target)
    {
        target.BringIntoView();
    }

    private void LinkMusicCount_Click(object? sender, RoutedEventArgs e)
    {
        ScrollToElement(MusicStatsSection);
    }

    private void LinkRadioCount_Click(object? sender, RoutedEventArgs e)
    {
        ScrollToElement(RadioStatsSection);
    }

    private void LinkPodcastCount_Click(object? sender, RoutedEventArgs e)
    {
        ScrollToElement(PodcastStatsSection);
    }

    private void LinkAudiobookCount_Click(object? sender, RoutedEventArgs e)
    {
        ScrollToElement(AudiobookStatsSection);
    }

    private static Grid CreateBreakdownRow(string label, string count, string? size)
    {
        var row = new Grid
        {
            ColumnDefinitions = size != null
                ? ColumnDefinitions.Parse("140,120,*")
                : ColumnDefinitions.Parse("200,*"),
        };

        row.Children.Add(CreateStatCell(label, 0, 0.6));
        row.Children.Add(CreateStatCell(count, 1, 1.0));

        if (size != null)
        {
            row.Children.Add(CreateStatCell(size, 2, 0.5));
        }

        return row;
    }

    private static TextBlock CreateStatCell(string text, int column, double opacity)
    {
        var tb = new TextBlock
        {
            Text = text,
            Opacity = opacity,
            // A fixed grid column doesn't clip - an over-long label (a raw codec MIME
            // type, a mile-long genre) would render straight into the next column.
            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
        };

        if (column == 0)
        {
            ToolTip.SetTip(tb, text);
        }

        Grid.SetColumn(tb, column);
        return tb;
    }

    private static TextBlock CreateStatLabel(string text, double opacity)
    {
        return new TextBlock
        {
            Text = text,
            Opacity = opacity,
        };
    }


    private void SaveSettings()
    {
        // General
        if (_pendingFolderPath != App.FolderPath)
        {
            App.FolderPath = _pendingFolderPath;
            Settings.Set("OrgZ.FolderPath", _pendingFolderPath);
            FolderChanged = true;
        }

        Settings.Set("OrgZ.MinimizeToTray", MinimizeToTrayCheck.IsChecked == true);
        Settings.Set("OrgZ.RememberLastTrack", RememberLastTrackCheck.IsChecked == true);

        Settings.Set("OrgZ.BadFormat.ShowInSidebar", BadFormatShowCheck.IsChecked == true);
        Settings.Set("OrgZ.BadFormat.NoTitle", BadFormatNoTitleCheck.IsChecked == true);
        Settings.Set("OrgZ.BadFormat.NoArtist", BadFormatNoArtistCheck.IsChecked == true);
        Settings.Set("OrgZ.BadFormat.NoYear", BadFormatNoYearCheck.IsChecked == true);
        Settings.Set("OrgZ.BadFormat.NoAlbumArt", BadFormatNoAlbumArtCheck.IsChecked == true);
        Settings.Set("OrgZ.BadFormat.LossyFormats", BadFormatLossyCheck.IsChecked == true);

        // Playback
        var bufferSize = BufferSizeCombo.SelectedIndex switch
        {
            0 => "Small",
            1 => "Medium",
            2 => "Large",
            3 => "Extra Large",
            _ => "Medium"
        };
        Settings.Set("OrgZ.StreamingBufferSize", bufferSize);

        Settings.Set("OrgZ.ShuffleBy", ShuffleAlbumRadio.IsChecked == true ? ShuffleBy.Album : ShuffleBy.Song);
        Settings.Set("OrgZ.AutoAdvance", AutoAdvanceCheck.IsChecked == true);
        Settings.Set("OrgZ.NormalizeVolume", NormalizeVolumeCheck.IsChecked == true);

        // Burning
        Settings.Set("OrgZ.Burn.AudioGapSeconds", (BurnAudioGapCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "0" ? 0 : 2);
        Settings.Set("OrgZ.Burn.CdText", BurnCdTextCheck.IsChecked == true);
        Settings.Set("OrgZ.Burn.DataFormat", (BurnDataFormatCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "original");
        Settings.Set("OrgZ.Burn.LossyQualityKbps", int.TryParse((BurnLossyQualityCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var burnKbps) ? burnKbps : 256);

        // Services
        Settings.Set("OrgZ.Services.Sharing.Enabled", ShareEnabledCheck.IsChecked == true);
        Settings.Set("OrgZ.Services.Sharing.Name", string.IsNullOrWhiteSpace(ShareNameInput.Text) ? $"{Environment.MachineName} Library" : ShareNameInput.Text.Trim());

        Settings.Set("OrgZ.Services.KeepAlive.Burning", ServiceKeepBurningCheck.IsChecked == true);
        Settings.Set("OrgZ.Services.KeepAlive.IPodSync", ServiceKeepSyncCheck.IsChecked == true);
        Settings.Set("OrgZ.Services.KeepAlive.Sharing", ServiceKeepSharingCheck.IsChecked == true);

        // Playback → Mini-Player
        var miniMode = MiniPlayerModeSideBySide.IsChecked == true ? MiniPlayerMode.SideBySide : MiniPlayerMode.Replace;
        MainWindowViewModel.SaveMiniPlayerMode(miniMode);

        // Podcasts
        var checkTag = (PodcastCheckIntervalCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "day";
        Settings.Set("OrgZ.Podcasts.CheckInterval", checkTag);

        var newActionTag = (PodcastNewEpisodeActionCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "recent";
        Settings.Set("OrgZ.Podcasts.NewEpisodeAction", newActionTag);

        var keepTag = (PodcastKeepCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";
        Settings.Set("OrgZ.Podcasts.Keep", keepTag);

        Settings.Save();
    }

    // ── Services tab: library sharing ────────────────────────

    /// <summary>
    /// The one line the Services tab shows for sharing. Pure so the wording is
    /// tested rather than eyeballed: a share needs the background service, and
    /// saying "on" while nothing is listening would be a lie.
    /// </summary>
    internal static string DescribeShareState(bool serviceAvailable, Services.DeviceHelper.DeviceHelperClient.ShareState? state)
    {
        if (!serviceAvailable)
        {
            return "Needs the background service — install it above";
        }

        if (state is not { Sharing: true })
        {
            return "Not sharing";
        }

        var name = string.IsNullOrWhiteSpace(state.Name) ? "This library" : state.Name;
        return $"Sharing “{name}” on port {state.Port}";
    }

    private async Task RefreshShareStatusAsync()
    {
        var available = await Services.DeviceHelper.DeviceHelperClient.IsAvailableAsync();
        var state = available ? await Services.DeviceHelper.DeviceHelperClient.ShareStatusAsync() : null;

        ShareStatusText.Text = DescribeShareState(available, state);
        ShareEnabledCheck.IsEnabled = available;
        ShareEnabledCheck.IsChecked = state is { Sharing: true };
    }

    private async void ShareEnabled_Click(object? sender, RoutedEventArgs e)
    {
        var wantOn = ShareEnabledCheck.IsChecked == true;
        ShareStatusText.Text = wantOn ? "Starting…" : "Stopping…";

        var name = string.IsNullOrWhiteSpace(ShareNameInput.Text) ? $"{Environment.MachineName} Library" : ShareNameInput.Text.Trim();

        var state = wantOn
            ? await Services.DeviceHelper.DeviceHelperClient.StartShareAsync(name, Services.Sharing.ShareServiceOps.DefaultPort)
            : await Services.DeviceHelper.DeviceHelperClient.StopShareAsync();

        // Persist immediately: a share is a live network state, not a pending edit -
        // the checkbox must not disagree with what the service is actually doing.
        Settings.Set("OrgZ.Services.Sharing.Enabled", state is { Sharing: true });
        Settings.Set("OrgZ.Services.Sharing.Name", name);
        Settings.Save();

        await RefreshShareStatusAsync();
    }

    // ── Services tab ─────────────────────────────────────────

    /// <summary>
    /// Whether the background-service card appears at all. Development only: a shipping
    /// user makes one choice - run OrgZ standalone (each privileged disc operation asks
    /// for UAC) or with the service installed (asked once, then silent) - and lifecycle
    /// buttons past that point can only put them somewhere broken, like installed-but-
    /// stopped with no idea why burning started prompting again.
    ///
    /// The installer code itself stays compiled rather than being #if'd out: the
    /// pre-commit suite runs in Release, and <c>DeviceHelperInstallerTests</c> has to
    /// build there. It is simply unreachable in a Release UI.
    /// </summary>
    internal const bool ShowServiceLifecycle =
#if DEBUG
        true;
#else
        false;
#endif

    /// <summary>Which of the four service buttons a given state allows.</summary>
    internal readonly record struct ServiceButtons(bool Install, bool Start, bool Stop, bool Uninstall);

    internal static ServiceButtons ButtonsFor(DeviceHelperInstaller.ServiceState state) => state switch
    {
        DeviceHelperInstaller.ServiceState.Running => new(Install: false, Start: false, Stop: true, Uninstall: true),
        DeviceHelperInstaller.ServiceState.Stopped => new(Install: false, Start: true, Stop: false, Uninstall: true),
        _ => new(Install: true, Start: false, Stop: false, Uninstall: false),
    };

    /// <summary>
    /// The status line. It reports the OS's view of the service AND whether a helper is
    /// actually answering on the socket, because those can disagree: a developer can run
    /// <c>OrgZ --device-helper</c> by hand with nothing installed, and a status line
    /// claiming "not installed" while sharing plainly works is a lie.
    /// </summary>
    internal static string DescribeServiceState(DeviceHelperInstaller.ServiceState state, bool answering) => state switch
    {
        DeviceHelperInstaller.ServiceState.Running => "Installed and running",
        DeviceHelperInstaller.ServiceState.Stopped => answering ? "Stopped (a helper is answering separately)" : "Installed, stopped",
        _ => answering ? "Running, not installed" : "Not installed",
    };

    private async Task RefreshServiceStatusAsync()
    {
        ServiceStatusText.Text = "Checking…";
        SetServiceButtons(new ServiceButtons(false, false, false, false));

        var state = await DeviceHelperInstaller.QueryStateAsync();
        var answering = await Services.DeviceHelper.DeviceHelperClient.IsAvailableAsync();

        ServiceStatusText.Text = DescribeServiceState(state, answering);
        SetServiceButtons(ButtonsFor(state));
    }

    private void SetServiceButtons(ServiceButtons buttons)
    {
        ServiceInstallButton.IsEnabled = buttons.Install;
        ServiceStartButton.IsEnabled = buttons.Start;
        ServiceStopButton.IsEnabled = buttons.Stop;
        ServiceUninstallButton.IsEnabled = buttons.Uninstall;
    }

    private async void ServiceRefresh_Click(object? sender, RoutedEventArgs e)
    {
        await RefreshServiceStatusAsync();
    }

    /// <summary>Runs one privileged service action, then re-reads the real state either way.</summary>
    private async Task RunServiceActionAsync(string progress, string failurePrefix, Func<Task<DeviceHelperInstaller.InstallResult>> action)
    {
        ServiceStatusText.Text = progress;
        SetServiceButtons(new ServiceButtons(false, false, false, false));

        var result = await action();
        if (!result.Ok)
        {
            // Show why, then still re-read: a cancelled UAC prompt leaves the previous
            // state intact and the buttons must come back.
            ServiceStatusText.Text = $"{failurePrefix}: {result.Detail}";
            SetServiceButtons(ButtonsFor(await DeviceHelperInstaller.QueryStateAsync()));
            return;
        }

        await RefreshServiceStatusAsync();
    }

    private async void ServiceInstall_Click(object? sender, RoutedEventArgs e)
        => await RunServiceActionAsync("Installing…", "Install failed", DeviceHelperInstaller.InstallAsync);

    private async void ServiceUninstall_Click(object? sender, RoutedEventArgs e)
        => await RunServiceActionAsync("Uninstalling…", "Uninstall failed", DeviceHelperInstaller.UninstallAsync);

    private async void ServiceStart_Click(object? sender, RoutedEventArgs e)
        => await RunServiceActionAsync("Starting…", "Start failed", DeviceHelperInstaller.StartAsync);

    private async void ServiceStop_Click(object? sender, RoutedEventArgs e)
        => await RunServiceActionAsync("Stopping…", "Stop failed", DeviceHelperInstaller.StopAsync);

    private async void ChangeFolderButton_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "Select OrgZ Music Folder",
                AllowMultiple = false
            });

        if (folders.Count > 0)
        {
            _pendingFolderPath = folders[0].Path.LocalPath;
            FolderPathText.Text = _pendingFolderPath;
        }
    }

    private void ResetFolderButton_Click(object? sender, RoutedEventArgs e)
    {
        _pendingFolderPath = string.Empty;
        FolderPathText.Text = "(No folder selected)";
    }

    private void ResetWindowSizesButton_Click(object? sender, RoutedEventArgs e)
    {
        WindowSizeTracker.ResetAll();
        ResetWindowSizesButton.IsEnabled = false;
        ResetWindowSizesButton.Content = "Reset";
    }

    private void ResetSettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        SettingsReset = true;
        ResetSettingsButton.IsEnabled = false;
        ResetSettingsButton.Content = "Will reset on OK";
    }

    private void OkButton_Click(object? sender, RoutedEventArgs e)
    {
        if (SettingsReset)
        {
            Settings.Clear();
            Settings.Save();
        }
        else
        {
            SaveSettings();
        }

        Close(true);
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        FolderChanged = false;
        SettingsReset = false;
        Close(false);
    }
}
