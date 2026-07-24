// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Net;
using System.Text;
using System.Text.Json;
using Serilog;

namespace OrgZ.Services.Sharing;

/// <summary>
/// Serves a library read-only on the LAN and advertises it over mDNS as
/// <c>_orgz._tcp</c>. Two endpoints, deliberately boring: <c>/catalogue</c> returns the
/// track list as JSON, <c>/stream/{id}</c> returns the audio file with Range support so
/// a remote OrgZ can seek. Spiritually DAAP without the dead protocol. Read-only by
/// construction - there is no mutating route to reach.
/// </summary>
public sealed class LibraryShareServer : IDisposable
{
    private static readonly ILogger _log = Logging.For("LibraryShare");

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<List<MediaItem>> _loadLibrary;
    private MdnsAdvertiser? _advertiser;
    private Task? _loop;

    public string ShareName { get; }
    public int Port { get; }

    public LibraryShareServer(string shareName, int port, Func<List<MediaItem>>? loadLibrary = null)
    {
        ShareName = string.IsNullOrWhiteSpace(shareName) ? "OrgZ Library" : shareName;
        Port = port;
        _loadLibrary = loadLibrary ?? MediaCache.LoadAll;
        _listener.Prefixes.Add($"http://+:{port}/");
    }

    public void Start()
    {
        try
        {
            _listener.Start();
        }
        catch (HttpListenerException ex)
        {
            // Binding "+" needs a URL ACL on Windows for a non-elevated process; the
            // service (LocalSystem) has it. Fall back to loopback so a GUI-hosted share
            // still works for local testing instead of dying outright.
            _log.Warning(ex, "Share bind on +:{Port} failed - falling back to localhost", Port);
            _listener.Prefixes.Clear();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
        }

        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));

        _advertiser = new MdnsAdvertiser(ShareName, (ushort)Port);
        _advertiser.Start();

        _log.Information("Library share \"{Name}\" listening on {Port}", ShareName, Port);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (ct.IsCancellationRequested || !_listener.IsListening)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "share accept failed");
                continue;
            }

            _ = Task.Run(() => HandleAsync(context), ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            // Read-only share: anything that isn't a GET/HEAD is refused outright.
            if (context.Request.HttpMethod is not ("GET" or "HEAD"))
            {
                context.Response.StatusCode = 405;
                context.Response.Close();
                return;
            }

            var path = context.Request.Url?.AbsolutePath ?? "/";

            if (path.Equals("/catalogue", StringComparison.OrdinalIgnoreCase))
            {
                var json = BuildCatalogueJson(ShareName, _loadLibrary());
                var bytes = Encoding.UTF8.GetBytes(json);
                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
                return;
            }

            if (TryParseStreamId(path, out var id))
            {
                await ServeStreamAsync(context, id);
                return;
            }

            context.Response.StatusCode = 404;
            context.Response.Close();
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "share request failed");
            try { context.Response.Abort(); } catch { /* client already gone */ }
        }
    }

    private async Task ServeStreamAsync(HttpListenerContext context, string id)
    {
        var track = _loadLibrary().FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));
        if (track?.FilePath is null || !File.Exists(track.FilePath))
        {
            context.Response.StatusCode = 404;
            context.Response.Close();
            return;
        }

        await using var file = new FileStream(track.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var total = file.Length;
        var range = ParseRange(context.Request.Headers["Range"], total);

        context.Response.ContentType = ContentTypeFor(track.FilePath);
        context.Response.Headers["Accept-Ranges"] = "bytes";

        if (range is { } r)
        {
            context.Response.StatusCode = 206;
            context.Response.Headers["Content-Range"] = $"bytes {r.Start}-{r.End}/{total}";
            context.Response.ContentLength64 = r.End - r.Start + 1;
            file.Seek(r.Start, SeekOrigin.Begin);
        }
        else
        {
            context.Response.ContentLength64 = total;
        }

        if (context.Request.HttpMethod == "HEAD")
        {
            context.Response.Close();
            return;
        }

        var remaining = context.Response.ContentLength64;
        var buffer = new byte[81920];
        while (remaining > 0)
        {
            var want = (int)Math.Min(buffer.Length, remaining);
            var read = await file.ReadAsync(buffer.AsMemory(0, want));
            if (read <= 0)
            {
                break;
            }

            await context.Response.OutputStream.WriteAsync(buffer.AsMemory(0, read));
            remaining -= read;
        }

        context.Response.Close();
    }

    // ── Pure helpers (unit-tested) ───────────────────────────

    /// <summary>Extracts the media id from <c>/stream/{id}</c>; false for any other path.</summary>
    internal static bool TryParseStreamId(string path, out string id)
    {
        id = string.Empty;
        const string prefix = "/stream/";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || path.Length <= prefix.Length)
        {
            return false;
        }

        id = Uri.UnescapeDataString(path[prefix.Length..]);
        return id.Length > 0;
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

    /// <summary>Builds the read-only catalogue payload: share name + the playable tracks.</summary>
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
                year = t.Year,
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
        _cts.Cancel();
        _advertiser?.Dispose();

        try
        {
            if (_listener.IsListening)
            {
                _listener.Stop();
            }
            _listener.Close();
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "share listener teardown");
        }

        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // A cancelled accept loop throwing on the way down is expected.
        }

        _cts.Dispose();
        _log.Information("Library share \"{Name}\" stopped", ShareName);
    }
}
