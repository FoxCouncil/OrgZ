// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace OrgZ.StationCurator.Services;

public sealed record GeoInfo(string Ip, string? Country, string? CountryCode);

/// <summary>
/// Resolves a stream host to the country its server actually lives in: DNS to an IP, then
/// ip-api.com for the geolocation. Lookups are cached per host for the session and coalesced
/// into batch calls (up to 100 IPs per POST, ≥4.5 s apart) so a Probe All across the whole
/// store stays inside the free tier's 15 batch requests/minute.
/// </summary>
public static class GeoIp
{
    private static readonly ConcurrentDictionary<string, Task<GeoInfo?>> _byHost = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Channel<(string Ip, TaskCompletionSource<GeoInfo?> Result)> _pending = Channel.CreateUnbounded<(string, TaskCompletionSource<GeoInfo?>)>();
    private static readonly HttpClient Http = Web.Create(TimeSpan.FromSeconds(20));
    private static int _pumpStarted;

    public static Task<GeoInfo?> LookupAsync(string host) => _byHost.GetOrAdd(host, ResolveAsync);

    private static async Task<GeoInfo?> ResolveAsync(string host)
    {
        try
        {
            var ip = IPAddress.TryParse(host, out var literal)
                ? literal
                : (await Dns.GetHostAddressesAsync(host)).OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).FirstOrDefault();
            if (ip == null)
            {
                return null;
            }

            if (Interlocked.Exchange(ref _pumpStarted, 1) == 0)
            {
                _ = Task.Run(PumpAsync);
            }

            var tcs = new TaskCompletionSource<GeoInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
            await _pending.Writer.WriteAsync((ip.ToString(), tcs));
            return await tcs.Task;
        }
        catch
        {
            return null;
        }
    }

    private static async Task PumpAsync()
    {
        var reader = _pending.Reader;
        while (await reader.WaitToReadAsync())
        {
            // Gather whatever shows up within a short window, capped at the API's batch limit.
            var batch = new List<(string Ip, TaskCompletionSource<GeoInfo?> Result)>();
            while (batch.Count < 100 && reader.TryRead(out var item))
            {
                batch.Add(item);
            }
            if (batch.Count < 100)
            {
                await Task.Delay(400);
                while (batch.Count < 100 && reader.TryRead(out var item))
                {
                    batch.Add(item);
                }
            }

            var results = new Dictionary<string, GeoInfo?>(StringComparer.Ordinal);
            try
            {
                using var resp = await Http.PostAsJsonAsync("http://ip-api.com/batch?fields=status,country,countryCode,query", batch.Select(b => b.Ip).Distinct().ToArray());
                if (resp.IsSuccessStatusCode)
                {
                    foreach (var row in await resp.Content.ReadFromJsonAsync<List<IpApiRow>>() ?? [])
                    {
                        results[row.Query ?? ""] = row.Status == "success" ? new GeoInfo(row.Query!, row.Country, row.CountryCode) : null;
                    }
                }
            }
            catch
            {
                // Every waiter below resolves null; probes carry on without geo data.
            }

            foreach (var (ip, tcs) in batch)
            {
                tcs.TrySetResult(results.TryGetValue(ip, out var geo) ? geo : null);
            }

            await Task.Delay(TimeSpan.FromSeconds(4.5));
        }
    }

    private sealed class IpApiRow
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("country")] public string? Country { get; set; }
        [JsonPropertyName("countryCode")] public string? CountryCode { get; set; }
        [JsonPropertyName("query")] public string? Query { get; set; }
    }
}
