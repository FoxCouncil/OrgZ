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

/// <summary>One purchased Libro.fm book tile: the book plus whether it's already on the shelf.</summary>
public sealed record LibroBookRow(LibroBook Book, bool IsDownloaded);

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
        AudiobookDownloadService.Instance.ProgressChanged += OnDownloadProgress;
        AudiobookDownloadService.Instance.Completed += OnDownloadCompleted;
        AudiobookDownloadService.Instance.Failed += OnDownloadFailed;
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

    // ── owned ⇄ store toggle ────────────────────────────────────────────────────
    // The page opens on YOUR books (the card wall). "Browse Store" reveals the LibriVox/Libro
    // store (and its results grid); searching implies browsing, so it flips here too.

    [ObservableProperty]
    private bool _showingStore;

    [RelayCommand]
    private void BrowseStore() => ShowingStore = true;

    [RelayCommand]
    private void ShowOwned()
    {
        ShowingStore = false;
        CurrentView = AudiobooksView.Store;   // leave any open book detail so returning shows the store landing
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

        await TryRestoreLibroSessionAsync();
    }

    // ── Libro.fm - the login store (the user's own DRM-free purchases) ─────────

    public ObservableCollection<LibroBookRow> LibroBooks { get; } = [];

    [ObservableProperty]
    private bool _isLibroLoggedIn;

    [ObservableProperty]
    private string _libroUsername = string.Empty;

    [ObservableProperty]
    private string _libroPassword = string.Empty;

    [ObservableProperty]
    private string? _libroStatusText;

    [ObservableProperty]
    private bool _isLibroBusy;

    private string? _libroToken;

    private async Task TryRestoreLibroSessionAsync()
    {
        var token = LibroFmSession.LoadToken();
        if (token is null)
        {
            LibroUsername = LibroFmSession.Username ?? string.Empty;
            return;
        }
        _libroToken = token;
        await LoadLibroLibraryAsync();
    }

    [RelayCommand]
    private async Task SignInToLibroAsync()
    {
        var username = LibroUsername.Trim();
        if (username.Length == 0 || LibroPassword.Length == 0)
        {
            return;
        }

        IsLibroBusy = true;
        LibroStatusText = null;
        try
        {
            var token = await LibroFmClient.LoginAsync(username, LibroPassword);
            if (token is null)
            {
                LibroStatusText = "Sign-in failed — check the email and password.";
                return;
            }
            _libroToken = token;
            LibroPassword = string.Empty;   // never kept beyond the login call
            LibroFmSession.Save(token, username);
            await LoadLibroLibraryAsync();
        }
        finally
        {
            IsLibroBusy = false;
        }
    }

    [RelayCommand]
    private void SignOutOfLibro()
    {
        _libroToken = null;
        LibroFmSession.Clear();
        LibroBooks.Clear();
        IsLibroLoggedIn = false;
        LibroStatusText = null;
    }

    private async Task LoadLibroLibraryAsync()
    {
        if (_libroToken is null)
        {
            return;
        }
        IsLibroBusy = true;
        try
        {
            var books = await LibroFmClient.GetLibraryAsync(_libroToken);
            if (books is null)
            {
                // Expired/refused token - back to the sign-in form rather than an empty shelf.
                SignOutOfLibro();
                LibroStatusText = "Session expired — sign in again.";
                return;
            }
            IsLibroLoggedIn = true;
            RefreshLibroRows(books);
            _log.Information("Libro.fm library loaded: {Count} purchase(s)", books.Count);
        }
        finally
        {
            IsLibroBusy = false;
        }
    }

    private void RefreshLibroRows(IReadOnlyList<LibroBook>? books = null)
    {
        var source = books ?? LibroBooks.Select(r => r.Book).ToList();
        LibroBooks.Clear();
        foreach (var book in source)
        {
            var state = AudiobookDownloadService.Instance.GetState(AudiobookDownloadService.ListingFor(book), App.FolderPath);
            LibroBooks.Add(new LibroBookRow(book, state == AudiobookDownloadState.Downloaded));
        }
    }

    public async Task DownloadLibroBookAsync(LibroBook book)
    {
        if (_libroToken is null || string.IsNullOrWhiteSpace(App.FolderPath))
        {
            return;
        }
        AudiobookLibrary.RecordLibroAcquisition(book);
        LibroStatusText = $"Downloading {book.Title}…";
        await AudiobookDownloadService.Instance.EnqueueLibroAsync(book, _libroToken, App.FolderPath);
    }

    /// <summary>
    /// Re-fetches a previously-acquired Libro.fm book from its record. Libro re-download needs a live
    /// session (the token isn't stored), so this reveals the store and asks for sign-in when absent.
    /// </summary>
    internal async Task ReDownloadLibroAsync(string isbn, string? title, string? creator)
    {
        if (_libroToken is null)
        {
            ShowingStore = true;
            LibroStatusText = "Sign in to Libro.fm to re-download this book.";
            return;
        }
        if (string.IsNullOrWhiteSpace(App.FolderPath))
        {
            return;
        }

        var book = new LibroBook { Isbn = isbn, Title = title, Authors = string.IsNullOrWhiteSpace(creator) ? [] : [creator!] };
        LibroStatusText = $"Downloading {title}…";
        await AudiobookDownloadService.Instance.EnqueueLibroAsync(book, _libroToken, App.FolderPath);
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

        ShowingStore = true;   // a search is a store browse - reveal it if the user was on their shelf

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
        DownloadState = AudiobookDownloadService.Instance.GetState(book, App.FolderPath);
        DownloadProgressPercent = 0;
        DownloadProgressText = null;

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

    // ── download ───────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload), nameof(IsDownloadingBook), nameof(IsBookDownloaded))]
    private AudiobookDownloadState _downloadState;

    public bool CanDownload => DownloadState == AudiobookDownloadState.NotDownloaded;
    public bool IsDownloadingBook => DownloadState == AudiobookDownloadState.InProgress;
    public bool IsBookDownloaded => DownloadState == AudiobookDownloadState.Downloaded;

    [ObservableProperty]
    private double _downloadProgressPercent;

    [ObservableProperty]
    private string? _downloadProgressText;

    [ObservableProperty]
    private string? _downloadErrorText;

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (SelectedBook is not { } book || string.IsNullOrWhiteSpace(App.FolderPath))
        {
            return;
        }
        // Acquiring is remembered independently of the bytes: the record persists even if the
        // download is interrupted or the file is later deleted, so the book can be re-downloaded.
        AudiobookLibrary.RecordArchiveAcquisition(book);
        DownloadState = AudiobookDownloadState.InProgress;
        DownloadProgressPercent = 0;
        DownloadProgressText = "Starting…";
        DownloadErrorText = null;
        await AudiobookDownloadService.Instance.EnqueueAsync(book, App.FolderPath);
    }

    private void OnDownloadProgress(AudiobookDownloadProgress p)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (p.Identifier.StartsWith("libro:", StringComparison.Ordinal))
            {
                var pct = p.Total > 0 ? $" — {100.0 * p.Received / p.Total:0}%" : $" — {p.Received / (1024.0 * 1024):0} MB";
                LibroStatusText = $"Downloading {p.Title}{pct}";
                return;
            }
            if (p.Identifier != SelectedBook?.Identifier)
            {
                return;
            }
            DownloadProgressPercent = p.Total > 0 ? 100.0 * p.Received / p.Total : 0;
            DownloadProgressText = p.FileCount > 1
                ? $"File {p.FileIndex} of {p.FileCount} — {DownloadProgressPercent:0}%"
                : $"{DownloadProgressPercent:0}%";
        });
    }

    private void OnDownloadCompleted(AudiobookListing book)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (book.Identifier.StartsWith("libro:", StringComparison.Ordinal))
            {
                LibroStatusText = null;
                RefreshLibroRows();   // the tile flips to its gold check
            }
            else if (book.Identifier == SelectedBook?.Identifier)
            {
                DownloadState = AudiobookDownloadState.Downloaded;
                DownloadProgressText = null;
            }
            // The files live inside the watched library folder; a delta scan folds them into
            // _allItems as audiobooks (the m4b by container, .audiobooks by location),
            // which is exactly what fills the grid under the store.
            _ = _main?.ScanAndAnalyzeLibraryAsync();
        });
    }

    private void OnDownloadFailed(string identifier, Exception ex)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (identifier.StartsWith("libro:", StringComparison.Ordinal))
            {
                LibroStatusText = ex is OperationCanceledException ? null : ex.Message;
                return;
            }
            if (identifier == SelectedBook?.Identifier)
            {
                DownloadState = AudiobookDownloadState.NotDownloaded;
                DownloadProgressText = null;
                DownloadErrorText = ex is OperationCanceledException ? null : ex.Message;
            }
        });
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

    /// <summary>Shared with the download stamp - see <see cref="ArchiveOrgClient.StripHtml"/>.</summary>
    internal static string? StripHtml(string? html) => ArchiveOrgClient.StripHtml(html);
}
