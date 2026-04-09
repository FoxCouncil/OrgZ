// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OrgZ.Views;

public partial class PlaylistNameDialog : Window
{
    public PlaylistNameDialog() : this(null) { }

    public PlaylistNameDialog(string? initialName)
    {
        InitializeComponent();

        if (!string.IsNullOrEmpty(initialName))
        {
            NameInput.Text = initialName;
        }

        Loaded += (_, _) => NameInput.Focus();
    }

    private void OkButton_Click(object? sender, RoutedEventArgs e)
    {
        Close(NameInput.Text);
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }
}
