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

    public MiniPlayerWindow()
    {
        InitializeComponent();
        WindowSizeTracker.Track(this, "MiniPlayer");

        var isMac = OperatingSystem.IsMacOS();
        MacChrome.IsVisible = isMac;
        WinChrome.IsVisible = !isMac;

        // Seed the Always-On-Top menu tick from the current Topmost value -
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

    private void LeftEdgeResize_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginResizeDrag(WindowEdge.West, e);
            e.Handled = true;
        }
    }

    private void RightEdgeResize_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginResizeDrag(WindowEdge.East, e);
            e.Handled = true;
        }
    }

    private void Lcd_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Single-click is a no-op - the chevron button inside the LCD owns
        // page cycling now. Double-click still goes full-screen. Clicks on
        // the seek slider are owned by the slider itself.
        if (IsWithinSeekSlider(e.Source as Avalonia.Visual))
        {
            return;
        }

        if (e.ClickCount >= 2)
        {
            FullScreenRequested?.Invoke();
            e.Handled = true;
        }
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
        if (DataContext is not OrgZ.ViewModels.MainWindowViewModel vm)
        {
            return;
        }

        AudioOutputFlyoutHelper.Populate(vm._audioOutput, AudioOutputDeviceList, AudioOutputFlyoutHint);
    }
}
