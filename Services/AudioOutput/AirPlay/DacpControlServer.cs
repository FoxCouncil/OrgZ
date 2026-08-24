// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using OrgZ.Services.Sharing;
using Serilog;

namespace OrgZ.Services.AudioOutput.AirPlay;

/// <summary>
/// The control endpoint an AirPlay receiver calls back to - DACP, the same mechanism iTunes
/// has always used.
///
/// Every RTSP request carries <c>DACP-ID</c> and <c>Active-Remote</c>, which together say
/// "here is a token, and here is the name to look me up under". The receiver then browses
/// mDNS for <c>iTunes_Ctrl_&lt;DACP-ID&gt;._dacp._tcp</c> and posts plain HTTP GETs to
/// whatever port it finds. Sending those two headers without publishing the service is a
/// promise with nothing behind it, and the remote shows "Controls Not Available" - the
/// receiver looked, found nothing, and greyed the buttons out.
///
/// One per process, not one per session: the id is an identity for this app as a sender, and
/// iTunes likewise publishes a single control endpoint however many speakers it drives.
/// </summary>
internal sealed class DacpControlServer : IDisposable
{
    private static readonly ILogger _log = Logging.For("Dacp");

    private const string ServiceType = "_dacp._tcp.local";

    private static readonly Lock _gate = new();
    private static DacpControlServer? _instance;

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Identifies this sender; the mDNS instance name is derived from it.</summary>
    public string DacpId { get; }

    /// <summary>
    /// The per-run token sent to the receiver in every RTSP request, which it echoes back in
    /// its <c>Active-Remote</c> header when it calls us.
    ///
    /// Sent but NOT enforced: nothing here rejects a request that omits it or gets it wrong,
    /// so any host on the network can drive transport and volume while a session is up. That
    /// is the same trust model as the rest of OrgZ's LAN surface (library sharing pins a
    /// certificate; discovery answers anyone), and tightening it needs a capture proving the
    /// HomePod echoes the token on every request shape first - refusing a poll it never
    /// signed would cost the controls entirely. The value is logged at Debug on arrival so
    /// that capture is one session away.
    /// </summary>
    public string ActiveRemote { get; }

    public int Port { get; }

    /// <summary>The four-character command the receiver asked for ("play", "pause", "nextitem"...).</summary>
    public event EventHandler<string>? Command;

    /// <summary>The remote moved the volume; carries a linear 0-1 level.</summary>
    public event EventHandler<float>? VolumeChanged;

    /// <summary>
    /// Reads a volume out of a DACP request, or null when it isn't one.
    ///
    /// The remote sends <c>setproperty?dmcp.device-volume=-14.5</c> in AirPlay's decibels
    /// (-30..0, with -144 meaning muted), or occasionally <c>dmcp.volume</c> as a 0-100
    /// percentage. Treating setproperty as a plain command drops the entire payload, since
    /// everything it means is in the query string.
    ///
    /// Non-finite values are refused rather than parsed. This endpoint is reachable by any
    /// host on the network, "NaN" and "Infinity" are things <see cref="double.TryParse(string, System.Globalization.NumberStyles, IFormatProvider, out double)"/>
    /// happily accepts under <see cref="System.Globalization.NumberStyles.Float"/>, and a NaN
    /// level travels all the way into the persisted output settings - where serializing it
    /// throws and takes the process down with a live AirPlay session attached.
    /// </summary>
    internal static float? ParseVolume(string request)
    {
        var line = request.Split("\r\n")[0];
        var query = line.IndexOf('?');
        if (query < 0)
        {
            return null;
        }

        var end = line.IndexOf(' ', query);
        var parameters = end > query ? line[(query + 1)..end] : line[(query + 1)..];

        foreach (var pair in parameters.Split('&'))
        {
            var split = pair.IndexOf('=');
            if (split <= 0)
            {
                continue;
            }

            var key = pair[..split].Trim();
            var value = pair[(split + 1)..].Trim();

            if (key.Equals("dmcp.device-volume", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var db)
                && double.IsFinite(db))
            {
                return AirPlay2Session.LinearFromDb(db);
            }

            if (key.Equals("dmcp.volume", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var percent)
                && double.IsFinite(percent))
            {
                return (float)Math.Clamp(percent / 100.0, 0.0, 1.0);
            }
        }

        return null;
    }

    /// <summary>
    /// Whether the control endpoint advertises itself on the network. Off for tests: the
    /// suite would otherwise publish a real <c>iTunes_Ctrl_</c> record on the developer's LAN,
    /// under the same identity the running app uses, and every HomePod within earshot would
    /// cache a port that dies with the test host.
    /// </summary>
    internal static bool PublishMdns = true;

    private DacpControlServer()
    {
        // STABLE across launches, not freshly random each time.
        //
        // The id names an mDNS service the receiver browses for. A new one per launch leaves
        // the receiver's cache holding records for every previous run - all pointing at ports
        // nothing listens on any more - and a receiver that resolves a control endpoint it
        // cannot reach treats the sender as uncontrollable. iTunes keeps one identity for the
        // life of the install for exactly this reason.
        //
        // Validated, not merely checked for empty: the id is hex-decoded to seed the sender's
        // device MAC, so a hand-edited or truncated settings value throws there instead - and
        // that throw is on the connect path, which makes every receiver unreachable with no
        // hint as to why. A malformed id is replaced rather than trusted.
        DacpId = Settings.Get<string>("AirPlay.DacpId", string.Empty);
        if (DacpId.Length != 16 || !DacpId.All(Uri.IsHexDigit))
        {
            DacpId = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToUpperInvariant();
            Settings.Set("AirPlay.DacpId", DacpId);

            // Written now, not whenever something else happens to save. Until this reaches
            // disk the identity isn't stable across launches, which is the whole point of it.
            Settings.SaveDeferred();
        }

        // Active-Remote is the opposite of the id: FRESH each run, never persisted. It is a
        // per-session shared secret the receiver echoes back, and the reference senders mint
        // a new one every time - a reused token is the one a stale receiver cache also holds.
        ActiveRemote = ((uint)Random.Shared.Next(1, int.MaxValue)).ToString();

        _listener = new TcpListener(IPAddress.Any, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));

        // Published through the one advertiser that owns this process's 5353 socket. If
        // sharing hasn't started it yet there is nothing to publish through - remote control
        // is then simply unavailable, which is the same as it has always been.
        if (PublishMdns)
        {
            MdnsAdvertiser.EnsureResponder().Publish(ServiceType, $"iTunes_Ctrl_{DacpId}", (ushort)Port);
        }

        _log.Information("DACP control endpoint on port {Port} as iTunes_Ctrl_{Id}", Port, DacpId);
    }

    /// <summary>The process-wide endpoint, started on first use.</summary>
    public static DacpControlServer Instance
    {
        get
        {
            lock (_gate)
            {
                return _instance ??= new DacpControlServer();
            }
        }
    }

    /// <summary>
    /// Tears the endpoint down and forgets it, so the next <see cref="Instance"/> builds a
    /// fresh one.
    ///
    /// Called at app exit. Nothing else disposes a singleton, and without this the TTL-0
    /// goodbye in <see cref="Dispose"/> is dead code: OrgZ would leave every receiver on the
    /// network holding a cached <c>iTunes_Ctrl_</c> record pointing at a port that no longer
    /// exists, which is exactly the state a receiver reads as "this sender is uncontrollable".
    /// </summary>
    internal static void Shutdown()
    {
        lock (_gate)
        {
            _instance?.Dispose();
            _instance = null;
        }
    }

    /// <summary>
    /// How many receiver connections may be in flight at once. A receiver uses one and holds
    /// it; this is a ceiling against an unfriendly host on the network opening sockets until
    /// the process runs out of handles, not a capacity target.
    /// </summary>
    private static readonly SemaphoreSlim _slots = new(8, 8);

    /// <summary>How long a held-open connection may say nothing before we reclaim it.</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);

    /// <summary>The most we will buffer looking for the end of a request head.</summary>
    private const int MaxHeadBytes = 8 * 1024;

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(ct);

                if (!await _slots.WaitAsync(0, ct))
                {
                    _log.Debug("DACP connection refused: {Max} already in flight", 8);
                    client.Dispose();
                    continue;
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ServeAsync(client, ct);
                    }
                    finally
                    {
                        _slots.Release();
                    }
                }, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                // The listener was stopped underneath us; accepting again would spin.
                return;
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                // A queued connection that reset before we accepted it surfaces here as a
                // SocketException. Returning on it killed the accept loop for the LIFE OF
                // THE PROCESS while the port stayed bound and advertised - the "controls
                // worked once, then never again" failure. Log and keep accepting.
                _log.Debug(ex, "DACP accept failed");
            }
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var buffer = new byte[2048];

                // Accumulated across reads. One TCP read is not one HTTP request: a poll can
                // arrive split across segments, and two can arrive coalesced into one. Treating
                // a read as a request drops the second of a coalesced pair, and the receiver
                // counts five unanswered polls as "this sender is uncontrollable".
                var pending = new List<byte>(2048);

                // The receiver KEEPS this connection and polls it - dmcp.volume about once a
                // second as a health check - and five bad answers is it declaring the sender
                // uncontrollable, which the remote renders as "Controls Not Available". So
                // one-request-per-socket is not a simplification, it is a countdown: the
                // second poll hits EOF and starts it.
                while (!ct.IsCancellationRequested)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    idle.CancelAfter(IdleTimeout);

                    int read;
                    try
                    {
                        read = await stream.ReadAsync(buffer, idle.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        _log.Debug("DACP connection idle for {Minutes} minutes; closing", IdleTimeout.TotalMinutes);
                        return;
                    }

                    if (read <= 0)
                    {
                        return;
                    }

                    pending.AddRange(buffer.AsSpan(0, read));

                    // Answer every complete request now in the buffer, then go back for more.
                    while (TryTakeRequest(pending, out var request))
                    {
                        await AnswerAsync(stream, request, ct);
                    }

                    if (pending.Count > MaxHeadBytes)
                    {
                        // Nothing that big is a DACP request; someone is dribbling bytes to
                        // keep the socket (and the buffer) growing.
                        _log.Debug("DACP request head exceeded {Max} bytes; closing", MaxHeadBytes);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "DACP request failed");
            }
        }
    }

    /// <summary>
    /// Pulls one complete request off the front of the buffer, consuming its bytes. Returns
    /// false while the head is still incomplete, so the caller reads more.
    /// </summary>
    private static bool TryTakeRequest(List<byte> pending, out string request)
    {
        request = string.Empty;

        var text = Encoding.ASCII.GetString([.. pending]);
        var headEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (headEnd < 0)
        {
            return false;
        }

        var head = text[..(headEnd + 4)];

        // DACP requests are GETs and carry no body, but an announced one has to be consumed
        // or it would be read as the start of the next request.
        var length = 0;
        foreach (var line in head.Split("\r\n"))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line["Content-Length:".Length..].Trim(), out var declared)
                && declared > 0)
            {
                length = Math.Min(declared, MaxHeadBytes);
            }
        }

        if (pending.Count < head.Length + length)
        {
            return false;
        }

        pending.RemoveRange(0, head.Length + length);
        request = head;
        return true;
    }

    private async Task AnswerAsync(Stream stream, string request, CancellationToken ct)
    {
        var line = request.Split("\r\n")[0];

        // The receiver reaching our control endpoint AT ALL is the thing that separates
        // "Controls Available" from not - so log the first line of every inbound request,
        // health-check polls included. Truncated: the line comes off the network.
        _log.Information("DACP inbound: {Line}", line.Length > 200 ? line[..200] : line);

        // Recorded, not enforced - see the note on ActiveRemote. One session's worth of these
        // is what decides whether the token can become a gate.
        foreach (var header in request.Split("\r\n"))
        {
            if (header.StartsWith("Active-Remote:", StringComparison.OrdinalIgnoreCase))
            {
                var presented = header["Active-Remote:".Length..].Trim();
                _log.Debug("DACP Active-Remote: {Presented} (ours {Ours}, match {Match})", presented, ActiveRemote, presented == ActiveRemote);
            }
        }

        var command = ParseCommand(request);
        var volume = ParseVolume(request);

        // Answer before acting: the remote wants a prompt reply, and whatever the command
        // does to playback is none of its business.
        var response = BuildResponse(request, command);
        await stream.WriteAsync(response, ct);
        await stream.FlushAsync(ct);

        // setproperty carries its whole meaning in the query string, so it is handled as a
        // level rather than as a verb.
        if (volume is { } level)
        {
            _volumePercent = (int)Math.Round(level * 100);
            _log.Information("DACP volume: {Percent}%", _volumePercent);
            VolumeChanged?.Invoke(this, level);
        }
        else if (command is not null && !QueryCommands.Contains(command))
        {
            _log.Information("DACP command: {Command}", command);
            Command?.Invoke(this, command);
        }
    }

    /// <summary>Requests that ask about us rather than telling playback to do something.</summary>
    private static readonly HashSet<string> QueryCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "getproperty", "getspeakers", "setproperty",
    };

    /// <summary>The level answered to a health-check poll, 0-100. Kept by <see cref="ReportVolume"/>.</summary>
    private volatile int _volumePercent = 50;

    /// <summary>
    /// Records the current output level so the receiver's polls get the truth. Called by the
    /// session whenever anything - the app, the speaker's own controls - moves the volume.
    /// </summary>
    public void ReportVolume(float linear) => _volumePercent = (int)Math.Round(Math.Clamp(linear, 0f, 1f) * 100);

    /// <summary>
    /// The answer a DACP request gets. These are the receiver's HEALTH CHECKS, and the
    /// contract comes from the reference receiver's client: <c>getproperty</c> for
    /// dmcp.volume wants a <c>cmgt</c> DMAP body (mstt, cmvo), <c>getspeakers</c> wants
    /// <c>casp</c> with one mdcl per speaker, and actual commands answer 204 No Content. An
    /// empty 200 to a poll counts as a BAD answer - and five of those is "Controls Not
    /// Available" on the remote.
    /// </summary>
    private byte[] BuildResponse(string request, string? command)
    {
        if (command is not null && command.Equals("getproperty", StringComparison.OrdinalIgnoreCase) && request.Contains("dmcp.volume", StringComparison.OrdinalIgnoreCase))
        {
            var body = new DmapWriter()
                .Int("mstt", 200)
                .Int("cmvo", _volumePercent)
                .Wrap("cmgt");
            return WithBody(body);
        }

        if (command is not null && command.Equals("getspeakers", StringComparison.OrdinalIgnoreCase))
        {
            var speaker = new DmapWriter()
                .String("minm", "OrgZ")
                .Long("msma", 0)
                .Char("caia", 1)
                .Int("cmvo", _volumePercent);

            var body = new DmapWriter()
                .Int("mstt", 200)
                .Container("mdcl", speaker)
                .Wrap("casp");
            return WithBody(body);
        }

        if (command is not null && command.Equals("getproperty", StringComparison.OrdinalIgnoreCase))
        {
            return WithBody(new DmapWriter().Int("mstt", 200).Wrap("cmgt"));
        }

        // Verbs - play, pause, setproperty - are acknowledged, not answered.
        return "HTTP/1.1 204 No Content\r\nContent-Length: 0\r\n\r\n"u8.ToArray();
    }

    private static byte[] WithBody(byte[] body)
    {
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/x-dmap-tagged\r\nContent-Length: {body.Length}\r\n\r\n");
        var response = new byte[head.Length + body.Length];
        head.CopyTo(response, 0);
        body.CopyTo(response, head.Length);
        return response;
    }

    /// <summary>
    /// Pulls the command out of a DACP request line - <c>GET /ctrl-int/1/playpause HTTP/1.1</c>.
    /// Internal-static so the parsing is testable without a socket.
    /// </summary>
    internal static string? ParseCommand(string request)
    {
        var line = request.Split("\r\n")[0];
        var parts = line.Split(' ');
        if (parts.Length < 2)
        {
            return null;
        }

        var path = parts[1];
        var marker = path.IndexOf("/ctrl-int/", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            return null;
        }

        // ".../ctrl-int/<n>/<command>" - the command is the last segment, minus any query.
        var tail = path[(marker + "/ctrl-int/".Length)..];
        var slash = tail.IndexOf('/');
        if (slash < 0)
        {
            return null;
        }

        var command = tail[(slash + 1)..];
        var query = command.IndexOf('?');
        if (query >= 0)
        {
            command = command[..query];
        }

        return string.IsNullOrWhiteSpace(command) ? null : command;
    }

    public void Dispose()
    {
        // Withdraw the mDNS record with a goodbye BEFORE the port dies. A receiver that
        // caches iTunes_Ctrl_ pointing at a dead port treats the next session as
        // uncontrollable for as long as the record lives.
        try
        {
            // Through the responder rather than through `Running`: a responder that never
            // managed to bind 5353 still HOLDS the record, and would announce it as soon as
            // it did bind. Withdrawing via the static reaches that case too.
            MdnsAdvertiser.Withdraw(ServiceType);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "DACP unpublish failed");
        }

        _cts.Cancel();
        _listener.Stop();
        _cts.Dispose();
    }
}
