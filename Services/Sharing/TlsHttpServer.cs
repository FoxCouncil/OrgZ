// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Serilog;

namespace OrgZ.Services.Sharing;

/// <summary>
/// A deliberately small HTTPS/1.1 server: TcpListener + SslStream + a hand-parsed
/// request head. Exists because HttpListener cannot speak TLS without a machine-level
/// http.sys certificate binding - and as a side effect, plain sockets need no URL ACL,
/// so an unelevated GUI can host a LAN-reachable share for the first time.
///
/// One request per connection (Connection: close). The share's traffic is a catalogue
/// fetch and long audio streams; connection reuse buys nothing worth the state machine.
/// </summary>
public sealed class TlsHttpServer : IDisposable
{
    private static readonly ILogger _log = Logging.For("TlsHttpServer");

    /// <summary>Request head cap - a client that sends more than this isn't speaking our HTTP.</summary>
    private const int MaxHeadBytes = 16 * 1024;

    private readonly TcpListener _listener;
    private readonly X509Certificate2 _certificate;
    private readonly Func<Request, Response, Task> _handler;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    public sealed record Request(string Method, string Path, IReadOnlyDictionary<string, string> Headers);

    /// <summary>
    /// The response surface the handler writes: mirrors the slice of HttpListener the
    /// share handlers used, so their logic ports without re-thinking.
    /// </summary>
    public sealed class Response(Stream stream)
    {
        private readonly Dictionary<string, string> _headers = new(StringComparer.OrdinalIgnoreCase);
        private bool _headSent;

        public int StatusCode { get; set; } = 200;
        public string? ContentType { get; set; }
        public long ContentLength64 { get; set; } = -1;

        public bool HeadersSent => _headSent;

        public void SetHeader(string name, string value) => _headers[name] = value;

        /// <summary>The body stream. Writing the first byte commits the head.</summary>
        public async Task WriteAsync(ReadOnlyMemory<byte> body, CancellationToken ct = default)
        {
            await EnsureHeadAsync(ct);
            await stream.WriteAsync(body, ct);
        }

        /// <summary>Finishes the response - sends the head even when there is no body.</summary>
        public async Task CloseAsync(CancellationToken ct = default)
        {
            await EnsureHeadAsync(ct);
            await stream.FlushAsync(ct);
        }

        private async Task EnsureHeadAsync(CancellationToken ct)
        {
            if (_headSent)
            {
                return;
            }
            _headSent = true;

            var sb = new StringBuilder(256);
            sb.Append("HTTP/1.1 ").Append(StatusCode).Append(' ').Append(ReasonFor(StatusCode)).Append("\r\n");
            if (ContentType is not null)
            {
                sb.Append("Content-Type: ").Append(ContentType).Append("\r\n");
            }
            if (ContentLength64 >= 0)
            {
                sb.Append("Content-Length: ").Append(ContentLength64).Append("\r\n");
            }
            foreach (var (name, value) in _headers)
            {
                sb.Append(name).Append(": ").Append(value).Append("\r\n");
            }
            sb.Append("Connection: close\r\n\r\n");

            await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct);
        }

        private static string ReasonFor(int status) => status switch
        {
            200 => "OK",
            206 => "Partial Content",
            404 => "Not Found",
            405 => "Method Not Allowed",
            500 => "Internal Server Error",
            _ => "Status",
        };
    }

    public TlsHttpServer(IPAddress bindAddress, int port, X509Certificate2 certificate, Func<Request, Response, Task> handler)
    {
        _listener = new TcpListener(bindAddress, port);
        _certificate = certificate;
        _handler = handler;
    }

    public void Start()
    {
        _listener.Start();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "share accept failed");
                continue;
            }

            _ = Task.Run(() => ServeConnectionAsync(client, ct), ct);
        }
    }

    private async Task ServeConnectionAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        client.NoDelay = true;

        try
        {
            await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);

            // One deadline covers the whole connection SETUP - handshake AND request head.
            // The head read used to run on the server-lifetime token only, so a peer that
            // completed the handshake and then sent nothing pinned a connection forever
            // (slowloris shape). Streaming after the head has no deadline by design -
            // audio streams are legitimately long.
            using var setupCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            setupCts.CancelAfter(TimeSpan.FromSeconds(10));
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _certificate,
                ClientCertificateRequired = false,
            }, setupCts.Token);

            if (await ReadRequestAsync(ssl, setupCts.Token) is not { } request)
            {
                return;   // not our HTTP (or too slow to say so) - drop without ceremony
            }

            var response = new Response(ssl);
            try
            {
                await _handler(request, response);
            }
            catch (Exception ex)
            {
                // The server owns the 500 path - handlers just throw. Warning, not Debug:
                // a handler that fails on EVERY request (as the LocalSystem empty-library
                // bug did) must not be invisible. Answering 500 makes the failure diagnose
                // as a server problem instead of a network one; mid-stream failures
                // (headers already sent) just drop the connection.
                _log.Warning(ex, "share request failed for {Path}", request.Path);
                if (!response.HeadersSent)
                {
                    response.StatusCode = 500;
                    response.ContentLength64 = 0;
                    await response.CloseAsync(ct);
                }
            }
        }
        catch (Exception ex)
        {
            // A failed handshake is every port-scanner and every non-TLS client; noise, not news.
            _log.Debug(ex, "share connection dropped");
        }
    }

    /// <summary>
    /// Reads and parses one request head. Null for anything that isn't well-formed
    /// HTTP within the size cap - hostile input gets a dropped connection, not a parse
    /// adventure. The body (which our routes never use) is left unread.
    /// </summary>
    internal static async Task<Request?> ReadRequestAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxHeadBytes];
        var filled = 0;
        var searchFrom = 3;
        int headEnd;

        while (true)
        {
            headEnd = FindHeadEnd(buffer.AsSpan(0, filled), searchFrom);
            if (headEnd >= 0)
            {
                break;
            }
            // Resume the scan where it left off (the terminator can span the read
            // boundary by at most 3 bytes) - a byte-at-a-time sender used to make the
            // accumulation O(N²) in comparisons.
            searchFrom = Math.Max(3, filled - 3);

            if (filled == buffer.Length)
            {
                return null;   // head larger than the cap
            }

            var read = await stream.ReadAsync(buffer.AsMemory(filled), ct);
            if (read <= 0)
            {
                return null;
            }
            filled += read;
        }

        return ParseHead(Encoding.ASCII.GetString(buffer, 0, headEnd));
    }

    private static int FindHeadEnd(ReadOnlySpan<byte> data, int from)
    {
        for (var i = Math.Max(3, from); i < data.Length; i++)
        {
            if (data[i] == (byte)'\n' && data[i - 1] == (byte)'\r' && data[i - 2] == (byte)'\n' && data[i - 3] == (byte)'\r')
            {
                return i - 3;
            }
        }
        return -1;
    }

    /// <summary>Pure head parsing: "METHOD /path HTTP/1.1" + "Name: value" lines.</summary>
    internal static Request? ParseHead(string head)
    {
        var lines = head.Split("\r\n");
        var start = lines[0].Split(' ');
        if (start.Length != 3 || !start[2].StartsWith("HTTP/1.", StringComparison.Ordinal) || start[1].Length == 0 || start[1][0] != '/')
        {
            return null;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        // Strip any query: the share's routes are path-only.
        var path = start[1];
        var query = path.IndexOf('?');
        if (query >= 0)
        {
            path = path[..query];
        }

        return new Request(start[0], path, headers);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* teardown */ }
        try { _acceptLoop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* cancelled */ }
        _cts.Dispose();
    }
}
