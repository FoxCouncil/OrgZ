// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Collections;
using Avalonia.Threading;
using OrgZ.Models;
using OrgZ.Services;
using OrgZ.StationCurator.Models;
using OrgZ.StationCurator.Services;

namespace OrgZ.StationCurator.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly RadioBrowserClient _radioBrowser = new();
    private readonly AudioPlayer _player = new();
    private readonly CuratedDb _db;
    private List<SourceStation> _selectedSources = [];
    private CancellationTokenSource? _cts;

    public MainViewModel()
    {
        _db = CuratedStore.Load();
        _player.StateChanged += state => Dispatcher.UIThread.Post(() => OnPlayerState(state));
        _player.NowPlayingChanged += title => Dispatcher.UIThread.Post(() => OnNowPlayingMeta(title));
        _player.FactsSettled += facts => Dispatcher.UIThread.Post(() => _ = ApplyAuditionFactsAsync(facts));
        if (!_player.IsAvailable)
        {
            StatusMessage = $"Playback unavailable: {_player.UnavailableReason}";
        }
        RefreshCuratedView();
        UpdateStoreSummary();
    }

    // -- Toolbar / status --

    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string _nowPlaying = "";
    [ObservableProperty] private string _storeSummary = "";
    [ObservableProperty] private bool _isBusy;

    // -- Source pane --

    [ObservableProperty] private int _sourceTabIndex;
    [ObservableProperty] private string _rbName = "";
    [ObservableProperty] private string _rbTag = "";
    [ObservableProperty] private string _rbCountry = "";
    [ObservableProperty] private string _rbOrder = "clickcount";
    [ObservableProperty] private int _rbLimit = 100;
    [ObservableProperty] private string _scGenre = "";
    [ObservableProperty] private string _scQuery = "";
    [ObservableProperty] private string _icecastUrl = "";
    [ObservableProperty] private SourceStation? _selectedSource;
    [ObservableProperty] private string _importGenre = "Auto";

    // Pre-import probe result for the SOURCE pane - one StreamVariant row rendered by the
    // same grid the STATION pane uses, so both sides read identically. Cleared on selection move.
    public ObservableCollection<StreamVariant> SourceProbeRows { get; } = [];

    partial void OnSelectedSourceChanged(SourceStation? value)
    {
        SourceProbeRows.Clear();
    }

    public ObservableCollection<SourceStation> SourceResults { get; } = [];

    public string[] RbOrders { get; } = ["clickcount", "votes", "bitrate"];
    public int[] RbLimits { get; } = [50, 100, 200, 500];
    public string[] ImportGenreChoices { get; } = ["Auto", .. RadioGenres.All.Select(g => g.DisplayName())];

    public void SetSelectedSources(List<SourceStation> rows)
    {
        _selectedSources = rows;
    }

    [RelayCommand]
    private Task SearchRadioBrowserAsync() => RunSourceQueryAsync("radio-browser", ct => _radioBrowser.SearchAsync(RbName, RbTag, RbCountry, RbOrder, RbLimit, ct));

    [RelayCommand]
    private Task ShoutcastTopAsync() => RunSourceQueryAsync("SHOUTcast top", ShoutcastClient_Top);

    [RelayCommand]
    private Task ShoutcastGenreAsync() =>
        string.IsNullOrWhiteSpace(ScGenre) ? Task.CompletedTask : RunSourceQueryAsync($"SHOUTcast genre '{ScGenre}'", ct => new ShoutcastClient().BrowseGenreAsync(ScGenre, ct));

    [RelayCommand]
    private Task ShoutcastSearchAsync() =>
        string.IsNullOrWhiteSpace(ScQuery) ? Task.CompletedTask : RunSourceQueryAsync($"SHOUTcast search '{ScQuery}'", ct => new ShoutcastClient().SearchAsync(ScQuery, ct));

    [RelayCommand]
    private Task FetchIcecastAsync() =>
        string.IsNullOrWhiteSpace(IcecastUrl) ? Task.CompletedTask : RunSourceQueryAsync($"Icecast {IcecastUrl}", ct => IcecastClient.FetchMountsAsync(IcecastUrl, ct));

    private Task<List<SourceStation>> ShoutcastClient_Top(CancellationToken ct) => new ShoutcastClient().TopAsync(ct);

    private async Task RunSourceQueryAsync(string label, Func<CancellationToken, Task<List<SourceStation>>> query)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        StatusMessage = $"Querying {label}…";
        try
        {
            var results = await query(_cts.Token);
            SourceResults.Clear();
            foreach (var r in results)
            {
                r.CuratedMark = StationMatcher.FindMatch(_db.Stations, r) != null ? "✓" : "";
                SourceResults.Add(r);
            }
            StatusMessage = $"{label}: {results.Count} stations";
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer query.
        }
        catch (Exception ex)
        {
            StatusMessage = $"{label} failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // -- Import --

    [RelayCommand]
    private Task ImportSelectedAsync() => ImportRowsAsync(autoGenre: false);

    /// <summary>Import forcing the tag-detected (Suggested) genre, ignoring the Genre dropdown - the "always use our detection" shortcut.</summary>
    [RelayCommand]
    private Task ImportSelectedAutoAsync() => ImportRowsAsync(autoGenre: true);

    private async Task ImportRowsAsync(bool autoGenre)
    {
        var rows = _selectedSources.Count > 0 ? _selectedSources.ToList() : SelectedSource != null ? [SelectedSource] : [];
        if (rows.Count == 0)
        {
            StatusMessage = "Nothing selected to import";
            return;
        }

        IsBusy = true;
        int added = 0, merged = 0, present = 0, unresolved = 0;
        try
        {
            foreach (var src in rows)
            {
                var url = await ResolveSourceUrlAsync(src);
                if (string.IsNullOrEmpty(url))
                {
                    unresolved++;
                    continue;
                }

                var variant = new StreamVariant
                {
                    Url = url,
                    Format = src.Format,
                    Bitrate = src.Bitrate,
                    Source = src.Source,
                    SourceId = src.SourceId,
                };

                var match = StationMatcher.FindMatch(_db.Stations, src);
                if (match != null)
                {
                    if (match.Streams.Any(v => StationMatcher.UrlsEqual(v.Url, url)))
                    {
                        present++;
                        continue;
                    }
                    match.Streams.Add(variant);
                    match.Country ??= src.Country;
                    match.CountryCode ??= src.CountryCode;
                    match.Homepage ??= src.Homepage;
                    match.LogoUrl ??= src.LogoUrl;
                    if (string.IsNullOrEmpty(match.Description))
                    {
                        match.Description = src.Tags;
                    }
                    merged++;
                }
                else
                {
                    var genreId = autoGenre || ImportGenre == "Auto" ? (int)src.SuggestedGenre : (int)RadioGenres.FromDisplayName(ImportGenre);
                    var id = StationMatcher.MakeStationId(src);
                    while (_db.Stations.Any(s => s.Id == id))
                    {
                        id += "+";
                    }
                    _db.Stations.Add(new CuratedStation
                    {
                        Id = id,
                        Name = src.Name,
                        GenreId = genreId,
                        Country = src.Country,
                        CountryCode = src.CountryCode,
                        Homepage = src.Homepage,
                        LogoUrl = src.LogoUrl,
                        Description = src.Tags,
                        Streams = [variant],
                    });
                    added++;
                }
            }

            SaveStore();
            RemarkSourceRows();
            RefreshCuratedView();
            StatusMessage = $"Imported {added} new, merged {merged} variants, {present} already present" + (unresolved > 0 ? $", {unresolved} unresolved" : "");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RemarkSourceRows()
    {
        // In-place update + INPC poke - rebuilding the collection would reset the source
        // grid's scroll position (and multi-selection) on every import.
        foreach (var r in SourceResults)
        {
            r.CuratedMark = StationMatcher.FindMatch(_db.Stations, r) != null ? "✓" : "";
            r.NotifyChanged();
        }
    }

    // -- Curated pane --

    public ObservableCollection<StreamVariant> Variants { get; } = [];

    /// <summary>Raised by "Show in Curated" so the view can expand the target's genre group and scroll the row into view.</summary>
    public event Action<CuratedStation>? CuratedRevealRequested;

    /// <summary>
    /// Grid-bound view of the curated list, grouped by genre the same way the main app's radio
    /// view is - DataGridCollectionView + a path group description gives Avalonia's collapsible
    /// row-group headers. Rebuilt by <see cref="RefreshCuratedView"/> on every filter/store change.
    /// </summary>
    [ObservableProperty] private DataGridCollectionView? _curatedView;
    private List<CuratedStation> _curatedList = [];

    [ObservableProperty] private CuratedStation? _selectedCurated;
    [ObservableProperty] private StreamVariant? _selectedVariant;
    [ObservableProperty] private string _newStreamUrl = "";

    // The stream-level buttons act on the SELECTED row only - disabled without one, so a
    // click with nothing selected can never surprise-probe anything.
    partial void OnSelectedVariantChanged(StreamVariant? value) => ProbeVariantCommand.NotifyCanExecuteChanged();

    private bool CanProbeVariant() => SelectedVariant != null;
    [ObservableProperty] private string _curatedFilterGenre = "All";
    [ObservableProperty] private string _curatedSearch = "";

    public string[] CuratedFilterChoices { get; } = ["All", "Unassigned", .. RadioGenres.All.Select(g => g.DisplayName())];
    public string[] DetailGenreChoices { get; } = ["—", .. RadioGenres.All.Select(g => g.DisplayName())];

    public string SelectedCuratedGenreName
    {
        get => SelectedCurated == null || SelectedCurated.GenreId == 0 ? "—" : RadioGenres.DisplayName(SelectedCurated.GenreId);
        set
        {
            if (SelectedCurated != null)
            {
                SelectedCurated.GenreId = (int)RadioGenres.FromDisplayName(value);
                SelectedCurated.NotifyChanged();
                OnPropertyChanged();
                // Regroup so the station physically moves under its new genre header now,
                // not on whatever refresh happens to come next.
                RefreshCuratedView();
            }
        }
    }

    partial void OnSelectedCuratedChanged(CuratedStation? value)
    {
        RebuildVariants();
        OnPropertyChanged(nameof(SelectedCuratedGenreName));
    }

    partial void OnCuratedFilterGenreChanged(string value) => RefreshCuratedView();
    partial void OnCuratedSearchChanged(string value) => RefreshCuratedView();

    private void RebuildVariants()
    {
        // Clearing the collection makes the grid reset its selection (nulling SelectedVariant
        // through the binding) - remember it and re-select the same variant after the rebuild.
        var keepId = SelectedVariant?.Id;

        Variants.Clear();
        if (SelectedCurated == null)
        {
            return;
        }
        var best = SelectedCurated.BestVariant();
        foreach (var v in SelectedCurated.Streams)
        {
            v.PreferredMark = v.Id == SelectedCurated.PreferredStreamId ? "★" : v == best ? "•" : "";
            v.NotifyChanged();
            Variants.Add(v);
        }

        if (keepId != null)
        {
            SelectedVariant = Variants.FirstOrDefault(v => v.Id == keepId);
        }
        // Auto-select the first stream when nothing carried over (e.g. a fresh station selection),
        // so the stream-level actions and Play target a row immediately.
        SelectedVariant ??= Variants.FirstOrDefault();
    }

    private void RefreshCuratedView()
    {
        var keepId = SelectedCurated?.Id;

        IEnumerable<CuratedStation> rows = _db.Stations;
        if (CuratedFilterGenre == "Unassigned")
        {
            rows = rows.Where(s => s.GenreId == 0);
        }
        else if (CuratedFilterGenre != "All")
        {
            var id = (int)RadioGenres.FromDisplayName(CuratedFilterGenre);
            rows = rows.Where(s => s.GenreId == id);
        }
        if (!string.IsNullOrWhiteSpace(CuratedSearch))
        {
            rows = rows.Where(s =>
                s.Name.Contains(CuratedSearch, StringComparison.OrdinalIgnoreCase) ||
                (s.Country?.Contains(CuratedSearch, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (s.Description?.Contains(CuratedSearch, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var filtered = rows.ToList();

        // Empty genres stay visible: one inert "-" placeholder row per stationless genre, so
        // coverage gaps (like a freshly added genre) are impossible to miss. Skipped while
        // searching (a text search should surface matches only) and for "Unassigned" (that
        // pseudo-bucket only exists when stations are actually in it). Placeholders live in
        // the VIEW list only - never the store, so save/export/import can't see them.
        if (string.IsNullOrWhiteSpace(CuratedSearch) && CuratedFilterGenre != "Unassigned")
        {
            IEnumerable<RadioGenre> scope = CuratedFilterGenre == "All"
                ? RadioGenres.All
                : [RadioGenres.FromDisplayName(CuratedFilterGenre)];
            var present = filtered.Select(s => s.GenreId).ToHashSet();
            foreach (var genre in scope)
            {
                if (genre != RadioGenre.Unknown && !present.Contains((int)genre))
                {
                    filtered.Add(new CuratedStation { Id = $"placeholder:{(int)genre}", Name = "—", GenreId = (int)genre, IsPlaceholder = true });
                }
            }
        }

        // No SortDescriptions on the view: the list is pre-sorted by taxonomy id (unassigned
        // last), and DataGridCollectionView's insertion-order fallback keeps the genre groups
        // in that order instead of re-sorting the headers alphabetically. Genre ids ARE the
        // display order by invariant - taxonomy changes renumber and migrate curated.json.
        _curatedList = filtered.OrderBy(s => s.GenreId == 0 ? int.MaxValue : s.GenreId).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var view = new DataGridCollectionView(_curatedList);
        view.GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(CuratedStation.GenreName)));
        CuratedView = view;

        SelectedCurated = keepId == null ? null : _curatedList.FirstOrDefault(s => s.Id == keepId);
        UpdateStoreSummary();
    }

    private void UpdateStoreSummary()
    {
        var streams = _db.Stations.Sum(s => s.Streams.Count);
        var shipping = _db.Stations.Count(s => s.GenreId != 0 && RadioGenres.DisplayName(s.GenreId).Length > 0 && s.BestVariant() != null);
        StoreSummary = $"{_db.Stations.Count} stations · {streams} streams · {shipping} shippable";
    }

    /// <summary>Create a blank station from scratch and select it, so every field can be filled by hand
    /// (Name/Genre/Country/Home/Logo/Notes in the STATION panel, plus Add Stream for the URL).</summary>
    [RelayCommand]
    private void NewStation()
    {
        var id = "manual-" + Guid.NewGuid().ToString("N")[..8];
        var station = new CuratedStation { Id = id, Name = "New Station", GenreId = 0 };
        _db.Stations.Add(station);
        SaveStore();
        // Clear filters so an active genre/search filter can't hide the fresh (unassigned) station.
        CuratedFilterGenre = "All";
        CuratedSearch = "";
        RefreshCuratedView();
        SelectedCurated = _curatedList.FirstOrDefault(s => s.Id == id);
        CuratedRevealRequested?.Invoke(station);
        StatusMessage = "New blank station — edit its details, then paste a URL into Add Stream";
    }

    [RelayCommand]
    private void RemoveStation()
    {
        if (SelectedCurated == null)
        {
            return;
        }
        _db.Stations.Remove(SelectedCurated);
        SelectedCurated = null;
        SaveStore();
        RemarkSourceRows();
        RefreshCuratedView();
    }

    [RelayCommand]
    private void RemoveVariant()
    {
        if (SelectedCurated == null || SelectedVariant == null)
        {
            return;
        }
        SelectedCurated.Streams.Remove(SelectedVariant);
        if (SelectedCurated.PreferredStreamId == SelectedVariant.Id)
        {
            SelectedCurated.PreferredStreamId = null;
        }
        SelectedVariant = null;
        SaveStore();
        RebuildVariants();
        UpdateStoreSummary();
    }

    [RelayCommand]
    private void SetPreferredVariant()
    {
        if (SelectedCurated == null || SelectedVariant == null)
        {
            return;
        }
        SelectedCurated.PreferredStreamId = SelectedCurated.PreferredStreamId == SelectedVariant.Id ? null : SelectedVariant.Id;
        SaveStore();
        RebuildVariants();
    }

    /// <summary>Manually attach a stream URL (e.g. a SomaFM .pls or a direct ICY URL) to the selected station.</summary>
    [RelayCommand]
    private void AddStream()
    {
        var station = SelectedCurated;
        var url = NewStreamUrl?.Trim();
        if (station == null || string.IsNullOrWhiteSpace(url))
        {
            StatusMessage = "Select a station and type a stream URL first";
            return;
        }
        if (station.Streams.Any(v => StationMatcher.UrlsEqual(v.Url, url)))
        {
            StatusMessage = "That URL is already a stream on this station";
            return;
        }
        station.Streams.Add(new StreamVariant { Url = url, Source = "manual" });
        NewStreamUrl = "";
        SaveStore();
        RebuildVariants();
        UpdateStoreSummary();
        StatusMessage = $"Added stream to {station.Name} — probe it to fill format/bitrate";
    }

    // -- Playback --

    [RelayCommand]
    private async Task PreviewSourceAsync()
    {
        if (!PlayerReady())
        {
            return;
        }
        var src = SelectedSource;
        if (src == null)
        {
            StatusMessage = "Select a source row first";
            return;
        }
        var url = await ResolveSourceUrlAsync(src);
        if (string.IsNullOrEmpty(url))
        {
            StatusMessage = $"No stream URL for {src.Name}";
            return;
        }
        _auditionVariant = null;
        _auditionStation = null;
        _auditionSource = src;
        _auditionUrl = url;
        StartPlayback(src.Name, url);
    }

    /// <summary>SHOUTcast directory rows carry no direct URL until their tunein id is resolved; everyone else already has one.</summary>
    private async Task<string> ResolveSourceUrlAsync(SourceStation src)
    {
        var url = src.StreamUrl;
        if (string.IsNullOrEmpty(url) && src.Source == "shoutcast" && src.SourceId != null)
        {
            StatusMessage = $"Resolving {src.Name}…";
            url = await ShoutcastClient.ResolveStreamUrlAsync(src.SourceId, CancellationToken.None) ?? "";
            src.StreamUrl = url;
        }

        // The directory API carries no homepage or images, but the DNAS server describes
        // itself - homepage (→ favicon logo), listener stats, uptime, version. Fetched once
        // per row; rides into imports via src.Homepage/LogoUrl.
        if (src.Source == "shoutcast" && !string.IsNullOrEmpty(url) && src.ServerInfo == null)
        {
            var details = await ShoutcastClient.FetchServerDetailsAsync(url, CancellationToken.None);
            if (details != null)
            {
                if (!string.IsNullOrWhiteSpace(details.Homepage))
                {
                    src.Homepage ??= details.Homepage;
                    src.LogoUrl ??= await ShoutcastClient.ResolveLogoUrlAsync(details.Homepage, CancellationToken.None);
                }
                src.ServerInfo = details.Summary;
                src.NotifyChanged();
            }
        }
        return url;
    }

    /// <summary>
    /// The toolbar transport's Play - routes to whatever is selected: a curated station
    /// (its selected variant, else its best), otherwise the highlighted source row.
    /// </summary>
    [RelayCommand]
    private async Task PlayAsync()
    {
        if (SelectedCurated == null && SelectedSource == null)
        {
            StatusMessage = "Select a station or source row first";
            return;
        }
        if (SelectedCurated != null)
        {
            PlayVariant();
            return;
        }
        await PreviewSourceAsync();
    }

    private void PlayVariant()
    {
        if (!PlayerReady())
        {
            return;
        }
        if (SelectedCurated == null)
        {
            StatusMessage = "Select a station first";
            return;
        }

        // No variant row picked → the station's best stream, same as export would ship.
        var variant = SelectedVariant ?? SelectedCurated.BestVariant();
        if (variant == null)
        {
            StatusMessage = $"{SelectedCurated.Name} has no streams";
            return;
        }
        _auditionVariant = variant;
        _auditionStation = SelectedCurated;
        _auditionSource = null;
        _auditionUrl = variant.PlayUrl;
        StartPlayback($"{SelectedCurated.Name} ({variant.EffectiveFormat} {variant.EffectiveBitrate}k)", variant.PlayUrl);
    }

    [RelayCommand]
    private void StopPlayback()
    {
        _player.Stop();
        _nowPlayingBase = "";
        NowPlaying = "";
        StatusMessage = "Stopped (stop button)";
    }

    public void PlayBestOfSelectedCurated()
    {
        if (!PlayerReady())
        {
            return;
        }
        var best = SelectedCurated?.BestVariant();
        if (best == null)
        {
            return;
        }
        _auditionVariant = best;
        _auditionStation = SelectedCurated;
        _auditionSource = null;
        _auditionUrl = best.PlayUrl;
        StartPlayback($"{SelectedCurated!.Name} ({best.EffectiveFormat} {best.EffectiveBitrate}k)", best.PlayUrl);
    }

    // -- Audition probing: every listen IS a probe. The player's session settles its facts
    // (real codec, bitrate, metadata channel, tune-in time) off the SAME connection the
    // audio rides - no second connection is ever opened for an audition. --

    /// <summary>What the in-flight audition should stamp when the session's facts settle.</summary>
    private StreamVariant? _auditionVariant;
    private CuratedStation? _auditionStation;
    private SourceStation? _auditionSource;
    private string? _auditionUrl;

    private async Task ApplyAuditionFactsAsync(OrgZ.Services.StreamFacts facts)
    {
        try
        {
            var url = _auditionUrl;
            if (url == null)
            {
                return;
            }
            var outcome = await StreamProber.FinishOutcomeAsync(StreamProber.FromFacts(facts), url);

            if (_auditionVariant is { } variant)
            {
                ApplyProbe(variant, outcome);
                if (outcome.Ok && _auditionStation is { } station)
                {
                    await EnrichStationFromServerAsync(station, url);
                }
                SaveStore();
                RebuildVariants();
                RefreshCuratedGridCells();
            }
            else if (_auditionSource is { } src)
            {
                var row = new StreamVariant
                {
                    Url = url,
                    Format = src.Format,
                    Bitrate = src.Bitrate,
                    Source = src.Source,
                    SourceId = src.SourceId,
                };
                ApplyProbe(row, outcome);
                if (SelectedSource == src)
                {
                    SourceProbeRows.Clear();
                    SourceProbeRows.Add(row);
                }
            }
        }
        catch
        {
            // Best-effort: playback reports its own failures.
        }
    }

    private bool PlayerReady()
    {
        if (!_player.IsAvailable)
        {
            StatusMessage = $"Playback unavailable: {_player.UnavailableReason}";
            return false;
        }
        return true;
    }

    /// <summary>The "▶ station" part of the toolbar readout; live stream metadata gets appended after ♪.</summary>
    private string _nowPlayingBase = "";

    private void StartPlayback(string label, string url)
    {
        _nowPlayingBase = $"▶ {label}";
        NowPlaying = _nowPlayingBase;
        StatusMessage = $"Opening {url}…";
        _player.Play(url);
    }

    // One source of truth: LibVLC's MetaChanged. Titles the demuxer can't read (https ICY,
    // HLS timed-ID3) are injected into the Media by the player's sidecar and arrive here
    // through the exact same event.
    private void OnNowPlayingMeta(string? title)
    {
        if (_nowPlayingBase.Length == 0)
        {
            return;
        }
        NowPlaying = string.IsNullOrWhiteSpace(title) ? _nowPlayingBase : $"{_nowPlayingBase} ♪ {IcyMetadata.CleanStreamTitle(title!)}";
    }

    private void OnPlayerState(string state)
    {
        switch (state)
        {
            case "opening":
            {
                StatusMessage = "Opening stream…";
            }
            break;

            case "playing":
            {
                StatusMessage = $"Playing {NowPlaying.TrimStart('▶', ' ')}";
            }
            break;

            case "error":
            {
                StatusMessage = $"Playback failed: {NowPlaying.TrimStart('▶', ' ')}";
                _nowPlayingBase = "";
                NowPlaying = "";
            }
            break;

            case "ended":
            {
                StatusMessage = "Stream ended";
                _nowPlayingBase = "";
                NowPlaying = "";
            }
            break;

            case "stopped":
            {
                _nowPlayingBase = "";
                NowPlaying = "";
            }
            break;
        }
    }

    // -- Probing --

    /// <summary>
    /// Pre-import probe for the SOURCE pane - same prober, and the result is materialized as a
    /// StreamVariant so the pane's grid shows the exact columns the STATION pane does.
    /// </summary>
    [RelayCommand]
    private async Task ProbeSourceAsync()
    {
        var src = SelectedSource;
        if (src == null)
        {
            return;
        }
        IsBusy = true;
        try
        {
            var url = await ResolveSourceUrlAsync(src);
            if (string.IsNullOrEmpty(url))
            {
                StatusMessage = $"No stream URL for {src.Name}";
                return;
            }
            StatusMessage = $"Probing {url}…";
            var variant = new StreamVariant
            {
                Url = url,
                Format = src.Format,
                Bitrate = src.Bitrate,
                Source = src.Source,
                SourceId = src.SourceId,
            };
            ApplyProbe(variant, await StreamProber.ProbeAsync(url, CancellationToken.None));
            if (SelectedSource != src)
            {
                return; // Selection moved on while the probe ran; don't stamp the wrong row's pane.
            }
            SourceProbeRows.Clear();
            SourceProbeRows.Add(variant);
            StatusMessage = $"Probe: {variant.ProbeStatus} — {variant.ProbeDetail}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Overwrites the station's advertised country with the probed server's GeoIP country - the one that actually matters for shipping.</summary>
    [RelayCommand]
    private void UseGeoIpCountry()
    {
        var station = SelectedCurated;
        var best = station?.BestVariant();
        if (station == null || best?.ServerCountryCode == null)
        {
            StatusMessage = "No GeoIP data — probe the station first";
            return;
        }
        station.Country = best.ServerCountry ?? best.ServerCountryCode;
        station.CountryCode = best.ServerCountryCode;
        station.NotifyChanged();
        StatusMessage = $"Country set from GeoIP: {station.Country} ({station.CountryCode})";
    }

    /// <summary>
    /// SHOUTcast-compatible servers self-describe (stats?sid=1&amp;json=1) - a probe of any
    /// live variant fills the station's BLANK identity fields: homepage and favicon-derived
    /// logo. Never overwrites curated values (and Notes is Fox's field - enrichment stays
    /// out of it); a non-DNAS server (Icecast, plain CDN) answers nothing and this no-ops.
    /// </summary>
    private static async Task<bool> EnrichStationFromServerAsync(CuratedStation station, string url)
    {
        if (!string.IsNullOrWhiteSpace(station.Homepage) && !string.IsNullOrWhiteSpace(station.LogoUrl))
        {
            return false;
        }
        var details = await ShoutcastClient.FetchServerDetailsAsync(url, CancellationToken.None);
        if (details == null || string.IsNullOrWhiteSpace(details.Homepage))
        {
            return false;
        }

        var changed = false;
        if (string.IsNullOrWhiteSpace(station.Homepage))
        {
            station.Homepage = details.Homepage;
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(station.LogoUrl))
        {
            station.LogoUrl = await ShoutcastClient.ResolveLogoUrlAsync(details.Homepage, CancellationToken.None);
            changed |= !string.IsNullOrWhiteSpace(station.LogoUrl);
        }
        if (changed)
        {
            station.NotifyChanged();
        }
        return changed;
    }

    [RelayCommand(CanExecute = nameof(CanProbeVariant))]
    private async Task ProbeVariantAsync()
    {
        // Work off a captured reference: RebuildVariants clears the variants collection, which
        // makes the grid drop its selection and push null back into SelectedVariant mid-method.
        var variant = SelectedVariant;
        var station = SelectedCurated;
        if (station == null || variant == null)
        {
            StatusMessage = "Select a stream row first";
            return;
        }
        IsBusy = true;
        try
        {
            StatusMessage = $"Probing {variant.Url}…";
            ApplyProbe(variant, await StreamProber.ProbeAsync(variant.Url, CancellationToken.None));
            if (variant.ProbeStatus == ProbeStatus.Ok)
            {
                await EnrichStationFromServerAsync(station, variant.PlayUrl);
            }
            SaveStore();
            RebuildVariants();
            RefreshCuratedGridCells();
            StatusMessage = $"Probe: {variant.ProbeStatus} — {variant.ProbeDetail}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ProbeStationAsync()
    {
        if (SelectedCurated == null)
        {
            return;
        }
        await ProbeStationsAsync([SelectedCurated]);
    }

    [RelayCommand]
    private async Task ProbeAllAsync()
    {
        await ProbeStationsAsync(_db.Stations.ToList());
    }

    private async Task ProbeStationsAsync(List<CuratedStation> stations)
    {
        var work = stations.SelectMany(s => s.Streams).ToList();
        if (work.Count == 0)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var done = 0;
            var gate = new SemaphoreSlim(8);
            var tasks = work.Select(async variant =>
            {
                await gate.WaitAsync();
                try
                {
                    var outcome = await StreamProber.ProbeAsync(variant.Url, CancellationToken.None);
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        ApplyProbe(variant, outcome);
                        done++;
                        StatusMessage = $"Probing… {done}/{work.Count}";
                    });
                }
                finally
                {
                    gate.Release();
                }
            });
            await Task.WhenAll(tasks);

            // Identity backfill off the same round: one DNAS self-description per station
            // with blanks, via its first live variant.
            var enriched = 0;
            foreach (var station in stations)
            {
                if (station.Streams.FirstOrDefault(v => v.ProbeStatus == ProbeStatus.Ok) is { } live && await EnrichStationFromServerAsync(station, live.PlayUrl))
                {
                    enriched++;
                }
            }

            SaveStore();
            RebuildVariants();
            RefreshCuratedGridCells();
            var ok = work.Count(v => v.ProbeStatus == ProbeStatus.Ok);
            var dead = work.Count(v => v.ProbeStatus == ProbeStatus.Dead);
            var geo = work.Count(v => v.ProbeStatus == ProbeStatus.Geo);
            StatusMessage = $"Probed {work.Count} streams: {ok} ok, {dead} dead, {geo} geoblocked" + (enriched > 0 ? $", {enriched} enriched" : "");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static void ApplyProbe(StreamVariant variant, ProbeOutcome outcome)
    {
        variant.ProbeStatus = outcome.Status;
        variant.ProbeDetail = outcome.Detail;
        if (!string.IsNullOrEmpty(outcome.Format))
        {
            variant.ProbeFormat = outcome.Format;
        }
        variant.ProbeBitrate = outcome.Bitrate ?? variant.ProbeBitrate;
        variant.ResolvedUrl = outcome.ResolvedUrl;
        variant.ProbedAtUtc = DateTimeOffset.UtcNow;
        variant.GeoRisk = outcome.GeoSuspect;
        variant.ProbeRedirects = outcome.Redirects;
        variant.ProbeMetaint = outcome.MetaInt;
        variant.ProbeHlsMeta = outcome.HlsMeta;
        variant.ProbeTitle = outcome.StreamTitle;
        variant.ProbeMeasuredFormat = outcome.MeasuredFormat;
        variant.ProbeMeasuredBitrate = outcome.MeasuredBitrate;
        variant.ProbeTuneInMs = outcome.TuneInMs ?? variant.ProbeTuneInMs;
        variant.ServerIp = outcome.ServerIp;
        variant.ServerCountry = outcome.ServerCountry;
        variant.ServerCountryCode = outcome.ServerCountryCode;
        variant.NotifyChanged();
    }

    /// <summary>The grids show computed cells (ProbeSummary etc.) off plain POCOs; poke every row so recycled cells re-read.</summary>
    private void RefreshCuratedGridCells()
    {
        foreach (var station in _db.Stations)
        {
            station.NotifyChanged();
        }
        UpdateStoreSummary();
    }

    // -- Persistence / export --

    [RelayCommand]
    public void SaveStore()
    {
        CuratedStore.Save(_db);
        RefreshCuratedGridCells();
    }

    [RelayCommand]
    private void ExportStations()
    {
        SaveStore();
        var result = StationExporter.Export(_db);
        StatusMessage = $"Exported {result.Exported} stations to Assets/stations.json" +
                        (result.SkippedUnassigned > 0 ? $" — {result.SkippedUnassigned} unassigned skipped" : "") +
                        (result.SkippedNoStream > 0 ? $" — {result.SkippedNoStream} without streams skipped" : "");
    }

    [RelayCommand]
    private void JumpToCurated()
    {
        if (SelectedSource == null)
        {
            return;
        }
        var match = StationMatcher.FindMatch(_db.Stations, SelectedSource);
        if (match == null)
        {
            StatusMessage = "Not in the curated list yet";
            return;
        }
        CuratedFilterGenre = "All";
        CuratedSearch = "";
        RefreshCuratedView();
        SelectedCurated = match;
        CuratedRevealRequested?.Invoke(match);
    }
}
