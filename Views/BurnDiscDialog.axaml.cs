// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using OrgZ.Services;

namespace OrgZ.Views;

/// <summary>Which kind of disc <see cref="BurnDiscDialog"/> will produce.</summary>
public enum BurnDiscMode
{
    AudioCd,
    DataCd,
    DataDvd,
}

/// <summary>What the user confirmed in <see cref="BurnDiscDialog"/>.</summary>
public sealed record BurnDiscChoice
{
    public required string DrivePath { get; init; }
    public required BurnDiscMode Mode { get; init; }
    public string? DiscTitle { get; init; }
    public bool TestWrite { get; init; }

    /// <summary>Write CD-TEXT (disc title + per-track title/artist) into the lead-in. Audio mode only.</summary>
    public bool WriteCdText { get; init; } = true;

    /// <summary>Silence between tracks in seconds (encoded as each track's pregap). Audio mode only;
    /// track 1's mandatory 2-second Red Book pregap is separate and always present.</summary>
    public double GapSeconds { get; init; } = 2;

    /// <summary>Burn speed in kB/s (176 = 1x audio); null = drive maximum.</summary>
    public int? WriteSpeedKBps { get; init; }
}

public partial class BurnDiscDialog : Window
{
    private static readonly IBrush OverCapacityBrush = new SolidColorBrush(Color.Parse("#C01C28"));
    private static readonly IBrush NearCapacityBrush = new SolidColorBrush(Color.Parse("#E5A50A"));

    /// <summary>DVD+RW single-layer nominal capacity; READ DISC INFORMATION can't report it.</summary>
    private const long DvdNominalBytes = 4_700_000_000;

    private readonly IReadOnlyList<string> _drives;
    private readonly int _trackCount;
    private readonly TimeSpan _totalLength;
    private readonly long _totalDataBytes;
    private readonly Func<string, Task<CdBurnService.BurnMediaInfo>>? _probeAsync;
    private readonly IBrush? _defaultLengthBrush;

    private CdBurnService.BurnMediaInfo? _media;
    private int _probeGeneration;
    private bool _initialized;

    // Content exceeds the loaded disc - Burn stays disabled until multi-disc
    // burning exists (roadmap: split across discs with a swap prompt).
    private bool _overCapacity;

    public BurnDiscDialog() : this(["D:"], 0, TimeSpan.Zero, 0, null, null) { }

    /// <summary>
    /// iTunes-style burn confirmation. The media line at the top reflects the selected
    /// drive live (probed per selection change); Audio CD / Data CD / Data DVD modes and
    /// the test-write toggle enable from the detected media profile.
    /// </summary>
    public BurnDiscDialog(
        IReadOnlyList<string> drives,
        int trackCount,
        TimeSpan totalLength,
        long totalDataBytes,
        string? initialTitle,
        Func<string, Task<CdBurnService.BurnMediaInfo>>? probeAsync)
    {
        InitializeComponent();

        _drives = drives;
        _trackCount = trackCount;
        _totalLength = totalLength;
        _totalDataBytes = totalDataBytes;
        _probeAsync = probeAsync;
        _defaultLengthBrush = LengthText.Foreground;

        // Always the same control in the same row: a dropdown, read-only (disabled)
        // when there's nothing to choose between.
        DriveCombo.ItemsSource = drives;
        DriveCombo.SelectedIndex = drives.Count > 0 ? 0 : -1;
        DriveCombo.IsEnabled = drives.Count > 1;

        TracksText.Text = trackCount.ToString();
        TitleInput.Text = initialTitle ?? string.Empty;
        SpeedCombo.SelectedIndex = 0;
        MediaText.Text = "Checking…";
        BurnButton.IsEnabled = false;

        // Defaults come from Settings (OrgZ.Burn.*); the dialog only overrides per-burn.
        GapInput.Value = (decimal)Settings.Get("OrgZ.Burn.AudioGapSeconds", 2.0);
        CdTextCheck.IsChecked = Settings.Get("OrgZ.Burn.CdText", true);

        // DVD Data only ever appears once a probe finds DVD-class media.
        ModeCombo.Items.Remove(DataDvdItem);

        _initialized = true;

        Loaded += (_, _) =>
        {
            TitleInput.Focus();
            TitleInput.CaretIndex = TitleInput.Text?.Length ?? 0;
            _ = ProbeSelectedDriveAsync();
        };
    }

    private string? SelectedDrive => DriveCombo.SelectedItem as string ?? _drives.FirstOrDefault();

    private BurnDiscMode? SelectedMode => ModeCombo.SelectedItem switch
    {
        ComboBoxItem item when item == AudioCdItem => BurnDiscMode.AudioCd,
        ComboBoxItem item when item == DataCdItem => BurnDiscMode.DataCd,
        ComboBoxItem item when item == DataDvdItem => BurnDiscMode.DataDvd,
        _ => null,
    };

    private async Task ProbeSelectedDriveAsync()
    {
        var drive = SelectedDrive;
        if (drive == null || _probeAsync == null)
        {
            // Design-time / previewer: no probe wired - show placeholders, allow audio.
            _media = null;
            ApplyMediaState();
            return;
        }

        int generation = ++_probeGeneration;
        MediaText.Text = "Checking…";
        BurnButton.IsEnabled = false;

        CdBurnService.BurnMediaInfo probed;
        try
        {
            probed = await _probeAsync(drive);
        }
        catch
        {
            probed = new CdBurnService.BurnMediaInfo { Status = CdBurnService.BurnMediaStatus.DriveError };
        }

        // A newer probe superseded this one (user flipped drives mid-check).
        if (generation != _probeGeneration)
        {
            return;
        }

        _media = probed;
        ApplyMediaState();
    }

    private void ApplyMediaState()
    {
        if (_media is not { } m)
        {
            MediaText.Text = "—";
            SetModeItems(cdModes: true, dvdMode: false);
            AudioCdItem.IsEnabled = true;
            DataCdItem.IsEnabled = false;
            DataDvdItem.IsEnabled = false;
            if (ModeCombo.SelectedIndex < 0)
            {
                ModeCombo.SelectedItem = AudioCdItem;
            }

            UpdateModeDependentUi();
            UpdateBurnGate();
            return;
        }

        MediaText.Text = m.Status switch
        {
            CdBurnService.BurnMediaStatus.NoMedia => "No disc",
            CdBurnService.BurnMediaStatus.Busy => "Drive busy — eject and reinsert the disc",
            CdBurnService.BurnMediaStatus.DriveError => "Couldn't read the drive",
            CdBurnService.BurnMediaStatus.NotWritable => $"{m.MediaLabel} — drive can't write",
            CdBurnService.BurnMediaStatus.NotBlank => m.Erasable ? $"{m.MediaLabel} — not blank (erasable)" : $"{m.MediaLabel} — not blank",
            _ => m.CapacitySectors is { } cap
                ? $"{m.MediaLabel} — blank, {FormatCdLength(TimeSpan.FromSeconds(cap / 75.0))}"
                : (m.Profile == CdBurnService.ProfileDvdPlusRw ? $"{m.MediaLabel} — ready" : $"{m.MediaLabel} — blank"),
        };

        // Only modes that make sense for the loaded media are in the list at all: DVD
        // Data exists only while DVD-class media is actually inserted; the CD modes
        // show for CD media and for an empty tray (disabled until a disc lands).
        bool hasMedia = m.Status != CdBurnService.BurnMediaStatus.NoMedia && m.Profile != 0;
        bool cdClass = m.Profile is 0x0008 or CdBurnService.ProfileCdR or CdBurnService.ProfileCdRw;

        SetModeItems(cdModes: !hasMedia || cdClass, dvdMode: hasMedia && !cdClass);

        // Usable now, or one confirmed quick-blank away from it (offered after Burn).
        bool usable = m.Status == CdBurnService.BurnMediaStatus.Ready
                   || (m.Status == CdBurnService.BurnMediaStatus.NotBlank && m.Erasable);

        AudioCdItem.IsEnabled = usable && m.IsCdRecordable;
        DataCdItem.IsEnabled = usable && m.IsCdRecordable;
        DataDvdItem.IsEnabled = usable && m.IsDataDvdCapable;

        if (ModeCombo.SelectedItem is not ComboBoxItem { IsEnabled: true })
        {
            ModeCombo.SelectedItem = ModeCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.IsEnabled);
        }

        UpdateModeDependentUi();
        UpdateBurnGate();
    }

    private void UpdateBurnGate()
    {
        bool modeOk = ModeCombo.SelectedItem is ComboBoxItem { IsEnabled: true };
        bool mediaOk = _media is not { } m
            || m.Status == CdBurnService.BurnMediaStatus.Ready
            || (m.Status == CdBurnService.BurnMediaStatus.NotBlank && m.Erasable);
        BurnButton.IsEnabled = modeOk && mediaOk && !_overCapacity;
    }

    private void UpdateModeDependentUi()
    {
        bool audio = SelectedMode is BurnDiscMode.AudioCd or null;

        TracksLabel.Text = audio ? "Tracks:" : "Files:";
        LengthLabel.Text = audio ? "Length:" : "Size:";

        // Speed programs the audio DAO write; the data path has no speed knob today.
        SpeedCombo.IsEnabled = audio;

        // CD-TEXT lives in the audio lead-in; a data disc's title is its volume label.
        CdTextCheck.IsEnabled = audio;

        // Inter-track gap is a CD-DA pregap concept - meaningless on data discs.
        GapInput.IsEnabled = audio;

        // Rewritable media refuses simulation (MMC prohibits test writes on high-speed RW).
        bool testable = _media is { } m && m.Status == CdBurnService.BurnMediaStatus.Ready && !m.IsRewritable;
        TestWriteCheck.IsEnabled = testable;
        if (!testable)
        {
            TestWriteCheck.IsChecked = false;
        }

        LengthText.Foreground = _defaultLengthBrush;
        DiscsRow.IsVisible = false;
        _overCapacity = false;

        if (audio)
        {
            // The gap burns as real pregap sectors between tracks, so it counts
            // against the disc exactly like audio does.
            var gap = TimeSpan.FromSeconds((double)(GapInput.Value ?? 0));
            var effectiveLength = _totalLength + (_trackCount > 1 ? (_trackCount - 1) * gap : TimeSpan.Zero);

            var lengthStr = FormatCdLength(effectiveLength);
            if (_media?.CapacitySectors is { } sectors)
            {
                var capacity = TimeSpan.FromSeconds(sectors / 75.0);
                if (effectiveLength > capacity)
                {
                    LengthText.Text = $"{lengthStr} — exceeds {FormatCdLength(capacity)}";
                    LengthText.Foreground = OverCapacityBrush;
                    _overCapacity = true;

                    long discs = (effectiveLength.Ticks + capacity.Ticks - 1) / capacity.Ticks;
                    DiscsText.Text = $"{discs} × {FormatCdLength(capacity)}";
                    DiscsRow.IsVisible = true;
                }
                else
                {
                    LengthText.Text = $"{lengthStr} of {FormatCdLength(capacity)}";
                }
            }
            else if (effectiveLength > TimeSpan.FromMinutes(80))
            {
                LengthText.Text = $"{lengthStr} — over 80:00";
                LengthText.Foreground = NearCapacityBrush;
            }
            else
            {
                LengthText.Text = lengthStr;
            }

            return;
        }

        // Data modes compare bytes: CD Mode 1 sectors carry 2048 bytes, DVD+RW falls
        // back to the 4.7 GB nominal because the drive doesn't report a capacity.
        long? capBytes = SelectedMode == BurnDiscMode.DataCd
            ? (_media?.CapacitySectors is { } cs ? cs * 2048L : null)
            : (_media?.IsDataDvdCapable == true ? DvdNominalBytes : null);

        var sizeStr = FormatDataSize(_totalDataBytes);
        if (capBytes is { } cb)
        {
            if (_totalDataBytes > cb)
            {
                LengthText.Text = $"{sizeStr} — exceeds {FormatDataSize(cb)}";
                LengthText.Foreground = OverCapacityBrush;
                _overCapacity = true;

                // Too big for one disc: show how many of this medium the set needs.
                long discs = (_totalDataBytes + cb - 1) / cb;
                DiscsText.Text = $"{discs} × {FormatDataSize(cb)}";
                DiscsRow.IsVisible = true;
            }
            else
            {
                LengthText.Text = $"{sizeStr} of {FormatDataSize(cb)}";
            }
        }
        else
        {
            LengthText.Text = sizeStr;
        }
    }

    /// <summary>
    /// The mode list holds only applicable items - ComboBox virtualization ignores
    /// ComboBoxItem.IsVisible, so inapplicable modes are removed outright.
    /// </summary>
    private void SetModeItems(bool cdModes, bool dvdMode)
    {
        var desired = new List<ComboBoxItem>();
        if (cdModes)
        {
            desired.Add(AudioCdItem);
            desired.Add(DataCdItem);
        }

        if (dvdMode)
        {
            desired.Add(DataDvdItem);
        }

        if (ModeCombo.Items.OfType<ComboBoxItem>().SequenceEqual(desired))
        {
            return;
        }

        var selected = ModeCombo.SelectedItem;
        ModeCombo.Items.Clear();
        foreach (var item in desired)
        {
            ModeCombo.Items.Add(item);
        }

        ModeCombo.SelectedItem = selected is ComboBoxItem s && desired.Contains(s) ? s : null;
    }

    private void DriveCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        _ = ProbeSelectedDriveAsync();
    }

    private void ModeCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        UpdateModeDependentUi();
        UpdateBurnGate();
    }

    private void GapInput_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        UpdateModeDependentUi();
        UpdateBurnGate();
    }

    /// <summary>CD-style minutes:seconds, minutes uncapped ("77:54").</summary>
    internal static string FormatCdLength(TimeSpan length)
    {
        return $"{(int)length.TotalMinutes}:{length.Seconds:D2}";
    }

    /// <summary>Data sizes the way disc vendors label them: MB under a gig, GB over.</summary>
    internal static string FormatDataSize(long bytes)
    {
        return bytes >= 1_000_000_000
            ? $"{bytes / 1_000_000_000.0:0.#} GB"
            : $"{bytes / 1_000_000.0:0} MB";
    }

    private void BurnButton_Click(object? sender, RoutedEventArgs e)
    {
        var drive = SelectedDrive;
        var mode = SelectedMode;
        if (drive == null || mode == null)
        {
            Close(null);
            return;
        }

        var speed = (SpeedCombo.SelectedItem as ComboBoxItem)?.Tag is string speedTag && int.TryParse(speedTag, out var kbps) ? (int?)kbps : null;

        Close(new BurnDiscChoice
        {
            DrivePath = drive,
            Mode = mode.Value,
            DiscTitle = string.IsNullOrWhiteSpace(TitleInput.Text) ? null : TitleInput.Text.Trim(),
            TestWrite = TestWriteCheck.IsChecked == true,
            WriteCdText = CdTextCheck.IsChecked == true,
            GapSeconds = (double)(GapInput.Value ?? 2),
            WriteSpeedKBps = mode == BurnDiscMode.AudioCd ? speed : null,
        });
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }
}
