// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Serilog;

namespace OrgZ.Services.Sharing;

/// <summary>
/// Announces one <c>_orgz._tcp</c> instance on the local link: an unsolicited
/// announcement at start (and periodically), plus a response whenever someone browses
/// for the service type. Deliberately small - no conflict probing, no goodbye storms;
/// a duplicate name on the LAN just means two shares with the same label.
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

    private readonly MdnsWire.ServiceInstance _instance;
    private readonly CancellationTokenSource _cts = new();
    private List<(IPAddress Address, IPAddress Mask)> _interfaces = [];
    private UdpClient? _client;
    private Task? _loop;

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

        return sent > 0;
    }

    public MdnsAdvertiser(string shareName, ushort port, IEnumerable<string>? extraTxt = null)
    {
        var host = $"{SanitizeLabel(Environment.MachineName)}.local";
        _instance = new MdnsWire.ServiceInstance(
            SanitizeLabel(shareName),
            host,
            port,
            [$"name={shareName}", "version=1", "readonly=1", .. extraTxt ?? []],
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
        try
        {
            _client = new UdpClient(AddressFamily.InterNetwork);
            _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
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
            // still serves, it just isn't auto-discovered.
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

            foreach (var question in MdnsWire.ReadQuestions(packet.Buffer))
            {
                if (question.Name.Equals(MdnsWire.ServiceType, StringComparison.OrdinalIgnoreCase)
                    && question.Type is MdnsWire.TypePtr or MdnsWire.TypeAny)
                {
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
                        var reachable = BestAddressFor(packet.RemoteEndPoint.Address, _interfaces)?.ToString() ?? _instance.Address;
                        var legacy = MdnsWire.BuildResponse(_instance with { Address = reachable }, ttlSeconds: 10, id: MdnsWire.ReadId(packet.Buffer), cacheFlush: false);
                        await SendAsync(legacy, packet.RemoteEndPoint, ct);
                    }
                    else
                    {
                        await AnnounceAsync(ct);
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

    private async Task AnnounceAsync(CancellationToken ct)
    {
        var endpoint = new IPEndPoint(IPAddress.Parse(MdnsWire.MulticastAddress), MdnsWire.Port);

        await _announceGate.WaitAsync(ct);
        try
        {
            if (_interfaces.Count == 0)
            {
                await SendAsync(MdnsWire.BuildResponse(_instance), endpoint, ct);
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

                await SendAsync(MdnsWire.BuildResponse(_instance with { Address = address.ToString() }), endpoint, ct);
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
        _cts.Cancel();
        try { _client?.Dispose(); } catch { /* teardown */ }
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* expected on cancel */ }
        _cts.Dispose();
    }
}
