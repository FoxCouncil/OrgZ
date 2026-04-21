// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

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
/// The window uses <see cref="SystemDecorations.BorderOnly"/> rather than fully
/// borderless so window-manager resize edges still work - users can widen the
/// display to read longer track names.  Dragging the brushed-metal chassis moves
/// the window via <see cref="Window.BeginMoveDrag"/>.
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

        // Render the platform-appropriate left chrome.  macOS gets the three
        // circular traffic-light buttons, everyone else gets the Windows-style
        // square buttons from the iTunes-for-Windows build.
        var isMac = OperatingSystem.IsMacOS();
        MacChrome.IsVisible = isMac;
        WinChrome.IsVisible = !isMac;
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
        if (e.ClickCount >= 2)
        {
            FullScreenRequested?.Invoke();
            e.Handled = true;
            return;
        }

        // Let single-click on the LCD drag the window too - makes it feel like
        // one solid chassis rather than a button trap.
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    // -- Windows / Linux chrome ------------------------------------------

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Expand_Click(object? sender, RoutedEventArgs e)
    {
        RestoreMainRequested?.Invoke();
        Close();
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

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
}
