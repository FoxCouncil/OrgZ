// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using OrgZ.Helpers;
using OrgZ.ViewModels;

namespace OrgZ.Controls;

public partial class PodcastsFeedDetailView : UserControl
{
    private PodcastsViewModel? _watchedViewModel;

    public PodcastsFeedDetailView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) => AttachToViewModel();
    }

    private PodcastsViewModel? ViewModel => DataContext as PodcastsViewModel;

    private void AttachToViewModel()
    {
        if (_watchedViewModel is not null)
        {
            _watchedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _watchedViewModel = null;
        }

        if (DataContext is PodcastsViewModel vm)
        {
            _watchedViewModel = vm;
            vm.PropertyChanged += OnViewModelPropertyChanged;
            RebuildFeedDescription();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PodcastsViewModel.SelectedFeed))
        {
            RebuildFeedDescription();
        }
    }

    /// <summary>
    /// Renders the selected feed's description into the header
    /// <see cref="FeedDescriptionBlock"/>. The PodcastIndex feed description
    /// is basic HTML; <see cref="HtmlInlinesBuilder"/> turns it into a flow
    /// of plain runs + clickable hyperlink runs.
    /// </summary>
    private void RebuildFeedDescription()
    {
        if (FeedDescriptionBlock.Inlines is not { } inlines)
        {
            return;
        }

        inlines.Clear();

        var description = _watchedViewModel?.SelectedFeed?.Description;

        foreach (var inline in HtmlInlinesBuilder.Build(description))
        {
            inlines.Add(inline);
        }
    }

    private void FeedDescriptionBlock_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Run run)
        {
            return;
        }

        var url = HtmlInlinesBuilder.GetUrl(run);

        if (string.IsNullOrEmpty(url))
        {
            return;
        }

        if (!e.GetCurrentPoint(FeedDescriptionBlock).Properties.IsLeftButtonPressed)
        {
            return;
        }

        HtmlInlinesBuilder.OpenUrl(url);
        e.Handled = true;
    }

    private void DownloadEpisode_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && ResolveRow(b) is { } row && ViewModel is { } vm)
        {
            // No-op when a job is already running for this episode — the
            // service ignores duplicate enqueues, but skipping the call here
            // also avoids touching the row state on a redundant click.
            if (row.IsDownloading)
            {
                return;
            }
            vm.DownloadEpisode(row.Episode);
        }
    }

    private void StreamEpisode_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && ResolveRow(b) is { } row && ViewModel is { } vm)
        {
            vm.StreamEpisode(row.Episode);
        }
    }

    /// <summary>
    /// Buttons inside a DataGridTemplateColumn end up with a DataContext that
    /// looks like the row but is missing observable updates from the source --
    /// the cell presenter does something to its binding source. The reliable
    /// source of truth is the row's DataGridRow.DataContext, which we find
    /// by walking up the visual tree.
    /// </summary>
    private static PodcastEpisodeRow? ResolveRow(Button b)
    {
        Avalonia.Visual? cur = b;
        while (cur is not null)
        {
            if (cur is DataGridRow row && row.DataContext is PodcastEpisodeRow ep)
            {
                return ep;
            }
            cur = cur.GetVisualParent();
        }
        return (b.DataContext as PodcastEpisodeRow) ?? (b.Tag as PodcastEpisodeRow);
    }

    private void EpisodesGrid_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is DataGrid g && g.SelectedItem is PodcastEpisodeRow row && ViewModel is { } vm)
        {
            vm.StreamEpisode(row.Episode);
        }
    }

    private PodcastEpisodeRow? GetEpisodesGridSelection()
    {
        return EpisodesGrid.SelectedItem as PodcastEpisodeRow;
    }

    private void ContextPlay_Click(object? sender, RoutedEventArgs e)
    {
        if (GetEpisodesGridSelection() is { } row && ViewModel is { } vm)
        {
            vm.StreamEpisode(row.Episode);
        }
    }

    private void ContextDownload_Click(object? sender, RoutedEventArgs e)
    {
        if (GetEpisodesGridSelection() is { } row && ViewModel is { } vm)
        {
            vm.DownloadEpisode(row.Episode);
        }
    }

    private async void ContextGetInfo_Click(object? sender, RoutedEventArgs e)
    {
        if (GetEpisodesGridSelection() is { } row && ViewModel is { } vm)
        {
            await vm.ShowEpisodeInfoAsync(row.Episode);
        }
    }
}
