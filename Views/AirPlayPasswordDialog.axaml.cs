// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OrgZ.Views;

/// <summary>
/// Asks for a receiver's AirPlay password - the one a HomePod requires when "Require
/// Password" is set for speaker access in the Home app.
/// </summary>
/// <remarks>
/// Returns the password, or null when cancelled. "Remember" is reported separately via
/// <see cref="Remember"/> rather than folded into the result, because declining to store a
/// password is not the same as declining to use one.
/// </remarks>
public partial class AirPlayPasswordDialog : Window
{
    public AirPlayPasswordDialog() : this(null) { }

    public AirPlayPasswordDialog(string? deviceName, string? existingPassword = null)
    {
        InitializeComponent();

        if (!string.IsNullOrEmpty(deviceName))
        {
            PromptText.Text = $"Password for “{deviceName}”:";
        }

        if (!string.IsNullOrEmpty(existingPassword))
        {
            PasswordInput.Text = existingPassword;
        }

        Loaded += (_, _) =>
        {
            PasswordInput.Focus();
            PasswordInput.CaretIndex = PasswordInput.Text?.Length ?? 0;
        };
    }

    /// <summary>Whether the password should be persisted for this receiver.</summary>
    public bool Remember => RememberInput.IsChecked == true;

    private void OkButton_Click(object? sender, RoutedEventArgs e)
    {
        // Enter submits, but an empty password would just re-fail the handshake.
        if (!string.IsNullOrEmpty(PasswordInput.Text))
        {
            Close(PasswordInput.Text);
        }
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(null);
}
