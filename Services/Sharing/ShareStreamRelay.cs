// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using Serilog;

namespace OrgZ.Services.Sharing;

/// <summary>
/// The client's loopback doorway to TLS shares: a tiny local HTTP server that forwards
/// each request to the remote share over pinned TLS. Everything that consumes share
/// URLs - libvlc most of all, which cannot be taught to accept a self-signed
/// certificate - talks plain HTTP to 127.0.0.1 and never learns TLS exists. One relay,
/// every mounted share, started on first use.
/// </summary>
public static class ShareStreamRelay
{
    private static readonly ILogger _log = Logging.For("ShareRelay");

    private sealed record Registration(string RemoteBase, string Pin, HttpClient Client);

    private static readonly ConcurrentDictionary<string, Registration> _shares = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock _startGate = new();
    private static HttpListener? _listener;
    private static int _port;

    /// <summary>Headers forwarded from the remote response - the ones streaming correctness rides on.</summary>
    private static readonly string[] _forwardedHeaders = ["Content-Range", "Accept-Ranges"];

    /// <summary>
    /// Registers a TLS share and returns the local base URL to use in its place
    /// (e.g. <c>http://127.0.0.1:52114/s/{token}</c>). Idempotent per share key.
    /// </summary>
    public static string Register(string shareKey, string remoteBaseUrl, string pin)
    {
        EnsureStarted();

        var token = TokenFor(shareKey);

        // A re-registration with a NEW pin replaces the old one: a share whose
        // certificate rotated (ephemeral-cert fallback) announces a new pin, and
        // holding the stale one would refuse a legitimate host forever.
        if (_shares.TryGetValue(token, out var existing) && existing.Pin != pin && _shares.TryRemove(token, out var stale))
        {
            stale.Client.Dispose();
        }

        _shares.GetOrAdd(token, _ =>
        {
            var handler = new SocketsHttpHandler();
            handler.SslOptions.RemoteCertificateValidationCallback = (_, cert, _, _) =>
                cert is not null && Convert.ToBase64String(SHA256.HashData(cert.GetRawCertData())) == pin;
            return new Registration(remoteBaseUrl.TrimEnd('/'), pin, new HttpClient(handler));
        });

        return $"http://127.0.0.1:{_port}/s/{token}";
    }

    public static void Unregister(string shareKey)
    {
        if (_shares.TryRemove(TokenFor(shareKey), out var gone))
        {
            gone.Client.Dispose();
        }
    }

    /// <summary>The relay base for a registered share, or null when it isn't one (plain HTTP).</summary>
    public static string? ProxyBaseFor(string shareKey)
        => _listener is not null && _shares.ContainsKey(TokenFor(shareKey))
            ? $"http://127.0.0.1:{_port}/s/{TokenFor(shareKey)}"
            : null;

    /// <summary>A stable, URL-safe token for a share key (host:port contains ':').</summary>
    internal static string TokenFor(string shareKey)
        => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(shareKey)))[..16];

    private static void EnsureStarted()
    {
        lock (_startGate)
        {
            if (_listener is not null)
            {
                return;
            }

            // Loopback binding needs no URL ACL. Probe ephemeral-range ports until one
            // takes; the port only ever appears in URLs we mint this session.
            for (var attempt = 0; attempt < 20; attempt++)
            {
                var port = Random.Shared.Next(49500, 65000);
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                try
                {
                    listener.Start();
                }
                catch (HttpListenerException)
                {
                    continue;
                }

                _listener = listener;
                _port = port;
                _ = Task.Run(AcceptLoopAsync);
                _log.Information("Share relay listening on 127.0.0.1:{Port}", port);
                return;
            }

            throw new InvalidOperationException("share relay could not find a loopback port");
        }
    }

    private static async Task AcceptLoopAsync()
    {
        var listener = _listener!;
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception)
            {
                return;
            }

            _ = Task.Run(() => ForwardAsync(context));
        }
    }

    /// <summary>/s/{token}/{remote path...} → https://{share}/{remote path...} with the pin enforced.</summary>
    private static async Task ForwardAsync(HttpListenerContext context)
    {
        // Once a byte of body is out the door the status line is spent and a reset is the
        // only truth left to tell. Before that, a failure can still be answered as HTTP.
        var bodyStarted = false;

        try
        {
            // The relay is as read-only as the share: forwarding a POST as anything
            // would launder a mutating verb into a GET. Refuse it here, like the
            // server would.
            if (context.Request.HttpMethod is not ("GET" or "HEAD"))
            {
                context.Response.StatusCode = 405;
                context.Response.Close();
                return;
            }

            var segments = (context.Request.Url?.AbsolutePath ?? "/").Split('/', 4, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 3 || segments[0] != "s" || !_shares.TryGetValue(segments[1], out var share))
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }

            var remoteUrl = $"{share.RemoteBase}/{segments[2]}{(segments.Length > 3 ? "/" + segments[3] : string.Empty)}";
            using var upstream = new HttpRequestMessage(
                context.Request.HttpMethod == "HEAD" ? HttpMethod.Head : HttpMethod.Get, remoteUrl);
            if (context.Request.Headers["Range"] is { } range)
            {
                upstream.Headers.TryAddWithoutValidation("Range", range);
            }

            using var remote = await share.Client.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead);

            context.Response.StatusCode = (int)remote.StatusCode;
            if (remote.Content.Headers.ContentType is { } contentType)
            {
                context.Response.ContentType = contentType.ToString();
            }
            if (remote.Content.Headers.ContentLength is { } length)
            {
                context.Response.ContentLength64 = length;
            }
            foreach (var name in _forwardedHeaders)
            {
                if (remote.Content.Headers.TryGetValues(name, out var values) || remote.Headers.TryGetValues(name, out values))
                {
                    context.Response.Headers[name] = string.Join(",", values);
                }
            }

            if (context.Request.HttpMethod != "HEAD")
            {
                await using var body = await remote.Content.ReadAsStreamAsync();
                // Idle deadline per READ, not per stream: audio bodies are legitimately
                // hours long, but a healthy remote never goes 30s without producing a
                // byte. The bare CopyToAsync had no deadline at all (ResponseHeadersRead
                // scopes the client timeout to the HEADERS), so a stalled host held the
                // loopback connection open forever.
                var buffer = new byte[81920];
                while (true)
                {
                    int read;
                    using (var idle = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                    {
                        try
                        {
                            read = await body.ReadAsync(buffer, idle.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            throw new TimeoutException($"remote produced no bytes for 30s: {remoteUrl}");
                        }
                    }

                    if (read <= 0)
                    {
                        break;
                    }
                    bodyStarted = true;
                    await context.Response.OutputStream.WriteAsync(buffer.AsMemory(0, read));
                }
            }

            context.Response.Close();
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "relay forward failed");

            // Aborting is only a failure signal on Windows: HTTP.SYS resets the connection and
            // the client raises, while the managed listener on macOS/Linux lets the request
            // complete as a normal empty answer - so a refused pin, an unreachable host and a
            // stalled transfer all read to the caller as "the share is empty". Answer 502 while
            // the body hasn't started so the failure looks the same on every platform.
            if (!bodyStarted)
            {
                try
                {
                    context.Response.StatusCode = 502;
                    context.Response.StatusDescription = "Bad Gateway";
                    context.Response.ContentLength64 = 0;
                    context.Response.Close();
                    return;
                }
                catch (Exception)
                {
                    // Headers already on the wire (or the client left) - fall through to the reset.
                }
            }

            try { context.Response.Abort(); } catch { /* client gone */ }
        }
    }
}
