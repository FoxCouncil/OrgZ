// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Net;
using System.Net.Sockets;
using Serilog;

namespace OrgZ.Services.AudioOutput.AirPlay;

/// <summary>
/// Discovers AirPlay receivers on the LAN via mDNS (<c>_raop._tcp.local</c>) and streams
/// to them over RAOP (see <see cref="RaopSession"/>: RTSP handshake, RSA-wrapped AES key,
/// uncompressed-ALAC framing, RTP audio with sync + timing).
/// </summary>
/// <remarks>
/// <para>
/// Only receivers whose SRV/A records resolved to a host and port are marked available -
/// a device we can't actually reach stays visible but disabled rather than becoming a
/// placebo that silently eats the stream. The sink likewise THROWS when the handshake
/// fails instead of dropping samples, so a refusal surfaces as an error.
/// </para>
/// <para>
/// Auth flavors: this handles the classic RSA/AES receivers (AirPort Express, most
/// third-party speakers). Receivers demanding FairPlay or an AirPlay-2 pairing handshake
/// refuse ANNOUNCE, and that refusal is reported verbatim rather than retried.
/// </para>
/// <para>
/// The mDNS browser here uses raw UDP multicast against
/// 224.0.0.251:5353 - no Bonjour / avahi dependency.  Sweeps are one-shot
/// (a query plus ~2s of answer collection) and run on the thread pool;
/// <see cref="EnumerateDevices"/> itself never blocks - it serves the cache
/// and raises <see cref="DevicesChanged"/> when a background sweep finds a
/// different receiver set.
/// </para>
/// </remarks>
internal sealed class AirPlayDeviceProvider : IAudioSinkProvider
{
    public const string Id = "airplay";

    private static readonly ILogger _log = Logging.For("AirPlay");
    private static readonly IPEndPoint MdnsEndpoint = new(IPAddress.Parse("224.0.0.251"), 5353);
    private const string RaopService = "_raop._tcp.local";
    private const string AirplayService = "_airplay._tcp.local";

    public string ProviderId => Id;
    public string ProviderName => "AirPlay";
    public bool IsSupported => true;

    public event EventHandler? DevicesChanged;

    // mDNS discovery is expensive (~2s of UDP multicast per sweep) and noisy in the
    // debugger. EnumerateDevices therefore never blocks: it hands back the cached list
    // and, when the cache has gone stale, kicks one background sweep that refreshes it
    // and raises DevicesChanged. Callers on the UI thread (the speaker flyout used to
    // eat the whole 2s sweep on open) get an instant answer; the refreshed topology
    // arrives via the event, and a Settings "Refresh" click behaves the same way.
    private static readonly TimeSpan DiscoveryTtl = TimeSpan.FromMinutes(2);
    private readonly object _cacheLock = new();
    private List<AudioDeviceInfo> _cachedDevices = [];
    private Dictionary<string, (string Host, int Port)> _endpoints = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _cachedAt = DateTime.MinValue;
    private int _sweeping;   // 0/1 - at most one background sweep in flight

    public IReadOnlyList<AudioDeviceInfo> EnumerateDevices()
    {
        List<AudioDeviceInfo> cached;
        bool fresh;
        lock (_cacheLock)
        {
            cached = _cachedDevices;
            fresh = DateTime.UtcNow - _cachedAt < DiscoveryTtl;
        }

        if (!fresh && System.Threading.Interlocked.CompareExchange(ref _sweeping, 1, 0) == 0)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    SweepNow();
                }
                catch (Exception ex)
                {
                    _log.Debug(ex, "AirPlay background sweep failed");
                }
                finally
                {
                    System.Threading.Interlocked.Exchange(ref _sweeping, 0);
                }
            });
        }

        return cached;
    }

    /// <summary>One blocking mDNS sweep; updates the cache and raises <see cref="DevicesChanged"/> when the receiver set changed.</summary>
    private void SweepNow()
    {
        var receivers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var endpoints = new Dictionary<string, (string Host, int Port)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            QueryMdns(RaopService, receivers, endpoints, TimeSpan.FromMilliseconds(1500));
            QueryMdns(AirplayService, receivers, endpoints, TimeSpan.FromMilliseconds(500));
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "AirPlay mDNS discovery failed");
        }

        var result = new List<AudioDeviceInfo>(receivers.Count);
        foreach (var kvp in receivers)
        {
            // Only a receiver whose SRV/A records gave us somewhere to connect is offered as
            // usable; the rest stay listed but disabled rather than failing at play time.
            var reachable = endpoints.TryGetValue(kvp.Key, out var endpoint);
            result.Add(new AudioDeviceInfo
            {
                DeviceId = kvp.Key,
                DisplayName = kvp.Value,
                ProviderId = Id,
                ProviderName = ProviderName,
                IsAvailable = reachable,
            });
        }

        lock (_cacheLock)
        {
            _endpoints = endpoints;
        }

        bool changed;
        lock (_cacheLock)
        {
            changed = !_cachedDevices.Select(d => d.DeviceId).ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(result.Select(d => d.DeviceId));
            _cachedDevices = result;
            _cachedAt = DateTime.UtcNow;
        }

        if (changed)
        {
            DevicesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public IAudioSink CreateSink(AudioDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);

        (string Host, int Port) endpoint;
        lock (_cacheLock)
        {
            if (!_endpoints.TryGetValue(device.DeviceId, out endpoint))
            {
                // No resolved address: refuse loudly. Handing back a sink that can't reach
                // anything is how audio disappears with no explanation.
                throw new InvalidOperationException($"“{device.DisplayName}” hasn't resolved to an address yet - refresh the device list and try again.");
            }
        }

        return new AirPlayRaopSink(device, endpoint.Host, endpoint.Port);
    }

    /// <summary>
    /// Minimal mDNS PTR query for <paramref name="service"/>.  Sends a DNS
    /// query packet to 224.0.0.251:5353 and collects PTR-record answers for
    /// <paramref name="timeout"/> before returning.  Parses enough of the
    /// DNS wire format to extract the instance name (e.g., "LivingRoom"
    /// from "LivingRoom._raop._tcp.local").
    /// </summary>
    private static void QueryMdns(string service, Dictionary<string, string> receivers, Dictionary<string, (string Host, int Port)> endpoints, TimeSpan timeout)
    {
        using var udp = new UdpClient();
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        udp.JoinMulticastGroup(MdnsEndpoint.Address);

        var query = BuildMdnsQuery(service);
        udp.Send(query, query.Length, MdnsEndpoint);

        // Poll Available instead of blocking on Receive with a timeout -
        // ReceiveTimeout throws SocketException per expiry, which clutters
        // the debugger's first-chance exception view with dozens of noise
        // entries every sweep.  A short sleep between checks is fine: mDNS
        // answers arrive within tens of ms, so we don't miss any.
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        var remote = new IPEndPoint(IPAddress.Any, 0);
        while (Environment.TickCount64 < deadline)
        {
            if (udp.Available <= 0)
            {
                System.Threading.Thread.Sleep(40);
                continue;
            }

            try
            {
                var data = udp.Receive(ref remote);
                ExtractPtrNames(data, service, receivers);
                ExtractEndpoints(data, receivers, endpoints);
            }
            catch (SocketException)
            {
                break;
            }
        }
    }

    private static byte[] BuildMdnsQuery(string service)
    {
        // DNS header: ID=0, flags=0, QD=1, AN=0, NS=0, AR=0
        var bytes = new List<byte>
        {
            0, 0,             // transaction ID
            0, 0,             // flags (standard query)
            0, 1,             // QDCOUNT=1
            0, 0, 0, 0, 0, 0, // ANCOUNT, NSCOUNT, ARCOUNT
        };

        foreach (var label in service.Split('.'))
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
        }
        bytes.Add(0); // terminator

        // QTYPE=PTR (12), QCLASS=IN (1)
        bytes.Add(0); bytes.Add(12);
        bytes.Add(0); bytes.Add(1);

        return [.. bytes];
    }

    private static void ExtractPtrNames(byte[] data, string service, Dictionary<string, string> receivers)
    {
        if (data.Length < 12)
        {
            return;
        }

        // Skip the question section - its size depends on contents; for our
        // own query it's fixed, but responses may echo different questions.
        // Easiest: scan for the PTR answer by searching for the service name.
        var serviceLower = service.ToLowerInvariant();
        int idx = 12;
        // Skip QDCOUNT questions
        int qdCount = (data[4] << 8) | data[5];
        for (int q = 0; q < qdCount && idx < data.Length; q++)
        {
            idx = SkipName(data, idx);
            idx += 4; // QTYPE + QCLASS
        }

        int anCount = (data[6] << 8) | data[7];
        for (int a = 0; a < anCount && idx < data.Length; a++)
        {
            int nameStart = idx;
            idx = SkipName(data, idx);
            if (idx + 10 > data.Length) return;

            int rType = (data[idx] << 8) | data[idx + 1];
            idx += 8;
            int rdLength = (data[idx] << 8) | data[idx + 1];
            idx += 2;
            int rdStart = idx;

            if (rType == 12) // PTR
            {
                var ownerName = ReadName(data, nameStart).ToLowerInvariant();
                if (ownerName.Contains(serviceLower))
                {
                    var targetName = ReadName(data, rdStart);
                    // "LivingRoom._raop._tcp.local" → "LivingRoom"
                    var dot = targetName.IndexOf('.');
                    var instance = dot > 0 ? targetName[..dot] : targetName;

                    // AirPlay names sometimes have "XX:XX:XX:XX:XX:XX@" MAC prefix.
                    var at = instance.IndexOf('@');
                    if (at >= 0 && at < instance.Length - 1)
                    {
                        instance = instance[(at + 1)..];
                    }

                    receivers[targetName] = instance;
                }
            }

            idx = rdStart + rdLength;
        }
    }

    /// <summary>
    /// Pulls SRV (port + target host) and A (IPv4) records out of an mDNS response and
    /// pairs them with the receivers PTR already named. Receivers answer PTR/SRV/A in one
    /// packet, so a single sweep normally resolves everything; a receiver whose A record
    /// didn't arrive simply stays unavailable until the next sweep.
    /// </summary>
    internal static void ExtractEndpoints(byte[] data, Dictionary<string, string> receivers, Dictionary<string, (string Host, int Port)> endpoints)
    {
        if (data.Length < 12)
        {
            return;
        }

        var srv = new Dictionary<string, (string Target, int Port)>(StringComparer.OrdinalIgnoreCase);
        var addresses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        int idx = 12;
        int qdCount = (data[4] << 8) | data[5];
        for (int q = 0; q < qdCount && idx < data.Length; q++)
        {
            idx = SkipName(data, idx);
            idx += 4;
        }

        // Answers, authority and additional all carry usable records - AirPlay receivers
        // put the SRV/A pair in "additional" more often than not.
        int records = ((data[6] << 8) | data[7]) + ((data[8] << 8) | data[9]) + ((data[10] << 8) | data[11]);
        for (int r = 0; r < records && idx < data.Length; r++)
        {
            int nameStart = idx;
            idx = SkipName(data, idx);
            if (idx + 10 > data.Length)
            {
                return;
            }

            int rType = (data[idx] << 8) | data[idx + 1];
            idx += 8;
            int rdLength = (data[idx] << 8) | data[idx + 1];
            idx += 2;
            int rdStart = idx;
            if (rdStart + rdLength > data.Length)
            {
                return;
            }

            switch (rType)
            {
                case 33 when rdLength >= 7:   // SRV: priority(2) weight(2) port(2) target
                {
                    var port = (data[rdStart + 4] << 8) | data[rdStart + 5];
                    srv[ReadName(data, nameStart)] = (ReadName(data, rdStart + 6), port);
                }
                break;

                case 1 when rdLength == 4:    // A
                {
                    addresses[ReadName(data, nameStart)] = $"{data[rdStart]}.{data[rdStart + 1]}.{data[rdStart + 2]}.{data[rdStart + 3]}";
                }
                break;
            }

            idx = rdStart + rdLength;
        }

        foreach (var instance in receivers.Keys)
        {
            if (!srv.TryGetValue(instance, out var entry))
            {
                continue;
            }

            // Prefer the A record we just saw; fall back to the SRV target name, which the
            // OS resolver can usually handle via mDNS itself.
            var host = addresses.TryGetValue(entry.Target, out var ip) ? ip : entry.Target;
            if (!string.IsNullOrWhiteSpace(host) && entry.Port > 0)
            {
                endpoints[instance] = (host, entry.Port);
            }
        }
    }

    private static int SkipName(byte[] data, int idx)
    {
        while (idx < data.Length)
        {
            int len = data[idx];
            if (len == 0)
            {
                return idx + 1;
            }
            if ((len & 0xC0) == 0xC0)
            {
                return idx + 2; // pointer
            }
            idx += len + 1;
        }
        return idx;
    }

    private static string ReadName(byte[] data, int idx)
    {
        var sb = new System.Text.StringBuilder();
        int jumps = 0;
        while (idx < data.Length && jumps < 10)
        {
            int len = data[idx];
            if (len == 0)
            {
                break;
            }
            if ((len & 0xC0) == 0xC0)
            {
                int offset = ((len & 0x3F) << 8) | data[idx + 1];
                idx = offset;
                jumps++;
                continue;
            }
            if (sb.Length > 0) sb.Append('.');
            sb.Append(System.Text.Encoding.ASCII.GetString(data, idx + 1, len));
            idx += len + 1;
        }
        return sb.ToString();
    }

    internal void RaiseDevicesChanged() => DevicesChanged?.Invoke(this, EventArgs.Empty);
}
