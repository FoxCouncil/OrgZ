// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Controls;
using Avalonia.Interactivity;
using OrgZ.ViewModels;
using OrgZ.Views;

namespace OrgZ.Controls;

public partial class RadioFilterPanel : UserControl
{
    public RadioFilterPanel()
    {
        InitializeComponent();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private void CollapseAll_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is MainWindow window)
        {
            window.CollapseAllRowGroups();
        }
    }

    private async void AddStation_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window window)
        {
            return;
        }

        var nameBox = new TextBox { PlaceholderText = "Station Name", Margin = new Avalonia.Thickness(0, 0, 0, 8) };
        var urlBox = new TextBox { PlaceholderText = "Stream URL (http://...)", Margin = new Avalonia.Thickness(0, 0, 0, 8) };
        var genreBox = new ComboBox
        {
            PlaceholderText = "Genre",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            ItemsSource = RadioGenres.All.Select(g => g.DisplayName()).ToList(),
        };
        var errorText = new TextBlock
        {
            Foreground = Avalonia.Media.Brushes.OrangeRed,
            FontSize = 12,
            IsVisible = false,
            Margin = new Avalonia.Thickness(0, 8, 0, 0),
        };

        var dialog = new Window
        {
            Title = "Add Radio Station",
            MinWidth = 400,
            MinHeight = 200,
            SizeToContent = Avalonia.Controls.SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = true,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(16),
                Children =
                {
                    new TextBlock { Text = "Add a custom radio station:", Margin = new Avalonia.Thickness(0, 0, 0, 12) },
                    nameBox,
                    urlBox,
                    genreBox,
                    errorText,
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

        ViewModels.MainWindowViewModel.NewUserStation? result = null;

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
                            var genre = genreBox.SelectedItem as string ?? string.Empty;

                            // A bad URL used to close the dialog silently and nothing
                            // appeared - keep it open and say what's wrong instead.
                            if (string.IsNullOrEmpty(url)
                                || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
                                || uri.Scheme is not ("http" or "https"))
                            {
                                errorText.Text = "Enter a valid http(s) stream URL.";
                                errorText.IsVisible = true;
                                return;
                            }

                            result = new ViewModels.MainWindowViewModel.NewUserStation(name, url, genre);
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
}
