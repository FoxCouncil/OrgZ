// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Serilog;

namespace OrgZ.Services.Sharing;

/// <summary>
/// Announces one <c>_orgz._tcp</c> instance on the local link: an unsolicited
/// announcement at start (and periodically), plus a response whenever someone browses
/// for the service type. Deliberately small - no conflict probing; a duplicate name on
/// the LAN just means two shares with the same label. Goodbyes (TTL-0) do go out on
/// unpublish and dispose, because peers cache records for two minutes and a cached
/// control endpoint pointing at a dead port is how "Controls Not Available" outlives
/// the run that caused it.
///
/// Every up IPv4 interface is joined and announced on, per RFC 6762's
/// all-interfaces responder model. A bare JoinMulticastGroup joins only the
/// default multicast interface - and on a host with Hyper-V switches, VPN
/// adapters and VMware NICs, WHICH interface that is changes with metric
/// ordering across restarts, so discovery worked or didn't by coin toss.
/// </summary>
public sealed class MdnsAdvertiser : IDisposable
{
    private static readonly ILogger _log = Logging.For("MdnsAdvertiser");

    private readonly MdnsWire.ServiceInstance? _instance;

    /// <summary>
    /// What this advertiser announces. Defaults to the library-share type; AirPlay remote
    /// control publishes <c>_dacp._tcp</c> through the same machinery rather than standing up
    /// a second mDNS stack.
    /// </summary>
    private readonly string _serviceType;
    private readonly CancellationTokenSource _cts = new();
    private List<(IPAddress Address, IPAddress Mask)> _interfaces = [];
    private UdpClient? _client;
    private Task? _loop;
    private bool _disposed;

    /// <summary>
    /// The responder this advertiser folded into, when <see cref="Start"/> found one already
    /// owning the socket. It holds our record, so it is also the only place that can take it
    /// back off the wire when we're disposed.
    /// </summary>
    private MdnsAdvertiser? _foldedInto;

    /// <summary>
    /// The live advertiser, once <see cref="Start"/> has succeeded - the single owner of
    /// this process's mDNS socket.
    ///
    /// Library sharing announces through it; AirPlay discovery browses through it. Two
    /// sockets bound to 5353 and joined to the same group inside one process is not a
    /// second opinion, it's a race - so there is exactly one, and both features use it.
    /// </summary>
    public static MdnsAdvertiser? Running { get; private set; }

    /// <summary>Every packet this socket receives, for consumers doing their own parsing.</summary>
    public event Action<byte[], IPEndPoint>? PacketReceived;

    /// <summary>
    /// Additional service instances announced through this same socket.
    ///
    /// AirPlay remote control has to publish a <c>_dacp._tcp</c> instance for the receiver to
    /// find, and that cannot be a second advertiser: this class owns the process's only
    /// 5353 socket by design. So extra services register here and are answered by the same
    /// responder loop.
    /// </summary>
    private readonly List<(string ServiceType, MdnsWire.ServiceInstance Instance)> _extra = [];

    /// <summary>
    /// The always-on responder, started on first use and owning this process's 5353 socket.
    ///
    /// mDNS is infrastructure, not a feature of library sharing. The share toggle publishes
    /// and withdraws one service record through this; it does not start and stop the stack,
    /// because AirPlay remote control needs the same socket whether or not the user shares
    /// their library.
    /// </summary>
    public static MdnsAdvertiser EnsureResponder()
    {
        lock (_responderGate)
        {
            if (Running is { } running)
            {
                return running;
            }

            // ONE responder instance even across failed starts. A 5353 bind can fail
            // transiently (another responder holding the port, an interface mid-flap), and
            // this used to hand back a socketless advertiser whose published records were
            // never announced - and nothing ever retried, so controls stayed dead for the
            // process lifetime. Records published while the socket was down live in _extra
            // and are announced the moment a retry binds.
            _responder ??= new MdnsAdvertiser();
            _responder.Start();
            return Running ?? _responder;
        }
    }

    private static MdnsAdvertiser? _responder;

    private static readonly Lock _responderGate = new();

    /// <summary>
    /// Withdraws a service from the process responder whether or not its socket ever came
    /// up. <see cref="EnsureResponder"/> hands out the responder even when the 5353 bind
    /// failed, so records can be sitting in a socketless one; going through
    /// <see cref="Running"/> would silently skip those and the next successful start would
    /// announce a service the caller has already stopped.
    /// </summary>
    public static void Withdraw(string serviceType)
    {
        MdnsAdvertiser? running;
        MdnsAdvertiser? responder;

        lock (_responderGate)
        {
            running = Running;
            responder = _responder;
        }

        running?.Unpublish(serviceType);

        if (responder is not null && !ReferenceEquals(responder, running))
        {
            responder.Unpublish(serviceType);
        }
    }

    /// <summary>
    /// Tears the process's mDNS stack down at exit, which is the only path that gets the
    /// TTL-0 goodbyes onto the wire. Without it every peer keeps serving our records from
    /// its cache for up to two minutes after we're gone, and a receiver that resolves a
    /// cached <c>iTunes_Ctrl_</c> record to a dead port treats the NEXT session as
    /// uncontrollable. Safe to call more than once.
    /// </summary>
    public static void Shutdown()
    {
        MdnsAdvertiser? running;
        MdnsAdvertiser? responder;

        lock (_responderGate)
        {
            running = Running;
            responder = _responder;
        }

        // Dispose takes _responderGate itself, so it runs outside the lock.
        running?.Dispose();

        if (responder is not null && !ReferenceEquals(responder, running))
        {
            responder.Dispose();
        }
    }

    /// <summary>Responder-only: owns the socket, announces nothing until something is published.</summary>
    private MdnsAdvertiser()
    {
        _serviceType = MdnsWire.ServiceType;
        _instance = null;
    }

    /// <summary>
    /// Withdraws a published service - what turning library sharing off should do. A goodbye
    /// (the same records at TTL 0) goes out first, so peers drop the entry now instead of
    /// serving a dead port out of their caches for the record's remaining lifetime.
    /// </summary>
    public void Unpublish(string serviceType)
    {
        (string ServiceType, MdnsWire.ServiceInstance Instance)[] leaving;
        lock (_extra)
        {
            leaving = [.. _extra.Where(e => e.ServiceType.Equals(serviceType, StringComparison.OrdinalIgnoreCase))];
            _extra.RemoveAll(e => e.ServiceType.Equals(serviceType, StringComparison.OrdinalIgnoreCase));
        }

        if (leaving.Length == 0)
        {
            // Nothing of ours was published under that type - a share started without
            // advertising, or a second withdrawal. Saying so would be a lie in the log.
            return;
        }

        if (_client is not null)
        {
            _ = Task.Run(() => GoodbyeAsync(leaving, _cts.Token));
        }

        _log.Information("mDNS withdrawing {Type}", serviceType);
    }

    /// <summary>
    /// Multicasts TTL-0 records for services on their way out. Stale records are not
    /// hypothetical: a receiver that cached an <c>iTunes_Ctrl_</c> record from a previous
    /// run resolves a control endpoint nothing answers on, and treats the sender as
    /// uncontrollable while doing it.
    /// </summary>
    private async Task GoodbyeAsync(IReadOnlyList<(string ServiceType, MdnsWire.ServiceInstance Instance)> services, CancellationToken ct)
    {
        var endpoint = new IPEndPoint(IPAddress.Parse(MdnsWire.MulticastAddress), MdnsWire.Port);

        try
        {
            await _announceGate.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (_interfaces.Count == 0)
            {
                foreach (var (type, instance) in services)
                {
                    await SendAsync(MdnsWire.BuildResponse(instance, type, ttlSeconds: 0, cacheFlush: false), endpoint, ct);
                }
                return;
            }

            foreach (var (address, _) in _interfaces)
            {
                try
                {
                    _client!.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                }
                catch (SocketException)
                {
                    continue;
                }

                foreach (var (type, instance) in services)
                {
                    await SendAsync(MdnsWire.BuildResponse(instance with { Address = address.ToString() }, type, ttlSeconds: 0, cacheFlush: false), endpoint, ct);
                }
            }
        }
        finally
        {
            _announceGate.Release();
        }
    }

    /// <summary>
    /// Publishes another service through this advertiser. Safe to call before or after
    /// <see cref="Start"/>; a late registration is picked up by the next browse or announce.
    /// </summary>
    public void Publish(string serviceType, string instanceName, ushort port, IEnumerable<string>? txt = null)
    {
        var instance = new MdnsWire.ServiceInstance(
            SanitizeLabel(instanceName),
            $"{SanitizeLabel(Environment.MachineName)}.local",
            port,
            [.. txt ?? []],
            LocalIPv4());

        lock (_extra)
        {
            _extra.RemoveAll(e => e.ServiceType.Equals(serviceType, StringComparison.OrdinalIgnoreCase));
            _extra.Add((serviceType, instance));
        }

        _log.Information("mDNS publishing {Instance} for {Type} on port {Port}", instanceName, serviceType, port);

        // Announce NOW rather than on the 60-second timer. A receiver decides whether a
        // sender is controllable within seconds of the session starting, and the initial
        // announcement can have already run (with nothing to say) before this Publish
        // landed - which left the DACP record off the wire for up to a minute, i.e.
        // "Controls Not Available" on some runs and not others, by race.
        if (_client is not null)
        {
            _ = Task.Run(() => AnnounceAsync(_cts.Token));
        }
    }

    private (string ServiceType, MdnsWire.ServiceInstance Instance)[] ExtraServices()
    {
        lock (_extra)
        {
            return [.. _extra];
        }
    }

    /// <summary>
    /// Sends a query out of every joined interface. Steering each send matters: an
    /// un-steered one leaves only the default adapter, which on a host with Hyper-V
    /// switches is whichever won the metric race.
    /// </summary>
    public bool SendQuery(byte[] query)
    {
        if (_client is null)
        {
            return false;
        }

        var endpoint = new IPEndPoint(IPAddress.Parse(MdnsWire.MulticastAddress), MdnsWire.Port);
        var sent = 0;

        // Same gate the announcer takes, for the same reason: steering the shared socket
        // and then sending is two operations. A sweep's query landing between an
        // announcement's steer and its send puts that announcement - A record and all -
        // out of the wrong adapter.
        _announceGate.Wait();
        try
        {
            foreach (var (address, _) in _interfaces)
            {
                try
                {
                    _client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                    _client.Send(query, query.Length, endpoint);
                    sent++;
                }
                catch (SocketException ex)
                {
                    _log.Debug(ex, "mDNS query send failed on {Address}", address);
                }
            }

            if (sent == 0)
            {
                try
                {
                    _client.Send(query, query.Length, endpoint);
                    sent++;
                }
                catch (SocketException ex)
                {
                    _log.Debug(ex, "mDNS query send failed on the default interface");
                }
            }
        }
        finally
        {
            _announceGate.Release();
        }

        return sent > 0;
    }

    public MdnsAdvertiser(string shareName, ushort port, IEnumerable<string>? extraTxt = null)
    {
        _serviceType = MdnsWire.ServiceType;

        var host = $"{SanitizeLabel(Environment.MachineName)}.local";

        string[] txt = [$"name={shareName}", "version=1", "readonly=1", .. extraTxt ?? []];

        _instance = new MdnsWire.ServiceInstance(
            SanitizeLabel(shareName),
            host,
            port,
            txt,
            LocalIPv4());
    }

    /// <summary>Every up, multicast-capable, non-loopback IPv4 address with its subnet mask.</summary>
    internal static List<(IPAddress Address, IPAddress Mask)> LocalInterfaceAddresses()
    {
        var result = new List<(IPAddress, IPAddress)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || !nic.SupportsMulticast
                    || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        result.Add((unicast.Address, unicast.IPv4Mask ?? IPAddress.None));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "interface enumeration failed");
        }

        return result;
    }

    /// <summary>
    /// The local address a given peer can actually reach - the one sharing its subnet.
    /// The A record must name it: advertising the default-route address to a peer on a
    /// different attached network hands them an address that may not route back.
    /// </summary>
    internal static IPAddress? BestAddressFor(IPAddress peer, IReadOnlyList<(IPAddress Address, IPAddress Mask)> interfaces)
    {
        foreach (var (address, mask) in interfaces)
        {
            if (mask.Equals(IPAddress.None))
            {
                continue;
            }

            var a = address.GetAddressBytes();
            var m = mask.GetAddressBytes();
            var p = peer.GetAddressBytes();
            var match = true;
            for (var i = 0; i < 4 && match; i++)
            {
                match = (a[i] & m[i]) == (p[i] & m[i]);
            }

            if (match)
            {
                return address;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a question is about a service we publish: a PTR/ANY browse for the type, an
    /// SRV/TXT/ANY resolve of the instance itself, or an A/ANY lookup of the host the SRV
    /// names. Static and pure so the matching rules are testable without a socket.
    /// </summary>
    internal static bool QuestionMatches(MdnsWire.Question question, string serviceType, MdnsWire.ServiceInstance instance)
    {
        if (question.Name.Equals(serviceType, StringComparison.OrdinalIgnoreCase)
            && question.Type is MdnsWire.TypePtr or MdnsWire.TypeAny)
        {
            return true;
        }

        if (question.Name.Equals($"{instance.InstanceName}.{serviceType}", StringComparison.OrdinalIgnoreCase)
            && question.Type is MdnsWire.TypeSrv or MdnsWire.TypeTxt or MdnsWire.TypeAny)
        {
            return true;
        }

        if (question.Name.Equals(instance.HostName, StringComparison.OrdinalIgnoreCase)
            && question.Type is MdnsWire.TypeA or MdnsWire.TypeAny)
        {
            return true;
        }

        return false;
    }

    /// <summary>mDNS labels can't carry dots (they'd split the name) - swap them out.</summary>
    internal static string SanitizeLabel(string value)
    {
        var trimmed = string.IsNullOrWhiteSpace(value) ? "OrgZ" : value.Trim();
        return trimmed.Replace('.', '-');
    }

    internal static string? LocalIPv4()
    {
        try
        {
            // No traffic is sent - connecting a UDP socket just picks the outbound
            // interface, which is the address peers should reach us on.
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect("8.8.8.8", 65530);
            return (probe.LocalEndPoint as IPEndPoint)?.Address.ToString();
        }
        catch
        {
            return null;
        }
    }

    public void Start()
    {
        // Someone already owns the socket: fold into it rather than binding a second one.
        // Two sockets on 5353 inside one process is the race this class exists to avoid, and
        // the order these come up in is not fixed - library sharing starts one when the user
        // enables it, AirPlay remote control needs one the moment a receiver is selected.
        if (Running is { } running && !ReferenceEquals(running, this))
        {
            running.Publish(_serviceType, _instance!.InstanceName, _instance.Port, _instance.TxtRecords);
            _foldedInto = running;
            _log.Debug("mDNS: folded {Type} into the running advertiser", _serviceType);
            return;
        }

        if (_client is not null)
        {
            return;   // already bound - EnsureResponder retries are a no-op once up
        }

        try
        {
            _client = new UdpClient(AddressFamily.InterNetwork);
            _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

            // Windows UDP gotcha, and the one that made "Controls Not Available" permanent for
            // the app but never the short-lived lab: when a datagram we multicast draws an ICMP
            // "port unreachable" back, the NEXT ReceiveAsync on this socket throws
            // WSAECONNRESET (10054) - even though nothing is wrong with the socket. Unhandled,
            // that killed the responder's receive loop, and from then on it answered no browse
            // for ANY service (DACP or the library share). SIO_UDP_CONNRESET off tells Windows
            // to stop surfacing those spurious resets. Winsock-only ioctl - everywhere else
            // the receive loop's per-packet resilience is all there is (and all that's needed).
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);
                    _client.Client.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null);
                }
                catch (SocketException ex)
                {
                    _log.Debug(ex, "SIO_UDP_CONNRESET ioctl failed - relying on loop resilience");
                }
            }

            _client.Client.Bind(new IPEndPoint(IPAddress.Any, MdnsWire.Port));

            var group = IPAddress.Parse(MdnsWire.MulticastAddress);
            _interfaces = LocalInterfaceAddresses();
            var joined = 0;
            foreach (var (address, _) in _interfaces)
            {
                try
                {
                    _client.JoinMulticastGroup(group, address);
                    joined++;
                }
                catch (SocketException ex)
                {
                    _log.Debug(ex, "join failed on {Address}", address);
                }
            }

            if (joined == 0)
            {
                // No enumerable interfaces (or every join refused): the old single
                // default-interface join is still better than deafness.
                _client.JoinMulticastGroup(group);
            }

            _log.Information("mDNS advertiser up on {Count} interface(s): {Addresses}", joined, string.Join(", ", _interfaces.Select(i => i.Address)));

            // This socket is now THE process's mDNS socket - see Running/Browse. AirPlay
            // discovery used to open a second one bound to the same group, which is two
            // responders fighting over one link in one process.
            Running = this;
            _loop = Task.Run(() => RunAsync(_cts.Token));
        }
        catch (Exception ex)
        {
            // A busy 5353 (another responder) is common and non-fatal: the HTTP share
            // still serves, it just isn't auto-discovered. The half-made socket is torn
            // down so a later EnsureResponder retry starts clean rather than finding a
            // bound-looking client that never joined the group.
            try { _client?.Dispose(); } catch { /* teardown */ }
            _client = null;
            _log.Warning(ex, "mDNS advertiser could not start - share will not be auto-discovered");
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        await AnnounceAsync(ct);

        var announce = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(60), ct);
                    await AnnounceAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }, ct);

        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try
            {
                packet = await _client!.ReceiveAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException ex)
            {
                // A datagram-level error (a spurious WSAECONNRESET from an ICMP port-unreachable,
                // an interface flapping) is about ONE packet, not the socket. Breaking here was
                // the bug that made the responder go permanently deaf mid-run. Log once at debug
                // and keep serving - the socket is still bound and joined.
                if (!ct.IsCancellationRequested)
                {
                    _log.Debug(ex, "mDNS receive hiccup ({Code}) - continuing", ex.SocketErrorCode);
                }
                continue;
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                {
                    // The responder just died: the share keeps serving but stops being
                    // discoverable, which looked like "the share vanished" with no trail.
                    _log.Warning(ex, "mDNS receive loop stopped - the share is no longer auto-discoverable");
                }
                break;
            }

            // Hand every packet to other consumers (AirPlay discovery) before we look for
            // our own questions - they browse through this socket rather than opening one.
            try
            {
                PacketReceived?.Invoke(packet.Buffer, packet.RemoteEndPoint);
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "mDNS packet consumer threw");
            }

            // Primary and extras answer through ONE path - the primary's legacy-unicast
            // handling used to be its own branch and the extras' a near-copy below it.
            var published = new List<(string ServiceType, MdnsWire.ServiceInstance Instance)>();
            if (_instance is not null)
            {
                published.Add((_serviceType, _instance));
            }
            published.AddRange(ExtraServices());

            foreach (var question in MdnsWire.ReadQuestions(packet.Buffer))
            {
                foreach (var (serviceType, instance) in published)
                {
                    // Browse (PTR for the type), direct resolve (SRV/TXT for the instance),
                    // and host resolve (A for the machine) all deserve an answer. Only the
                    // type browse used to get one, and a receiver that skipped the browse -
                    // resolving iTunes_Ctrl_<id> straight from its cache's PTR - asked a
                    // direct SRV question into silence, which on the remote reads as
                    // "Controls Not Available".
                    if (!QuestionMatches(question, serviceType, instance))
                    {
                        continue;
                    }

                    if (packet.RemoteEndPoint.Port != MdnsWire.Port)
                    {
                        // RFC 6762 §6.7: a query from any port but 5353 is a one-shot
                        // "legacy" query, and the reply must go unicast to that exact
                        // source - a multicast answer to :5353 is a packet an
                        // ephemeral-port querier (our own ShareDiscovery included) can
                        // never receive. Legacy replies echo the query id, keep a short
                        // TTL, clear the cache-flush bit - and carry the A record the
                        // QUERIER can reach: our address on its subnet, not whatever
                        // the default route says.
                        var reachable = BestAddressFor(packet.RemoteEndPoint.Address, _interfaces)?.ToString() ?? instance.Address;
                        var legacy = MdnsWire.BuildResponse(instance with { Address = reachable }, serviceType, ttlSeconds: 10, id: MdnsWire.ReadId(packet.Buffer), cacheFlush: false);
                        await SendAsync(legacy, packet.RemoteEndPoint, ct);
                    }
                    else
                    {
                        // RFC 6762 §6: a record just multicast within the last second answers
                        // this question already - the querier heard it. Without the
                        // suppression, a host spraying PTR questions turns this responder into
                        // a multicast amplifier and, because the announce is awaited under the
                        // gate, starves PacketReceived consumers (AirPlay discovery) of the
                        // real traffic behind it.
                        if (Environment.TickCount64 - Volatile.Read(ref _lastAnnounceTicks) < 1000)
                        {
                            break;
                        }

                        // A multicast query goes through the announcer rather than out this
                        // socket directly. Announcing steers the shared socket to each
                        // interface in turn under a gate; sending from here instead used
                        // whichever adapter won the metric race, and carried that adapter's
                        // address - which on a multi-homed box is an answer pointing at a
                        // network the receiver isn't on. Off-thread so the receive loop keeps
                        // draining while the announce walks the interfaces. The stamp is
                        // taken here, not just inside the announce, so the remaining
                        // questions in this packet don't each queue one behind the gate.
                        Volatile.Write(ref _lastAnnounceTicks, Environment.TickCount64);
                        _ = Task.Run(() => AnnounceAsync(ct), ct);
                    }

                    break;
                }
            }
        }

        await announce;
    }

    /// <summary>
    /// Multicasts the announcement out every joined interface, each carrying that
    /// interface's own address as the A record. One un-steered send goes out only the
    /// default multicast interface - which is whichever adapter won the metric race.
    /// </summary>
    // Announces are serialized. Each one STEERS the shared socket (SetSocketOption
    // MulticastInterface) and then sends - so the periodic announcer and a query-triggered
    // reply running concurrently could interleave one's steer with the other's send, and
    // the answer would go out the wrong interface carrying the wrong A record.
    private readonly SemaphoreSlim _announceGate = new(1, 1);

    /// <summary>When the last multicast announcement started, for RFC 6762 §6 suppression.</summary>
    private long _lastAnnounceTicks;

    private async Task AnnounceAsync(CancellationToken ct)
    {
        var endpoint = new IPEndPoint(IPAddress.Parse(MdnsWire.MulticastAddress), MdnsWire.Port);

        await _announceGate.WaitAsync(ct);
        try
        {
            // Every published service, primary and extra alike. A responder with nothing
            // published is legitimate - that is what running while library sharing is OFF
            // looks like, and AirPlay still needs the socket.
            var services = new List<(string Type, MdnsWire.ServiceInstance Instance)>();
            if (_instance is not null)
            {
                services.Add((_serviceType, _instance));
            }
            services.AddRange(ExtraServices().Select(e => (e.ServiceType, e.Instance)));

            if (services.Count == 0)
            {
                return;
            }

            // Stamped only once there is something to send. Opening the suppression window on
            // an announce that multicast nothing would silence the answer to a question that
            // arrives in the second after a Publish - the first question about a brand new
            // record being exactly the one worth answering.
            Volatile.Write(ref _lastAnnounceTicks, Environment.TickCount64);

            if (_interfaces.Count == 0)
            {
                foreach (var (type, instance) in services)
                {
                    await SendAsync(MdnsWire.BuildResponse(instance, type), endpoint, ct);
                }
                return;
            }

            foreach (var (address, _) in _interfaces)
            {
                try
                {
                    _client!.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                }
                catch (SocketException)
                {
                    continue;   // interface went away since Start - skip it
                }

                foreach (var (type, instance) in services)
                {
                    await SendAsync(MdnsWire.BuildResponse(instance with { Address = address.ToString() }, type), endpoint, ct);
                }
            }
        }
        finally
        {
            _announceGate.Release();
        }
    }

    private async Task SendAsync(byte[] payload, IPEndPoint endpoint, CancellationToken ct)
    {
        try
        {
            await _client!.SendAsync(payload, payload.Length, endpoint);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.Debug(ex, "mDNS send failed");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // A folded advertiser owns no socket: its record lives in the responder it folded
        // into, so withdrawing there is the only thing that takes it off the wire (and it
        // sends the goodbye on our behalf).
        if (_foldedInto is { } host)
        {
            host.Unpublish(_serviceType);
        }

        // Goodbye first, while the socket still exists. Without it every peer serves our
        // records out of its cache for up to two minutes after we're gone - and a receiver
        // that resolves a cached iTunes_Ctrl_ record to a dead port treats the next session
        // as uncontrollable. (Music Assistant hit exactly this and goodbyes for the same
        // reason.)
        try
        {
            var leaving = new List<(string ServiceType, MdnsWire.ServiceInstance Instance)>();
            if (_instance is not null)
            {
                leaving.Add((_serviceType, _instance));
            }
            leaving.AddRange(ExtraServices());

            if (_client is not null && leaving.Count > 0)
            {
                // Task.Run, not a bare .Wait on the returned task: Dispose is called from the
                // window's teardown on the Avalonia UI thread, where a captured synchronisation
                // context would post every continuation inside GoodbyeAsync back to the very
                // thread blocked here. That is a deadlock that only ends when the one-second
                // timeout expires - with no goodbye sent, which is the whole point of the call.
                Task.Run(() => GoodbyeAsync(leaving, CancellationToken.None)).Wait(TimeSpan.FromSeconds(1));
            }
        }
        catch { /* teardown */ }

        _cts.Cancel();
        try { _client?.Dispose(); } catch { /* teardown */ }
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* expected on cancel */ }
        _cts.Dispose();

        // A disposed advertiser must not stay published as THE process responder -
        // EnsureResponder hands Running out unconditionally, and a dead one here meant
        // every later Publish landed in an advertiser whose socket was gone.
        lock (_responderGate)
        {
            if (ReferenceEquals(Running, this))
            {
                Running = null;
            }

            if (ReferenceEquals(_responder, this))
            {
                _responder = null;
            }
        }
    }
}
