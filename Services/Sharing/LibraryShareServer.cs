// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Net;
using System.Text;
using System.Text.Json;
using Serilog;

namespace OrgZ.Services.Sharing;

/// <summary>
/// Serves a library read-only on the LAN and advertises it over mDNS as
/// <c>_orgz._tcp</c>. Deliberately boring routes: <c>/catalogue</c> (the full track
/// records as JSON), <c>/playlists</c> (names + ordered track ids), <c>/stream/{id}</c>
/// (the audio with Range support so a remote OrgZ can seek), and <c>/art/{id}</c>.
/// Spiritually DAAP without the dead protocol. Read-only by construction - there is no
/// mutating route to reach.
/// </summary>
public sealed class LibraryShareServer : IDisposable
{
    private static readonly ILogger _log = Logging.For("LibraryShare");

    /// <summary>One playlist as served on the wire. Type distinguishes the synthetic
    /// Favorites ("favorites") from ordinary playlists ("playlist") - clients key UI
    /// off the TYPE, never the display name.</summary>
    public sealed record ServedPlaylist(string Name, List<string> TrackIds, string Type = "playlist");

    private TlsHttpServer? _server;
    private readonly Func<List<MediaItem>> _loadLibrary;
    private readonly Func<List<ServedPlaylist>> _loadPlaylists;
    private readonly string _certificateDirectory;

    public string ShareName { get; }
    public int Port { get; }

    /// <summary>The TLS certificate pin this share announces; set once Start has run.</summary>
    public string? CertificatePin { get; private set; }

    public LibraryShareServer(string shareName, int port, Func<List<MediaItem>>? loadLibrary = null, Func<List<ServedPlaylist>>? loadPlaylists = null, string? certificateDirectory = null)
    {
        ShareName = string.IsNullOrWhiteSpace(shareName) ? "OrgZ Library" : shareName;
        Port = port;
        _loadLibrary = loadLibrary ?? MediaCache.LoadAll;
        _loadPlaylists = loadPlaylists ?? LoadLocalPlaylists;
        // Beside the library database, so the GUI and the service - whichever hosts -
        // present the same identity and clients keep their remembered pin.
        _certificateDirectory = certificateDirectory ?? (Path.GetDirectoryName(MediaCache.CurrentDatabasePath) ?? ".");
    }

    /// <summary>
    /// The local library's playlists as (name, ordered track ids) - Favorites first.
    /// Favorites isn't a playlist row (it's the per-track flag), so it has to be
    /// synthesized here or the remote never sees it.
    /// </summary>
    private static List<ServedPlaylist> LoadLocalPlaylists()
    {
        var playlists = new List<ServedPlaylist>();
        if (FavoritesPlaylist(MediaCache.LoadAll()) is { } favorites)
        {
            playlists.Add(new ServedPlaylist(favorites.Name, favorites.TrackIds, "favorites"));
        }
        playlists.AddRange(MediaCache.LoadAllPlaylists().Select(p => new ServedPlaylist(p.Name, MediaCache.GetPlaylistTrackIds(p.Id))));
        return playlists;
    }

    /// <summary>The synthetic Favorites playlist: every favorited track, library order. Null when there are none.</summary>
    internal static (string Name, List<string> TrackIds)? FavoritesPlaylist(IReadOnlyList<MediaItem> library)
    {
        var ids = library.Where(t => t.IsFavorite).Select(t => t.Id).ToList();
        return ids.Count > 0 ? ("Favorites", ids) : null;
    }

    public void Start() => Start(advertise: true);

    /// <summary>
    /// Serving and advertising are separate concerns: <paramref name="advertise"/> false
    /// brings the TLS side up without touching the mDNS multicast socket.
    /// </summary>
    internal void Start(bool advertise)
    {
        var certificate = ShareCertificate.LoadOrCreate(_certificateDirectory);
        CertificatePin = ShareCertificate.PinOf(certificate);

        // A raw socket needs no URL ACL, so - unlike the HttpListener era - an
        // unelevated GUI host is LAN-reachable, not loopback-only.
        _server = new TlsHttpServer(IPAddress.Any, Port, certificate, HandleRequestAsync);
        _server.Start();

        if (advertise)
        {
            // ONE mDNS responder for the whole process; the library share is one service on
            // it, the AirPlay DACP control endpoint is another. Registering the share here
            // rather than standing up a second advertiser is what keeps the two from fighting
            // over the single 5353 socket - the fight that left DACP undiscoverable and
            // controls unavailable.
            MdnsAdvertiser.EnsureResponder().Publish(
                MdnsWire.ServiceType,
                ShareName,
                (ushort)Port,
                [$"name={ShareName}", "version=1", "readonly=1", "tls=1", $"pin={CertificatePin}"]);
        }

        _log.Information("Library share \"{Name}\" listening on {Port} (TLS, pin {Pin})", ShareName, Port, CertificatePin);
    }

    private async Task HandleRequestAsync(TlsHttpServer.Request request, TlsHttpServer.Response response)
    {
        // No try/catch here: TlsHttpServer owns the 500 path (it logs the failure and
        // answers 500 when the head hasn't gone out yet) - two layers doing it meant
        // two places to keep the behaviour in sync.

        // Read-only share: anything that isn't a GET/HEAD is refused outright.
        if (request.Method is not ("GET" or "HEAD"))
        {
            response.StatusCode = 405;
            response.ContentLength64 = 0;
            await response.CloseAsync();
            return;
        }

        var path = request.Path;

        if (path.Equals("/catalogue", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(response, BuildCatalogueJson(ShareName, Snapshot().Library));
            return;
        }

        if (path.Equals("/playlists", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(response, BuildPlaylistsJson(_loadPlaylists()));
            return;
        }

        if (TryParseSegment(path, "/stream/", out var streamId))
        {
            await ServeStreamAsync(request, response, streamId);
            return;
        }

        if (TryParseSegment(path, "/art/", out var artId))
        {
            await ServeArtAsync(request, response, artId);
            return;
        }

        response.StatusCode = 404;
        response.ContentLength64 = 0;
        await response.CloseAsync();
    }

    // ── Library snapshot ─────────────────────────────────────
    // Every /stream/{id} Range request used to re-read the entire library from SQLite
    // and linear-scan it (twice, counting the extension-stripped retry); a seeking
    // client fires dozens of those a minute. The library changes far slower than
    // requests arrive, so a short-TTL snapshot with an id index serves the hot path,
    // and the TTL keeps a just-imported track reachable within seconds.
    private static readonly TimeSpan SnapshotTtl = TimeSpan.FromSeconds(5);
    private readonly object _snapshotGate = new();
    private List<MediaItem>? _snapshotLibrary;
    private Dictionary<string, MediaItem>? _snapshotById;
    private long _snapshotExpires;

    private (List<MediaItem> Library, Dictionary<string, MediaItem> ById) Snapshot()
    {
        lock (_snapshotGate)
        {
            if (_snapshotLibrary is null || Environment.TickCount64 >= _snapshotExpires)
            {
                _snapshotLibrary = _loadLibrary();
                _snapshotById = new Dictionary<string, MediaItem>(_snapshotLibrary.Count, StringComparer.Ordinal);
                foreach (var track in _snapshotLibrary)
                {
                    // TryAdd keeps the first occurrence, matching the linear scan's
                    // behaviour if two rows ever carried the same id.
                    _snapshotById.TryAdd(track.Id, track);
                }
                _snapshotExpires = Environment.TickCount64 + (long)SnapshotTtl.TotalMilliseconds;
            }

            return (_snapshotLibrary, _snapshotById!);
        }
    }

    private static async Task WriteJsonAsync(TlsHttpServer.Response response, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.WriteAsync(bytes);
        await response.CloseAsync();
    }

    private async Task ServeArtAsync(TlsHttpServer.Request request, TlsHttpServer.Response response, string id)
    {
        var track = ResolveTrack(Snapshot().ById, id);
        if (track?.FilePath is null || !IsShareable(track.FilePath) || AlbumArtWriter.ReadArtwork(track.FilePath) is not { } art)
        {
            // No art is a perfectly ordinary answer - the client shows the placeholder.
            response.StatusCode = 404;
            response.ContentLength64 = 0;
            await response.CloseAsync();
            return;
        }

        response.ContentType = art.MimeType;
        response.ContentLength64 = art.Data.Length;

        if (request.Method != "HEAD")
        {
            await response.WriteAsync(art.Data);
        }

        await response.CloseAsync();
    }

    private async Task ServeStreamAsync(TlsHttpServer.Request request, TlsHttpServer.Response response, string id)
    {
        var track = ResolveTrack(Snapshot().ById, id);
        if (track?.FilePath is null || !IsShareable(track.FilePath) || !File.Exists(track.FilePath))
        {
            response.StatusCode = 404;
            response.ContentLength64 = 0;
            await response.CloseAsync();
            return;
        }

        await using var file = new FileStream(track.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var total = file.Length;
        var range = ParseRange(request.Headers.TryGetValue("Range", out var rangeHeader) ? rangeHeader : null, total);

        response.ContentType = ContentTypeFor(track.FilePath);
        response.SetHeader("Accept-Ranges", "bytes");

        if (range is { } r)
        {
            response.StatusCode = 206;
            response.SetHeader("Content-Range", $"bytes {r.Start}-{r.End}/{total}");
            response.ContentLength64 = r.End - r.Start + 1;
            file.Seek(r.Start, SeekOrigin.Begin);
        }
        else
        {
            response.ContentLength64 = total;
        }

        if (request.Method == "HEAD")
        {
            await response.CloseAsync();
            return;
        }

        var remaining = response.ContentLength64;
        var buffer = new byte[81920];
        while (remaining > 0)
        {
            var want = (int)Math.Min(buffer.Length, remaining);
            var read = await file.ReadAsync(buffer.AsMemory(0, want));
            if (read <= 0)
            {
                break;
            }

            await response.WriteAsync(buffer.AsMemory(0, read));
            remaining -= read;
        }

        await response.CloseAsync();
    }

    // ── Pure helpers (unit-tested) ───────────────────────────

    /// <summary>Extracts the media id from <c>/stream/{id}</c>; false for any other path.</summary>
    internal static bool TryParseStreamId(string path, out string id) => TryParseSegment(path, "/stream/", out id);

    /// <summary>Extracts the id following a route prefix; false for any other path.</summary>
    internal static bool TryParseSegment(string path, string prefix, out string id)
    {
        id = string.Empty;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || path.Length <= prefix.Length)
        {
            return false;
        }

        id = Uri.UnescapeDataString(path[prefix.Length..]);
        return id.Length > 0;
    }

    /// <summary>Extensions we serve, and the only suffixes a stream URL may carry.</summary>
    private static readonly HashSet<string> _audioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".m4b", ".aac", ".flac", ".ogg", ".opus", ".wav", ".wma",
    };

    /// <summary>
    /// Finds the track a request is asking for. Clients append the file extension to the
    /// stream URL - libvlc picks a demuxer far more reliably when the location looks like
    /// a file - so a miss on the literal segment retries with a known audio suffix removed.
    /// Exact match wins first, so an id that genuinely ends in ".mp3" is still reachable.
    /// </summary>
    internal static MediaItem? ResolveTrack(IReadOnlyList<MediaItem> library, string segment)
    {
        // List overload kept for the unit tests' convenience; the server's hot path
        // resolves against the snapshot's id index below.
        var byId = new Dictionary<string, MediaItem>(library.Count, StringComparer.Ordinal);
        foreach (var track in library)
        {
            byId.TryAdd(track.Id, track);
        }
        return ResolveTrack(byId, segment);
    }

    /// <summary>
    /// Whether a row's FilePath may actually leave this machine.
    ///
    /// The id index already prevents path traversal from the REQUEST side - a caller can only
    /// name an id, never a path. This guards the other side: the rows themselves. The database
    /// the server reads is named by whoever started the share, so a row saying
    /// <c>C:\Users\someone\Documents\taxes.pdf</c> would otherwise be served to the LAN
    /// verbatim. Only files under a configured music root are shareable, whatever the row says.
    /// </summary>
    internal static bool IsShareable(string filePath)
    {
        // Ownership, not a configured root: when the SERVICE hosts the share it runs as
        // LocalSystem and cannot read the user's library-folder setting at all, so a root
        // comparison would silently pass everything in exactly the case that matters. The
        // helper's recorded owner is available there, and "the person who started the share
        // could have read this file themselves" is the honest bar for putting it on the LAN.
        //
        // In the GUI-hosted case no owner is recorded and this allows everything, which is
        // right: that process is already the user, so there is no boundary to enforce.
        if (DeviceHelper.PrivilegedPaths.MayUse(filePath, out var reason))
        {
            return true;
        }

        _log.Warning("Refusing to serve {Path}: {Reason}", filePath, reason);
        return false;
    }

    /// <inheritdoc cref="ResolveTrack(IReadOnlyList{MediaItem}, string)"/>
    internal static MediaItem? ResolveTrack(IReadOnlyDictionary<string, MediaItem> byId, string segment)
    {
        if (byId.TryGetValue(segment, out var track))
        {
            return track;
        }

        var dot = segment.LastIndexOf('.');
        if (dot <= 0 || !_audioExtensions.Contains(segment[dot..]))
        {
            return null;
        }

        return byId.TryGetValue(segment[..dot], out track) ? track : null;
    }

    internal readonly record struct ByteRange(long Start, long End);

    /// <summary>
    /// Parses a single "bytes=start-end" range against a known length. Null means
    /// "send the whole file" - including for malformed, multi-range, or unsatisfiable
    /// headers, which is the safe reading rather than erroring the stream.
    /// </summary>
    internal static ByteRange? ParseRange(string? header, long totalLength)
    {
        if (string.IsNullOrWhiteSpace(header) || totalLength <= 0)
        {
            return null;
        }

        const string unit = "bytes=";
        if (!header.StartsWith(unit, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var spec = header[unit.Length..].Trim();
        if (spec.Contains(','))
        {
            return null;   // multi-range: not worth the complexity, serve it whole
        }

        var dash = spec.IndexOf('-');
        if (dash < 0)
        {
            return null;
        }

        var startText = spec[..dash];
        var endText = spec[(dash + 1)..];

        long start;
        long end;

        if (startText.Length == 0)
        {
            // Suffix form: "-N" = the last N bytes.
            if (!long.TryParse(endText, out var suffix) || suffix <= 0)
            {
                return null;
            }

            start = Math.Max(0, totalLength - suffix);
            end = totalLength - 1;
        }
        else
        {
            if (!long.TryParse(startText, out start) || start < 0 || start >= totalLength)
            {
                return null;
            }

            end = endText.Length == 0 ? totalLength - 1 : long.TryParse(endText, out var parsed) ? parsed : totalLength - 1;
            end = Math.Min(end, totalLength - 1);
        }

        return end < start ? null : new ByteRange(start, end);
    }

    /// <summary>
    /// Builds the read-only catalogue payload: share name + the playable tracks, with
    /// the full track record - everything the local grid can show, the share grid can
    /// show. Only local-machine concerns (file path, rip state, checkbox) stay home.
    /// </summary>
    internal static string BuildCatalogueJson(string shareName, IReadOnlyList<MediaItem> library)
    {
        var tracks = library
            .Where(t => t.Kind is MediaKind.Music or MediaKind.Audiobook or MediaKind.Podcast)
            .Where(t => !string.IsNullOrEmpty(t.FilePath))
            .Select(t => new
            {
                id = t.Id,
                title = t.Title,
                artist = t.Artist,
                album = t.Album,
                kind = t.Kind.ToString(),
                durationTicks = t.Duration?.Ticks ?? 0,
                track = t.Track,
                totalTracks = t.TotalTracks,
                disc = t.Disc,
                totalDiscs = t.TotalDiscs,
                year = t.Year,
                genre = t.Genre,
                composer = t.Composer,
                comment = t.Comment,
                bpm = t.Bpm,
                rating = t.Rating,
                playCount = t.PlayCount,
                fileSize = t.FileSize,
                bitrate = t.AudioBitrate,
                sampleRate = t.SampleRate,
                bitDepth = t.BitDepth,
                channels = t.AudioChannels,
                codec = t.CodecDescription,
                dateAdded = t.DateAdded,
                // The client hangs this off the stream URL so the remote player sees a
                // file-shaped location instead of a bare id.
                ext = ExtensionFor(t),
            })
            .ToList();

        return JsonSerializer.Serialize(new
        {
            share = shareName,
            version = 1,
            count = tracks.Count,
            tracks,
        });
    }

    /// <summary>The playlists payload: name, ordered track ids, and the playlist type.</summary>
    internal static string BuildPlaylistsJson(IReadOnlyList<ServedPlaylist> playlists)
        => JsonSerializer.Serialize(new
        {
            playlists = playlists.Select(p => new { name = p.Name, trackIds = p.TrackIds, type = p.Type }).ToList(),
        });

    /// <summary>The track's audio extension, lowercased, or empty when it isn't one we serve.</summary>
    internal static string ExtensionFor(MediaItem track)
    {
        var ext = track.Extension ?? (track.FilePath is { } path ? Path.GetExtension(path) : null);
        return ext is not null && _audioExtensions.Contains(ext) ? ext.ToLowerInvariant() : string.Empty;
    }

    internal static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp3" => "audio/mpeg",
        ".m4a" or ".m4b" or ".aac" => "audio/mp4",
        ".flac" => "audio/flac",
        ".ogg" or ".opus" => "audio/ogg",
        ".wav" => "audio/wav",
        ".wma" => "audio/x-ms-wma",
        _ => "application/octet-stream",
    };

    public void Dispose()
    {
        // Withdraw only OUR service from the shared responder - the responder itself lives on
        // for AirPlay DACP (and any other service). Turning sharing off must not tear down the
        // process's one mDNS socket. Withdraw rather than Running?.Unpublish, because the
        // record may be sitting in a responder whose 5353 bind failed - Running is null
        // there, and a record left behind gets announced the moment a later start binds.
        MdnsAdvertiser.Withdraw(MdnsWire.ServiceType);
        _server?.Dispose();
        _log.Information("Library share \"{Name}\" stopped", ShareName);
    }
}
