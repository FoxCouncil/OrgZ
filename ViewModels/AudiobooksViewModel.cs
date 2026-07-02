// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Models;
using OrgZ.Services.Audiobooks;
using Serilog;

namespace OrgZ.ViewModels;

public enum AudiobooksView
{
    Store,
    BookDetail,
}

/// <summary>One chapter (or m4b part) row in the book-detail file list.</summary>
public sealed record AudiobookChapterRow(int Number, string Name, string Duration);

/// <summary>
/// Drives the Audiobooks store panel: the store landing (popular / new / search over the
/// LibriVox collection on archive.org) and the book-detail view. The library grid that sits
/// under the store is NOT driven from here - it's a MainWindow-level DataGrid on the normal
/// FilteredItems pipeline, so search/selection/playback all behave like every other library view.
/// </summary>
public partial class AudiobooksViewModel : ObservableObject
{
    private static readonly ILogger _log = Logging.For<AudiobooksViewModel>();

    private readonly MainWindowViewModel? _main;
    private bool _storeLoaded;

    internal AudiobooksViewModel(MainWindowViewModel main)
    {
        _main = main;
    }

    /// <summary>
    /// Design-time only: sample data so the panel lays out in the designer without an
    /// archive.org round trip - same pattern as <see cref="PodcastsViewModel"/>.
    /// </summary>
    public AudiobooksViewModel()
    {
        _main = null;

        for (int i = 0; i < 3; i++)
        {
            Featured.Add(SampleBook(i, $"Featured Classic {i + 1}"));
        }
        for (int i = 0; i < 8; i++)
        {
            Popular.Add(SampleBook(10 + i, $"Popular Book {i + 1}"));
            Recent.Add(SampleBook(30 + i, $"New Catalog Entry {i + 1}"));
        }
    }

    private static AudiobookListing SampleBook(int i, string title) => new()
    {
        Identifier = $"sample_{i}",
        Title = title,
        Creator = "Jane Author",
        Runtime = "7:33:20",
        Downloads = 100_000 - i,
    };

    // ── view state ─────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStore), nameof(IsBookDetail), nameof(ShowBackButton))]
    private AudiobooksView _currentView = AudiobooksView.Store;

    public bool IsStore => CurrentView == AudiobooksView.Store;
    public bool IsBookDetail => CurrentView == AudiobooksView.BookDetail;
    public bool ShowBackButton => CurrentView != AudiobooksView.Store;

    [RelayCommand]
    private void GoBack()
    {
        CurrentView = AudiobooksView.Store;
    }

    // ── store landing ──────────────────────────────────────────────────────────

    public ObservableCollection<AudiobookListing> Featured { get; } = [];
    public ObservableCollection<AudiobookListing> Popular { get; } = [];
    public ObservableCollection<AudiobookListing> Recent { get; } = [];
    public ObservableCollection<AudiobookListing> SearchResults { get; } = [];

    [ObservableProperty]
    private bool _isStoreLoading;

    /// <summary>Search results replace the landing sections while a search is active.</summary>
    [ObservableProperty]
    private bool _isSearchActive;

    private CancellationTokenSource? _searchCts;

    /// <summary>Loads the landing sections once per session; the client's disk cache keeps it warm.</summary>
    public async Task LoadStoreAsync()
    {
        if (_storeLoaded || _main is null)
        {
            return;
        }
        _storeLoaded = true;

        IsStoreLoading = true;
        try
        {
            var popularTask = ArchiveOrgClient.GetPopularAsync(15);
            var recentTask = ArchiveOrgClient.GetRecentAsync(12);
            var popular = await popularTask;
            var recent = await recentTask;

            Featured.Clear();
            Popular.Clear();
            Recent.Clear();
            foreach (var b in popular.Take(3)) { Featured.Add(b); }
            foreach (var b in popular.Skip(3)) { Popular.Add(b); }
            foreach (var b in recent) { Recent.Add(b); }

            _log.Information("Audiobook store loaded: featured={Featured} popular={Popular} recent={Recent}", Featured.Count, Popular.Count, Recent.Count);
        }
        finally
        {
            IsStoreLoading = false;
        }
    }

    /// <summary>
    /// Driven by the global header search box (see <c>MainWindowViewModel</c>) - the composite
    /// has ONE search: the same text filters the library grid through the normal pipeline and,
    /// debounced here, searches the store. Clearing the box restores the landing sections.
    /// </summary>
    internal void ApplyHeaderSearch(string? text)
    {
        var q = text?.Trim() ?? "";

        // Supersede any in-flight / pending search.
        _searchCts?.Cancel();

        if (q.Length == 0)
        {
            IsSearchActive = false;
            return;
        }

        var cts = new CancellationTokenSource();
        _searchCts = cts;
        _ = DebouncedSearchAsync(q, cts.Token);
    }

    private async Task DebouncedSearchAsync(string query, CancellationToken ct)
    {
        try
        {
            await Task.Delay(400, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (ct.IsCancellationRequested)
        {
            return;
        }

        IsStoreLoading = true;
        try
        {
            var results = await ArchiveOrgClient.SearchAsync(query, rows: 24);
            if (ct.IsCancellationRequested)
            {
                return;   // a newer keystroke superseded this query while it was in flight
            }
            SearchResults.Clear();
            foreach (var b in results)
            {
                SearchResults.Add(b);
            }
            IsSearchActive = true;
            CurrentView = AudiobooksView.Store;   // searching from a book detail returns to results
            _log.Information("Audiobook search \"{Query}\": {Count} result(s)", query, results.Count);
        }
        finally
        {
            IsStoreLoading = false;
        }
    }

    // ── book detail ────────────────────────────────────────────────────────────

    [ObservableProperty]
    private AudiobookListing? _selectedBook;

    [ObservableProperty]
    private string? _bookDescription;

    [ObservableProperty]
    private bool _isBookLoading;

    public ObservableCollection<AudiobookChapterRow> BookChapters { get; } = [];

    public async Task OpenBookAsync(AudiobookListing book)
    {
        SelectedBook = book;
        CurrentView = AudiobooksView.BookDetail;
        BookDescription = null;
        BookChapters.Clear();

        IsBookLoading = true;
        try
        {
            var item = await ArchiveOrgClient.GetItemAsync(book.Identifier);
            if (item is null || SelectedBook != book)
            {
                return;   // fetch failed, or the user already navigated to a different book
            }

            BookDescription = StripHtml(item.Metadata?.Description);
            foreach (var row in BuildChapterRows(item.Files))
            {
                BookChapters.Add(row);
            }
        }
        finally
        {
            IsBookLoading = false;
        }
    }

    // ── pure pieces (unit-tested) ──────────────────────────────────────────────

    /// <summary>
    /// The downloadable files as display rows, numbered in play order - the chaptered m4b parts
    /// when the item has them, otherwise the MP3 chapter set.
    /// </summary>
    internal static List<AudiobookChapterRow> BuildChapterRows(IReadOnlyList<ArchiveItemFile> files)
    {
        var rows = new List<AudiobookChapterRow>();
        foreach (var f in ArchiveOrgClient.PickDownloadFiles(files))
        {
            var duration = ArchiveOrgClient.ParseFileLength(f.Length) is { } d
                ? (d.TotalHours >= 1 ? $"{(int)d.TotalHours}:{d.Minutes:D2}:{d.Seconds:D2}" : $"{d.Minutes}:{d.Seconds:D2}")
                : "—";
            rows.Add(new AudiobookChapterRow(rows.Count + 1, Path.GetFileNameWithoutExtension(f.Name ?? ""), duration));
        }
        return rows;
    }

    /// <summary>
    /// archive.org descriptions are HTML fragments. Renders them as plain text: br/p become
    /// line breaks, tags drop, entities decode, whitespace collapses.
    /// </summary>
    internal static string? StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var text = System.Text.RegularExpressions.Regex.Replace(html, @"<\s*(br|/p)\s*/?\s*>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", string.Empty);
        text = System.Net.WebUtility.HtmlDecode(text);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"[ \t]+", " ");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }
}
