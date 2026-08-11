// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Serilog;

namespace OrgZ.Services.AudioOutput.AirPlay;

/// <summary>
/// A live AirPlay 2 stream: transient pairing, then a plist-negotiated SETUP, then ALAC
/// frames sealed with ChaCha20-Poly1305 over UDP.
///
/// Differs from <see cref="RaopSession"/> in the negotiation, not the audio: the ALAC
/// framing is identical (<see cref="RaopAlac"/>), but the transport is negotiated with
/// binary plists instead of Transport headers, and every audio frame is individually
/// sealed rather than AES-CBC'd.
/// </summary>
internal sealed class AirPlay2Session : IDisposable
{
    private static readonly ILogger _log = Logging.For("AirPlay2");

    private const int SampleRate = 44100;
    private const uint LatencyFrames = 88200;

    /// <summary>Frames per audio packet - 352 is what every AirPlay receiver expects.</summary>
    internal const int FramesPerPacket = 352;

    /// <summary>Raw PCM 44.1 kHz 16-bit stereo, as the stream SETUP advertises it.</summary>
    private const long AudioFormatPcm441 = 0x800;

    /// <summary>A sender identity has to look like a MAC address; the receiver only keys off it.</summary>
    private static readonly string DeviceId = "AA:BB:CC:DD:EE:FF";

    private System.Net.Sockets.UdpClient? _timing;
    private int _timingPort;
    private System.Net.Sockets.UdpClient? _control;
    private int _controlPort;
    private IPEndPoint? _controlEndpoint;
    private CancellationTokenSource? _servers;
    private readonly long _streamConnectionId = Random.Shared.NextInt64(1, long.MaxValue);

    private readonly string _host;
    private readonly int _rtspPort;
    private readonly AirPlay2Pairing _pairing;

    private RtspClient? _rtsp;
    private AirPlay2Cipher? _cipher;
    private UdpClient? _audio;
    private IPEndPoint? _audioEndpoint;

    /// <summary>
    /// The RTP SSRC is the RTSP session id, NOT a random value - the receiver ties the
    /// audio stream to the session it negotiated by this field, and a random one leaves it
    /// with packets it can't attribute to any session.
    /// </summary>
    private uint _ssrc;
    private ushort _sequence;
    private uint _timestamp;
    private bool _first = true;
    private DateTime _streamStart;
    private uint _framesSent;

    public AirPlay2Session(string host, int rtspPort, string? password = null)
    {
        _host = host;
        _rtspPort = rtspPort;
        _pairing = new AirPlay2Pairing(password);
    }

    /// <summary>True when the receiver refused our password - the caller should ask for another.</summary>
    public bool PasswordRejected => _pairing.PasswordRejected;

    public bool IsConnected { get; private set; }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        var session = (uint)Random.Shared.Next(1, int.MaxValue);
        var sessionId = session.ToString();
        _ssrc = session;

        _rtsp = new RtspClient();
        await _rtsp.ConnectAsync(_host, _rtspPort, ct);

        var instance = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToUpperInvariant();
        _rtsp.DefaultHeaders["User-Agent"] = "AirPlay/550.10";
        _rtsp.DefaultHeaders["Client-Instance"] = instance;
        _rtsp.DefaultHeaders["DACP-ID"] = instance;
        _rtsp.DefaultHeaders["Active-Remote"] = Random.Shared.Next(1, int.MaxValue).ToString();

        // /info goes FIRST, as a GET, before pairing. Order matters more than it looks:
        // asking for it AFTER pair-setup makes a HomePod drop the connection outright
        // ("closed mid-response"), which is what kept this from ever reaching SETUP.
        var info = await _rtsp.SendAsync("GET", "/info", ct: ct);
        if (!info.IsSuccess)
        {
            _log.Debug("AirPlay 2 /info returned {Status} - continuing to pairing", info.StatusCode);
        }

        // Then pairing: until this completes the receiver answers everything with 401.
        await _pairing.PairAsync(_rtsp, ct);
        _cipher = new AirPlay2Cipher(_pairing.AudioKey);

        // The receiver drives the clock exchange, so our timing socket has to exist before
        // SETUP announces its port.
        StartTimingServer();

        // Session SETUP, then the stream SETUP that carries the audio key.
        //
        // This body is deliberately verbose. A HomePod refuses a sparse one - it wants a
        // sender identity and a timing arrangement it recognises, and answers anything
        // less by accepting SETUP and then never playing a note.
        var setupBody = BinaryPlist.Write(new Dictionary<string, object?>
        {
            ["deviceID"] = DeviceId,
            ["macAddress"] = DeviceId,
            ["sessionUUID"] = Guid.NewGuid().ToString().ToUpperInvariant(),
            ["timingPort"] = (long)_timingPort,
            ["timingProtocol"] = "NTP",
            ["isMultiSelectAirPlay"] = true,
            ["groupContainsGroupLeader"] = false,
            ["senderSupportsRelay"] = false,
            ["statsCollectionEnabled"] = false,
            ["model"] = "iPhone14,3",
            ["name"] = Environment.MachineName,
            ["osName"] = "iPhone OS",
            ["osVersion"] = "16.5",
            ["osBuildVersion"] = "20F66",
            ["sourceVersion"] = "690.7.1",
        });

        var setup = await _rtsp.SendAsync("SETUP", $"rtsp://{_rtsp.LocalAddress}/{sessionId}",
            contentType: "application/x-apple-binary-plist", body: setupBody, ct: ct);
        if (!setup.IsSuccess)
        {
            throw new InvalidOperationException($"AirPlay 2 SETUP refused ({setup.StatusCode} {setup.StatusText}).");
        }

        // The session SETUP answers with an event port the receiver expects a sender to
        // connect to. We never read from it - holding the socket open is what matters.
        if (ExtractStreamPort(setup.BodyBytes, "eventPort") is { } eventPort)
        {
            await OpenEventChannelAsync(eventPort, ct);
        }

        StartControlClient();

        var streamBody = BinaryPlist.Write(new Dictionary<string, object?>
        {
            ["streams"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["type"] = 96L,                        // realtime audio
                    ["audioFormat"] = AudioFormatPcm441,
                    ["audioMode"] = "default",
                    // ct=1 is RAW PCM. AirPlay 2 does carry ALAC, but the realtime path a
                    // HomePod actually accepts from a third-party sender is uncompressed -
                    // announcing ALAC here yields a session that sets up and stays silent.
                    ["ct"] = 1L,
                    ["sr"] = (long)SampleRate,
                    ["spf"] = (long)FramesPerPacket,
                    ["controlPort"] = (long)_controlPort,
                    ["streamConnectionID"] = _streamConnectionId,
                    ["supportsDynamicStreamID"] = false,
                    // The audio key goes over verbatim - the receiver seals/unseals with it.
                    ["shk"] = _pairing.AudioKey,
                    ["isMedia"] = true,
                    ["latencyMin"] = 11025L,
                    ["latencyMax"] = 88200L,
                },
            },
        });

        var streamSetup = await _rtsp.SendAsync("SETUP", $"rtsp://{_rtsp.LocalAddress}/{sessionId}",
            contentType: "application/x-apple-binary-plist", body: streamBody, ct: ct);
        if (!streamSetup.IsSuccess)
        {
            throw new InvalidOperationException($"AirPlay 2 stream SETUP refused ({streamSetup.StatusCode} {streamSetup.StatusText}).");
        }

        var dataPort = ExtractDataPort(streamSetup.BodyBytes)
            ?? throw new InvalidOperationException("AirPlay 2 stream SETUP returned no dataPort.");

        var address = (await Dns.GetHostAddressesAsync(_host, ct)).First(a => a.AddressFamily == AddressFamily.InterNetwork);
        _audio = new UdpClient(0, AddressFamily.InterNetwork);
        _audioEndpoint = new IPEndPoint(address, dataPort);

        // The receiver answers with its own control port; sync goes there, not to the one
        // we asked for.
        if (ExtractStreamPort(streamSetup.BodyBytes, "controlPort") is { } receiverControlPort)
        {
            _controlEndpoint = new IPEndPoint(address, receiverControlPort);
        }

        _sequence = (ushort)Random.Shared.Next(ushort.MaxValue);

        // Start 1.5s into the timeline, as the reference sender does - a stream that starts
        // at timestamp 0 has no room for the receiver's own buffering.
        _timestamp = SampleRate + (SampleRate / 2);
        StartSyncLoop();

        // The receiver wants to know where the timeline starts before RECORD.
        await _rtsp.SendAsync("SET_PARAMETER", $"rtsp://{_rtsp.LocalAddress}/{sessionId}",
            contentType: "text/parameters",
            body: System.Text.Encoding.ASCII.GetBytes($"progress: {_timestamp}/{_timestamp}/{_timestamp + (SampleRate * 60)}\r\n"), ct: ct);

        // Volume before RECORD, exactly as a working sender does it - a receiver that
        // starts at its own level can be silent for reasons that have nothing to do with
        // the stream.
        await SetVolumeAsync(1f, ct);

        // RECORD carries no Range/RTP-Info here: the reference sender sends it bare, and
        // this is a realtime stream rather than a seekable one.
        var record = await _rtsp.SendAsync("RECORD", $"rtsp://{_rtsp.LocalAddress}/{sessionId}", ct: ct);
        if (!record.IsSuccess)
        {
            throw new InvalidOperationException($"AirPlay 2 RECORD refused ({record.StatusCode} {record.StatusText}).");
        }

        StartFeedbackLoop(sessionId);

        // FLUSH after RECORD, carrying the RTP timeline. The reference sender does this to
        // tell the receiver where the audio it's about to get begins; without it a receiver
        // can hold a live session and still play nothing.
        await _rtsp.SendAsync("FLUSH", $"rtsp://{_rtsp.LocalAddress}/{sessionId}", new Dictionary<string, string>
        {
            ["Range"] = "npt=0-",
            ["RTP-Info"] = $"seq={_sequence};rtptime={_timestamp}",
        }, ct: ct);

        _streamStart = DateTime.UtcNow;
        _framesSent = 0;
        IsConnected = true;
        _log.Information("AirPlay 2 session up: {Host} audio->{Port}", _host, dataPort);
    }

    private System.Net.Sockets.TcpClient? _events;

    /// <summary>
    /// Connects to the event port the receiver hands back from SETUP.
    ///
    /// The channel carries receiver-to-sender events we have no use for, and its payloads
    /// are encrypted with separately derived keys - so nothing is read here. A working
    /// sender does establish it though, and holding the socket open is cheap insurance
    /// against a receiver that treats its absence as the sender having gone away.
    /// </summary>
    private async Task OpenEventChannelAsync(int eventPort, CancellationToken ct)
    {
        try
        {
            _events = new System.Net.Sockets.TcpClient();
            await _events.ConnectAsync(_host, eventPort, ct);
            _log.Debug("AirPlay 2 event channel open on {Port}", eventPort);
        }
        catch (Exception ex)
        {
            // Not fatal - streaming may well work without it.
            _log.Debug(ex, "AirPlay 2 event channel connect failed on {Port}", eventPort);
            _events?.Dispose();
            _events = null;
        }
    }

    /// <summary>
    /// Posts /feedback on a heartbeat. A receiver uses it to tell that the sender is still
    /// alive; letting it lapse is a way to have a stream quietly stop.
    /// </summary>
    private void StartFeedbackLoop(string sessionId)
    {
        if (_rtsp is null || _servers is null)
        {
            return;
        }

        var token = _servers.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), token);
                    await _rtsp.SendAsync("POST", "/feedback", ct: token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // Best effort, exactly as the reference sender treats it.
                    _log.Debug(ex, "AirPlay 2 feedback failed");
                    return;
                }
            }
        }, token);
    }

    /// <summary>
    /// Answers the receiver's NTP clock queries.
    ///
    /// Required, not optional: SETUP advertises timingProtocol=NTP, and a receiver that
    /// can't establish the offset between the two clocks won't start a realtime stream. It
    /// asks; we reply with the three-stamp exchange and it works out the skew.
    /// </summary>
    private void StartTimingServer()
    {
        _timing = new UdpClient(0, AddressFamily.InterNetwork);
        _timingPort = ((IPEndPoint)_timing.Client.LocalEndPoint!).Port;
        _servers ??= new CancellationTokenSource();

        var token = _servers.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var query = await _timing.ReceiveAsync(token);
                    var received = RaopPackets.NtpNow();
                    if (RaopPackets.IsTimingRequest(query.Buffer))
                    {
                        var reply = RaopPackets.BuildTimingReply(query.Buffer, received, RaopPackets.NtpNow());
                        await _timing.SendAsync(reply, reply.Length, query.RemoteEndPoint);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _log.Debug(ex, "AirPlay 2 timing exchange failed");
                    return;
                }
            }
        }, token);
    }

    /// <summary>
    /// Opens the control socket and starts the sync heartbeat once the receiver's control
    /// port is known. Sync packets tie an NTP instant to the RTP timestamp playing at it,
    /// which is how the receiver keeps from drifting away from our clock.
    /// </summary>
    private void StartControlClient()
    {
        _control = new UdpClient(0, AddressFamily.InterNetwork);
        _controlPort = ((IPEndPoint)_control.Client.LocalEndPoint!).Port;
    }

    private void StartSyncLoop()
    {
        if (_control is null || _controlEndpoint is null || _servers is null)
        {
            return;
        }

        var token = _servers.Token;
        _ = Task.Run(async () =>
        {
            var first = true;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var now = _timestamp + _framesSent;
                    var packet = RaopPackets.BuildSync(now, RaopPackets.NtpNow(), now + LatencyFrames, first);
                    await _control.SendAsync(packet, packet.Length, _controlEndpoint);
                    first = false;
                    await Task.Delay(TimeSpan.FromSeconds(1), token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _log.Debug(ex, "AirPlay 2 sync send failed");
                    return;
                }
            }
        }, token);
    }

    /// <summary>Swaps 16-bit sample byte order, LE (what the bus produces) to BE (what RTP carries).</summary>
    internal static byte[] ToBigEndian(ReadOnlySpan<byte> littleEndian)
    {
        var swapped = new byte[littleEndian.Length];
        for (var i = 0; i + 1 < littleEndian.Length; i += 2)
        {
            swapped[i] = littleEndian[i + 1];
            swapped[i + 1] = littleEndian[i];
        }
        return swapped;
    }

    /// <summary>Digs a named port out of the SETUP reply's streams array.</summary>
    internal static int? ExtractStreamPort(byte[] plist, string name)
    {
        try
        {
            if (BinaryPlist.Read(plist) is not Dictionary<string, object?> root)
            {
                return null;
            }

            if (root.TryGetValue("streams", out var streamsValue) && streamsValue is List<object?> streams)
            {
                foreach (var entry in streams)
                {
                    if (entry is Dictionary<string, object?> stream && stream.TryGetValue(name, out var port) && port is long p and > 0)
                    {
                        return (int)p;
                    }
                }
            }

            return root.TryGetValue(name, out var direct) && direct is long d and > 0 ? (int)d : null;
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "AirPlay 2 SETUP reply was not a readable plist");
            return null;
        }
    }

    /// <summary>Digs dataPort out of the SETUP reply's streams array.</summary>
    internal static int? ExtractDataPort(byte[] plist)
    {
        try
        {
            if (BinaryPlist.Read(plist) is not Dictionary<string, object?> root)
            {
                return null;
            }

            if (root.TryGetValue("streams", out var streamsValue) && streamsValue is List<object?> streams)
            {
                foreach (var entry in streams)
                {
                    if (entry is Dictionary<string, object?> stream && stream.TryGetValue("dataPort", out var port) && port is long p and > 0)
                    {
                        return (int)p;
                    }
                }
            }

            // Some receivers answer with the port at the top level instead.
            return root.TryGetValue("dataPort", out var direct) && direct is long d and > 0 ? (int)d : null;
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "AirPlay 2 SETUP reply was not a readable plist");
            return null;
        }
    }

    /// <summary>Sends one 352-frame packet, sealed and paced against the stream clock.</summary>
    public async Task SendPacketAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct)
    {
        if (!IsConnected || _audio is null || _audioEndpoint is null || _cipher is null)
        {
            return;
        }

        // The header is built first because its timestamp+ssrc words authenticate the
        // payload as AAD - the receiver checks them, so they can't be filled in afterwards.
        var header = RaopPackets.BuildAudio(_sequence, _timestamp + LatencyFrames, _ssrc, [], _first);

        // Raw LITTLE-endian PCM, sent exactly as the bus produced it. The reference sender
        // hands its source's frames to the socket untouched - there is no byte swap here,
        // despite RTP payloads usually being big-endian.
        //
        // The sealed payload carries its own nonce on the end, so the packet is
        // header + ciphertext + tag + nonce.
        var body = _cipher.SealAudio(pcm.Span, header.AsSpan(4, 8));

        var packet = new byte[header.Length + body.Length];
        header.CopyTo(packet, 0);
        body.CopyTo(packet, header.Length);

        _first = false;
        _sequence++;
        _timestamp += FramesPerPacket;
        _framesSent += FramesPerPacket;

        await _audio.SendAsync(packet, packet.Length, _audioEndpoint);

        var due = _streamStart.AddSeconds((double)_framesSent / SampleRate);
        var wait = due - DateTime.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, ct);
        }
    }

    public async Task SetVolumeAsync(float linear, CancellationToken ct = default)
    {
        // Deliberately NOT gated on IsConnected: the initial volume is set during setup,
        // before the session is marked up.
        if (_rtsp is null)
        {
            return;
        }

        var db = linear <= 0.001f ? -144f : -30f + (Math.Clamp(linear, 0f, 1f) * 30f);
        try
        {
            await _rtsp.SendAsync("SET_PARAMETER", $"rtsp://{_rtsp.LocalAddress}/stream", contentType: "text/parameters",
                body: System.Text.Encoding.ASCII.GetBytes($"volume: {db.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture)}\r\n"), ct: ct);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "AirPlay 2 volume set failed");
        }
    }

    private bool _disposed;

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
            _servers?.Cancel();
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "AirPlay 2 server shutdown failed");
        }

        try
        {
            if (_rtsp is not null && _rtsp.SessionId is not null)
            {
                _rtsp.SendAsync("TEARDOWN", $"rtsp://{_rtsp.LocalAddress}/stream").Wait(TimeSpan.FromSeconds(2));
            }
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "AirPlay 2 teardown failed");
        }

        _cipher?.Dispose();
        _audio?.Dispose();
        _timing?.Dispose();
        _control?.Dispose();
        _events?.Dispose();
        _servers?.Dispose();
        _rtsp?.Dispose();
    }
}
