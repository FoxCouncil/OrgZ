// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Serilog;

namespace OrgZ.Services.Sharing;

/// <summary>One OrgZ library share found on the LAN.</summary>
public sealed record DiscoveredShare(string Name, string Host, int Port, string? Address, string? Pin = null)
{
    /// <summary>Stable identity for sidebar bookkeeping - host:port, not the display name.</summary>
    public string Key => $"{Address ?? Host}:{Port}";

    /// <summary>
    /// Where requests actually go. A TLS share (it announced a certificate pin) is
    /// reached through the local pinned relay, so every consumer - libvlc included -
    /// stays a plain-HTTP client while the wire to the remote is encrypted. A share
    /// with no pin is an older host: plain HTTP, exactly as before.
    /// </summary>
    public string BaseUrl => Pin is null
        ? $"http://{Address ?? Host}:{Port}"
        : ShareStreamRelay.Register(Key, $"https://{Address ?? Host}:{Port}", Pin);
}

/// <summary>
/// Finds <c>_orgz._tcp</c> library shares on the local link and reads their catalogues.
/// Browsing is a one-shot multicast query with a short collection window - the sidebar
/// refreshes on a timer rather than holding a socket open forever.
/// </summary>
public static class ShareDiscovery
{
    private static readonly ILogger _log = Logging.For("ShareDiscovery");

    /// <summary>Catalogue/art/playlist fetches: small payloads, so a short timeout is right.</summary>
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>
    /// Track downloads: no overall timeout, because a multi-hundred-MB FLAC legitimately
    /// takes longer than any catalogue fetch. Sharing the 10 s client truncated a slow
    /// transfer - the aborted connection surfaced as a premature EOF, so CopyToAsync
    /// returned "done" on a short file and it got renamed into the library corrupt.
    /// </summary>
    private static readonly HttpClient _downloadHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>
    /// Multicasts a browse query and collects answers for <paramref name="window"/>.
    /// Never throws: a firewalled or busy socket yields an empty list.
    /// </summary>
    public static async Task<List<DiscoveredShare>> BrowseAsync(TimeSpan window, CancellationToken ct = default)
    {
        var found = new Dictionary<string, DiscoveredShare>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var client = new UdpClient(AddressFamily.InterNetwork);
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            client.Client.Bind(new IPEndPoint(IPAddress.Any, 0));   // ephemeral: never fights a responder on 5353
            client.JoinMulticastGroup(IPAddress.Parse(MdnsWire.MulticastAddress));

            // The query goes out every up interface, not just the default multicast one -
            // the share may live on any attached network, and which adapter is "default"
            // on a multi-homed host changes with metric ordering. Replies come back
            // unicast to this one socket regardless of which interface carried the query.
            var query = MdnsWire.BuildQuery();
            var target = new IPEndPoint(IPAddress.Parse(MdnsWire.MulticastAddress), MdnsWire.Port);
            var interfaces = MdnsAdvertiser.LocalInterfaceAddresses();

            if (interfaces.Count == 0)
            {
                await client.SendAsync(query, query.Length, target);
            }

            foreach (var (address, _) in interfaces)
            {
                try
                {
                    client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                    await client.SendAsync(query, query.Length, target);
                }
                catch (SocketException)
                {
                    // One refusing interface (VPN, teredo-ish oddities) must not end the browse.
                }
            }

            using var windowCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            windowCts.CancelAfter(window);

            while (!windowCts.IsCancellationRequested)
            {
                UdpReceiveResult packet;
                try
                {
                    packet = await client.ReceiveAsync(windowCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                foreach (var instance in MdnsWire.ReadResponse(packet.Buffer))
                {
                    // Prefer the A record; fall back to the sender's address, which is
                    // where the packet actually came from.
                    var address = instance.Address ?? packet.RemoteEndPoint.Address.ToString();
                    var share = new DiscoveredShare(DisplayNameFor(instance), instance.HostName, instance.Port, address, PinFor(instance));

                    // One share, many interfaces: the same instance answers once per
                    // attached network, each reply carrying a different (equally
                    // reachable) address. Dedup on instance identity, keeping the
                    // numerically smallest address so the pick - and with it the
                    // share's Key and mounted ids - is stable across browses.
                    var identity = $"{instance.HostName}:{instance.Port}";
                    if (!found.TryGetValue(identity, out var existing) || CompareAddresses(share.Address, existing.Address) < 0)
                    {
                        found[identity] = share;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "share browse failed");
        }

        return [.. found.Values];
    }

    /// <summary>
    /// Byte-wise IPv4 ordering for the multi-interface dedup. Null or unparsable
    /// addresses sort last, so a real address always wins over a missing one.
    /// </summary>
    internal static int CompareAddresses(string? a, string? b)
    {
        if (a is null || b is null)
        {
            return a is null ? (b is null ? 0 : 1) : -1;
        }
        if (!IPAddress.TryParse(a, out var ipA))
        {
            return 1;
        }
        if (!IPAddress.TryParse(b, out var ipB))
        {
            return -1;
        }

        var bytesA = ipA.GetAddressBytes();
        var bytesB = ipB.GetAddressBytes();
        if (bytesA.Length != bytesB.Length)
        {
            return bytesA.Length - bytesB.Length;   // IPv4 sorts ahead of IPv6
        }

        for (var i = 0; i < bytesA.Length; i++)
        {
            if (bytesA[i] != bytesB[i])
            {
                return bytesA[i] - bytesB[i];
            }
        }

        return 0;
    }

    /// <summary>Prefers the TXT "name=" (which keeps dots and punctuation) over the mDNS label.</summary>
    internal static string DisplayNameFor(MdnsWire.ServiceInstance instance)
    {
        foreach (var entry in instance.TxtRecords)
        {
            if (entry.StartsWith("name=", StringComparison.OrdinalIgnoreCase) && entry.Length > 5)
            {
                return entry[5..];
            }
        }

        return instance.InstanceName;
    }

    /// <summary>The TXT "pin=" a TLS share announces; null for a plain-HTTP (older) share.</summary>
    internal static string? PinFor(MdnsWire.ServiceInstance instance)
    {
        foreach (var entry in instance.TxtRecords)
        {
            if (entry.StartsWith("pin=", StringComparison.OrdinalIgnoreCase) && entry.Length > 4)
            {
                return entry[4..];
            }
        }

        return null;
    }

    /// <summary>
    /// Fetches a share's catalogue and turns it into MediaItems whose StreamUrl points at
    /// the share. Read-only: no FilePath, so nothing in OrgZ will try to edit or delete them.
    /// </summary>
    public static async Task<List<MediaItem>> FetchCatalogueAsync(DiscoveredShare share, CancellationToken ct = default)
    {
        try
        {
            var json = await _http.GetStringAsync($"{share.BaseUrl}/catalogue", ct);
            return ParseCatalogue(json, share);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "catalogue fetch failed for {Share}", share.Key);
            return [];
        }
    }

    /// <summary>
    /// A remote playlist: display name, the mounted (namespaced) ids of its tracks in
    /// order, and the server's playlist type ("favorites" for the synthetic Favorites,
    /// "playlist" otherwise - which is also the fallback for older servers).
    /// </summary>
    public sealed record SharePlaylist(string Name, List<string> TrackIds, string Type = "playlist")
    {
        public bool IsFavorites => Type == "favorites";
    }

    /// <summary>
    /// Fetches a share's playlists. Ids come back namespaced the same way the catalogue's
    /// tracks are mounted, so they join against the share's MediaItems directly. Empty on
    /// any failure - an old server without the route is just a share with no playlists.
    /// </summary>
    public static async Task<List<SharePlaylist>> FetchPlaylistsAsync(DiscoveredShare share, CancellationToken ct = default)
    {
        try
        {
            var json = await _http.GetStringAsync($"{share.BaseUrl}/playlists", ct);
            return ParsePlaylists(json, share);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "playlists fetch failed for {Share}", share.Key);
            return [];
        }
    }

    /// <summary>Pure playlists → SharePlaylist mapping. Malformed payloads yield an empty list.</summary>
    internal static List<SharePlaylist> ParsePlaylists(string json, DiscoveredShare share)
    {
        var result = new List<SharePlaylist>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("playlists", out var playlists) || playlists.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var playlist in playlists.EnumerateArray())
            {
                var name = playlist.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                if (string.IsNullOrWhiteSpace(name) || !playlist.TryGetProperty("trackIds", out var ids) || ids.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var trackIds = ids.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => $"share:{share.Key}:{e.GetString()}")
                    .ToList();

                var type = playlist.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "playlist";
                result.Add(new SharePlaylist(name, trackIds, type));
            }
        }
        catch (JsonException ex)
        {
            _log.Debug(ex, "malformed playlists from {Share}", share.Key);
        }

        return result;
    }

    /// <summary>
    /// Downloads a share track's audio to <paramref name="destinationPath"/>. Writes to a
    /// ".part" sibling first so a failed transfer never leaves a half-file where the
    /// library scanner can find it. False on any failure; never throws.
    /// </summary>
    public static async Task<bool> DownloadTrackAsync(MediaItem item, string destinationPath, CancellationToken ct = default)
    {
        if (item.StreamUrl is null)
        {
            return false;
        }

        var partial = destinationPath + ".part";
        try
        {
            using var response = await _downloadHttp.GetAsync(item.StreamUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var expected = response.Content.Headers.ContentLength;

            long written;
            await using (var file = File.Create(partial))
            {
                await response.Content.CopyToAsync(file, ct);
                written = file.Length;
            }

            // Reject a short transfer rather than rename it into the library. An aborted
            // connection can surface as a clean EOF, so "CopyToAsync returned" is not
            // proof the file is whole - the declared length is.
            if (expected is { } len && written != len)
            {
                _log.Warning("share download truncated for {Url}: got {Written} of {Expected} bytes", item.StreamUrl, written, len);
                try { File.Delete(partial); } catch { /* best effort */ }
                return false;
            }

            File.Move(partial, destinationPath, overwrite: false);
            return true;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "share download failed for {Url}", item.StreamUrl);
            try { File.Delete(partial); } catch { /* best effort */ }
            return false;
        }
    }

    /// <summary>True for a track mounted from a remote share - no local file, plays over HTTP.</summary>
    public static bool IsShareItem(MediaItem item)
        => item.Source?.StartsWith("share:", StringComparison.Ordinal) == true;

    /// <summary>
    /// The cover-art URL for a mounted share track, rebuilt from its namespaced id
    /// (<c>share:{host}:{port}:{remoteId}</c>) - a share carries no local file to read a
    /// tag out of, so art is a second fetch. Null for anything that isn't a share item.
    /// </summary>
    internal static string? ArtUrlFor(MediaItem item)
    {
        const string prefix = "share:";
        if (!IsShareItem(item) || item.Source is not { } source || item.Id.Length <= source.Length + 1)
        {
            return null;
        }

        var shareKey = source[prefix.Length..];
        var remoteId = Uri.EscapeDataString(item.Id[(source.Length + 1)..]);

        // A TLS share's art rides the pinned relay like everything else; a plain
        // (older) share keeps the direct http URL.
        return ShareStreamRelay.ProxyBaseFor(shareKey) is { } proxied
            ? $"{proxied}/art/{remoteId}"
            : $"http://{shareKey}/art/{remoteId}";
    }

    /// <summary>Fetches cover bytes from a share. Null for a 404, a timeout, or anything unreadable.</summary>
    public static async Task<byte[]?> FetchArtAsync(string url, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync(url, ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync(ct) : null;
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "share art fetch failed for {Url}", url);
            return null;
        }
    }

    /// <summary>Pure catalogue → MediaItem mapping. Malformed payloads yield an empty list.</summary>
    internal static List<MediaItem> ParseCatalogue(string json, DiscoveredShare share)
    {
        var items = new List<MediaItem>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("tracks", out var tracks) || tracks.ValueKind != JsonValueKind.Array)
            {
                return items;
            }

            foreach (var track in tracks.EnumerateArray())
            {
                var id = track.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                if (string.IsNullOrEmpty(id))
                {
                    continue;
                }

                string? Text(string name) => track.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
                long Number(string name) => track.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetInt64() : 0;
                uint? UNumber(string name) => Number(name) is > 0 and var v ? (uint)v : null;
                int? INumber(string name) => Number(name) is > 0 and var v ? (int)v : null;

                var kind = Enum.TryParse<MediaKind>(Text("kind"), out var parsed) ? parsed : MediaKind.Music;
                var ticks = Number("durationTicks");

                // libvlc picks a demuxer more reliably when the URL looks like a file, so
                // the extension rides the stream URL - but only when the id doesn't ALREADY
                // end with it. This share's ids are file paths, so appending blindly gave
                // "...flac.flac", which only worked because the server strips one suffix.
                var ext = Text("ext") ?? string.Empty;
                var urlSuffix = ext.Length > 0 && id.EndsWith(ext, StringComparison.OrdinalIgnoreCase) ? string.Empty : ext;

                items.Add(new MediaItem
                {
                    // Namespaced so two shares (or a share and the local library) can't collide.
                    Id = $"share:{share.Key}:{id}",
                    Kind = kind,
                    Title = Text("title") ?? "Unknown",
                    Artist = Text("artist"),
                    Album = Text("album"),
                    Duration = ticks > 0 ? TimeSpan.FromTicks(ticks) : null,
                    Track = UNumber("track"),
                    TotalTracks = UNumber("totalTracks"),
                    Disc = UNumber("disc"),
                    TotalDiscs = UNumber("totalDiscs"),
                    Year = UNumber("year"),
                    Genre = Text("genre"),
                    Composer = Text("composer"),
                    Comment = Text("comment"),
                    Bpm = UNumber("bpm"),
                    Rating = INumber("rating"),
                    PlayCount = (int)Number("playCount"),
                    FileSize = Number("fileSize") is > 0 and var size ? size : null,
                    AudioBitrate = INumber("bitrate"),
                    SampleRate = INumber("sampleRate"),
                    BitDepth = INumber("bitDepth"),
                    AudioChannels = INumber("channels"),
                    CodecDescription = Text("codec"),
                    DateAdded = DateTime.TryParse(Text("dateAdded"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var added) ? added : DateTime.UtcNow,
                    Extension = ext.Length > 0 ? ext : null,
                    StreamUrl = $"{share.BaseUrl}/stream/{Uri.EscapeDataString(id)}{urlSuffix}",
                    Source = $"share:{share.Key}",
                });
            }
        }
        catch (JsonException ex)
        {
            _log.Debug(ex, "malformed catalogue from {Share}", share.Key);
        }

        return items;
    }
}
