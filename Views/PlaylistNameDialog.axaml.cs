// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OrgZ.Views;

public partial class PlaylistNameDialog : Window
{
    public PlaylistNameDialog() : this(null) { }

    /// <summary>A one-line name-input dialog. Defaults to its playlist wording; pass
    /// <paramref name="title"/> + <paramref name="prompt"/> to reuse it for anything named
    /// (e.g. renaming an iPod).</summary>
    public PlaylistNameDialog(string? initialName, string? title = null, string? prompt = null)
    {
        InitializeComponent();

        if (!string.IsNullOrEmpty(initialName))
        {
            NameInput.Text = initialName;
        }
        if (title != null)
        {
            Title = title;
        }
        if (prompt != null)
        {
            PromptText.Text = prompt;
        }

        Loaded += (_, _) =>
        {
            NameInput.Focus();
            NameInput.CaretIndex = NameInput.Text?.Length ?? 0;   // append-ready, not start-of-text
        };
    }

    private void OkButton_Click(object? sender, RoutedEventArgs e)
    {
        // OK is the default button (Enter submits) - but only a non-empty name counts as OK.
        if (!string.IsNullOrWhiteSpace(NameInput.Text))
        {
            Close(NameInput.Text);
        }
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }
}
