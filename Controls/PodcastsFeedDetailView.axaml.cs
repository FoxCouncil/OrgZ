// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using OrgZ.Helpers;
using OrgZ.ViewModels;
using OrgZ.Views;

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
        if (!e.GetCurrentPoint(FeedDescriptionBlock).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var url = HitTestForLinkUrl(e.GetPosition(FeedDescriptionBlock));
        if (string.IsNullOrEmpty(url))
        {
            return;
        }

        HtmlInlinesBuilder.OpenUrl(url);
        e.Handled = true;
    }

    private void FeedDescriptionBlock_PointerMoved(object? sender, PointerEventArgs e)
    {
        // Hand cursor whenever the pointer is over a hyperlink Run, default
        // otherwise. SelectableTextBlock paints its own I-beam everywhere by
        // default; this overrides per-position based on what's actually under
        // the cursor in the inline flow.
        var url = HitTestForLinkUrl(e.GetPosition(FeedDescriptionBlock));
        FeedDescriptionBlock.Cursor = string.IsNullOrEmpty(url)
            ? new Cursor(StandardCursorType.Ibeam)
            : new Cursor(StandardCursorType.Hand);
    }

    /// <summary>
    /// Resolves the pixel point under the cursor to the URL of the inline Run
    /// the layout hit (or null if it landed in plain text / outside the layout).
    /// Avalonia's SelectableTextBlock doesn't dispatch pointer events per inline
    /// - the whole control is the hit target - so we hit-test its TextLayout
    /// to a character index and walk the Inlines counting characters to find
    /// which Run owns that index.
    /// </summary>
    private string? HitTestForLinkUrl(Avalonia.Point point)
    {
        var layout = FeedDescriptionBlock.TextLayout;
        if (layout is null)
        {
            return null;
        }

        var hit = layout.HitTestPoint(point);
        if (!hit.IsInside)
        {
            return null;
        }

        var inlines = FeedDescriptionBlock.Inlines;
        if (inlines is null)
        {
            return null;
        }

        int offset = 0;
        foreach (var inline in inlines)
        {
            int len = inline switch
            {
                Run r => r.Text?.Length ?? 0,
                LineBreak => 1,
                _ => 0,
            };
            if (hit.TextPosition >= offset && hit.TextPosition < offset + len)
            {
                return inline is Run run ? HtmlInlinesBuilder.GetUrl(run) : null;
            }
            offset += len;
        }
        return null;
    }

    private async void DownloadEpisode_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button b || ResolveRow(b) is not { } row || ViewModel is not { } vm)
        {
            return;
        }

        // No-op when a job is already running for this episode - the service ignores
        // duplicate enqueues, but skipping here avoids touching row state on a redundant click.
        if (row.IsDownloading)
        {
            return;
        }

        // A downloaded episode's button is a delete affordance (it shows a trash on hover) -
        // confirm, then remove the file. Anything else (re)downloads.
        if (row.IsDownloaded)
        {
            if (TopLevel.GetTopLevel(this) is Window owner)
            {
                var dialog = new ConfirmDialog(
                    "Remove download",
                    $"Remove the downloaded file for “{row.Episode.Title}”? You can download it again later.",
                    "Remove");
                if (await dialog.ShowDialog<bool>(owner) != true)
                {
                    return;
                }
            }
            vm.RemoveDownload(row.Episode);
            return;
        }

        vm.DownloadEpisode(row.Episode);
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
