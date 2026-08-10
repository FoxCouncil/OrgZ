// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Net.Sockets;
using System.Text;
using Serilog;

namespace OrgZ.Services.AudioOutput.AirPlay;

/// <summary>One parsed RTSP response: status line, headers, and the (usually empty) body.</summary>
internal sealed record RtspResponse(int StatusCode, string StatusText, Dictionary<string, string> Headers, string Body)
{
    public bool IsSuccess => StatusCode is >= 200 and < 300;

    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
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
    private NetworkStream? _stream;
    private int _cSeq;
    private bool _disposed;

    /// <summary>Headers sent on every request - identity the receiver logs and keys sessions on.</summary>
    public Dictionary<string, string> DefaultHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? SessionId { get; private set; }

    /// <summary>Our address as the receiver sees it - the SDP's origin line needs it.</summary>
    public string LocalAddress { get; private set; } = "0.0.0.0";

    public async Task ConnectAsync(string host, int port, CancellationToken ct)
    {
        await _tcp.ConnectAsync(host, port, ct);
        _stream = _tcp.GetStream();
        if (_tcp.Client.LocalEndPoint is System.Net.IPEndPoint local)
        {
            LocalAddress = local.Address.ToString();
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

        var request = BuildRequest(++_cSeq, method, uri, DefaultHeaders, headers, contentType, body, SessionId);
        await _stream.WriteAsync(request, ct);
        await _stream.FlushAsync(ct);

        var response = await ReadResponseAsync(_stream, ct);
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

    private static async Task<RtspResponse> ReadResponseAsync(NetworkStream stream, CancellationToken ct)
    {
        // Read to the blank line that ends the headers, then exactly Content-Length more.
        var head = new List<byte>(512);
        var one = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(one, ct);
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
        }

        var headText = Encoding.ASCII.GetString(head.ToArray());
        var parsed = ParseResponse(headText, "");

        var length = int.TryParse(parsed.Header("Content-Length"), out var len) ? len : 0;
        if (length <= 0)
        {
            return parsed;
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

        return parsed with { Body = Encoding.UTF8.GetString(bodyBytes) };
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
    }
}
