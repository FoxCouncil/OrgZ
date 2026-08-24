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
    private Dictionary<string, bool> _passwordRequired = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _cachedAt = DateTime.MinValue;
    private int _sweeping;   // 0/1 - at most one background sweep in flight

    public IReadOnlyList<AudioDeviceInfo> EnumerateDevices()
    {
        EnsurePassiveListener();

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

    /// <summary>
    /// Hooks the process's shared mDNS socket so state changes land the moment a receiver
    /// broadcasts them, not on the next sweep. A pod re-announces its TXT record when it
    /// starts or stops rendering, which is exactly when a picker icon should repaint -
    /// polling sweeps would show a speaker as free seconds after someone started using it.
    /// </summary>
    private void EnsurePassiveListener()
    {
        if (Sharing.MdnsAdvertiser.Running is not { } shared)
        {
            return;   // no shared socket yet - sweeps still work, retry on the next call
        }

        // EnumerateDevices is called from the UI thread and from the output manager's poll
        // timer at the same time, so the check and the subscribe have to be one step or both
        // callers hook the same responder. Keeping the instance we hooked also lets a
        // replaced responder (tests, a future restart) get re-hooked instead of leaving the
        // handler on a dead socket.
        lock (_cacheLock)
        {
            if (ReferenceEquals(_passiveSource, shared))
            {
                return;
            }

            if (_passiveSource is { } previous)
            {
                previous.PacketReceived -= OnMdnsAnnouncement;
            }

            _passiveSource = shared;
            shared.PacketReceived += OnMdnsAnnouncement;
        }
    }

    private Sharing.MdnsAdvertiser? _passiveSource;

    private void OnMdnsAnnouncement(byte[] data, IPEndPoint from)
    {
        try
        {
            var updates = ExtractAnnouncedFlags(data);
            if (updates.Count == 0)
            {
                return;
            }

            var changed = false;
            lock (_cacheLock)
            {
                List<AudioDeviceInfo>? next = null;
                foreach (var (instance, flags) in updates)
                {
                    // The announcement may arrive under either service instance name; the
                    // cached device kept whichever one the collapse preferred. Match by
                    // name first, then by resolved host.
                    var host = _endpoints.TryGetValue(instance, out var endpoint) ? endpoint.Host : null;

                    var source = next ?? _cachedDevices;
                    for (var i = 0; i < source.Count; i++)
                    {
                        var device = source[i];
                        var sameName = device.DeviceId.Equals(instance, StringComparison.OrdinalIgnoreCase);
                        var sameHost = host is not null
                            && _endpoints.TryGetValue(device.DeviceId, out var deviceEndpoint)
                            && deviceEndpoint.Host.Equals(host, StringComparison.OrdinalIgnoreCase);

                        if ((sameName || sameHost) && device.StateFlags != flags)
                        {
                            // Never mutate the published list - EnumerateDevices hands out
                            // the reference. Replace it wholesale.
                            next ??= [.. _cachedDevices];
                            next[i] = device with { StateFlags = flags };
                            changed = true;
                        }
                    }
                }

                if (next is not null)
                {
                    _cachedDevices = next;
                }
            }

            if (changed)
            {
                DevicesChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "AirPlay announcement parse failed");
        }
    }

    /// <summary>TXT status flags for AirPlay/RAOP instances found in one mDNS packet.</summary>
    internal static List<(string Instance, long Flags)> ExtractAnnouncedFlags(byte[] data)
    {
        var result = new List<(string, long)>();
        if (data.Length < 12)
        {
            return result;
        }

        var idx = 12;
        int qdCount = (data[4] << 8) | data[5];
        for (var q = 0; q < qdCount && idx < data.Length; q++)
        {
            idx = SkipName(data, idx);
            idx += 4;
        }

        int records = ((data[6] << 8) | data[7]) + ((data[8] << 8) | data[9]) + ((data[10] << 8) | data[11]);
        for (var r = 0; r < records && idx < data.Length; r++)
        {
            var nameStart = idx;
            idx = SkipName(data, idx);
            if (idx + 10 > data.Length)
            {
                return result;
            }

            int rType = (data[idx] << 8) | data[idx + 1];
            idx += 8;
            int rdLength = (data[idx] << 8) | data[idx + 1];
            idx += 2;
            var rdStart = idx;
            if (rdStart + rdLength > data.Length)
            {
                return result;
            }

            if (rType == 16)
            {
                var name = ReadName(data, nameStart);
                if ((name.EndsWith(RaopService, StringComparison.OrdinalIgnoreCase) || name.EndsWith(AirplayService, StringComparison.OrdinalIgnoreCase))
                    && TxtStatusFlags(data, rdStart, rdLength) is { } flags)
                {
                    result.Add((name, flags));
                }
            }

            idx = rdStart + rdLength;
        }

        return result;
    }

    /// <summary>One blocking mDNS sweep; updates the cache and raises <see cref="DevicesChanged"/> when the receiver set changed.</summary>
    private void SweepNow()
    {
        var receivers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var endpoints = new Dictionary<string, (string Host, int Port)>(StringComparer.OrdinalIgnoreCase);
        var passwordRequired = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var stateFlags = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            QueryMdns(RaopService, receivers, endpoints, TimeSpan.FromMilliseconds(1500), passwordRequired, stateFlags);
            QueryMdns(AirplayService, receivers, endpoints, TimeSpan.FromMilliseconds(500), passwordRequired, stateFlags);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "AirPlay mDNS discovery failed");
        }

        // One speaker, two service records, one truth: the same status bits arrive as
        // "sf" on _raop and "flags" on _airplay. Merge by resolved host so whichever
        // instance survives the collapse below still carries the state.
        var flagsByHost = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (instance, flags) in stateFlags)
        {
            if (endpoints.TryGetValue(instance, out var endpoint))
            {
                flagsByHost[endpoint.Host] = flagsByHost.TryGetValue(endpoint.Host, out var existing) ? existing | flags : flags;
            }
        }

        // One physical speaker answers BOTH service types, so it arrives twice under two
        // instance names. Collapse by resolved host, preferring the _raop entry - that's
        // the one carrying the audio port we actually stream to.
        var result = new List<AudioDeviceInfo>(receivers.Count);
        var byHost = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in receivers.OrderByDescending(r => r.Key.Contains("._raop.", StringComparison.OrdinalIgnoreCase)))
        {
            // Only a receiver whose SRV/A records gave us somewhere to connect is offered as
            // usable; the rest stay listed but disabled rather than failing at play time.
            var reachable = endpoints.TryGetValue(kvp.Key, out var endpoint);

            if (reachable)
            {
                if (byHost.ContainsKey(endpoint.Host))
                {
                    continue;   // already have this speaker, via the service type we prefer
                }
                byHost[endpoint.Host] = result.Count;
            }

            result.Add(new AudioDeviceInfo
            {
                DeviceId = kvp.Key,
                DisplayName = kvp.Value,
                ProviderId = Id,
                ProviderName = ProviderName,
                IsAvailable = reachable,
                StateFlags = reachable && flagsByHost.TryGetValue(endpoint.Host, out var flags)
                    ? flags
                    : stateFlags.GetValueOrDefault(kvp.Key),
            });
        }

        lock (_cacheLock)
        {
            _endpoints = endpoints;
            _passwordRequired = passwordRequired;
        }

        bool changed;
        lock (_cacheLock)
        {
            // A state-flag change IS a device change: the picker shows busy/playing
            // icons from these bits, and a receiver that just started or stopped
            // rendering should repaint without waiting to appear or disappear.
            changed = !_cachedDevices.Select(d => (d.DeviceId, d.StateFlags)).ToHashSet()
                .SetEquals(result.Select(d => (d.DeviceId, d.StateFlags)));
            _cachedDevices = result;
            _cachedAt = DateTime.UtcNow;
        }

        // A silent sweep left "are there receivers, or is discovery broken?" unanswerable
        // from the log. Report the outcome once per sweep - Information when the set
        // changed, Debug for the steady state (which includes a first sweep finding none).
        var summary = result.Count == 0
            ? "no AirPlay receivers on this network"
            : string.Join(", ", result.Select(d => $"{d.DisplayName}{(d.IsAvailable ? "" : " (unresolved)")}"));

        if (changed)
        {
            _log.Information("AirPlay sweep: {Count} receiver(s) - {Summary}", result.Count, summary);
            DevicesChanged?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _log.Debug("AirPlay sweep: unchanged, {Count} receiver(s)", result.Count);
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

        return new AirPlayRaopSink(device, endpoint.Host, endpoint.Port, FailureReportBus, AirPlayCredentials.Get(device.DeviceId));
    }

    /// <summary>True when this receiver advertised that it wants an AirPlay password.</summary>
    internal bool RequiresPassword(string deviceId)
    {
        lock (_cacheLock)
        {
            return _passwordRequired.TryGetValue(deviceId, out var required) && required;
        }
    }

    /// <summary>
    /// The bus a created sink reports late failures to (set by <see cref="AudioOutputManager"/>).
    /// Null in tests, where nothing is listening.
    /// </summary>
    internal AudioSinkBus? FailureReportBus { get; set; }

    /// <summary>
    /// Minimal mDNS PTR query for <paramref name="service"/>.  Sends a DNS
    /// query packet to 224.0.0.251:5353 and collects PTR-record answers for
    /// <paramref name="timeout"/> before returning.  Parses enough of the
    /// DNS wire format to extract the instance name (e.g., "LivingRoom"
    /// from "LivingRoom._raop._tcp.local").
    ///
    /// The query goes out EVERY up IPv4 interface, and the socket joins the group on
    /// each - the same all-interfaces model <see cref="Sharing.MdnsAdvertiser"/> already
    /// uses. A bare join/send picks whichever adapter won the metric race, so on a host
    /// with Hyper-V switches or VPN adapters discovery worked or didn't by coin toss.
    /// </summary>
    private static void QueryMdns(string service, Dictionary<string, string> receivers, Dictionary<string, (string Host, int Port)> endpoints, TimeSpan timeout, Dictionary<string, bool>? passwordRequired = null, Dictionary<string, long>? stateFlags = null)
    {
        // Prefer the process's ONE mDNS socket when it's up. Opening a second socket joined
        // to the same group inside one process is two responders racing on one link, and
        // OrgZ already owns a responder for library sharing.
        if (Sharing.MdnsAdvertiser.Running is { } shared)
        {
            var answers = new List<(byte[] Data, IPEndPoint From)>();
            // Collect runs on the responder's receive loop while this sweep waits on its own
            // thread, so the plain List has to be locked on both ends - an Add landing inside
            // a snapshot copy either throws or hands back a half-filled array.
            void Collect(byte[] data, IPEndPoint from)
            {
                lock (answers)
                {
                    answers.Add((data, from));
                }
            }

            shared.PacketReceived += Collect;
            try
            {
                shared.SendQuery(BuildMdnsQuery(service));

                var until = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
                while (Environment.TickCount64 < until)
                {
                    System.Threading.Thread.Sleep(40);
                }

                shared.PacketReceived -= Collect;

                (byte[] Data, IPEndPoint From)[] snapshot;
                lock (answers)
                {
                    snapshot = [.. answers];
                }

                foreach (var (data, from) in snapshot)
                {
                    // Every multicast datagram on the link lands here, not just answers to our
                    // query. One malformed packet must cost its own record, not the sweep.
                    try
                    {
                        ExtractPtrNames(data, service, receivers);
                        ExtractEndpoints(data, receivers, endpoints, passwordRequired, stateFlags);
                    }
                    catch (Exception ex)
                    {
                        _log.Debug(ex, "AirPlay mDNS packet parse failed from {From}", from);
                    }
                }
            }
            finally
            {
                shared.PacketReceived -= Collect;
            }

            return;
        }

        using var udp = new UdpClient();
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        var interfaces = Sharing.MdnsAdvertiser.LocalInterfaceAddresses();
        var joined = 0;
        foreach (var (address, _) in interfaces)
        {
            try
            {
                udp.JoinMulticastGroup(MdnsEndpoint.Address, address);
                joined++;
            }
            catch (SocketException ex)
            {
                _log.Debug(ex, "AirPlay mDNS join failed on {Address}", address);
            }
        }

        if (joined == 0)
        {
            // Nothing enumerable (or every join refused): a default-interface join still
            // beats deafness.
            udp.JoinMulticastGroup(MdnsEndpoint.Address);
        }

        var query = BuildMdnsQuery(service);
        if (interfaces.Count == 0)
        {
            udp.Send(query, query.Length, MdnsEndpoint);
        }
        else
        {
            foreach (var (address, _) in interfaces)
            {
                try
                {
                    // Steer each send: an un-steered one only leaves the default adapter.
                    udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                    udp.Send(query, query.Length, MdnsEndpoint);
                }
                catch (SocketException ex)
                {
                    _log.Debug(ex, "AirPlay mDNS query failed on {Address}", address);
                }
            }
        }

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

            byte[] data;
            try
            {
                data = udp.Receive(ref remote);
            }
            catch (SocketException)
            {
                break;
            }

            // A malformed datagram costs its own record, never the rest of the sweep.
            try
            {
                ExtractPtrNames(data, service, receivers);
                ExtractEndpoints(data, receivers, endpoints, passwordRequired, stateFlags);
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "AirPlay mDNS packet parse failed from {From}", remote);
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
            if (rdStart + rdLength > data.Length)
            {
                return;
            }

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
    internal static void ExtractEndpoints(byte[] data, Dictionary<string, string> receivers, Dictionary<string, (string Host, int Port)> endpoints, Dictionary<string, bool>? passwordRequired = null, Dictionary<string, long>? stateFlags = null)
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

                case 16:   // TXT
                {
                    var instance = ReadName(data, nameStart);
                    if (passwordRequired is not null)
                    {
                        passwordRequired[instance] = TxtDemandsPassword(data, rdStart, rdLength);
                    }

                    if (stateFlags is not null && TxtStatusFlags(data, rdStart, rdLength) is { } flags)
                    {
                        stateFlags[instance] = flags;
                    }
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

    /// <summary>
    /// Reads the status-flags value out of a TXT record (<c>flags</c> on _airplay,
    /// <c>sf</c> on _raop - same bits, two spellings), or null when the record carries
    /// neither. This is the receiver narrating its own LIVE state to the whole LAN:
    /// bit 17 = inside an AirPlay session, bit 20 = audio pipeline running. Free to read,
    /// no connection, and rebroadcast the moment the state changes.
    /// </summary>
    internal static long? TxtStatusFlags(byte[] data, int rdStart, int rdLength)
    {
        var end = Math.Min(rdStart + rdLength, data.Length);
        var idx = rdStart;
        while (idx < end)
        {
            int length = data[idx];
            idx++;
            if (length == 0 || idx + length > end)
            {
                break;
            }

            var entry = System.Text.Encoding.UTF8.GetString(data, idx, length);
            idx += length;

            var split = entry.IndexOf('=');
            if (split <= 0)
            {
                continue;
            }

            var key = entry[..split];
            var value = entry[(split + 1)..];

            if ((key.Equals("sf", StringComparison.OrdinalIgnoreCase) || key.Equals("flags", StringComparison.OrdinalIgnoreCase))
                && value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && long.TryParse(value.AsSpan(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var flags))
            {
                return flags;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads a TXT record and reports whether the receiver wants an AirPlay password.
    ///
    /// Two spellings mean the same thing: an explicit <c>pw=true</c>, or bit 0x80 in the
    /// status flags (<c>sf</c> on _raop, <c>flags</c> on _airplay) - which is what a
    /// HomePod with "Require Password" set in the Home app actually advertises. Getting
    /// this wrong is expensive: pairing with the wrong credential trips the receiver's
    /// brute-force lockout, so it's worth reading rather than discovering by failure.
    /// </summary>
    internal static bool TxtDemandsPassword(byte[] data, int rdStart, int rdLength)
    {
        const int PasswordBit = 0x80;

        var end = Math.Min(rdStart + rdLength, data.Length);
        var idx = rdStart;
        while (idx < end)
        {
            int length = data[idx];
            idx++;
            if (length == 0 || idx + length > end)
            {
                break;
            }

            var entry = System.Text.Encoding.UTF8.GetString(data, idx, length);
            idx += length;

            var split = entry.IndexOf('=');
            if (split <= 0)
            {
                continue;
            }

            var key = entry[..split];
            var value = entry[(split + 1)..];

            if (key.Equals("pw", StringComparison.OrdinalIgnoreCase) && value.Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if ((key.Equals("sf", StringComparison.OrdinalIgnoreCase) || key.Equals("flags", StringComparison.OrdinalIgnoreCase))
                && value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && long.TryParse(value.AsSpan(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var flags)
                && (flags & PasswordBit) != 0)
            {
                return true;
            }
        }

        return false;
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
                // A pointer needs its second byte; a truncated or crafted packet can put the
                // 0xC0 on the last byte, and the name is simply whatever we read so far.
                if (idx + 1 >= data.Length)
                {
                    break;
                }
                int offset = ((len & 0x3F) << 8) | data[idx + 1];
                idx = offset;
                jumps++;
                continue;
            }
            // A label claiming more bytes than the packet holds is a malformed (or hostile)
            // record - stop rather than reading past the buffer.
            if (idx + 1 + len > data.Length)
            {
                break;
            }
            if (sb.Length > 0) sb.Append('.');
            // UTF-8, not ASCII: RFC 6763 names carry real punctuation, and a HomePod named
            // "a Mac mini" (typographic apostrophe) decoded as "Fox???s" under ASCII.
            sb.Append(System.Text.Encoding.UTF8.GetString(data, idx + 1, len));
            idx += len + 1;
        }
        return sb.ToString();
    }

    internal void RaiseDevicesChanged() => DevicesChanged?.Invoke(this, EventArgs.Empty);
}
