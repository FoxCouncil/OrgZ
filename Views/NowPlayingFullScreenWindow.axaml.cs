// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace OrgZ.Views;

/// <summary>
/// Full-screen "now playing" view.  Reuses the existing ViewModel bindings so every
/// player action (seek, transport, metadata) stays in sync with the main window.
/// Exits on Esc, double-click on the art, or the top-right close button.
/// </summary>
public partial class NowPlayingFullScreenWindow : Window
{
    public NowPlayingFullScreenWindow()
    {
        InitializeComponent();
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
