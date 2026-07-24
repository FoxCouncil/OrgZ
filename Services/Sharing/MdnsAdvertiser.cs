// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Net;
using System.Net.Sockets;
using Serilog;

namespace OrgZ.Services.Sharing;

/// <summary>
/// Announces one <c>_orgz._tcp</c> instance on the local link: an unsolicited
/// announcement at start (and periodically), plus a response whenever someone browses
/// for the service type. Deliberately small - no conflict probing, no goodbye storms;
/// a duplicate name on the LAN just means two shares with the same label.
/// </summary>
public sealed class MdnsAdvertiser : IDisposable
{
    private static readonly ILogger _log = Logging.For("MdnsAdvertiser");

    private readonly MdnsWire.ServiceInstance _instance;
    private readonly CancellationTokenSource _cts = new();
    private UdpClient? _client;
    private Task? _loop;

    public MdnsAdvertiser(string shareName, ushort port)
    {
        var host = $"{SanitizeLabel(Environment.MachineName)}.local";
        _instance = new MdnsWire.ServiceInstance(
            SanitizeLabel(shareName),
            host,
            port,
            [$"name={shareName}", "version=1", "readonly=1"],
            LocalIPv4());
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
            _client.JoinMulticastGroup(IPAddress.Parse(MdnsWire.MulticastAddress));
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
        var endpoint = new IPEndPoint(IPAddress.Parse(MdnsWire.MulticastAddress), MdnsWire.Port);
        var response = MdnsWire.BuildResponse(_instance);

        await SendAsync(response, endpoint, ct);

        var announce = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(60), ct);
                    await SendAsync(response, endpoint, ct);
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
            catch (Exception)
            {
                break;
            }

            foreach (var question in MdnsWire.ReadQuestions(packet.Buffer))
            {
                if (question.Name.Equals(MdnsWire.ServiceType, StringComparison.OrdinalIgnoreCase)
                    && question.Type is MdnsWire.TypePtr or MdnsWire.TypeAny)
                {
                    await SendAsync(response, endpoint, ct);
                    break;
                }
            }
        }

        await announce;
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
