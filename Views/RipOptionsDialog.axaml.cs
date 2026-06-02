// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OrgZ.Views;

public partial class RipOptionsDialog : Window
{
    private CdRipOptions _result;

    public RipOptionsDialog()
    {
        InitializeComponent();
        WindowSizeTracker.Track(this, "RipOptions");
        _result = CdRipOptions.Default;
        LoadFromOptions(_result);
        ApplyFormatVisibility(_result.Format);
    }

    public RipOptionsDialog(CdRipOptions initial) : this()
    {
        _result = initial;
        LoadFromOptions(initial);
        ApplyFormatVisibility(initial.Format);
    }

    public CdRipOptions? Result { get; private set; }

    private void LoadFromOptions(CdRipOptions options)
    {
        FormatCombo.SelectedIndex = (int)options.Format;

        FlacCompressionCombo.SelectedIndex = options.FlacCompression switch
        {
            0 => 0,
            3 => 1,
            5 => 2,
            6 => 3,
            8 => 4,
            _ => 2,
        };

        Mp3ModeCombo.SelectedIndex = (int)options.Mp3Mode;

        Mp3VbrCombo.SelectedIndex = options.Mp3Quality switch
        {
            0 => 0,
            2 => 1,
            4 => 2,
            6 => 3,
            9 => 4,
            _ => 1,
        };

        var cbrIdx = Array.IndexOf(CdRipOptions.CbrBitrates, options.Mp3Quality);
        Mp3CbrCombo.SelectedIndex = cbrIdx >= 0 ? cbrIdx : 4; // 192 kbps default

        ApplyMp3ModeVisibility();
    }

    private void ApplyFormatVisibility(RipFormat format)
    {
        WavPanel.IsVisible = format == RipFormat.Wav;
        FlacPanel.IsVisible = format == RipFormat.Flac;
        Mp3Panel.IsVisible = format == RipFormat.Mp3;
    }

    private void ApplyMp3ModeVisibility()
    {
        var isVbr = Mp3ModeCombo.SelectedIndex == (int)Mp3Mode.Vbr;
        Mp3VbrRow.IsVisible = isVbr;
        Mp3CbrRow.IsVisible = !isVbr;
    }

    private void FormatCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (FormatCombo.SelectedIndex >= 0)
        {
            ApplyFormatVisibility((RipFormat)FormatCombo.SelectedIndex);
        }
    }

    private void Mp3ModeCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        ApplyMp3ModeVisibility();
    }

    private void OkButton_Click(object? sender, RoutedEventArgs e)
    {
        Result = BuildOptions();
        Close(Result);
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Result = null;
        Close(null);
    }

    private CdRipOptions BuildOptions()
    {
        var format = FormatCombo.SelectedIndex >= 0
            ? (RipFormat)FormatCombo.SelectedIndex
            : RipFormat.Flac;

        var flacCompression = (FlacCompressionCombo.SelectedItem as ComboBoxItem)?.Tag is string flacTag
            && int.TryParse(flacTag, out var flac)
            ? flac
            : 5;

        var mp3Mode = Mp3ModeCombo.SelectedIndex == (int)Mp3Mode.Cbr
            ? Mp3Mode.Cbr
            : Mp3Mode.Vbr;

        int mp3Quality;
        if (mp3Mode == Mp3Mode.Cbr)
        {
            mp3Quality = (Mp3CbrCombo.SelectedItem as ComboBoxItem)?.Tag is string cbrTag
                && int.TryParse(cbrTag, out var cbr)
                ? cbr
                : 192;
        }
        else
        {
            mp3Quality = (Mp3VbrCombo.SelectedItem as ComboBoxItem)?.Tag is string vbrTag
                && int.TryParse(vbrTag, out var vbr)
                ? vbr
                : 2;
        }

        return new CdRipOptions
        {
            Format = format,
            FlacCompression = flacCompression,
            Mp3Mode = mp3Mode,
            Mp3Quality = mp3Quality,
            // Paranoia (re-read attempts) is no longer user-facing - keep whatever
            // the caller supplied (defaults to CdRipOptions.Default's value).
            ReReadAttempts = _result.ReReadAttempts,
        };
    }
}
