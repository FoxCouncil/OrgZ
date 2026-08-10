// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Serilog;

namespace OrgZ.Services.AudioOutput.AirPlay;

/// <summary>
/// One live RAOP stream to a receiver: the RTSP handshake, the three UDP ports
/// (audio / control / timing), the sync + timing chatter that keeps the receiver's clock
/// aligned, and the paced send loop that drips 352-frame packets out in real time.
///
/// Pacing matters: a receiver buffers roughly two seconds and drops anything that arrives
/// past its play point, so packets go out on a wall clock rather than as fast as the
/// caller writes. The caller (the sink) hands over PCM; this class owns the timing.
/// </summary>
internal sealed class RaopSession : IDisposable
{
    private static readonly ILogger _log = Logging.For("Raop");

    // The receiver plays ~2s behind the sender; audio timestamps are offset by this so a
    // packet arrives before its play instant. 44100 * 2 is what iTunes itself used.
    private const uint LatencyFrames = 88200;
    private const int SampleRate = 44100;

    private readonly string _host;
    private readonly int _rtspPort;
    private readonly RaopCrypto _crypto = new();

    private RtspClient? _rtsp;
    private UdpClient? _audio;
    private UdpClient? _control;
    private UdpClient? _timing;
    private IPEndPoint? _audioEndpoint;
    private IPEndPoint? _controlEndpoint;

    private readonly uint _ssrc = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4));
    private ushort _sequence;
    private uint _timestamp;
    private bool _first = true;
    private DateTime _streamStart;
    private uint _framesSent;

    private CancellationTokenSource? _cts;
    private Task? _timingLoop;
    private Task? _syncLoop;

    public RaopSession(string host, int rtspPort)
    {
        _host = host;
        _rtspPort = rtspPort;
    }

    public bool IsConnected { get; private set; }

    /// <summary>
    /// Runs the full handshake. Throws on refusal - callers surface that rather than
    /// pretending the stream is live (the whole point of not shipping a silent placeholder).
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        var sessionId = Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString();

        _rtsp = new RtspClient();
        await _rtsp.ConnectAsync(_host, _rtspPort, ct);

        _rtsp.DefaultHeaders["User-Agent"] = "iTunes/7.6.2 (Windows; N;)";
        _rtsp.DefaultHeaders["Client-Instance"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        _rtsp.DefaultHeaders["DACP-ID"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToUpperInvariant();
        _rtsp.DefaultHeaders["Active-Remote"] = Random.Shared.Next(1, int.MaxValue).ToString();

        var local = _rtsp.LocalAddress;
        var uri = $"rtsp://{local}/{sessionId}";

        var options = await _rtsp.SendAsync("OPTIONS", "*", ct: ct);
        if (!options.IsSuccess)
        {
            throw new InvalidOperationException($"AirPlay receiver refused OPTIONS ({options.StatusCode} {options.StatusText}).");
        }

        var sdp = BuildSdp(sessionId, local, _host, _crypto);
        var announce = await _rtsp.SendAsync("ANNOUNCE", uri, contentType: "application/sdp", body: Encoding.UTF8.GetBytes(sdp), ct: ct);
        if (!announce.IsSuccess)
        {
            // 453 = another sender owns the receiver; 401 = it wants a password we don't support yet.
            throw new InvalidOperationException(announce.StatusCode switch
            {
                453 => "That AirPlay receiver is already in use by another sender.",
                401 => "That AirPlay receiver requires a password, which OrgZ can't supply yet.",
                _ => $"AirPlay receiver refused ANNOUNCE ({announce.StatusCode} {announce.StatusText}).",
            });
        }

        // Bind our three ports BEFORE SETUP - the receiver is told them in the Transport header.
        _control = new UdpClient(0, AddressFamily.InterNetwork);
        _timing = new UdpClient(0, AddressFamily.InterNetwork);
        _audio = new UdpClient(0, AddressFamily.InterNetwork);
        var controlPort = ((IPEndPoint)_control.Client.LocalEndPoint!).Port;
        var timingPort = ((IPEndPoint)_timing.Client.LocalEndPoint!).Port;

        var setup = await _rtsp.SendAsync("SETUP", uri, new Dictionary<string, string>
        {
            ["Transport"] = $"RTP/AVP/UDP;unicast;interleaved=0-1;mode=record;control_port={controlPort};timing_port={timingPort}",
        }, ct: ct);
        if (!setup.IsSuccess)
        {
            throw new InvalidOperationException($"AirPlay receiver refused SETUP ({setup.StatusCode} {setup.StatusText}).");
        }

        var ports = ParseTransportPorts(setup.Header("Transport") ?? "");
        if (ports.Server == 0)
        {
            throw new InvalidOperationException("AirPlay receiver returned no server_port in SETUP.");
        }

        var address = (await Dns.GetHostAddressesAsync(_host, ct)).First(a => a.AddressFamily == AddressFamily.InterNetwork);
        _audioEndpoint = new IPEndPoint(address, ports.Server);
        _controlEndpoint = new IPEndPoint(address, ports.Control != 0 ? ports.Control : ports.Server);

        _sequence = (ushort)Random.Shared.Next(ushort.MaxValue);
        _timestamp = (uint)Random.Shared.Next();

        var record = await _rtsp.SendAsync("RECORD", uri, new Dictionary<string, string>
        {
            ["Range"] = "npt=0-",
            ["RTP-Info"] = $"seq={_sequence};rtptime={_timestamp}",
        }, ct: ct);
        if (!record.IsSuccess)
        {
            throw new InvalidOperationException($"AirPlay receiver refused RECORD ({record.StatusCode} {record.StatusText}).");
        }

        _streamStart = DateTime.UtcNow;
        _framesSent = 0;
        IsConnected = true;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _timingLoop = Task.Run(() => ServeTimingAsync(_cts.Token), CancellationToken.None);
        _syncLoop = Task.Run(() => SendSyncAsync(_cts.Token), CancellationToken.None);

        _log.Information("AirPlay session up: {Host}:{Port} audio->{Audio} control->{Control}", _host, _rtspPort, ports.Server, ports.Control);
    }

    /// <summary>
    /// Sends one 352-frame packet, blocking until its scheduled wall-clock instant so the
    /// receiver's buffer neither starves nor overruns.
    /// </summary>
    public async Task SendPacketAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct)
    {
        if (!IsConnected || _audio is null || _audioEndpoint is null)
        {
            return;
        }

        var alac = RaopAlac.Encode(pcm.Span);
        _crypto.EncryptPayload(alac);

        var packet = RaopPackets.BuildAudio(_sequence, _timestamp + LatencyFrames, _ssrc, alac, _first);
        _first = false;
        _sequence++;
        _timestamp += RaopAlac.FramesPerPacket;
        _framesSent += RaopAlac.FramesPerPacket;

        await _audio.SendAsync(packet, packet.Length, _audioEndpoint);

        // Pace against the stream's own clock rather than sleeping a fixed interval, so
        // scheduling jitter can't accumulate into drift over a long track.
        var due = _streamStart.AddSeconds((double)_framesSent / SampleRate);
        var wait = due - DateTime.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, ct);
        }
    }

    /// <summary>Volume as AirPlay wants it: -30..0 dB, or -144 for mute.</summary>
    public async Task SetVolumeAsync(float linear, CancellationToken ct = default)
    {
        if (_rtsp is null || !IsConnected)
        {
            return;
        }

        var db = linear <= 0.001f ? -144f : -30f + (Math.Clamp(linear, 0f, 1f) * 30f);
        var body = Encoding.ASCII.GetBytes($"volume: {db.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture)}\r\n");
        try
        {
            await _rtsp.SendAsync("SET_PARAMETER", $"rtsp://{_rtsp.LocalAddress}/stream", contentType: "text/parameters", body: body, ct: ct);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "AirPlay volume set failed");
        }
    }

    /// <summary>Tells the receiver to drop what it has buffered (seek / stop).</summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        if (_rtsp is null || !IsConnected)
        {
            return;
        }

        try
        {
            await _rtsp.SendAsync("FLUSH", $"rtsp://{_rtsp.LocalAddress}/stream", new Dictionary<string, string>
            {
                ["RTP-Info"] = $"seq={_sequence};rtptime={_timestamp}",
            }, ct: ct);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "AirPlay flush failed");
        }
    }

    /// <summary>Answers the receiver's clock queries for as long as the stream is up.</summary>
    private async Task ServeTimingAsync(CancellationToken ct)
    {
        if (_timing is null)
        {
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _timing.ReceiveAsync(ct);
                if (!RaopPackets.IsTimingRequest(result.Buffer))
                {
                    continue;
                }

                var received = RaopPackets.NtpNow();
                var reply = RaopPackets.BuildTimingReply(result.Buffer, received, RaopPackets.NtpNow());
                await _timing.SendAsync(reply, reply.Length, result.RemoteEndPoint);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "AirPlay timing exchange failed");
                return;
            }
        }
    }

    /// <summary>Sync packets: one immediately, then ~1/s, keeping the receiver's clock tied to ours.</summary>
    private async Task SendSyncAsync(CancellationToken ct)
    {
        var first = true;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_control is not null && _controlEndpoint is not null)
                {
                    var now = _timestamp;
                    var packet = RaopPackets.BuildSync(now, RaopPackets.NtpNow(), now + LatencyFrames, first);
                    await _control.SendAsync(packet, packet.Length, _controlEndpoint);
                    first = false;
                }

                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "AirPlay sync send failed");
                return;
            }
        }
    }

    /// <summary>The ANNOUNCE SDP: session identity plus the ALAC format and the wrapped AES key.</summary>
    internal static string BuildSdp(string sessionId, string localIp, string peerIp, RaopCrypto crypto) =>
        $"v=0\r\n" +
        $"o=iTunes {sessionId} 0 IN IP4 {localIp}\r\n" +
        $"s=iTunes\r\n" +
        $"c=IN IP4 {peerIp}\r\n" +
        $"t=0 0\r\n" +
        $"m=audio 0 RTP/AVP 96\r\n" +
        $"a=rtpmap:96 AppleLossless\r\n" +
        // frames-per-packet, then the fixed ALAC config iTunes advertises, then the rate.
        $"a=fmtp:96 {RaopAlac.FramesPerPacket} 0 16 40 10 14 2 255 0 0 {SampleRate}\r\n" +
        $"a=rsaaeskey:{crypto.EncryptedKeyBase64()}\r\n" +
        $"a=aesiv:{crypto.IvBase64}\r\n";

    /// <summary>Pulls server_port / control_port / timing_port out of SETUP's Transport header.</summary>
    internal static (int Server, int Control, int Timing) ParseTransportPorts(string transport)
    {
        int Find(string key)
        {
            foreach (var part in transport.Split(';'))
            {
                var kv = part.Split('=', 2);
                if (kv.Length == 2 && kv[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(kv[1].Trim(), out var value))
                {
                    return value;
                }
            }
            return 0;
        }

        return (Find("server_port"), Find("control_port"), Find("timing_port"));
    }

    private bool _disposed;

    /// <summary>Idempotent - the sink disposes defensively alongside the pump that owns the session.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        IsConnected = false;

        try
        {
            _cts?.Cancel();
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "AirPlay session cancel failed");
        }

        // Best-effort TEARDOWN so the receiver frees itself for the next sender instead of
        // staying "in use" until it times out.
        try
        {
            if (_rtsp is not null && _rtsp.SessionId is not null)
            {
                _rtsp.SendAsync("TEARDOWN", $"rtsp://{_rtsp.LocalAddress}/stream").Wait(TimeSpan.FromSeconds(2));
            }
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "AirPlay teardown failed");
        }

        try { _timingLoop?.Wait(TimeSpan.FromSeconds(1)); } catch (Exception ex) { _log.Debug(ex, "timing loop shutdown"); }
        try { _syncLoop?.Wait(TimeSpan.FromSeconds(1)); } catch (Exception ex) { _log.Debug(ex, "sync loop shutdown"); }

        _cts?.Dispose();
        _audio?.Dispose();
        _control?.Dispose();
        _timing?.Dispose();
        _rtsp?.Dispose();
    }
}
