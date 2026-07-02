// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Net;
using System.Net.Sockets;
using Serilog;

namespace OrgZ.Services.AudioOutput.AirPlay;

/// <summary>
/// Discovers AirPlay receivers on the LAN via mDNS (<c>_raop._tcp.local</c>).
/// Discovery is functional today; actual streaming via RTSP + RAOP (RSA key
/// exchange, ALAC encoding, RTP transport, NTP sync) is a substantial
/// protocol stack and lands in a follow-up - <see cref="CreateSink"/> returns
/// a placeholder sink that logs and silently drops samples so the UI can
/// still show AirPlay devices in the list and we have a place to iterate.
/// </summary>
/// <remarks>
/// <para>
/// Why discover-only for now: RAOP is ~2000 LOC of protocol-critical code
/// (RSA key negotiation, per-receiver auth flavors, ALAC framing, sync).
/// Shipping a half-done implementation would silently corrupt audio or
/// hang the UI when a user picks an AirPlay target; better to have the
/// device visible with a "not yet supported" error than to pretend.
/// </para>
/// <para>
/// The mDNS browser here uses raw UDP multicast against
/// 224.0.0.251:5353 - no Bonjour / avahi dependency.  Responses are
/// one-shot: each call to <see cref="EnumerateDevices"/> sends a query and
/// waits ~1.5 seconds for answers.  A background "watch" mode firing
/// <see cref="DevicesChanged"/> will come with the RAOP implementation.
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

    // mDNS discovery is expensive (~2s of UDP multicast per sweep) and noisy
    // in the debugger.  We cache the last result and only re-sweep at a
    // relaxed cadence, or when the user hits "Refresh Devices" in Settings
    // (which calls this directly without the cache hint).
    private static readonly TimeSpan DiscoveryTtl = TimeSpan.FromMinutes(2);
    private readonly object _cacheLock = new();
    private List<AudioDeviceInfo> _cachedDevices = [];
    private DateTime _cachedAt = DateTime.MinValue;

    public IReadOnlyList<AudioDeviceInfo> EnumerateDevices()
    {
        lock (_cacheLock)
        {
            if (DateTime.UtcNow - _cachedAt < DiscoveryTtl)
            {
                return _cachedDevices;
            }
        }

        var receivers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            QueryMdns(RaopService, receivers, TimeSpan.FromMilliseconds(1500));
            QueryMdns(AirplayService, receivers, TimeSpan.FromMilliseconds(500));
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "AirPlay mDNS discovery failed");
        }

        var result = new List<AudioDeviceInfo>(receivers.Count);
        foreach (var kvp in receivers)
        {
            result.Add(new AudioDeviceInfo
            {
                DeviceId = kvp.Key,
                DisplayName = kvp.Value,
                ProviderId = Id,
                ProviderName = ProviderName,
                // Discovery works; STREAMING doesn't yet (CreateSink returns the placeholder that
                // drops all samples). Unavailable keeps the picker honest - the device shows,
                // disabled, instead of being a placebo that silently kills audio. Flip when RAOP
                // streaming lands (see roadmap).
                IsAvailable = false,
            });
        }

        lock (_cacheLock)
        {
            _cachedDevices = result;
            _cachedAt = DateTime.UtcNow;
        }

        return result;
    }

    public IAudioSink CreateSink(AudioDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return new AirPlayPlaceholderSink(device);
    }

    /// <summary>
    /// Minimal mDNS PTR query for <paramref name="service"/>.  Sends a DNS
    /// query packet to 224.0.0.251:5353 and collects PTR-record answers for
    /// <paramref name="timeout"/> before returning.  Parses enough of the
    /// DNS wire format to extract the instance name (e.g., "LivingRoom"
    /// from "LivingRoom._raop._tcp.local").
    /// </summary>
    private static void QueryMdns(string service, Dictionary<string, string> receivers, TimeSpan timeout)
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

    /// <summary>
    /// Placeholder sink used until full RAOP streaming is implemented -
    /// <see cref="Open"/> succeeds (so the bus keeps the sink active) but
    /// <see cref="Write"/> silently drops samples and logs a one-time warning.
    /// </summary>
    private sealed class AirPlayPlaceholderSink : IAudioSink
    {
        private static readonly ILogger _log = Logging.For("AirPlayPlaceholderSink");

        private bool _warned;
        private bool _disposed;

        public AirPlayPlaceholderSink(AudioDeviceInfo device)
        {
            Id = device.QualifiedId;
            DisplayName = device.DisplayName;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public AudioFormat? CurrentFormat { get; private set; }
        public float Volume { get; set; } = 1f;
        public bool IsMuted { get; set; }
        public bool IsOpen { get; private set; }

        public void Open(AudioFormat format)
        {
            CurrentFormat = format;
            IsOpen = true;
        }

        public void Write(ReadOnlySpan<byte> pcm)
        {
            if (!_warned)
            {
                _warned = true;
                _log.Warning("AirPlay streaming for {Id} is not yet implemented — samples are being dropped.  See AirPlayDeviceProvider remarks for the RAOP TODO.", Id);
            }
        }

        public void Close()
        {
            IsOpen = false;
            CurrentFormat = null;
        }

        public void Pause() { }
        public void Resume() { }
        public void Flush() { }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Close();
        }
    }
}
