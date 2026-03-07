// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Controls;
using Avalonia.Interactivity;
using OrgZ.ViewModels;

namespace OrgZ.Controls;

public partial class RadioFilterPanel : UserControl
{
    public RadioFilterPanel()
    {
        InitializeComponent();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    internal event Func<Task>? SyncRequested;

    private async void AddStation_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window window)
        {
            return;
        }

        var nameBox = new TextBox { Watermark = "Station Name", Margin = new Avalonia.Thickness(0, 0, 0, 8) };
        var urlBox = new TextBox { Watermark = "Stream URL (http://...)" };

        var dialog = new Window
        {
            Title = "Add Radio Station",
            Width = 400,
            Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(16),
                Children =
                {
                    new TextBlock { Text = "Add a custom radio station:", Margin = new Avalonia.Thickness(0, 0, 0, 12) },
                    nameBox,
                    urlBox,
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Margin = new Avalonia.Thickness(0, 12, 0, 0),
                        Spacing = 8,
                        Children =
                        {
                            new Button { Content = "Cancel", Tag = "cancel" },
                            new Button { Content = "Add", Tag = "add" },
                        }
                    }
                }
            }
        };

        string? result = null;

        if (dialog.Content is StackPanel panel && panel.Children[^1] is StackPanel buttons)
        {
            foreach (var child in buttons.Children)
            {
                if (child is Button btn)
                {
                    btn.Click += (s, args) =>
                    {
                        if (btn.Tag?.ToString() == "add")
                        {
                            var name = nameBox.Text?.Trim() ?? string.Empty;
                            var url = urlBox.Text?.Trim() ?? string.Empty;

                            if (!string.IsNullOrEmpty(url))
                            {
                                result = string.IsNullOrEmpty(name) ? url : $"{name}|{url}";
                            }
                        }

                        dialog.Close();
                    };
                }
            }
        }

        await dialog.ShowDialog(window);

        if (result != null)
        {
            ViewModel?.AddUserStationCommand.Execute(result);
        }
    }

    private async void SyncButton_Click(object? sender, RoutedEventArgs e)
    {
        if (SyncRequested != null)
        {
            await SyncRequested.Invoke();
        }
    }
}
