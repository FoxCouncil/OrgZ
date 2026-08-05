// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using LibVLCSharp.Shared;

namespace OrgZ.Views;

public partial class MediaInfoDialog : Window
{
    private MediaItem _item;
    private readonly List<MediaItem> _items;
    private int _currentIndex;

    // Snapshot for cancel/revert
    private TagSnapshot? _originalTags;

    public bool ItemChanged { get; private set; }

    public MediaInfoDialog() : this(null!, []) { }

    public MediaInfoDialog(MediaItem item, List<MediaItem> items)
    {
        InitializeComponent();

        WindowSizeTracker.Track(this, "MediaInfo");

        _items = items;
        _currentIndex = items.IndexOf(item);
        if (_currentIndex < 0)
        {
            _currentIndex = 0;
        }

        _item = item;

        PopulateEqPresets();
        BuildRatingStars();
        LoadItem(item);
        UpdateNavigationButtons();
    }

    private void LoadItem(MediaItem item)
    {
        _item = item;
        _originalTags = TakeSnapshot(item);

        Title = item.Title ?? item.FileName ?? "Media Info";

        var isMusic = item.Kind == MediaKind.Music;
        var isRadio = item.Kind == MediaKind.Radio;
        var isPodcast = item.Kind == MediaKind.Podcast;

        // Tab + panel visibility. The Info tab hosts kind-specific sub-panels;
        // exactly one is visible at a time. Radio still uses its own "Station"
        // tab because the field set is unrelated.
        MusicInfoPanel.IsVisible = isMusic;
        PodcastInfoPanel.IsVisible = isPodcast;
        RadioInfoTab.IsVisible = isRadio;
        ArtworkTab.IsVisible = isMusic;
        StartStopPanel.IsVisible = isMusic || isPodcast;

        LoadSummary(item);

        if (isMusic)
        {
            LoadMusicInfo(item);
        }
        else if (isRadio)
        {
            LoadRadioInfo(item);
        }
        else if (isPodcast)
        {
            LoadPodcastInfo(item);
        }

        LoadOptions(item);
        LoadArtwork(item);
    }

    #region Summary Tab

    private void LoadSummary(MediaItem item)
    {
        var isMusic = item.Kind == MediaKind.Music;
        var isRadio = item.Kind == MediaKind.Radio;
        var isPodcast = item.Kind == MediaKind.Podcast;

        // Title line
        var durationStr = item.Duration.HasValue
            ? $" ({FormatHelper.FormatDurationCompact(item.Duration.Value)})"
            : "";
        SummaryTitle.Text = $"{item.Title ?? item.FileName ?? "Unknown"}{durationStr}";

        if (isMusic)
        {
            SummaryArtist.Text = item.Artist ?? "";
            SummaryAlbum.Text = item.Album ?? "";
        }
        else if (isPodcast)
        {
            // For podcasts: Artist is the show name, Album mirrors it; we
            // surface them under the title so the dialog clearly says "this
            // is an episode of <show>".
            SummaryArtist.Text = item.Artist ?? "";
            SummaryAlbum.Text = "";
        }
        else
        {
            SummaryArtist.Text = item.Country ?? "";
            SummaryAlbum.Text = item.Country ?? "";
        }

        // Album art -- music reads from local file tags, radio/podcast pull
        // from a URL (FaviconUrl carries the feed/episode image for podcasts).
        if (isMusic && !string.IsNullOrEmpty(item.FilePath))
        {
            Converters.RemoteImage.SetUrl(SummaryArt, null);
            _ = ShowAlbumArtAsync(SummaryArt, item.FilePath);
        }
        else if ((isRadio || isPodcast) && !string.IsNullOrWhiteSpace(item.FaviconUrl))
        {
            SummaryArt.Source = null;
            // RemoteImage owns remote artwork everywhere else: memory + disk cache, a
            // 6-way fetch gate, SVG handling, and the browser UA. The dialog used to
            // new up its own HttpClient per open and duplicate the UA string.
            Converters.RemoteImage.SetUrl(SummaryArt, item.FaviconUrl);
        }
        else
        {
            Converters.RemoteImage.SetUrl(SummaryArt, null);
            SummaryArt.Source = null;
        }

        if (isMusic)
        {
            LoadMusicSummary(item);
        }
        else if (isPodcast)
        {
            LoadPodcastSummary(item);
        }
        else
        {
            LoadRadioSummary(item);
        }

        // Common date/usage fields
        SummaryDateModified.Text = FormatHelper.FormatDateWithRelative(isMusic ? item.LastModified : null);
        SummaryPlayCount.Text = item.PlayCount.ToString();
        SummaryLastPlayed.Text = item.LastPlayed.HasValue
            ? FormatHelper.FormatDateWithRelative(item.LastPlayed)
            : "Never";
        SummaryDateAdded.Text = FormatHelper.FormatDateWithRelative(item.DateAdded);

        // Where / Stream
        if (isMusic)
        {
            SummaryWhereLabel.Text = "Where:";
            SummaryWherePath.Text = item.FilePath ?? "-";
        }
        else if (isPodcast && !string.IsNullOrWhiteSpace(item.FilePath))
        {
            SummaryWhereLabel.Text = "Downloaded:";
            SummaryWherePath.Text = item.FilePath!;
        }
        else
        {
            SummaryWhereLabel.Text = "Stream:";
            SummaryWherePath.Text = item.StreamUrl ?? "-";
        }
    }

    private void LoadMusicSummary(MediaItem item)
    {
        SummaryMediaKind.Text = item.Kind.ToString();

        SummarySize.Text = item.FileSize.HasValue ? FormatHelper.FormatFileSize(item.FileSize.Value) : "-";
        SummaryR1C2Label.Text = "Channels:";
        SummaryR1C2Value.Text = !string.IsNullOrEmpty(item.ChannelsLabel) ? item.ChannelsLabel : "-";

        SummaryR2C0Label.Text = "Bit Depth:";
        SummaryR2C0Value.Text = item.BitDepth is > 0 ? $"{item.BitDepth}-bit" : "-";

        // Items scanned before BitDepth existed have null here - probe the file so the
        // field fills without a full rescan. Off-thread: this is a second TagLib open of
        // a possibly-sleeping disk, and it used to block the dialog's first paint.
        if (item.BitDepth is null && !string.IsNullOrEmpty(item.FilePath))
        {
            var probePath = item.FilePath;
            _ = Task.Run(() =>
            {
                try
                {
                    using var f = TagLib.File.Create(probePath);
                    return f.Properties.BitsPerSample > 0 ? f.Properties.BitsPerSample : (int?)null;
                }
                catch
                {
                    return null;   // Unreadable file - leave the dash.
                }
            }).ContinueWith(t =>
            {
                if (t.Result is not int bits || !string.Equals(_item.FilePath, probePath, StringComparison.Ordinal))
                {
                    return;
                }
                item.BitDepth = bits;
                SummaryR2C0Value.Text = $"{bits}-bit";
            }, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.FromCurrentSynchronizationContext());
        }
        SummaryR2C2Label.Text = "Genre:";
        SummaryR2C2Value.Text = !string.IsNullOrEmpty(item.Genre) ? item.Genre : "-";

        SummaryR3C0Label.Text = "Bit Rate:";
        SummaryR3C0Value.Text = item.AudioBitrate is > 0 ? $"{item.AudioBitrate} kbps" : "-";
        SummaryKind.Text = item.KindLabel;

        SummaryR4C0Label.Text = "Sample Rate:";
        SummaryR4C0Value.Text = item.SampleRate is > 0 ? $"{item.SampleRate:N0} Hz" : "-";
        SummaryR4C2Label.Text = "Codec:";
        SummaryR4C2Value.Text = !string.IsNullOrEmpty(item.CodecDescription) ? item.CodecDescription : "-";

        SummaryR5C0Label.Text = "Encoded with:";
        SummaryR5C0Value.Text = !string.IsNullOrEmpty(item.EncoderSettings) ? item.EncoderSettings : "-";
        SummaryR5C2Label.Text = "";
        SummaryR5C2Value.Text = "";
    }

    private void LoadRadioSummary(MediaItem item)
    {
        SummaryMediaKind.Text = item.Kind.ToString();

        SummarySize.Text = item.Bitrate is > 0 ? $"{item.Bitrate} kbps" : "-";
        SummaryR1C2Label.Text = "Country:";
        SummaryR1C2Value.Text = !string.IsNullOrEmpty(item.Country) ? item.Country : "-";

        SummaryR2C0Label.Text = "Codec:";
        SummaryR2C0Value.Text = item.CodecLabel;
        SummaryR2C2Label.Text = "Votes:";
        SummaryR2C2Value.Text = item.Votes is > 0 ? $"{item.Votes:N0}" : "-";

        SummaryR3C0Label.Text = "Clicks:";
        SummaryR3C0Value.Text = item.ClickCount is > 0 ? $"{item.ClickCount:N0}" : "-";
        SummaryKind.Text = "Internet Radio";

        SummaryR4C0Label.Text = "";
        SummaryR4C0Value.Text = "";
        SummaryR4C2Label.Text = "";
        SummaryR4C2Value.Text = "";
        ClearSummaryRow5();
    }

    private void ClearSummaryRow5()
    {
        SummaryR5C0Label.Text = "";
        SummaryR5C0Value.Text = "";
        SummaryR5C2Label.Text = "";
        SummaryR5C2Value.Text = "";
    }

    /// <summary>
    /// Podcast variant of the Summary tab. Fills the same grid slots the radio
    /// path uses, with podcast-appropriate labels: show name, duration, publish
    /// date. Hides codec/votes/clicks (radio-only). Type label reads "Podcast".
    /// </summary>
    private void LoadPodcastSummary(MediaItem item)
    {
        SummaryMediaKind.Text = item.Kind.ToString();

        // Row 1: Size (file size when downloaded, otherwise blank) + Show
        SummarySize.Text = item.FileSize.HasValue
            ? FormatHelper.FormatFileSize(item.FileSize.Value)
            : "-";

        SummaryR1C2Label.Text = "Show:";
        SummaryR1C2Value.Text = !string.IsNullOrEmpty(item.Artist) ? item.Artist : "-";

        // Row 2: Duration + Published
        SummaryR2C0Label.Text = "Duration:";
        SummaryR2C0Value.Text = item.Duration.HasValue
            ? FormatHelper.FormatDurationCompact(item.Duration.Value)
            : "-";

        SummaryR2C2Label.Text = "Published:";
        SummaryR2C2Value.Text = FormatHelper.FormatDateWithRelative(item.DateAdded);

        // Row 3: leave first slot blank, Type column populated by shared row
        SummaryR3C0Label.Text = "";
        SummaryR3C0Value.Text = "";
        SummaryKind.Text = "Podcast";

        // Row 4 reserved for Podcast 2.0 augmentations (chapters URL, transcript
        // URL, persons) once the DTO carries them; blank for now.
        SummaryR4C0Label.Text = "";
        SummaryR4C0Value.Text = "";
        SummaryR4C2Label.Text = "";
        SummaryR4C2Value.Text = "";
        ClearSummaryRow5();
    }

    #endregion

    #region Info Tab

    private void LoadMusicInfo(MediaItem item)
    {
        InfoName.Text = item.Title ?? "";
        InfoArtist.Text = item.Artist ?? "";
        InfoYear.Text = item.Year is > 0 ? item.Year.ToString() : "";
        InfoComposer.Text = item.Composer ?? "";
        InfoTrack.Text = item.Track is > 0 ? item.Track.ToString() : "";
        InfoTotalTracks.Text = item.TotalTracks is > 0 ? item.TotalTracks.ToString() : "";
        InfoAlbum.Text = item.Album ?? "";
        InfoDisc.Text = item.Disc is > 0 ? item.Disc.ToString() : "";
        InfoTotalDiscs.Text = item.TotalDiscs is > 0 ? item.TotalDiscs.ToString() : "";
        InfoComments.Text = item.Comment ?? "";
        InfoGenre.Text = item.Genre ?? "";
        InfoBPM.Text = item.Bpm is > 0 ? item.Bpm.ToString() : "";
    }

    private void LoadRadioInfo(MediaItem item)
    {
        RadioInfoName.Text = item.Title ?? "";
        RadioInfoTags.Text = item.Tags ?? "";
        RadioInfoStreamUrl.Text = item.StreamUrl ?? "";
        RadioInfoSource.Text = string.IsNullOrWhiteSpace(item.Tags) ? "—" : item.Tags;
        RadioInfoCountry.Text = item.Country ?? "";
        RadioInfoHomepage.Text = item.HomepageUrl ?? "";
        RadioInfoCodec.Text = item.CodecLabel;
        RadioInfoBitrate.Text = item.Bitrate is > 0 ? $"{item.Bitrate} kbps" : "-";
    }

    private void LoadPodcastInfo(MediaItem item)
    {
        PodcastInfoTitle.Text = item.Title ?? "";
        PodcastInfoShow.Text = item.Artist ?? "";
        PodcastInfoDuration.Text = item.Duration.HasValue
            ? FormatHelper.FormatDurationCompact(item.Duration.Value)
            : "-";
        PodcastInfoPublished.Text = FormatHelper.FormatDateWithRelative(item.DateAdded);

        // Show notes ride on the Comment field -- the canonical "long
        // description" slot on MediaItem. PodcastIndex ships basic HTML;
        // HtmlInlinesBuilder turns it into a flow of Runs + LineBreaks where
        // anchor tags + bare URLs become hyperlink-styled runs that the
        // PointerPressed handler below opens with the OS shell.
        PodcastInfoNotes.Inlines?.Clear();

        if (PodcastInfoNotes.Inlines is { } inlines)
        {
            foreach (var inline in HtmlInlinesBuilder.Build(item.Comment))
            {
                inlines.Add(inline);
            }
        }

        PodcastInfoStreamUrl.Text = item.StreamUrl ?? "";
        PodcastInfoHomepage.Text = item.HomepageUrl ?? "";
    }

    private void PodcastInfoNotes_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (e.Source is not Avalonia.Controls.Documents.Run run)
        {
            return;
        }

        var url = HtmlInlinesBuilder.GetUrl(run);

        if (string.IsNullOrEmpty(url))
        {
            return;
        }

        if (!e.GetCurrentPoint(PodcastInfoNotes).Properties.IsLeftButtonPressed)
        {
            return;
        }

        HtmlInlinesBuilder.OpenUrl(url);
        e.Handled = true;
    }

    #endregion

    #region Options Tab

    private void PopulateEqPresets()
    {
        EqPresetCombo.Items.Add(new ComboBoxItem { Content = "None" });

        try
        {
            using var eq = new Equalizer();
            var count = eq.PresetCount;
            for (uint i = 0; i < count; i++)
            {
                EqPresetCombo.Items.Add(new ComboBoxItem { Content = eq.PresetName(i) });
            }
        }
        catch
        {
            // LibVLC not available at design time
        }

        EqPresetCombo.SelectedIndex = 0;
    }

    private void BuildRatingStars()
    {
        RatingPanel.Children.Clear();

        for (int i = 1; i <= 5; i++)
        {
            var star = i;
            var ellipse = new Ellipse
            {
                Width = 14,
                Height = 14,
                Fill = new SolidColorBrush(Color.Parse("#444")),
                Stroke = new SolidColorBrush(Color.Parse("#666")),
                StrokeThickness = 1,
                Cursor = new Cursor(StandardCursorType.Hand),
            };

            ellipse.PointerPressed += (_, _) =>
            {
                // Toggle: if clicking the current rating, clear it
                var currentRating = GetCurrentRating();
                SetRatingDisplay(currentRating == star ? 0 : star);
            };

            RatingPanel.Children.Add(ellipse);
        }
    }

    private int GetCurrentRating()
    {
        int rating = 0;
        for (int i = 0; i < RatingPanel.Children.Count; i++)
        {
            if (RatingPanel.Children[i] is Ellipse e && e.Fill is SolidColorBrush b && b.Color == Color.Parse("#FFD700"))
            {
                rating = i + 1;
            }
        }
        return rating;
    }

    private void SetRatingDisplay(int rating)
    {
        for (int i = 0; i < RatingPanel.Children.Count; i++)
        {
            if (RatingPanel.Children[i] is Ellipse e)
            {
                e.Fill = new SolidColorBrush(i < rating ? Color.Parse("#FFD700") : Color.Parse("#444"));
            }
        }
    }

    private void LoadOptions(MediaItem item)
    {
        VolumeAdjustmentSlider.Value = item.VolumeAdjustment;

        // Find EQ preset in combo
        EqPresetCombo.SelectedIndex = 0;
        if (!string.IsNullOrEmpty(item.EqPreset))
        {
            for (int i = 0; i < EqPresetCombo.Items.Count; i++)
            {
                if (EqPresetCombo.Items[i] is ComboBoxItem ci && (string)ci.Content! == item.EqPreset)
                {
                    EqPresetCombo.SelectedIndex = i;
                    break;
                }
            }
        }

        SetRatingDisplay(item.Rating ?? 0);

        UseStartTimeCheck.IsChecked = item.UseStartTime;
        UseStopTimeCheck.IsChecked = item.UseStopTime;
        StartTimeBox.Text = item.StartTime.HasValue ? FormatHelper.FormatTimeSpan(item.StartTime.Value) : "0:00";
        StopTimeBox.Text = item.StopTime.HasValue ? FormatHelper.FormatTimeSpan(item.StopTime.Value) : (item.Duration.HasValue ? FormatHelper.FormatTimeSpan(item.Duration.Value) : "0:00");
    }

    #endregion

    #region Artwork Tab

    private void LoadArtwork(MediaItem item)
    {
        // Art lives in the file: no local file, nothing to edit.
        var editable = item.Kind == MediaKind.Music && !string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath);
        ArtworkAddButton.IsEnabled = editable;
        ArtworkStatusText.Text = editable ? string.Empty : "This track has no local file to store artwork in.";

        if (item.Kind == MediaKind.Music && !string.IsNullOrEmpty(item.FilePath))
        {
            // Served from the same per-file cache the Summary tab filled; Delete's
            // enabled state settles with the image (see ShowAlbumArtAsync).
            ArtworkDeleteButton.IsEnabled = false;
            _ = ShowAlbumArtAsync(ArtworkImage, item.FilePath);
        }
        else
        {
            ArtworkImage.Source = null;
            ArtworkDeleteButton.IsEnabled = false;
        }
    }

    private async void ArtworkAdd_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_item.FilePath))
        {
            return;
        }

        var picked = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Choose Album Artwork",
            AllowMultiple = false,
            FileTypeFilter = [new Avalonia.Platform.Storage.FilePickerFileType("Images") { Patterns = ["*.jpg", "*.jpeg", "*.png"] }],
        });

        var path = picked?.Count > 0 ? picked[0].Path.LocalPath : null;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        ApplyArtResult(Services.AlbumArtWriter.SetArtwork(_item.FilePath, path), "Artwork updated.");
    }

    private void ArtworkDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_item.FilePath))
        {
            return;
        }

        ApplyArtResult(Services.AlbumArtWriter.RemoveArtwork(_item.FilePath), "Artwork removed.");
    }

    /// <summary>
    /// Reflects an artwork edit: reload from the file (so what's shown is what's stored,
    /// never an optimistic guess), drop the cached art, and report the outcome.
    /// </summary>
    private void ApplyArtResult(Services.AlbumArtWriter.ArtResult result, string successMessage)
    {
        if (!result.Ok)
        {
            ArtworkStatusText.Text = result.Error;
            return;
        }

        // The file's art just changed under the per-file cache - drop it, or the
        // reload below would serve the pre-edit bitmap straight back.
        _artCachePath = null;
        _artCacheBitmap = null;
        LoadArtwork(_item);
        ArtworkStatusText.Text = successMessage;
        ItemChanged = true;
    }

    #endregion

    #region Save Logic

    private void SaveCurrentItem()
    {
        if (_item.Kind == MediaKind.Music)
        {
            SaveMusicItem();
        }
        else if (_item.Kind == MediaKind.Radio)
        {
            SaveRadioItem();
        }

        SaveOptions();

        // Podcasts aren't persisted in the main library cache yet, so skip the
        // upsert -- saving an Options edit on a podcast still updates the live
        // MediaItem, just not the DB. (The deeper consolidation will route
        // podcasts through MediaCache like everything else.)
        if (_item.Kind != MediaKind.Podcast)
        {
            Services.MediaCache.UpsertMusic(_item);
        }

        ItemChanged = true;
    }

    private void SaveMusicItem()
    {
        var title = InfoName.Text?.Trim() ?? "";
        var artist = string.IsNullOrWhiteSpace(InfoArtist.Text) ? null : InfoArtist.Text.Trim();
        var album = string.IsNullOrWhiteSpace(InfoAlbum.Text) ? null : InfoAlbum.Text.Trim();
        uint? year = uint.TryParse(InfoYear.Text, out var y) && y > 0 ? y : null;
        uint? track = uint.TryParse(InfoTrack.Text, out var t) && t > 0 ? t : null;
        uint? totalTracks = uint.TryParse(InfoTotalTracks.Text, out var tt) && tt > 0 ? tt : null;
        uint? disc = uint.TryParse(InfoDisc.Text, out var d) && d > 0 ? d : null;
        uint? totalDiscs = uint.TryParse(InfoTotalDiscs.Text, out var td) && td > 0 ? td : null;
        var genre = string.IsNullOrWhiteSpace(InfoGenre.Text) ? null : InfoGenre.Text.Trim();
        var composer = string.IsNullOrWhiteSpace(InfoComposer.Text) ? null : InfoComposer.Text.Trim();
        var comment = string.IsNullOrWhiteSpace(InfoComments.Text) ? null : InfoComments.Text.Trim();
        uint? bpm = uint.TryParse(InfoBPM.Text, out var b) && b > 0 ? b : null;

        try
        {
            Services.AudioFileAnalyzer.WriteTagsAndReanalyze(
                _item, title, artist, album, year, track, totalTracks, disc, totalDiscs,
                genre, composer, comment, bpm);
        }
        catch
        {
            // If tag writing fails, at least update the in-memory fields
            _item.Title = title;
            _item.Artist = artist;
            _item.Album = album;
            _item.Year = year;
            _item.Track = track;
            _item.TotalTracks = totalTracks;
            _item.Disc = disc;
            _item.TotalDiscs = totalDiscs;
            _item.Genre = genre;
            _item.Composer = composer;
            _item.Comment = comment;
            _item.Bpm = bpm;
        }
    }

    private void SaveRadioItem()
    {
        _item.Title = RadioInfoName.Text?.Trim();
        // Tags is init-only on MediaItem, so we can't set it here.
        // For now, radio name is the only editable field.
    }

    private void SaveOptions()
    {
        _item.VolumeAdjustment = (int)VolumeAdjustmentSlider.Value;

        var selectedEq = EqPresetCombo.SelectedItem as ComboBoxItem;
        var eqName = selectedEq?.Content as string;
        _item.EqPreset = eqName == "None" ? null : eqName;

        _item.Rating = GetCurrentRating() > 0 ? GetCurrentRating() : null;

        _item.UseStartTime = UseStartTimeCheck.IsChecked == true;
        _item.UseStopTime = UseStopTimeCheck.IsChecked == true;
        _item.StartTime = FormatHelper.TryParseTimeSpan(StartTimeBox.Text);
        _item.StopTime = FormatHelper.TryParseTimeSpan(StopTimeBox.Text);
    }

    private void RevertCurrentItem()
    {
        if (_originalTags == null)
        {
            return;
        }

        // Restore original values
        _item.Title = _originalTags.Title;
        _item.Artist = _originalTags.Artist;
        _item.Album = _originalTags.Album;
        _item.Year = _originalTags.Year;
        _item.Track = _originalTags.Track;
        _item.TotalTracks = _originalTags.TotalTracks;
        _item.Disc = _originalTags.Disc;
        _item.TotalDiscs = _originalTags.TotalDiscs;
        _item.Genre = _originalTags.Genre;
        _item.Composer = _originalTags.Composer;
        _item.Comment = _originalTags.Comment;
        _item.Bpm = _originalTags.Bpm;
        _item.Rating = _originalTags.Rating;
        _item.VolumeAdjustment = _originalTags.VolumeAdjustment;
        _item.EqPreset = _originalTags.EqPreset;
        _item.StartTime = _originalTags.StartTime;
        _item.StopTime = _originalTags.StopTime;
        _item.UseStartTime = _originalTags.UseStartTime;
        _item.UseStopTime = _originalTags.UseStopTime;
    }

    #endregion

    #region Navigation

    private void UpdateNavigationButtons()
    {
        PreviousButton.IsEnabled = _currentIndex > 0;
        NextButton.IsEnabled = _currentIndex < _items.Count - 1;
    }

    private void NavigateTo(int index)
    {
        if (index < 0 || index >= _items.Count)
        {
            return;
        }

        // Auto-save current item before navigating (iTunes behavior)
        SaveCurrentItem();

        _currentIndex = index;
        LoadItem(_items[_currentIndex]);
        UpdateNavigationButtons();
    }

    private void PreviousButton_Click(object? sender, RoutedEventArgs e)
    {
        NavigateTo(_currentIndex - 1);
    }

    private void NextButton_Click(object? sender, RoutedEventArgs e)
    {
        NavigateTo(_currentIndex + 1);
    }

    #endregion

    #region Dialog Buttons

    private void OkButton_Click(object? sender, RoutedEventArgs e)
    {
        SaveCurrentItem();
        Close(true);
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        RevertCurrentItem();
        Close(false);
    }

    #endregion

    #region Helpers

    // Embedded art, read ONCE per file and shared by the Summary and Artwork tabs.
    // Both tabs used to call the synchronous reader on the UI thread for the same file,
    // so every open and every Previous/Next paid two TagLib opens and two full-size
    // decodes - on a sleeping disk that froze the dialog outright.
    private string? _artCachePath;
    private Bitmap? _artCacheBitmap;

    private async Task<Bitmap?> AlbumArtAsync(string filePath)
    {
        if (string.Equals(_artCachePath, filePath, StringComparison.Ordinal))
        {
            return _artCacheBitmap;
        }

        var bitmap = await Task.Run(() => LoadAlbumArtBitmap(filePath));
        _artCachePath = filePath;
        _artCacheBitmap = bitmap;
        return bitmap;
    }

    /// <summary>Loads embedded art off-thread and shows it - unless the user already navigated on.</summary>
    private async Task ShowAlbumArtAsync(Image target, string filePath)
    {
        var bitmap = await AlbumArtAsync(filePath);

        // Previous/Next can land while the read is in flight; the newest item wins.
        if (string.Equals(_item.FilePath, filePath, StringComparison.Ordinal))
        {
            target.Source = bitmap;
            ArtworkDeleteButton.IsEnabled = ArtworkAddButton.IsEnabled && bitmap is not null;
        }
    }

    private static Bitmap? LoadAlbumArtBitmap(string filePath)
    {
        try
        {
            using var file = TagLib.File.Create(filePath);
            if (file.Tag.Pictures?.Length > 0)
            {
                // Decoded to the largest size the dialog can show rather than the source's
                // full resolution - a 3000×3000 cover became a 36 MB GPU upload to fill a
                // 200px box.
                return Helpers.ImageDecoder.Decode(file.Tag.Pictures[0].Data.Data, 400);
            }
        }
        catch { }

        return null;
    }

    private static TagSnapshot TakeSnapshot(MediaItem item)
    {
        return new TagSnapshot
        {
            Title = item.Title,
            Artist = item.Artist,
            Album = item.Album,
            Year = item.Year,
            Track = item.Track,
            TotalTracks = item.TotalTracks,
            Disc = item.Disc,
            TotalDiscs = item.TotalDiscs,
            Genre = item.Genre,
            Composer = item.Composer,
            Comment = item.Comment,
            Bpm = item.Bpm,
            Rating = item.Rating,
            VolumeAdjustment = item.VolumeAdjustment,
            EqPreset = item.EqPreset,
            StartTime = item.StartTime,
            StopTime = item.StopTime,
            UseStartTime = item.UseStartTime,
            UseStopTime = item.UseStopTime,
        };
    }

    private class TagSnapshot
    {
        public string? Title;
        public string? Artist;
        public string? Album;
        public uint? Year;
        public uint? Track;
        public uint? TotalTracks;
        public uint? Disc;
        public uint? TotalDiscs;
        public string? Genre;
        public string? Composer;
        public string? Comment;
        public uint? Bpm;
        public int? Rating;
        public int VolumeAdjustment;
        public string? EqPreset;
        public TimeSpan? StartTime;
        public TimeSpan? StopTime;
        public bool UseStartTime;
        public bool UseStopTime;
    }

    #endregion
}
