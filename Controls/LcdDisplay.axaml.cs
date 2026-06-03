// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using OrgZ.Helpers;
using OrgZ.ViewModels;

namespace OrgZ.Controls;

/// <summary>
/// The iTunes-style LCD display shared by <c>MainWindow</c> and
/// <c>MiniPlayerWindow</c>. Owns the three pages (Playback / VU / Rip), the
/// marquee + edge-fade wiring for the two track-text lines, and the RAF loop
/// that drives the VU meter when its page is active. The host window remains
/// responsible for:
/// <list type="bullet">
///   <item>positioning the LCD in its layout (width, height, margin)</item>
///   <item>any overlay buttons it wants on top (chevron / cancel-X)</item>
///   <item>click behaviour (toggle / navigate / cycle)</item>
/// </list>
/// Apply <c>Classes="mini"</c> to compress fonts + track-line heights for the
/// mini-player surface.
/// </summary>
public partial class LcdDisplay : UserControl
{
    private CancellationTokenSource? _marqueeCts;
    private bool _vuRafScheduled;
    private bool _vuActive;
    private long _vuLastFrameTicks;
    private MainWindowViewModel? _watchedViewModel;

    // Decay rates expressed per second so the fall-off looks identical on
    // 30 / 60 / 120 Hz displays. Same values used for the main window's VU
    // before the LCD extraction.
    private const float MainVuDecayPerSecond = 1.25f;
    private const float MainVuPeakDecayPerSecond = 0.30f;

    public LcdDisplay()
    {
        InitializeComponent();

        DataContextChanged += (_, _) => AttachToViewModel();
        TrackLine1Container.SizeChanged += (_, _) => RestartMarquees();
        TrackLine2Container.SizeChanged += (_, _) => RestartMarquees();
        // Seek-drag handlers: the viewmodel suppresses position writes while
        // the user is dragging the thumb (so the bound Value doesn't fight
        // the libvlc-driven time updates). Tunnel + Bubble + handledEventsToo
        // so the Slider's own internal handling doesn't swallow our notify.
        SeekSlider.AddHandler(InputElement.PointerPressedEvent, SeekSlider_PointerPressed,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        SeekSlider.AddHandler(InputElement.PointerReleasedEvent, SeekSlider_PointerReleased,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        // The Track's manual arrange ignores Style-set Margin on the Thumb,
        // so we set it as a local value once the slider's template is applied.
        // This nudges the entire diamond container down a pixel so it sits
        // inside the 1px LCD frame instead of grazing the top edge.
        SeekSlider.TemplateApplied += (_, e) =>
        {
            if (e.NameScope.Find<Thumb>("thumb") is { } thumb)
            {
                thumb.Margin = new Thickness(0, 1, 0, 0);
            }
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _vuActive = false;
            _marqueeCts?.Cancel();
        };
    }

    private void SeekSlider_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _watchedViewModel?.CurrentTrackTimeNumberPointerPressed();
    }

    private void SeekSlider_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _watchedViewModel?.CurrentTrackTimeNumberPointerReleased();
    }

    /// <summary>
    /// Stops the chevron / cancel-X button click from bubbling to the host's
    /// LCD-body PointerPressed handler (which would otherwise cycle the page
    /// a second time on the mini-player, or fire NavigateToPlaying on the
    /// main window's double-click handler).
    /// </summary>
    private void ChromeButton_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
    }

    private void DurationLabel_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _watchedViewModel?.ToggleDurationDisplay();
        e.Handled = true;
    }

    private void AttachToViewModel()
    {
        if (_watchedViewModel != null)
        {
            _watchedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _watchedViewModel = null;
        }

        if (DataContext is MainWindowViewModel vm)
        {
            _watchedViewModel = vm;
            vm.PropertyChanged += OnViewModelPropertyChanged;
            SyncVuActivation();
            RestartMarquees();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainWindowViewModel.CurrentTrackLine1):
            case nameof(MainWindowViewModel.CurrentTrackLine2):
                RestartMarquees();
                break;
            case nameof(MainWindowViewModel.CurrentLcdPage):
                SyncVuActivation();
                break;
        }
    }

    private void RestartMarquees()
    {
        _marqueeCts = MarqueeHelper.Restart(
            TrackLine1, TrackLine2,
            TrackLine1Container, TrackLine2Container,
            _marqueeCts);
    }

    // -- VU activation -----------------------------------------------------

    /// <summary>
    /// Starts / stops the VU's RAF tick loop based on which LCD page is active.
    /// Idle when the VU page is hidden so we don't burn cycles sampling audio
    /// for a meter that isn't on screen.
    /// </summary>
    private void SyncVuActivation()
    {
        var shouldBeActive = _watchedViewModel?.CurrentLcdPage == MainWindowViewModel.LcdPage.Vu;
        if (shouldBeActive == _vuActive)
        {
            return;
        }
        _vuActive = shouldBeActive;

        if (_vuActive)
        {
            _vuLastFrameTicks = Environment.TickCount64;
            ScheduleNextVuFrame();
        }
        else
        {
            VuControl.Clear();
        }
    }

    private void ScheduleNextVuFrame()
    {
        if (_vuRafScheduled || !_vuActive)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
        {
            return;
        }

        _vuRafScheduled = true;
        topLevel.RequestAnimationFrame(_ =>
        {
            _vuRafScheduled = false;
            if (!_vuActive)
            {
                return;
            }
            TickVuMeter();
            ScheduleNextVuFrame();
        });
    }

    private void TickVuMeter()
    {
        var source = _watchedViewModel?.AudioVisualization;
        if (source == null)
        {
            return;
        }

        // Frame-time delta in seconds — used to scale decay so the visual
        // fall-off rate is identical on 30 / 60 / 120 Hz displays. Clamped to
        // 100 ms so a one-off hitch doesn't slam every bar to zero.
        var now = Environment.TickCount64;
        var dtSec = Math.Min((now - _vuLastFrameTicks) / 1000f, 0.1f);
        _vuLastFrameTicks = now;
        var decay = MainVuDecayPerSecond * dtSec;
        var peakDecay = MainVuPeakDecayPerSecond * dtSec;

        Span<float> left = stackalloc float[source.BandCount];
        Span<float> right = stackalloc float[source.BandCount];
        source.CopyBandLevelsStereo(left, right);

        VuControl.Update(left, right, decay, peakDecay);
    }
}
