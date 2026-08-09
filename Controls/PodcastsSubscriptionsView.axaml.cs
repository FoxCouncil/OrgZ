// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Controls;
using Avalonia.Interactivity;
using OrgZ.Models;
using OrgZ.ViewModels;

namespace OrgZ.Controls;

public partial class PodcastsSubscriptionsView : UserControl
{
    public PodcastsSubscriptionsView()
    {
        InitializeComponent();
    }

    private PodcastsViewModel? ViewModel => DataContext as PodcastsViewModel;

    private async void SubscriptionTile_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PodcastSubscription sub } && ViewModel is { } vm)
        {
            await vm.OpenSubscriptionAsync(sub);
        }
    }

    private async void AddRss_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || TopLevel.GetTopLevel(this) is not Window window)
        {
            return;
        }

        var urlBox = new TextBox { PlaceholderText = "RSS feed URL (https://...)", MinWidth = 360 };
        string? entered = null;
        var dialog = new Window
        {
            Title = "Add RSS Feed",
            MinWidth = 420,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };
        var add = new Button { Content = "Add" };
        var cancel = new Button { Content = "Cancel" };
        add.Click += (_, _) => { entered = urlBox.Text; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(16),
            Spacing = 12,
            Children =
            {
                urlBox,
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, add },
                },
            },
        };

        await dialog.ShowDialog(window);

        if (!string.IsNullOrWhiteSpace(entered))
        {
            await vm.AddByRssUrlAsync(entered);
        }
    }

    private async void ImportOpml_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        var files = await top.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Import OPML",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new Avalonia.Platform.Storage.FilePickerFileType("OPML") { Patterns = ["*.opml", "*.xml"] },
            ],
        });

        if (files.Count == 0)
        {
            return;
        }

        await using var stream = await files[0].OpenReadAsync();
        using var reader = new StreamReader(stream);
        await vm.ImportOpmlAsync(await reader.ReadToEndAsync());
    }

    private async void ExportOpml_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        var file = await top.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "Export OPML",
            SuggestedFileName = "orgz-podcasts.opml",
            DefaultExtension = "opml",
        });

        if (file is null)
        {
            return;
        }

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(vm.ExportOpml());
    }
}
