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

        // Tab visibility
        MusicInfoPanel.IsVisible = isMusic;
        RadioInfoTab.IsVisible = isRadio;
        ArtworkTab.IsVisible = isMusic;
        StartStopPanel.IsVisible = isMusic;

        // Make sure Info tab (index 1) is the music info tab
        // RadioInfoTab will only show for radio items

        LoadSummary(item);

        if (isMusic)
        {
            LoadMusicInfo(item);
        }
        else if (isRadio)
        {
            LoadRadioInfo(item);
        }

        LoadOptions(item);
        LoadArtwork(item);
    }

    #region Summary Tab

    private void LoadSummary(MediaItem item)
    {
        var isMusic = item.Kind == MediaKind.Music;

        // Title line
        var durationStr = item.Duration.HasValue ? $" ({item.Duration.Value:m\\:ss})" : "";
        SummaryTitle.Text = $"{item.Title ?? item.FileName ?? "Unknown"}{durationStr}";
        SummaryArtist.Text = isMusic ? (item.Artist ?? "") : item.SourceDisplayName;
        SummaryAlbum.Text = isMusic ? (item.Album ?? "") : (item.Country ?? "");

        // Album art
        if (isMusic && !string.IsNullOrEmpty(item.FilePath))
        {
            SummaryArt.Source = LoadAlbumArtBitmap(item.FilePath);
        }
        else
        {
            SummaryArt.Source = null;
        }

        if (isMusic)
        {
            LoadMusicSummary(item);
        }
        else
        {
            LoadRadioSummary(item);
        }

        // Common date/usage fields
        SummaryDateModified.Text = FormatHelper.FormatDateWithRelative(isMusic ? item.LastModified : null);
        SummaryPlayCount.Text = item.PlayCount.ToString();
        SummaryLastPlayed.Text = item.LastPlayed.HasValue ? FormatHelper.FormatDateWithRelative(item.LastPlayed) : "Never";
        SummaryDateAdded.Text = FormatHelper.FormatDateWithRelative(item.DateAdded);

        // Where / Stream
        if (isMusic)
        {
            SummaryWhereLabel.Text = "Where:";
            SummaryWherePath.Text = item.FilePath ?? "-";
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

        SummaryR2C0Label.Text = "Bit Rate:";
        SummaryR2C0Value.Text = item.AudioBitrate is > 0 ? $"{item.AudioBitrate} kbps" : "-";
        SummaryR2C2Label.Text = "Genre:";
        SummaryR2C2Value.Text = !string.IsNullOrEmpty(item.Genre) ? item.Genre : "-";

        SummaryR3C0Label.Text = "Sample Rate:";
        SummaryR3C0Value.Text = item.SampleRate is > 0 ? $"{item.SampleRate:N0} Hz" : "-";
        SummaryKind.Text = item.KindLabel;

        SummaryR4C0Label.Text = "Encoded with:";
        SummaryR4C0Value.Text = !string.IsNullOrEmpty(item.EncoderSettings) ? item.EncoderSettings : "-";
        SummaryR4C2Label.Text = "Codec:";
        SummaryR4C2Value.Text = !string.IsNullOrEmpty(item.CodecDescription) ? item.CodecDescription : "-";
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

        SummaryR3C0Label.Text = "Listeners:";
        SummaryR3C0Value.Text = item.ListenerCount is > 0 ? $"{item.ListenerCount:N0}" : "-";
        SummaryKind.Text = "Internet Radio";

        SummaryR4C0Label.Text = "";
        SummaryR4C0Value.Text = "";
        SummaryR4C2Label.Text = "";
        SummaryR4C2Value.Text = "";
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
        RadioInfoSource.Text = item.SourceDisplayName;
        RadioInfoCountry.Text = item.Country ?? "";
        RadioInfoHomepage.Text = item.HomepageUrl ?? "";
        RadioInfoCodec.Text = item.CodecLabel;
        RadioInfoBitrate.Text = item.Bitrate is > 0 ? $"{item.Bitrate} kbps" : "-";
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
        if (item.Kind == MediaKind.Music && !string.IsNullOrEmpty(item.FilePath))
        {
            ArtworkImage.Source = LoadAlbumArtBitmap(item.FilePath);
        }
        else
        {
            ArtworkImage.Source = null;
        }
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
        Services.MediaCache.UpsertMusic(_item);
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

    private static Bitmap? LoadAlbumArtBitmap(string filePath)
    {
        try
        {
            using var file = TagLib.File.Create(filePath);
            if (file.Tag.Pictures?.Length > 0)
            {
                var data = file.Tag.Pictures[0].Data.Data;
                using var stream = new MemoryStream(data);
                return new Bitmap(stream);
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
