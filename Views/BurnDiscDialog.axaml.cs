// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace OrgZ.Views;

/// <summary>What the user confirmed in <see cref="BurnDiscDialog"/>.</summary>
public sealed record BurnDiscChoice
{
    public required string DrivePath { get; init; }
    public string? DiscTitle { get; init; }
    public bool TestWrite { get; init; }

    /// <summary>Burn speed in kB/s (176 = 1x audio); null = drive maximum.</summary>
    public int? WriteSpeedKBps { get; init; }
}

public partial class BurnDiscDialog : Window
{
    private static readonly IBrush OverCapacityBrush = new SolidColorBrush(Color.Parse("#C01C28"));
    private static readonly IBrush NearCapacityBrush = new SolidColorBrush(Color.Parse("#E5A50A"));

    private readonly IReadOnlyList<string> _drives;

    public BurnDiscDialog() : this(["D:"], 0, TimeSpan.Zero, null, null) { }

    /// <summary>
    /// iTunes-style burn confirmation. Shows the target drive (a picker when several
    /// recorders exist), track count, total length against the loaded disc's capacity,
    /// an editable CD-TEXT disc title, and a test-write (simulated, laser-off) toggle.
    /// </summary>
    /// <param name="capacitySectors">Blank disc capacity in CD sectors, or null when the drive didn't report one (falls back to comparing against 80 minutes).</param>
    public BurnDiscDialog(IReadOnlyList<string> drives, int trackCount, TimeSpan totalLength, long? capacitySectors, string? initialTitle)
    {
        InitializeComponent();

        _drives = drives;

        if (drives.Count > 1)
        {
            DriveCombo.ItemsSource = drives;
            DriveCombo.SelectedIndex = 0;
            DriveCombo.IsVisible = true;
            DriveText.IsVisible = false;
        }
        else
        {
            DriveText.Text = drives.Count == 1 ? drives[0] : "—";
        }

        TracksText.Text = trackCount.ToString();
        TitleInput.Text = initialTitle ?? string.Empty;
        SpeedCombo.SelectedIndex = 0;

        var lengthStr = FormatCdLength(totalLength);
        if (capacitySectors is { } sectors)
        {
            var capacity = TimeSpan.FromSeconds(sectors / 75.0);
            if (totalLength > capacity)
            {
                LengthText.Text = $"{lengthStr} — exceeds {FormatCdLength(capacity)}";
                LengthText.Foreground = OverCapacityBrush;
            }
            else
            {
                LengthText.Text = $"{lengthStr} of {FormatCdLength(capacity)}";
            }
        }
        else if (totalLength > TimeSpan.FromMinutes(80))
        {
            LengthText.Text = $"{lengthStr} — over 80:00";
            LengthText.Foreground = NearCapacityBrush;
        }
        else
        {
            LengthText.Text = lengthStr;
        }

        Loaded += (_, _) =>
        {
            TitleInput.Focus();
            TitleInput.CaretIndex = TitleInput.Text?.Length ?? 0;
        };
    }

    /// <summary>CD-style minutes:seconds, minutes uncapped ("77:54").</summary>
    internal static string FormatCdLength(TimeSpan length)
    {
        return $"{(int)length.TotalMinutes}:{length.Seconds:D2}";
    }

    private void BurnButton_Click(object? sender, RoutedEventArgs e)
    {
        var drive = DriveCombo.IsVisible && DriveCombo.SelectedItem is string picked ? picked : _drives.FirstOrDefault();
        if (drive == null)
        {
            Close(null);
            return;
        }

        var speed = (SpeedCombo.SelectedItem as ComboBoxItem)?.Tag is string speedTag && int.TryParse(speedTag, out var kbps) ? (int?)kbps : null;

        Close(new BurnDiscChoice
        {
            DrivePath = drive,
            DiscTitle = string.IsNullOrWhiteSpace(TitleInput.Text) ? null : TitleInput.Text.Trim(),
            TestWrite = TestWriteCheck.IsChecked == true,
            WriteSpeedKBps = speed,
        });
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }
}
