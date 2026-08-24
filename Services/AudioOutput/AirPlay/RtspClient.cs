// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Net.Sockets;
using System.Text;
using Serilog;

namespace OrgZ.Services.AudioOutput.AirPlay;

/// <summary>
/// One parsed RTSP response. The body is kept as BYTES: AirPlay 2's pair-setup replies
/// are binary TLV8 and its SETUP replies are binary plists, so decoding to text up front
/// would corrupt them.
/// </summary>
internal sealed record RtspResponse(int StatusCode, string StatusText, Dictionary<string, string> Headers, byte[] BodyBytes)
{
    public bool IsSuccess => StatusCode is >= 200 and < 300;

    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;

    /// <summary>The body as text - only meaningful for the text/parameters style replies.</summary>
    public string Body => Encoding.UTF8.GetString(BodyBytes);
}

/// <summary>
/// Minimal RTSP/1.0 client for the RAOP handshake - enough for OPTIONS / ANNOUNCE /
/// SETUP / RECORD / SET_PARAMETER / FLUSH / TEARDOWN against an AirPlay receiver.
/// Not a general RTSP implementation: one request in flight at a time, no interleaved
/// data channel (RAOP carries audio on its own UDP ports).
/// </summary>
internal sealed class RtspClient : IDisposable
{
    private static readonly ILogger _log = Logging.For("Rtsp");

    private readonly TcpClient _tcp = new();
    private HapCryptoStream? _stream;
    private int _cSeq;
    private bool _disposed;

    /// <summary>
    /// One request in flight at a time. RTSP replies carry no way to match them to a
    /// request beyond arriving in order, and the session now writes from several places at
    /// once - the feedback heartbeat, progress updates, metadata, and volume changes. Two
    /// overlapping sends would interleave on the wire and each would read the other's reply.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Headers sent on every request - identity the receiver logs and keys sessions on.</summary>
    public Dictionary<string, string> DefaultHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? SessionId { get; private set; }

    /// <summary>Our address as the receiver sees it - the SDP's origin line needs it.</summary>
    public string LocalAddress { get; private set; } = "0.0.0.0";

    /// <summary>The receiver's resolved IP - what a peer list must carry, never its hostname.</summary>
    public string RemoteAddress { get; private set; } = "0.0.0.0";

    /// <summary>
    /// Switches the connection to HAP encryption. Everything after transient pair-setup
    /// must go through it; a plaintext request is simply dropped by the receiver.
    /// </summary>
    public void EnableEncryption(byte[] sharedSecret)
    {
        var (output, input) = HapCryptoStream.DeriveControlKeys(sharedSecret);
        _stream?.Enable(output, input);
        _log.Debug("RTSP connection is now HAP-encrypted");
    }

    public async Task ConnectAsync(string host, int port, CancellationToken ct)
    {
        await _tcp.ConnectAsync(host, port, ct);

        // Transparent until pairing turns encryption on - see HapCryptoStream.
        _stream = new HapCryptoStream(_tcp.GetStream());
        if (_tcp.Client.LocalEndPoint is System.Net.IPEndPoint local)
        {
            // TcpClient opens a dual-stack socket, so this comes back as an IPv4-mapped
            // IPv6 address and the request URI becomes "rtsp://::ffff:192.168.1.20/...".
            // A receiver can't parse that and simply never replies - a silent hang rather
            // than a refusal.
            var address = local.Address.IsIPv4MappedToIPv6 ? local.Address.MapToIPv4() : local.Address;
            LocalAddress = address.ToString();
        }

        if (_tcp.Client.RemoteEndPoint is System.Net.IPEndPoint remote)
        {
            var address = remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4() : remote.Address;
            RemoteAddress = address.ToString();
        }
    }

    public async Task<RtspResponse> SendAsync(
        string method,
        string uri,
        IReadOnlyDictionary<string, string>? headers = null,
        string? contentType = null,
        byte[]? body = null,
        CancellationToken ct = default)
    {
        if (_stream is null)
        {
            throw new InvalidOperationException("RTSP client is not connected.");
        }

        await _gate.WaitAsync(ct);
        try
        {
            return await SendLockedAsync(method, uri, headers, contentType, body, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<RtspResponse> SendLockedAsync(
        string method,
        string uri,
        IReadOnlyDictionary<string, string>? headers,
        string? contentType,
        byte[]? body,
        CancellationToken ct)
    {
        var stream = _stream ?? throw new InvalidOperationException("RTSP client is not connected.");

        var cSeq = ++_cSeq;
        var request = BuildRequest(cSeq, method, uri, DefaultHeaders, headers, contentType, body, SessionId);

        // Set ORGZ_RTSP_DUMP to capture exactly what goes on the wire. The app and the live
        // test run identical code against the same receiver with different outcomes, and
        // diffing the actual bytes beats reasoning about why.
        if (Environment.GetEnvironmentVariable("ORGZ_RTSP_DUMP") is { Length: > 0 } dumpPath)
        {
            var headLength = request.Length - (body?.Length ?? 0);
            // Include the body HEX for small bodies (DMAP metadata, MediaRemote /command,
            // progress) so an app-vs-lab diff shows the actual bytes, not just a size. Audio
            // and artwork are large and skipped - they'd bury the interesting frames.
            var bodyHex = body is { Length: > 0 and <= 2048 } ? Convert.ToHexString(body) : $"<{body?.Length ?? 0} bytes>";
            File.AppendAllText(dumpPath, Encoding.ASCII.GetString(request, 0, headLength) + $"<<<body {body?.Length ?? 0} bytes: {bodyHex}>>>\n\n");
        }

        // Synchronous on purpose - see HapCryptoStream.Read. The handshake runs on a
        // dedicated thread so a saturated thread pool can't stall it for ten seconds and
        // make a perfectly good reply look like it never arrived.
        stream.Write(request, 0, request.Length);
        stream.Flush();

        // A receiver that dislikes a request sometimes just never answers. Without a
        // deadline that hangs the handshake forever, which reads as silence with nothing
        // in the log - far harder to diagnose than a failure.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);

        RtspResponse response;
        try
        {
            _tcp.ReceiveTimeout = 10000;

            // The head is read with blocking reads (see ReadResponseAsync), so the deadline
            // above can't interrupt a peer that drips one byte every few seconds. Pulling the
            // socket out from under the read is what makes the deadline real.
            //
            // Registered on the DEADLINE only, never on the caller's token. This connection is
            // shared by every request on the session - the keep-alive loops, the feedback poll,
            // the teardown - so destroying the socket because one caller cancelled would take
            // the whole session down with it. A cancelled caller abandons its own read; only a
            // peer that has genuinely stopped answering loses the socket.
            using var abort = deadline.Token.Register(static state => ((TcpClient)state!).Client.Dispose(), _tcp);

            while (true)
            {
                response = await ReadResponseAsync(stream, timeout.Token);

                // A reply to a request we already gave up on is still sitting in the stream.
                // Consuming it as THIS request's answer desynchronises every pair after it,
                // so drain the stale ones instead.
                if (!int.TryParse(response.Header("CSeq"), out var seq) || seq >= cSeq)
                {
                    break;
                }

                _log.Debug("RTSP discarding stale reply CSeq {Seq} while waiting for {Expected}", seq, cSeq);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException && !ct.IsCancellationRequested)
        {
            // A stream we stopped waiting on can hold a reply we'll never read. Close it so
            // the next request opens a fresh connection rather than reading someone else's answer.
            _tcp.Client.Dispose();
            throw new TimeoutException($"RTSP {method} {uri} got no reply within 10s.", ex);
        }

        if (int.TryParse(response.Header("CSeq"), out var replySeq) && replySeq != cSeq)
        {
            _tcp.Client.Dispose();
            throw new IOException($"RTSP {method} {uri} reply CSeq {replySeq} does not match request {cSeq}.");
        }

        _log.Debug("RTSP {Method} {Uri} -> {Status}", method, uri, response.StatusCode);
        if (response.Header("Session") is { } session)
        {
            // "Session: DEADBEEF;timeout=60" - the id is everything before the first ';'.
            SessionId = session.Split(';')[0].Trim();
        }

        if (!response.IsSuccess)
        {
            _log.Debug("RTSP {Method} -> {Status} {Text}", method, response.StatusCode, response.StatusText);
        }

        return response;
    }

    /// <summary>Builds the on-wire request bytes. Pure, so the framing is tested without a socket.</summary>
    internal static byte[] BuildRequest(
        int cSeq,
        string method,
        string uri,
        IReadOnlyDictionary<string, string>? defaultHeaders,
        IReadOnlyDictionary<string, string>? headers,
        string? contentType,
        byte[]? body,
        string? sessionId)
    {
        var sb = new StringBuilder();
        sb.Append($"{method} {uri} RTSP/1.0\r\n");
        sb.Append($"CSeq: {cSeq}\r\n");

        if (defaultHeaders is not null)
        {
            foreach (var (k, v) in defaultHeaders)
            {
                sb.Append($"{k}: {v}\r\n");
            }
        }

        if (headers is not null)
        {
            foreach (var (k, v) in headers)
            {
                sb.Append($"{k}: {v}\r\n");
            }
        }

        if (sessionId is not null)
        {
            sb.Append($"Session: {sessionId}\r\n");
        }

        if (contentType is not null)
        {
            sb.Append($"Content-Type: {contentType}\r\n");
        }

        sb.Append($"Content-Length: {body?.Length ?? 0}\r\n");
        sb.Append("\r\n");

        var head = Encoding.ASCII.GetBytes(sb.ToString());
        if (body is null || body.Length == 0)
        {
            return head;
        }

        var full = new byte[head.Length + body.Length];
        head.CopyTo(full, 0);
        body.CopyTo(full, head.Length);
        return full;
    }

    /// <summary>Parses a complete response. Pure, so header/status handling is tested directly.</summary>
    internal static RtspResponse ParseResponse(string head, string body)
        => ParseResponse(head, Encoding.UTF8.GetBytes(body));

    internal static RtspResponse ParseResponse(string head, byte[] body)
    {
        var lines = head.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var status = lines.Length > 0 ? lines[0] : "";
        var parts = status.Split(' ', 3);
        var code = parts.Length > 1 && int.TryParse(parts[1], out var c) ? c : 0;
        var text = parts.Length > 2 ? parts[2] : "";

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0)
            {
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        return new RtspResponse(code, text, headers, body);
    }

    /// <summary>
    /// POSTs to an AirPlay 2 HTTP-style endpoint (<c>/pair-setup</c>, <c>/info</c>) over the
    /// same connection. These use absolute paths as the request URI, not an rtsp:// target.
    /// </summary>
    public Task<RtspResponse> PostAsync(string path, string contentType, byte[] body, IReadOnlyDictionary<string, string>? headers = null, CancellationToken ct = default)
        => SendAsync("POST", path, headers, contentType, body, ct);

    /// <summary>Ceilings on what a peer can make us buffer - every byte here is its choice, not ours.</summary>
    private const int MaxHeadBytes = 16 * 1024;
    private const int MaxBodyBytes = 1 << 20;

    private static async Task<RtspResponse> ReadResponseAsync(Stream stream, CancellationToken ct)
    {
        // Read to the blank line that ends the headers, then exactly Content-Length more.
        var head = new List<byte>(512);
        var one = new byte[1];
        while (true)
        {
            var read = stream.Read(one, 0, 1);
            if (read == 0)
            {
                throw new IOException("RTSP connection closed mid-response.");
            }

            head.Add(one[0]);
            var n = head.Count;
            if (n >= 4 && head[n - 4] == '\r' && head[n - 3] == '\n' && head[n - 2] == '\r' && head[n - 1] == '\n')
            {
                break;
            }

            if (n > MaxHeadBytes)
            {
                throw new IOException($"RTSP header exceeds {MaxHeadBytes} bytes.");
            }
        }

        var headText = Encoding.ASCII.GetString(head.ToArray());
        var parsed = ParseResponse(headText, []);

        var length = int.TryParse(parsed.Header("Content-Length"), out var len) ? len : 0;
        if (length <= 0)
        {
            return parsed;
        }

        if (length > MaxBodyBytes)
        {
            throw new IOException($"RTSP body of {length} bytes exceeds the {MaxBodyBytes} byte limit.");
        }

        var bodyBytes = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(bodyBytes.AsMemory(offset), ct);
            if (read == 0)
            {
                throw new IOException("RTSP connection closed mid-body.");
            }
            offset += read;
        }

        return parsed with { BodyBytes = bodyBytes };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _stream?.Dispose();
        _tcp.Dispose();
        _gate.Dispose();
    }
}
