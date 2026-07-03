// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Collections;
using Avalonia.Threading;
using OrgZ.Models;
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

    // Pre-import probe readout for the SOURCE pane; cleared whenever the selection moves.
    [ObservableProperty] private string _sourceProbeSummary = "";
    [ObservableProperty] private string _sourceProbeStats = "";

    partial void OnSelectedSourceChanged(SourceStation? value)
    {
        SourceProbeSummary = "";
        SourceProbeStats = "";
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
    private async Task ImportSelectedAsync()
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
                    var genreId = ImportGenre == "Auto" ? (int)src.SuggestedGenre : (int)RadioGenres.FromDisplayName(ImportGenre);
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
        var rows = SourceResults.ToList();
        SourceResults.Clear();
        foreach (var r in rows)
        {
            r.CuratedMark = StationMatcher.FindMatch(_db.Stations, r) != null ? "✓" : "";
            SourceResults.Add(r);
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

        // No SortDescriptions on the view: the list is pre-sorted by taxonomy id (unassigned
        // last), and DataGridCollectionView's insertion-order fallback keeps the genre groups
        // in that order instead of re-sorting the headers alphabetically.
        _curatedList = rows.OrderBy(s => s.GenreId == 0 ? int.MaxValue : s.GenreId).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
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
        StartPlayback($"{SelectedCurated.Name} ({variant.EffectiveFormat} {variant.EffectiveBitrate}k)", variant.PlayUrl);
    }

    [RelayCommand]
    private void StopPlayback()
    {
        _player.Stop();
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
        StartPlayback($"{SelectedCurated!.Name} ({best.EffectiveFormat} {best.EffectiveBitrate}k)", best.PlayUrl);
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

    private void StartPlayback(string label, string url)
    {
        NowPlaying = $"▶ {label}";
        StatusMessage = $"Opening {url}…";
        _player.Play(url);
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
                NowPlaying = "";
            }
            break;

            case "ended":
            {
                StatusMessage = "Stream ended";
                NowPlaying = "";
            }
            break;

            case "stopped":
            {
                NowPlaying = "";
            }
            break;
        }
    }

    // -- Probing --

    /// <summary>Pre-import probe for the SOURCE pane - same prober, results land in the pane labels instead of the store.</summary>
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
            SourceProbeSummary = "probing…";
            SourceProbeStats = "";
            var url = await ResolveSourceUrlAsync(src);
            if (string.IsNullOrEmpty(url))
            {
                SourceProbeSummary = "no stream URL";
                return;
            }
            var outcome = await StreamProber.ProbeAsync(url, CancellationToken.None);
            if (SelectedSource != src)
            {
                return; // Selection moved on while the probe ran; don't stamp the wrong row's pane.
            }
            SourceProbeSummary = $"{outcome.Status} — {outcome.Detail}";
            SourceProbeStats = FormatSourceProbeStats(src, outcome);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string FormatSourceProbeStats(SourceStation src, ProbeOutcome outcome)
    {
        var parts = new List<string>();
        var format = outcome.MeasuredFormat ?? outcome.Format;
        var kbps = outcome.MeasuredBitrate ?? outcome.Bitrate;
        if (!string.IsNullOrEmpty(format))
        {
            parts.Add(kbps is int k and > 0 ? $"{format} {k}k" : format!);
        }
        parts.Add($"{outcome.Redirects} hops");
        parts.Add(outcome.MetaInt is int m ? $"icy {m}" : "no metadata");
        if (!string.IsNullOrEmpty(outcome.MeasuredFormat) && !string.IsNullOrEmpty(src.Format) && !src.Format.Equals(outcome.MeasuredFormat, StringComparison.OrdinalIgnoreCase))
        {
            parts.Add($"≠ {src.Format}→{outcome.MeasuredFormat}");
        }
        if (outcome.MeasuredBitrate is int measured && src.Bitrate > 0 && Math.Abs(measured - src.Bitrate) > 16)
        {
            parts.Add($"≠ {src.Bitrate}→{measured}k");
        }
        if (outcome.ServerCountryCode != null)
        {
            parts.Add(outcome.GeoSuspect ? $"{outcome.ServerCountryCode} ⚠" : outcome.ServerCountryCode);
        }
        else if (outcome.GeoSuspect)
        {
            parts.Add("⚠");
        }
        return string.Join(" · ", parts);
    }

    [RelayCommand]
    private async Task ProbeVariantAsync()
    {
        // Work off a captured reference: RebuildVariants clears the variants collection, which
        // makes the grid drop its selection and push null back into SelectedVariant mid-method.
        var variant = SelectedVariant;
        if (SelectedCurated == null || variant == null)
        {
            return;
        }
        IsBusy = true;
        try
        {
            StatusMessage = $"Probing {variant.Url}…";
            ApplyProbe(variant, await StreamProber.ProbeAsync(variant.Url, CancellationToken.None));
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

            SaveStore();
            RebuildVariants();
            RefreshCuratedGridCells();
            var ok = work.Count(v => v.ProbeStatus == ProbeStatus.Ok);
            var dead = work.Count(v => v.ProbeStatus == ProbeStatus.Dead);
            var geo = work.Count(v => v.ProbeStatus == ProbeStatus.Geo);
            StatusMessage = $"Probed {work.Count} streams: {ok} ok, {dead} dead, {geo} geoblocked";
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
        variant.ProbeTitle = outcome.StreamTitle;
        variant.ProbeMeasuredFormat = outcome.MeasuredFormat;
        variant.ProbeMeasuredBitrate = outcome.MeasuredBitrate;
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
