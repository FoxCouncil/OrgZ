// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace OrgZ.Views;

/// <summary>
/// Full-screen "now playing" view.  Reuses the existing ViewModel bindings so every
/// player action (seek, transport, metadata) stays in sync with the main window.
/// Exits on Esc, double-click on the art, or the top-right close button.
/// </summary>
/// <remarks>
/// After 3 seconds of no pointer movement we fade the cursor + chrome (transport
/// row, close button, hint text) so the viewer sees nothing but the album art
/// and track name.  Any mouse move restores them instantly.
/// </remarks>
public partial class NowPlayingFullScreenWindow : Window
{
    private static readonly TimeSpan IdleThreshold = TimeSpan.FromSeconds(3);

    private readonly DispatcherTimer _idleTimer;
    private DateTime _lastActivity = DateTime.UtcNow;
    private bool _chromeHidden;

    public NowPlayingFullScreenWindow()
    {
        InitializeComponent();

        _idleTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => CheckIdle());
        Opened += (_, _) => _idleTimer.Start();
        Closed += (_, _) => _idleTimer.Stop();

        // Any pointer movement anywhere in the window wakes the UI back up.
        PointerMoved += OnAnyPointerActivity;
        PointerPressed += OnAnyPointerActivity;
        KeyDown += (_, _) => BumpActivity();
    }

    private void OnAnyPointerActivity(object? sender, PointerEventArgs e)
    {
        BumpActivity();
    }

    private void BumpActivity()
    {
        _lastActivity = DateTime.UtcNow;
        if (_chromeHidden)
        {
            Cursor = new Cursor(StandardCursorType.Arrow);
            if (this.FindControl<Control>("TransportLayer") is { } chrome)
            {
                chrome.IsVisible = true;
            }
            _chromeHidden = false;
        }
    }

    private void CheckIdle()
    {
        if (_chromeHidden)
        {
            return;
        }

        if (DateTime.UtcNow - _lastActivity < IdleThreshold)
        {
            return;
        }

        Cursor = new Cursor(StandardCursorType.None);
        if (this.FindControl<Control>("TransportLayer") is { } chrome)
        {
            chrome.IsVisible = false;
        }
        _chromeHidden = true;
    }

    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape || e.Key == Key.F11)
        {
            Close();
            e.Handled = true;
        }
    }

    private void Window_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount >= 2)
        {
            Close();
            e.Handled = true;
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
