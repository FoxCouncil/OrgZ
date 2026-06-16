// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Models;
using OrgZ.Services.Podcast;
using Serilog;

namespace OrgZ.ViewModels;

public enum PodcastsView
{
    Store,
    FeedDetail,
    Subscriptions,
    FeedList,
}

/// <summary>
/// Drives the Podcasts panel: store landing, feed-detail view (single podcast +
/// episodes), subscriptions view (your shows + downloaded queue), and search.
/// </summary>
public partial class PodcastsViewModel : ObservableObject
{
    private static readonly ILogger _log = Logging.For<PodcastsViewModel>();

    private readonly MainWindowViewModel? _main;

    internal PodcastsViewModel(MainWindowViewModel main)
    {
        _main = main;
        PodcastDownloadService.Instance.ProgressChanged += OnDownloadProgress;
        PodcastDownloadService.Instance.Completed       += OnDownloadCompleted;
        PodcastDownloadService.Instance.Failed          += OnDownloadFailed;
    }

    /// <summary>
    /// Design-time only: parameterless constructor wired up by the XAML
    /// designer so each split sub-view (Store, Subscriptions, FeedDetail) can
    /// be laid out with realistic-looking sample data without spinning up the
    /// full app + a PodcastIndex round trip. Populates every collection any
    /// sub-view reads from, so the same instance can be used as the design
    /// data context for all of them.
    /// </summary>
    public PodcastsViewModel()
    {
        _main = null;

        Featured.Add(SampleFeed(1, "The Daily", "The New York Times"));
        Featured.Add(SampleFeed(2, "Reply All", "Gimlet"));
        Featured.Add(SampleFeed(3, "Radiolab", "WNYC Studios"));

        for (int i = 0; i < 20; i++)
        {
            var feed = SampleFeed(100 + i, $"Top Show {i + 1}", $"Network {i + 1}");
            TopPodcasts.Add(feed);
            NumberedTopPodcasts.Add(new NumberedFeed(i + 1, feed));
        }

        // Category carousel sample data so the stacked carousels render in
        // the designer with representative content instead of empty cards.
        for (int i = 0; i < 8; i++)
        {
            NonProfitFeeds.Add(SampleFeed(500 + i, $"Non-Profit Show {i + 1}", "Foundation"));
            NewsFeeds.Add(SampleFeed(550 + i, $"News Show {i + 1}", "Network"));
            MusicFeeds.Add(SampleFeed(600 + i, $"Music Show {i + 1}", "Label"));
        }

        // A handful of categories so the left card has something to scroll.
        foreach (var name in new[]
        {
            "Arts", "Business", "Comedy", "Education", "Fiction", "Health & Fitness",
            "History", "Kids & Family", "Leisure", "Music", "News",
            "Religion & Spirituality", "Science", "Society & Culture",
            "Sports", "Technology", "True Crime", "TV & Film",
        })
        {
            Categories.Add(new PodcastCategory { Id = 0, Name = name });
        }

        for (int i = 0; i < 8; i++)
        {
            NewAndNotable.Add(SampleFeed(200 + i, $"Notable {i + 1}", $"New Voices {i + 1}"));
        }

        // FeedList sample data — the shared browse/search grid. Title + a dozen
        // feeds so the designer renders a representative wrapping grid.
        FeedListTitle = "Technology";
        for (int i = 0; i < 12; i++)
        {
            FeedListItems.Add(SampleFeed(700 + i, $"Tech Show {i + 1}", $"Studio {i + 1}"));
        }

        foreach (var (name, count) in new[]
        {
            ("Society & Culture", 6),
            ("News",              6),
            ("Technology",        6),
            ("Comedy",            6),
        })
        {
            var rail = new PodcastCategoryRail
            {
                CategoryId   = 0,
                CategoryName = name,
                Feeds        = new ObservableCollection<PodcastFeed>(),
            };

            for (int i = 0; i < count; i++)
            {
                rail.Feeds.Add(SampleFeed(300 + i, $"{name} {i + 1}", "Host Name"));
            }

            CategoryRails.Add(rail);
        }

        // Subscriptions sample data — mirrors the shape PodcastCache returns
        // for real subscriptions, so the SubscriptionsView previewer renders
        // the same tile grid the running app would.
        for (int i = 0; i < 5; i++)
        {
            Subscriptions.Add(new PodcastSubscription
            {
                FeedId       = 400 + i,
                Title        = $"Subscribed Show {i + 1}",
                Author       = $"Producer {i + 1}",
                Description  = "Sample subscription description for designer preview.",
                SubscribedAt = DateTime.UtcNow,
            });
        }

        // FeedDetail sample data — one selected feed plus a handful of
        // episodes in mixed download states so the row icons get exercised.
        SelectedFeed = new PodcastFeed
        {
            Id          = 999,
            Title       = "The Sample Show",
            Author      = "Sample Producer",
            Description = "A long-form interview podcast about sample data. "
                        + "Each week the host sits down with another fake guest to talk about realistic-looking content for designer previews.",
        };
        var sampleStates = new[]
        {
            PodcastDownloadState.NotDownloaded,
            PodcastDownloadState.Downloaded,
            PodcastDownloadState.InProgress,
            PodcastDownloadState.Incomplete,
            PodcastDownloadState.NotDownloaded,
            PodcastDownloadState.Downloaded,
        };
        for (int i = 0; i < sampleStates.Length; i++)
        {
            var ep = new PodcastEpisode
            {
                Id                  = 9000 + i,
                Title               = $"Episode {sampleStates.Length - i}: A representative title that wraps onto a second line",
                DurationSec         = 1800 + i * 327,
                DatePublishedPretty = $"Jan {sampleStates.Length - i}, 2026",
            };
            var row = new PodcastEpisodeRow(SelectedFeed, ep, libraryRoot: null);
            row.DownloadState = sampleStates[i];
            SelectedFeedEpisodes.Add(row);
        }
    }

    private static PodcastFeed SampleFeed(long id, string title, string author)
    {
        return new PodcastFeed
        {
            Id          = id,
            Title       = title,
            Author      = author,
            Description = "Sample show notes for designer preview.",
        };
    }

    // -- Page state ------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStore), nameof(IsFeedDetail), nameof(IsSubscriptions), nameof(IsFeedList), nameof(ShowBackButton))]
    private PodcastsView _currentView = PodcastsView.Store;

    public bool IsStore         => CurrentView == PodcastsView.Store;
    public bool IsFeedDetail    => CurrentView == PodcastsView.FeedDetail;
    public bool IsSubscriptions => CurrentView == PodcastsView.Subscriptions;
    public bool IsFeedList      => CurrentView == PodcastsView.FeedList;

    /// <summary>
    /// The in-panel back button shows only for drill-down views (a feed or a feed list).
    /// Store and Subscriptions are top-level entries reached from the sidebar, so they
    /// don't show one.
    /// </summary>
    public bool ShowBackButton  => CurrentView is PodcastsView.FeedDetail or PodcastsView.FeedList;

    // -- Store rails -----------------------------------------------------

    public ObservableCollection<PodcastFeed> Featured     { get; } = [];
    public ObservableCollection<PodcastFeed> TopPodcasts  { get; } = [];
    public ObservableCollection<NumberedFeed> NumberedTopPodcasts { get; } = [];
    public ObservableCollection<PodcastFeed> NewAndNotable { get; } = [];
    public ObservableCollection<PodcastFeed> NonProfitFeeds { get; } = [];
    public ObservableCollection<PodcastFeed> NewsFeeds { get; } = [];
    public ObservableCollection<PodcastFeed> MusicFeeds { get; } = [];
    public ObservableCollection<PodcastCategory> Categories { get; } = [];
    public ObservableCollection<PodcastCategoryRail> CategoryRails { get; } = [];

    // Full category list from the last store load (not just the popularity-ranked
    // top-40 shown in the left column). Lets a carousel's "See All" resolve its
    // title back to a category and browse it identically to a left-column click.
    private List<PodcastCategory> _allCategories = [];

    [ObservableProperty]
    private bool _isLoadingStore;

    [ObservableProperty]
    private string? _storeError;

    // -- Feed detail -----------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedFeedIsSubscribed))]
    private PodcastFeed? _selectedFeed;

    public bool SelectedFeedIsSubscribed =>
        SelectedFeed is not null && PodcastCache.IsSubscribed(SelectedFeed.Id);

    // Per-podcast rule overrides, surfaced on the feed page (combo index 0 = "use the global
    // default"). Loaded when the feed changes; a user change writes straight through to
    // PodcastSettings, so the next refresh of this feed honors it.
    private bool _loadingFeedOverrides;

    [ObservableProperty]
    private int _selectedFeedActionIndex;

    [ObservableProperty]
    private int _selectedFeedKeepIndex;

    partial void OnSelectedFeedChanged(PodcastFeed? value)
    {
        _loadingFeedOverrides = true;
        var feedId = value?.Id ?? 0;
        SelectedFeedActionIndex = PodcastSettings.FeedActionIndex(feedId);
        SelectedFeedKeepIndex = PodcastSettings.FeedKeepIndex(feedId);
        _loadingFeedOverrides = false;
    }

    partial void OnSelectedFeedActionIndexChanged(int value)
    {
        if (_loadingFeedOverrides || SelectedFeed is null)
        {
            return;
        }
        PodcastSettings.SetFeedActionIndex(SelectedFeed.Id, value);
    }

    partial void OnSelectedFeedKeepIndexChanged(int value)
    {
        if (_loadingFeedOverrides || SelectedFeed is null)
        {
            return;
        }
        PodcastSettings.SetFeedKeepIndex(SelectedFeed.Id, value);
    }

    public ObservableCollection<PodcastEpisodeRow> SelectedFeedEpisodes { get; } = [];

    [ObservableProperty]
    private bool _isLoadingFeed;

    // -- Subscriptions ---------------------------------------------------

    public ObservableCollection<PodcastSubscription> Subscriptions { get; } = [];

    // -- Feed list (shared surface for genre browse + store search) ------
    //
    // Both the left-column Categories list and the global header search box
    // render their results into this one view: a flat grid of feeds with a
    // title strip. Keeping a single collection + title means genre browse and
    // search stay visually identical and there's one navigation path to test.

    public ObservableCollection<PodcastFeed> FeedListItems { get; } = [];

    [ObservableProperty]
    private string _feedListTitle = "";

    [ObservableProperty]
    private bool _isLoadingFeedList;

    /// <summary>
    /// Footer summary for the feed-list view (shown in the main window's per-view
    /// status strip): the total number of feeds currently listed. Raised manually
    /// from <see cref="LoadFeedListAsync"/> since it derives from a collection count.
    /// </summary>
    public string FeedListSummary =>
        FeedListItems.Count == 1 ? "1 podcast" : $"{FeedListItems.Count} podcasts";

    // True when the feed-list view is currently showing search results (vs a
    // genre browse). Lets a header-search refine replace the list in place
    // instead of pushing a new back-stack entry per keystroke, and lets clearing
    // the box step back out of the results rather than jump straight home.
    private bool _feedListIsSearch;

    // -- Navigation roots + back-stack -----------------------------------
    //
    // Two independent navigation roots — the Store and the Subscriptions list — each with its OWN
    // back-stack and current view. The sidebar switches between them, restoring exactly where you
    // left each (a feed you'd drilled into included); switching never resets. Within a root, each
    // forward navigation (open a feed, browse a genre, run a search) pushes a snapshot of the view
    // it's leaving; Back pops and restores it without a network re-fetch.
    private readonly NavRoot _storeRoot = new() { Current = new NavEntry { View = PodcastsView.Store } };
    private readonly NavRoot _subsRoot  = new() { Current = new NavEntry { View = PodcastsView.Subscriptions } };
    private NavRoot? _activeRootField;
    private NavRoot _activeRoot { get => _activeRootField ??= _storeRoot; set => _activeRootField = value; }

    // The active root's back-stack. Every Push/Pop below operates on whichever root is currently
    // showing, so Store and Subscriptions accumulate their histories separately.
    private Stack<NavEntry> _navStack => _activeRoot.Stack;

    // -- Commands --------------------------------------------------------

    /// <summary>
    /// Resets all the way to the store (home) and empties the back-stack.
    /// </summary>
    [RelayCommand]
    internal void ShowStore()
    {
        _navStack.Clear();
        CurrentView = PodcastsView.Store;
        // Home clears a search that was driving the header box, so the store
        // isn't shown with a stale query still typed in.
        if (_main is { } main && !string.IsNullOrEmpty(main.SearchText))
        {
            main.SearchText = "";
        }
    }

    /// <summary>
    /// Selects the Store root from the sidebar, restoring its navigation exactly where it was left
    /// (a drilled-into feed included). No-op — and crucially no reset — if it's already active.
    /// </summary>
    internal void ActivateStoreRoot() => SwitchRoot(_storeRoot);

    /// <summary>
    /// Selects the Subscriptions root from the sidebar, restoring its navigation and refreshing the
    /// list. No-op if it's already active.
    /// </summary>
    internal void ActivateSubscriptionsRoot()
    {
        var entering = _activeRoot != _subsRoot;
        SwitchRoot(_subsRoot);
        if (entering)
        {
            ReloadSubscriptions();
        }
    }

    /// <summary>
    /// Swaps the active navigation root, saving the outgoing root's current view and restoring the
    /// incoming root's. Each root keeps its own back-stack, so the two histories never bleed.
    /// </summary>
    private void SwitchRoot(NavRoot target)
    {
        if (_activeRoot == target)
        {
            return; // already on this root — preserve its navigation, don't reset
        }
        _activeRoot.Current = SnapshotCurrent();   // remember where we were in the outgoing root
        _activeRoot = target;
        RestoreEntry(target.Current);              // restore where we left the incoming root
    }

    /// <summary>
    /// Steps back one level through the navigation stack (store = home). Restores
    /// the previous view's content from its snapshot, so Back from a feed returns
    /// to the genre / search list it was opened from — not all the way to the store.
    /// </summary>
    [RelayCommand]
    internal void GoBack()
    {
        if (_navStack.Count > 0)
        {
            RestoreEntry(_navStack.Pop());
        }
        else
        {
            CurrentView = PodcastsView.Store;
        }

        // Landing back on the store clears the header search so it isn't left
        // showing a query for results that are no longer on screen.
        if (CurrentView == PodcastsView.Store && _main is { } main && !string.IsNullOrEmpty(main.SearchText))
        {
            main.SearchText = "";
        }
    }

    private NavEntry SnapshotCurrent() => new()
    {
        View             = CurrentView,
        FeedListTitle    = FeedListTitle,
        FeedListIsSearch = _feedListIsSearch,
        FeedListItems    = FeedListItems.ToList(),
        SelectedFeed     = SelectedFeed,
        Episodes         = SelectedFeedEpisodes.ToList(),
    };

    private void RestoreEntry(NavEntry e)
    {
        _feedListIsSearch = false;
        switch (e.View)
        {
            case PodcastsView.FeedList:
            {
                FeedListTitle     = e.FeedListTitle;
                _feedListIsSearch = e.FeedListIsSearch;
                FeedListItems.Clear();
                foreach (var f in e.FeedListItems)
                {
                    FeedListItems.Add(f);
                }
                OnPropertyChanged(nameof(FeedListSummary));
            }
            break;

            case PodcastsView.FeedDetail:
            {
                SelectedFeed = e.SelectedFeed;
                SelectedFeedEpisodes.Clear();
                foreach (var r in e.Episodes)
                {
                    SelectedFeedEpisodes.Add(r);
                }
            }
            break;
        }

        CurrentView = e.View;
    }

    [RelayCommand]
    internal void ShowSubscriptions()
    {
        _navStack.Push(SnapshotCurrent());
        ReloadSubscriptions();
        CurrentView = PodcastsView.Subscriptions;
    }

    [RelayCommand]
    internal async Task OpenFeedAsync(PodcastFeed? feed)
    {
        if (feed == null) return;
        _navStack.Push(SnapshotCurrent());
        SelectedFeed = feed;
        SelectedFeedEpisodes.Clear();
        CurrentView = PodcastsView.FeedDetail;

        _log.Information("OpenFeedAsync: loading episodes for feed {Id} '{Title}'", feed.Id, feed.Title);
        IsLoadingFeed = true;
        try
        {
            var eps = await PodcastIndexClient.GetEpisodesByFeedIdAsync(feed.Id, max: 200);
            _log.Information("OpenFeedAsync: feed {Id} returned {Count} episodes (first duration={Dur})",
                feed.Id, eps.Count, eps.Count > 0 ? eps[0].DurationSec : -1);
            var libraryRoot = App.FolderPath;
            foreach (var e in eps)
            {
                SelectedFeedEpisodes.Add(new PodcastEpisodeRow(feed, e, libraryRoot));
            }
        }
        finally
        {
            IsLoadingFeed = false;
        }
    }

    [RelayCommand]
    internal void ToggleSubscribe()
    {
        var feed = SelectedFeed;
        if (feed == null) return;
        if (PodcastCache.IsSubscribed(feed.Id))
        {
            PodcastCache.RemoveSubscription(feed.Id);
        }
        else
        {
            PodcastCache.AddSubscription(feed);
        }
        OnPropertyChanged(nameof(SelectedFeedIsSubscribed));
    }

    /// <summary>
    /// Opens the shared MediaInfoDialog for an episode -- same Get Info dialog
    /// the Music and Radio views use, so the action is consistent regardless of
    /// MediaKind. The episode is projected onto a MediaItem so the dialog can
    /// read its fields uniformly.
    /// </summary>
    internal async Task ShowEpisodeInfoAsync(PodcastEpisode episode)
    {
        if (SelectedFeed is null)
        {
            return;
        }

        var publishedAt = episode.DatePublishedEpoch > 0
            ? DateTimeOffset.FromUnixTimeSeconds(episode.DatePublishedEpoch).UtcDateTime
            : DateTime.UtcNow;

        var item = new MediaItem
        {
            Id          = $"podcast:{episode.Id}",
            Kind        = MediaKind.Podcast,
            Source      = $"podcast:{SelectedFeed.Id}",
            SourceId    = episode.Guid,
            Title       = episode.Title,
            Artist      = SelectedFeed.Title,
            Album       = SelectedFeed.Title,
            Comment     = episode.Description,
            StreamUrl   = episode.EnclosureUrl,
            HomepageUrl = SelectedFeed.HomepageUrl,
            FaviconUrl  = episode.Image ?? SelectedFeed.DisplayImage,
            Duration    = episode.DurationSec > 0 ? TimeSpan.FromSeconds(episode.DurationSec) : null,
            DateAdded   = publishedAt,
            FileSize    = episode.EnclosureLength > 0 ? episode.EnclosureLength : null,
        };

        await _main.ShowMediaInfoForItemAsync(item);
    }

    [RelayCommand]
    internal void StreamEpisode(PodcastEpisode? episode)
    {
        if (episode == null || SelectedFeed == null) return;

        // "Stream" is now a misnomer kept for backward compatibility with the
        // existing button — if we have a fully downloaded copy on disk, play
        // that instead so the user gets gapless start, no-buffering, and works
        // offline. Falls through to the network stream when the file isn't on
        // disk (or is partial / corrupted).
        var libraryRoot = App.FolderPath;
        if (!string.IsNullOrWhiteSpace(libraryRoot))
        {
            var state = PodcastDownloadService.GetState(SelectedFeed, episode, libraryRoot);
            if (state == PodcastDownloadState.Downloaded)
            {
                var localPath = PodcastDownloadService.GetLocalPath(SelectedFeed, episode, libraryRoot);
                if (!string.IsNullOrWhiteSpace(localPath))
                {
                    _main.PlayPodcastEpisode(SelectedFeed, episode, localPath: localPath);
                    return;
                }
            }
        }
        _main.PlayPodcastEpisodeStream(SelectedFeed, episode);
    }

    [RelayCommand]
    internal void DownloadEpisode(PodcastEpisode? episode)
    {
        if (episode == null || SelectedFeed == null) return;
        var libraryRoot = App.FolderPath;
        if (string.IsNullOrWhiteSpace(libraryRoot))
        {
            _log.Warning("Cannot download episode {Id}: library root not set", episode.Id);
            return;
        }
        _ = PodcastDownloadService.Instance.EnqueueAsync(SelectedFeed, episode, libraryRoot);
        // Bump the row to InProgress immediately — the service signals Completed
        // / Failed when it's done, but the user expects the icon to flip now.
        RefreshEpisodeRowState(episode.Id);
    }

    internal void RemoveDownload(PodcastEpisode? episode)
    {
        if (episode == null || SelectedFeed == null)
        {
            return;
        }
        PodcastDownloadService.DeleteDownload(SelectedFeed, episode, App.FolderPath);
        RefreshEpisodeRowState(episode.Id);
    }

    // -- Genre browse + search (both render into the shared FeedList view) -

    // Cancels the in-flight / pending feed-list fetch when a newer one
    // supersedes it (a fresh keystroke debounce, or a genre tap interrupting
    // a search). Genre taps and searches share this token so they can't race
    // each other's results into the same collection.
    private CancellationTokenSource? _feedListCts;

    /// <summary>
    /// Opens the shared feed-list view for a category and fills it with that
    /// genre's trending feeds. Wired from the store's left-column Categories list.
    /// </summary>
    internal async Task ShowCategoryAsync(PodcastCategory? category)
    {
        if (category is null) return;
        _feedListCts?.Cancel();
        _navStack.Push(SnapshotCurrent());
        var cts = new CancellationTokenSource();
        _feedListCts = cts;
        await LoadFeedListAsync(category.Name,
            ct => PodcastIndexClient.GetTrendingAsync(max: 60, categoryId: category.Id, ct: ct),
            cts.Token,
            isSearch: false);
    }

    /// <summary>
    /// Wired from a carousel's "See All". If the rail maps to a real category
    /// (Non-Profit / News / Music), browse it through the exact same path as the
    /// left-column category click — a fresh category fetch — so "See All" and the
    /// Categories list are consistent. Rails with no category backing (e.g. "New"
    /// = recent feeds) just show their already-loaded set, no network fetch.
    /// </summary>
    internal async Task ShowFeedList(string title, IEnumerable<PodcastFeed> feeds)
    {
        var match = _allCategories.FirstOrDefault(c =>
            !string.IsNullOrEmpty(c.Name) &&
            string.Equals(c.Name, title, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            await ShowCategoryAsync(match);
            return;
        }

        _feedListCts?.Cancel();
        _navStack.Push(SnapshotCurrent());
        FeedListTitle = title;
        _feedListIsSearch = false;
        FeedListItems.Clear();
        foreach (var f in feeds)
        {
            FeedListItems.Add(f);
        }
        OnPropertyChanged(nameof(FeedListSummary));
        IsLoadingFeedList = false;
        CurrentView = PodcastsView.FeedList;
    }

    /// <summary>
    /// Driven by the global header search box (see <c>MainWindowViewModel</c>).
    /// Debounced so we don't fire a PodcastIndex round-trip on every keystroke.
    /// A non-empty query routes the panel to the shared feed-list view; clearing
    /// the box while that view is showing returns to the store.
    /// </summary>
    internal void ApplyHeaderSearch(string? text)
    {
        var q = text?.Trim() ?? "";

        // Supersede any in-flight / pending search.
        _feedListCts?.Cancel();

        if (q.Length == 0)
        {
            // Clearing the box steps back out of the search results (to whatever
            // we were on before searching) rather than jumping straight home —
            // but only when the search is what put us on the feed-list view.
            if (CurrentView == PodcastsView.FeedList && _feedListIsSearch)
            {
                GoBack();
            }
            return;
        }

        // Push the source view the first time we open search results; subsequent
        // keystrokes refine the same results in place (no new back-stack entry).
        bool entering = !(CurrentView == PodcastsView.FeedList && _feedListIsSearch);
        var cts = new CancellationTokenSource();
        _feedListCts = cts;
        _ = DebouncedSearchAsync(q, cts.Token, entering);
    }

    private async Task DebouncedSearchAsync(string query, CancellationToken ct, bool pushSource)
    {
        try
        {
            await Task.Delay(400, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (ct.IsCancellationRequested) return;
        if (pushSource)
        {
            _navStack.Push(SnapshotCurrent());
        }
        await LoadFeedListAsync($"Search: “{query}”",
            c => PodcastIndexClient.SearchByTermAsync(query, max: 50, ct: c),
            ct,
            isSearch: true);
    }

    /// <summary>
    /// Shared loader for the feed-list view: flips to the view, shows a spinner,
    /// runs <paramref name="fetch"/>, and replaces the visible feeds. Genre browse
    /// and search both funnel through here so the surface stays identical.
    /// </summary>
    private async Task LoadFeedListAsync(string title, Func<CancellationToken, Task<List<PodcastFeed>>> fetch, CancellationToken ct, bool isSearch)
    {
        FeedListTitle = title;
        _feedListIsSearch = isSearch;
        FeedListItems.Clear();
        OnPropertyChanged(nameof(FeedListSummary));
        CurrentView = PodcastsView.FeedList;
        IsLoadingFeedList = true;
        try
        {
            var feeds = await fetch(ct);
            if (ct.IsCancellationRequested) return;
            foreach (var f in feeds.Where(f => !string.IsNullOrEmpty(f.DisplayImage)))
            {
                FeedListItems.Add(f);
            }
            OnPropertyChanged(nameof(FeedListSummary));
        }
        catch (OperationCanceledException)
        {
            // Superseded — leave the newer request to own the view.
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Feed list load failed for '{Title}'", title);
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                IsLoadingFeedList = false;
            }
        }
    }

    // -- Loading ---------------------------------------------------------

    private bool _storeLoading;

    internal async Task LoadStoreAsync()
    {
        // Guard at entry, not just on Featured.Count: the rails are populated AFTER the
        // awaited fetch below, so two calls racing in before either finishes (e.g. the view
        // shows, then a sidebar rebuild re-selects the Podcasts item) would both pass a
        // Count==0 check and both append — doubling every rail. _storeLoading closes that gap.
        if (Featured.Count > 0 || _storeLoading) return;
        _storeLoading = true;
        IsLoadingStore = true;
        StoreError = null;
        try
        {
            var trendingTask  = PodcastIndexClient.GetTrendingAsync(max: 30);
            var recentTask    = PodcastIndexClient.GetRecentFeedsAsync(max: 16);
            var categoryTask  = PodcastIndexClient.GetCategoriesAsync();

            await Task.WhenAll(trendingTask, recentTask, categoryTask);

            var trending = trendingTask.Result;
            var recent   = recentTask.Result;
            var cats     = categoryTask.Result;
            _allCategories = cats;

            // Featured: first 3 trending with artwork
            foreach (var f in trending.Where(t => !string.IsNullOrEmpty(t.DisplayImage)).Take(3))
            {
                Featured.Add(f);
            }
            // Top Podcasts: next 20 trending. Mirrored into NumberedTopPodcasts
            // so the right-column list can show a 1-indexed rank without the
            // template needing to know its position.
            int rank = 1;
            foreach (var f in trending.Skip(3).Take(20))
            {
                TopPodcasts.Add(f);
                NumberedTopPodcasts.Add(new NumberedFeed(rank++, f));
            }
            // New & Notable: recent feeds
            foreach (var f in recent.Where(r => !string.IsNullOrEmpty(r.DisplayImage)).Take(8))
            {
                NewAndNotable.Add(f);
            }
            // Categories list — popularity-ranked, top 40 only. Popularity is
            // derived from how often each category shows up in the trending +
            // recent feed sample we already fetched: categories that mark a
            // lot of popular feeds bubble to the top. Ties break alphabetically.
            // Not a perfect signal (small sample, weighted by what's hot right
            // now), but PodcastIndex doesn't expose a per-category popularity
            // metric of its own, and this beats either "alphabetical" or
            // "whatever order the API returned them in".
            var categoryFreq = new Dictionary<int, int>();
            foreach (var f in trending.Concat(recent))
            {
                if (f.Categories is null) continue;
                foreach (var key in f.Categories.Keys)
                {
                    if (int.TryParse(key, out var id))
                    {
                        categoryFreq[id] = categoryFreq.GetValueOrDefault(id) + 1;
                    }
                }
            }
            foreach (var c in cats
                .OrderByDescending(c => categoryFreq.GetValueOrDefault(c.Id))
                .ThenBy(c => c.Name)
                .Take(40))
            {
                Categories.Add(c);
            }

            // Category-driven carousels stacked under the three-column row.
            // Resolve each category's ID by name from the loaded list so we
            // don't hard-code numerics that PodcastIndex could renumber.
            await LoadCategoryFeedsAsync(cats, "Non-Profit", NonProfitFeeds);
            await LoadCategoryFeedsAsync(cats, "News",       NewsFeeds);
            await LoadCategoryFeedsAsync(cats, "Music",      MusicFeeds);

        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Store load failed");
            StoreError = $"Failed to load: {ex.Message}";
        }
        finally
        {
            _storeLoading = false;
            IsLoadingStore = false;
        }
    }

    /// <summary>
    /// Resolves <paramref name="categoryName"/> against the loaded category
    /// list (case-insensitive, name match) and fills <paramref name="target"/>
    /// with that category's trending feeds. Skips silently if the API didn't
    /// return a matching category — keeps the carousel slot empty rather than
    /// taking the page down for a single missing rail.
    /// </summary>
    private static async Task LoadCategoryFeedsAsync(List<PodcastCategory> categories, string categoryName, ObservableCollection<PodcastFeed> target)
    {
        var match = categories.FirstOrDefault(c =>
            !string.IsNullOrEmpty(c.Name) &&
            string.Equals(c.Name, categoryName, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return;
        }
        var feeds = await PodcastIndexClient.GetTrendingAsync(max: 16, categoryId: match.Id);
        foreach (var f in feeds.Where(t => !string.IsNullOrEmpty(t.DisplayImage)))
        {
            target.Add(f);
        }
    }

    internal void ReloadSubscriptions()
    {
        Subscriptions.Clear();
        foreach (var s in PodcastCache.GetSubscriptions())
        {
            Subscriptions.Add(s);
        }
    }

    private void OnDownloadProgress(DownloadProgress p) { /* hook UI progress here if needed */ }

    private void OnDownloadCompleted(PodcastFeed feed, PodcastEpisode ep)
    {
        Dispatcher.UIThread.Post(() => RefreshEpisodeRowState(ep.Id));
    }

    private void OnDownloadFailed(long episodeId, Exception ex)
    {
        _log.Warning(ex, "Download failed for episode {Id}", episodeId);
        Dispatcher.UIThread.Post(() => RefreshEpisodeRowState(episodeId));
    }

    private void RefreshEpisodeRowState(long episodeId)
    {
        foreach (var row in SelectedFeedEpisodes)
        {
            if (row.Episode.Id == episodeId)
            {
                row.RefreshDownloadState();
            }
        }
    }
}

/// <summary>
/// DataGrid row wrapper for a podcast episode in the feed-detail view. Wraps a
/// raw <see cref="PodcastEpisode"/> record so the row can carry observable
/// state (the download button's icon changes as the file lands on disk, gets
/// orphaned as a .partial, or comes up size-mismatched). The pass-through
/// properties keep existing column bindings (Title, DatePublishedPretty,
/// DurationLabel) working unchanged.
/// </summary>
public partial class PodcastEpisodeRow : ObservableObject
{
    public PodcastFeed Feed { get; }
    public PodcastEpisode Episode { get; }
    private readonly string? _libraryRoot;

    public PodcastEpisodeRow(PodcastFeed feed, PodcastEpisode episode, string? libraryRoot)
    {
        Feed = feed;
        Episode = episode;
        _libraryRoot = libraryRoot;
        _downloadState = PodcastDownloadService.GetState(feed, episode, libraryRoot);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDownloaded), nameof(IsDownloading), nameof(IsIncomplete), nameof(IsNotDownloaded))]
    private PodcastDownloadState _downloadState;

    public bool IsDownloaded    => DownloadState == PodcastDownloadState.Downloaded;
    public bool IsDownloading   => DownloadState == PodcastDownloadState.InProgress;
    public bool IsIncomplete    => DownloadState == PodcastDownloadState.Incomplete;
    public bool IsNotDownloaded => DownloadState == PodcastDownloadState.NotDownloaded;

    // Pass-throughs so the DataGrid columns can keep binding to the same names
    // they used when the source was a raw PodcastEpisode.
    public string? Title               => Episode.Title;
    public string? DatePublishedPretty => Episode.DatePublishedPretty;
    public string  DurationLabel       => Episode.DurationLabel;

    public void RefreshDownloadState()
    {
        DownloadState = PodcastDownloadService.GetState(Feed, Episode, _libraryRoot);
    }
}

/// <summary>
/// A snapshot of a podcasts-panel view for the navigation back-stack — enough to
/// restore the view and its content (feed list or selected feed + episodes)
/// without re-fetching from the network.
/// </summary>
internal sealed class NavEntry
{
    public required PodcastsView View { get; init; }
    public string FeedListTitle { get; init; } = "";
    public bool FeedListIsSearch { get; init; }
    public IReadOnlyList<PodcastFeed> FeedListItems { get; init; } = [];
    public PodcastFeed? SelectedFeed { get; init; }
    public IReadOnlyList<PodcastEpisodeRow> Episodes { get; init; } = [];
}

/// <summary>
/// One sidebar navigation root (Store or Subscriptions): its own back-stack plus a snapshot of the
/// view currently shown under it, so switching between roots restores each independently.
/// </summary>
internal sealed class NavRoot
{
    public Stack<NavEntry> Stack { get; } = new();
    public NavEntry Current { get; set; } = new() { View = PodcastsView.Store };
}

public sealed class PodcastCategoryRail
{
    public required int CategoryId { get; init; }
    public required string CategoryName { get; init; }
    public required ObservableCollection<PodcastFeed> Feeds { get; init; }
}

/// <summary>
/// Pairs a <see cref="PodcastFeed"/> with a 1-indexed rank for the right-column
/// "Top Podcasts" list. The DataTemplate binds to <c>Number</c> for the leading
/// numeral and reaches through <c>Feed</c> for the title + subtitle, so the
/// row template doesn't need to know its position in the ItemsControl.
/// </summary>
public sealed record NumberedFeed(int Number, PodcastFeed Feed);
