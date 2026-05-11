// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OrgZ.Services.AudioOutput;

namespace OrgZ.Views;

/// <summary>
/// Compact "now playing" window styled after the classic iTunes brushed-metal
/// mini-player.  Shares its <see cref="Window.DataContext"/> with the main
/// <see cref="MainWindow"/> so every binding (title, artist, art, seek, transport)
/// stays in lockstep with the full player.
/// </summary>
/// <remarks>
/// <para>
/// Left chrome swaps per-platform: traffic lights on macOS, square close/expand
/// buttons on Windows / Linux.  The rest of the chassis is identical across platforms.
/// </para>
/// <para>
/// The window uses <see cref="SystemDecorations.None"/> with a transparent
/// window background so the rounded metal <see cref="Border"/> renders cleanly
/// without the OS drawing a square frame behind the corners.
/// </para>
/// </remarks>
public partial class MiniPlayerWindow : Window
{
    public event Action? RestoreMainRequested;
    public event Action? FullScreenRequested;

    // iTunes-style stereo segmented VU: N columns × M rows of ticks per
    // channel, left channel mirrored (columns reversed) so low frequencies
    // meet in the middle and highs sit at the outer edges — butterfly
    // symmetry.  Tick size is computed from canvas dimensions in
    // LayoutVuBars so the meter fills the LCD regardless of size.
    private const int VuColumnsPerChannel = 10;
    private const int VuRowsPerColumn = 12;
    private const double VuChannelGap = 8;
    private const double VuTickGap = 1;
    private double _vuTickWidth = 4;
    private double _vuTickHeight = 2;

    private readonly Rectangle[,] _vuTicksLeft = new Rectangle[VuColumnsPerChannel, VuRowsPerColumn];
    private readonly Rectangle[,] _vuTicksRight = new Rectangle[VuColumnsPerChannel, VuRowsPerColumn];
    private readonly float[] _vuLevelsLeft = new float[VuColumnsPerChannel];
    private readonly float[] _vuLevelsRight = new float[VuColumnsPerChannel];
    private readonly int[] _vuLastLitLeft = new int[VuColumnsPerChannel];
    private readonly int[] _vuLastLitRight = new int[VuColumnsPerChannel];
    // Peak-hold state per column: the peak slowly decays back down once the
    // main bar drops, leaving a "floating" line above the meter that dips
    // toward the bar over time.  Classic LED-meter aesthetic.
    private readonly float[] _vuPeakLeft = new float[VuColumnsPerChannel];
    private readonly float[] _vuPeakRight = new float[VuColumnsPerChannel];
    private readonly Rectangle[] _vuPeakMarkLeft = new Rectangle[VuColumnsPerChannel];
    private readonly Rectangle[] _vuPeakMarkRight = new Rectangle[VuColumnsPerChannel];

    private DispatcherTimer? _vuTimer;
    private bool _vuMode;

    // Per-frame smoothing at the UI layer — lets bars fall gracefully when
    // playback pauses or a song goes quiet, instead of snapping to zero.
    private const float VuDecayStep = 0.05f;   // linear drop per frame
    private const float VuPeakDecayStep = 0.012f; // peak falls slower than bar

    private static readonly IBrush VuOnBrush = new SolidColorBrush(Color.Parse("#3A4A30"));
    private static readonly IBrush VuOffBrush = new SolidColorBrush(Color.FromArgb(80, 0x3A, 0x4A, 0x30));
    private static readonly IBrush VuPeakBrush = new SolidColorBrush(Color.Parse("#1E2014"));

    public MiniPlayerWindow()
    {
        InitializeComponent();
        WindowSizeTracker.Track(this, "MiniPlayer");

        var isMac = OperatingSystem.IsMacOS();
        MacChrome.IsVisible = isMac;
        WinChrome.IsVisible = !isMac;

        BuildVuBars();

        // Seed the Always-On-Top menu tick from the current Topmost value —
        // the XAML default is true but a future session may override it.
        if (MenuAlwaysOnTop is MenuItem mi)
        {
            mi.Icon = new Avalonia.Controls.Shapes.Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = Topmost ? Avalonia.Media.Brushes.Gray : Avalonia.Media.Brushes.Transparent,
            };
        }
    }

    // -- Drag region -----------------------------------------------------

    private void Chassis_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void Lcd_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Ignore clicks that landed on the seek slider — the slider owns
        // those gestures.  Everything else on the LCD toggles the VU meter.
        if (IsWithinSeekSlider(e.Source as Avalonia.Visual))
        {
            return;
        }

        if (e.ClickCount >= 2)
        {
            FullScreenRequested?.Invoke();
            e.Handled = true;
            return;
        }

        ToggleVuMode();
        e.Handled = true;
    }

    private static bool IsWithinSeekSlider(Avalonia.Visual? source)
    {
        while (source != null)
        {
            if (source is Slider)
            {
                return true;
            }
            source = source.GetVisualParent();
        }
        return false;
    }

    // -- Windows / Linux chrome ------------------------------------------

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void Expand_Click(object? sender, RoutedEventArgs e)
    {
        RestoreMainRequested?.Invoke();
        Close();
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    // -- macOS traffic-light chrome --------------------------------------

    private void MacClose_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Close();
        e.Handled = true;
    }

    private void MacMinimize_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        WindowState = WindowState.Minimized;
        e.Handled = true;
    }

    private void MacExpand_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        RestoreMainRequested?.Invoke();
        Close();
        e.Handled = true;
    }

    // -- Context menu ----------------------------------------------------

    private void MenuFullScreen_Click(object? sender, RoutedEventArgs e)
    {
        FullScreenRequested?.Invoke();
    }

    private void MenuExpand_Click(object? sender, RoutedEventArgs e)
    {
        RestoreMainRequested?.Invoke();
        Close();
    }

    private void MenuClose_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void MenuAlwaysOnTop_Click(object? sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        if (sender is MenuItem mi)
        {
            mi.Icon = new Avalonia.Controls.Shapes.Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = Topmost ? Avalonia.Media.Brushes.Gray : Avalonia.Media.Brushes.Transparent,
            };
        }
    }

    // -- Audio output flyout --------------------------------------------

    private void AudioOutputButton_Click(object? sender, RoutedEventArgs e)
    {
        PopulateAudioOutputFlyout();
    }

    private void AudioOutputRefresh_Click(object? sender, RoutedEventArgs e)
    {
        PopulateAudioOutputFlyout();
    }

    private async void AudioOutputOpenSettings_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is OrgZ.ViewModels.MainWindowViewModel vm)
        {
            AudioOutputButton.Flyout?.Hide();
            await vm.ShowSettings();
        }
    }

    private void PopulateAudioOutputFlyout()
    {
        var vm = DataContext as OrgZ.ViewModels.MainWindowViewModel;
        if (vm == null)
        {
            return;
        }

        var manager = vm._audioOutput;
        AudioOutputDeviceList.Children.Clear();

        var devices = manager.EnumerateAllDevices();
        var activeSinks = manager.Bus.Sinks.ToDictionary(s => s.Id, s => s);
        AudioOutputFlyoutHint.Text = $"{devices.Count} device(s). Tick to route audio.";

        string? lastProvider = null;
        foreach (var device in devices)
        {
            if (device.ProviderName != lastProvider)
            {
                lastProvider = device.ProviderName;
                AudioOutputDeviceList.Children.Add(new TextBlock
                {
                    Text = device.ProviderName,
                    FontWeight = FontWeight.Bold,
                    FontSize = 11,
                    Opacity = 0.75,
                    Margin = new Thickness(0, 6, 0, 2),
                });
            }

            AudioOutputDeviceList.Children.Add(BuildFlyoutRow(manager, device, activeSinks));
        }
    }

    private Control BuildFlyoutRow(AudioOutputManager manager, AudioDeviceInfo device, Dictionary<string, IAudioSink> activeSinks)
    {
        var active = activeSinks.TryGetValue(device.QualifiedId, out var sink);
        var initialVolume = sink?.Volume ?? 1f;

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(0, 1, 0, 1),
        };

        var check = new CheckBox { IsChecked = active, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(check, 0);
        grid.Children.Add(check);

        var label = new TextBlock
        {
            Text = device.DisplayName,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 6, 0),
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = initialVolume * 100,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 100,
            IsEnabled = active,
        };
        Grid.SetColumn(slider, 2);
        grid.Children.Add(slider);

        check.IsCheckedChanged += (_, _) =>
        {
            slider.IsEnabled = check.IsChecked == true;
            ApplyFlyoutSelection(manager, device, check, slider);
        };

        slider.PropertyChanged += (_, ev) =>
        {
            if (ev.Property.Name == nameof(Slider.Value))
            {
                ApplyFlyoutSelection(manager, device, check, slider);
            }
        };

        return grid;
    }

    private static void ApplyFlyoutSelection(AudioOutputManager manager, AudioDeviceInfo device, CheckBox check, Slider slider)
    {
        // Rebuild from the currently-active sinks so toggling one device
        // doesn't drop the others.  The bus owns the full selection state.
        var selections = manager.Bus.Sinks
            .Where(s => s.Id != device.QualifiedId)
            .Select(s => new AudioOutputManager.SinkSelection
            {
                QualifiedId = s.Id,
                Volume = s.Volume,
                IsMuted = s.IsMuted,
            })
            .ToList();

        if (check.IsChecked == true)
        {
            selections.Add(new AudioOutputManager.SinkSelection
            {
                QualifiedId = device.QualifiedId,
                Volume = (float)(slider.Value / 100.0),
                IsMuted = false,
            });
        }

        manager.ApplySelections(selections);
        manager.SavePersistedSelections();
    }

    // -- VU meter --------------------------------------------------------

    /// <summary>
    /// Creates the static bar rectangles once.  The bars' heights are animated
    /// by <see cref="TickVuMeter"/> — the rectangles themselves are stable
    /// instances, so the Canvas doesn't churn layout every frame.
    /// </summary>
    private void BuildVuBars()
    {
        VuCanvas.Children.Clear();

        AllocateTicks(_vuTicksLeft);
        AllocateTicks(_vuTicksRight);
        AllocatePeakMarks(_vuPeakMarkLeft);
        AllocatePeakMarks(_vuPeakMarkRight);

        foreach (var t in _vuTicksLeft) VuCanvas.Children.Add(t);
        foreach (var t in _vuTicksRight) VuCanvas.Children.Add(t);
        foreach (var p in _vuPeakMarkLeft) VuCanvas.Children.Add(p);
        foreach (var p in _vuPeakMarkRight) VuCanvas.Children.Add(p);

        VuCanvas.SizeChanged += (_, _) => LayoutVuBars();
    }

    private static void AllocatePeakMarks(Rectangle[] marks)
    {
        for (int c = 0; c < marks.Length; c++)
        {
            var r = new Rectangle { Fill = VuPeakBrush, Opacity = 0 };
            Avalonia.Media.RenderOptions.SetEdgeMode(r, Avalonia.Media.EdgeMode.Aliased);
            marks[c] = r;
        }
    }

    private static void AllocateTicks(Rectangle[,] grid)
    {
        for (int c = 0; c < grid.GetLength(0); c++)
        {
            for (int r = 0; r < grid.GetLength(1); r++)
            {
                var rect = new Rectangle { Fill = VuOffBrush };
                // Aliased edges: tick rectangles should be crisp pixel blocks,
                // not antialiased.  AA + fractional positions round differently
                // per column and produce uneven-looking gaps.
                Avalonia.Media.RenderOptions.SetEdgeMode(rect, Avalonia.Media.EdgeMode.Aliased);
                grid[c, r] = rect;
            }
        }
    }

    private void LayoutVuBars()
    {
        var canvasW = VuCanvas.Bounds.Width;
        var canvasH = VuCanvas.Bounds.Height;
        if (canvasW <= 0 || canvasH <= 0)
        {
            return;
        }

        // Drive the layout in integer PHYSICAL pixels so every position /
        // size lands on a device-pixel boundary.  Converting back to DIP
        // (logical units) via `/ scale` gives Avalonia values that render
        // without subpixel drift — uniform column gaps, uniform row gaps,
        // regardless of DPI (100% / 125% / 150%).
        var scale = RenderScaling;

        int canvasW_phys = (int)Math.Floor(canvasW * scale);
        int canvasH_phys = (int)Math.Floor(canvasH * scale);
        int channelGap_phys = (int)Math.Round(VuChannelGap * scale);
        int tickGap_phys = Math.Max(1, (int)Math.Round(VuTickGap * scale));

        int availableW_phys = (canvasW_phys - channelGap_phys) / 2;
        int tickW_phys = Math.Max(2, (availableW_phys - (VuColumnsPerChannel - 1) * tickGap_phys) / VuColumnsPerChannel);
        int tickH_phys = Math.Max(2, (canvasH_phys - (VuRowsPerColumn - 1) * tickGap_phys) / VuRowsPerColumn);

        _vuTickWidth = tickW_phys / scale;
        _vuTickHeight = tickH_phys / scale;

        int channelW_phys = VuColumnsPerChannel * tickW_phys + (VuColumnsPerChannel - 1) * tickGap_phys;
        int totalW_phys = channelW_phys * 2 + channelGap_phys;
        int startX_phys = Math.Max(0, (canvasW_phys - totalW_phys) / 2);

        int totalH_phys = VuRowsPerColumn * tickH_phys + (VuRowsPerColumn - 1) * tickGap_phys;
        int bottomPad_phys = Math.Max(0, (canvasH_phys - totalH_phys) / 2);

        PositionChannel(_vuTicksLeft, startX_phys, tickW_phys, tickH_phys, tickGap_phys, bottomPad_phys, scale, mirror: false);
        PositionChannel(_vuTicksRight, startX_phys + channelW_phys + channelGap_phys, tickW_phys, tickH_phys, tickGap_phys, bottomPad_phys, scale, mirror: true);

        // Peak markers use the same X positions as their columns, same
        // width as a tick, same height (will be positioned by Y each frame).
        PositionPeakMarks(_vuPeakMarkLeft, startX_phys, tickW_phys, tickH_phys, tickGap_phys, scale, mirror: false);
        PositionPeakMarks(_vuPeakMarkRight, startX_phys + channelW_phys + channelGap_phys, tickW_phys, tickH_phys, tickGap_phys, scale, mirror: true);
    }

    private void PositionPeakMarks(Rectangle[] marks, int originX_phys, int tickW_phys, int tickH_phys, int tickGap_phys, double scale, bool mirror)
    {
        double tickW = tickW_phys / scale;
        double tickH = tickH_phys / scale;
        for (int c = 0; c < VuColumnsPerChannel; c++)
        {
            int visualIndex = mirror ? (VuColumnsPerChannel - 1 - c) : c;
            int colX_phys = originX_phys + visualIndex * (tickW_phys + tickGap_phys);
            marks[c].Width = tickW;
            marks[c].Height = tickH;
            Canvas.SetLeft(marks[c], colX_phys / scale);
        }
    }

    private void PositionChannel(Rectangle[,] grid, int originX_phys, int tickW_phys, int tickH_phys, int tickGap_phys, int bottomPad_phys, double scale, bool mirror)
    {
        double tickW = tickW_phys / scale;
        double tickH = tickH_phys / scale;

        for (int c = 0; c < VuColumnsPerChannel; c++)
        {
            int visualIndex = mirror ? (VuColumnsPerChannel - 1 - c) : c;
            int colX_phys = originX_phys + visualIndex * (tickW_phys + tickGap_phys);
            double colX = colX_phys / scale;

            for (int r = 0; r < VuRowsPerColumn; r++)
            {
                int rowY_phys = bottomPad_phys + r * (tickH_phys + tickGap_phys);
                double rowY = rowY_phys / scale;

                var rect = grid[c, r];
                rect.Width = tickW;
                rect.Height = tickH;
                Canvas.SetLeft(rect, colX);
                Canvas.SetBottom(rect, rowY);
            }
        }
    }

    private void VuToggle_Click(object? sender, RoutedEventArgs e) => ToggleVuMode();

    private void ToggleVuMode()
    {
        _vuMode = !_vuMode;

        LcdTextLayer.IsVisible = !_vuMode;
        VuCanvas.IsVisible = _vuMode;

        if (_vuMode)
        {
            // Audio tap is permanently attached in the ViewModel; spectrum
            // bands are available the moment a track is playing.  Just lay
            // out the bars and start the render timer.
            LayoutVuBars();
            StartVuTimer();
        }
        else
        {
            StopVuTimer();
        }
    }

    private void StartVuTimer()
    {
        _vuTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Normal, (_, _) => TickVuMeter());
        _vuTimer.Start();
    }

    private void StopVuTimer()
    {
        _vuTimer?.Stop();
        // Reset all ticks to the "off" state so the final frame is clean,
        // and reset the last-lit cache so the next render repopulates
        // everything from scratch (otherwise ticks stay visually "off"
        // but the cache still thinks they're "on" → no-op on next tick).
        foreach (var t in _vuTicksLeft) t.Fill = VuOffBrush;
        foreach (var t in _vuTicksRight) t.Fill = VuOffBrush;
        foreach (var p in _vuPeakMarkLeft) p.Opacity = 0;
        foreach (var p in _vuPeakMarkRight) p.Opacity = 0;
        Array.Clear(_vuLastLitLeft);
        Array.Clear(_vuLastLitRight);
        Array.Clear(_vuLevelsLeft);
        Array.Clear(_vuLevelsRight);
        Array.Clear(_vuPeakLeft);
        Array.Clear(_vuPeakRight);
    }

    private void TickVuMeter()
    {
        var source = (DataContext as OrgZ.ViewModels.MainWindowViewModel)?.AudioVisualization;
        if (source == null)
        {
            return;
        }

        // Band count in the analyzer is 24 by default.  Fold into VuColumnsPerChannel
        // by averaging groups — one band per column.
        Span<float> left = stackalloc float[source.BandCount];
        Span<float> right = stackalloc float[source.BandCount];
        source.CopyBandLevelsStereo(left, right);

        FoldAndRender(left, _vuTicksLeft, _vuLevelsLeft, _vuLastLitLeft, _vuPeakLeft, _vuPeakMarkLeft);
        FoldAndRender(right, _vuTicksRight, _vuLevelsRight, _vuLastLitRight, _vuPeakRight, _vuPeakMarkRight);
    }

    private void FoldAndRender(Span<float> source, Rectangle[,] ticks, float[] smoothed, int[] lastLit, float[] peaks, Rectangle[] peakMarks)
    {
        var srcLen = source.Length;
        if (srcLen == 0)
        {
            return;
        }

        var canvasH = VuCanvas.Bounds.Height;
        var usableH = canvasH;
        if (usableH <= 0)
        {
            return;
        }

        for (int c = 0; c < VuColumnsPerChannel; c++)
        {
            int start = c * srcLen / VuColumnsPerChannel;
            int end = (c + 1) * srcLen / VuColumnsPerChannel;
            if (end <= start) end = start + 1;

            float sum = 0;
            for (int i = start; i < end; i++)
            {
                sum += source[i];
            }
            float target = Math.Clamp(sum / (end - start), 0f, 1f);

            // UI-layer smoothing: fast attack, slow linear decay.  When
            // audio pauses or a song goes quiet, bars fall smoothly to
            // zero instead of snapping.
            if (target > smoothed[c])
            {
                smoothed[c] = target;
            }
            else
            {
                smoothed[c] = Math.Max(target, smoothed[c] - VuDecayStep);
            }

            // Peak: matches bar on the way up, slow decay on the way down.
            if (smoothed[c] > peaks[c])
            {
                peaks[c] = smoothed[c];
            }
            else
            {
                peaks[c] = Math.Max(smoothed[c], peaks[c] - VuPeakDecayStep);
            }

            int lit = (int)Math.Round(smoothed[c] * VuRowsPerColumn);
            int previousLit = lastLit[c];

            if (lit != previousLit)
            {
                int low = Math.Min(previousLit, lit);
                int high = Math.Max(previousLit, lit);
                for (int r = low; r < high; r++)
                {
                    ticks[c, r].Fill = r < lit ? VuOnBrush : VuOffBrush;
                }
                lastLit[c] = lit;
            }

            // Peak marker: position at the peak's Y, only visible when the
            // peak is above the current bar by at least one row.
            int peakRow = Math.Min(VuRowsPerColumn - 1, (int)Math.Round(peaks[c] * VuRowsPerColumn));
            var mark = peakMarks[c];
            if (peakRow > lit && peaks[c] > 0.02f)
            {
                // Canvas.Bottom of the peak-mark = the same Y as the tick
                // at row peakRow.  Keep the mark's position in sync with
                // the tick grid — reuse the tick's Canvas.Bottom.
                Canvas.SetBottom(mark, Canvas.GetBottom(ticks[c, peakRow]));
                mark.Opacity = 1;
            }
            else
            {
                mark.Opacity = 0;
            }
        }
    }
}
