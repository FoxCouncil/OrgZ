// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Serilog;

namespace OrgZ.Services.Sharing;

/// <summary>One OrgZ library share found on the LAN.</summary>
public sealed record DiscoveredShare(string Name, string Host, int Port, string? Address)
{
    /// <summary>Stable identity for sidebar bookkeeping - host:port, not the display name.</summary>
    public string Key => $"{Address ?? Host}:{Port}";

    public string BaseUrl => $"http://{Address ?? Host}:{Port}";
}

/// <summary>
/// Finds <c>_orgz._tcp</c> library shares on the local link and reads their catalogues.
/// Browsing is a one-shot multicast query with a short collection window - the sidebar
/// refreshes on a timer rather than holding a socket open forever.
/// </summary>
public static class ShareDiscovery
{
    private static readonly ILogger _log = Logging.For("ShareDiscovery");
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

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

            var query = MdnsWire.BuildQuery();
            await client.SendAsync(query, query.Length, new IPEndPoint(IPAddress.Parse(MdnsWire.MulticastAddress), MdnsWire.Port));

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
                    var share = new DiscoveredShare(DisplayNameFor(instance), instance.HostName, instance.Port, address);
                    found[share.Key] = share;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "share browse failed");
        }

        return [.. found.Values];
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

        return $"http://{source[prefix.Length..]}/art/{Uri.EscapeDataString(item.Id[(source.Length + 1)..])}";
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

                var kind = Enum.TryParse<MediaKind>(Text("kind"), out var parsed) ? parsed : MediaKind.Music;
                var ticks = Number("durationTicks");

                items.Add(new MediaItem
                {
                    // Namespaced so two shares (or a share and the local library) can't collide.
                    Id = $"share:{share.Key}:{id}",
                    Kind = kind,
                    Title = Text("title") ?? "Unknown",
                    Artist = Text("artist"),
                    Album = Text("album"),
                    Duration = ticks > 0 ? TimeSpan.FromTicks(ticks) : null,
                    Track = Number("track") is > 0 and var t ? (uint)t : null,
                    Year = Number("year") is > 0 and var y ? (uint)y : null,
                    Extension = Text("ext") is { Length: > 0 } ext ? ext : null,
                    StreamUrl = $"{share.BaseUrl}/stream/{Uri.EscapeDataString(id)}{Text("ext") ?? string.Empty}",
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
