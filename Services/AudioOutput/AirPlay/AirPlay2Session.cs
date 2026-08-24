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

    /// <summary>
    /// How far ahead of the track's start the progress "display" value sits - the reference
    /// sender's startup delay, ~348ms at 44.1kHz.
    /// </summary>
    private const uint MetadataLeadInFrames = 15360;

    /// <summary>
    /// Largest cover we'll put on the control channel. Roughly what a 600x600 JPEG costs,
    /// which is the size the reference sender resizes to.
    /// </summary>
    private const int MaxArtworkBytes = 150_000;

    /// <summary>ALAC 44.1 kHz 16-bit stereo (ALAC_44100_16_2), as the stream SETUP advertises it.</summary>
    private const long AudioFormatAlac441 = 0x40000;

    /// <summary>
    /// This install's sender identity, as a locally-administered MAC derived from the
    /// persisted DACP id. The receiver files per-client session state - including the wreck
    /// of any session that died without a goodbye - under this value. A constant shared by
    /// every install ever meant every OrgZ arrived wearing the same identity as every
    /// abandoned session before it; an iPhone never sees that wall because it shows up under
    /// its own. Stable per install, unique per install, never a constant.
    /// </summary>
    private static string DeviceIdFrom(string dacpId)
    {
        var bytes = Convert.FromHexString(dacpId);
        bytes[0] = (byte)((bytes[0] | 0x02) & 0xFE);
        return string.Join(":", bytes.Take(6).Select(b => b.ToString("X2")));
    }

    private System.Net.Sockets.UdpClient? _timing;
    private int _timingPort;
    private System.Net.Sockets.UdpClient? _control;
    private int _controlPort;
    private IPEndPoint? _controlEndpoint;
    private CancellationTokenSource? _servers;
    private string? _sessionUri;
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


    /// <summary>Where the current track began on the RTP timeline.</summary>
    private uint _trackStartTimestamp;


    /// <summary>
    /// The RTP timestamp the audio being handed to the socket right now actually carries.
    ///
    /// NOT <see cref="_timestamp"/>: every audio packet is stamped <c>_timestamp +
    /// LatencyFrames</c>, so the bare counter names a point two seconds before anything in
    /// the stream. Metadata and progress have to sit on the same timeline as the audio they
    /// describe, or the receiver is being told about a moment the stream does not contain -
    /// and it drops it rather than guessing.
    /// </summary>
    private uint CurrentRtpTime => _timestamp + LatencyFrames;

    // What we last told the receiver, so a repeat announcement of the SAME track (artwork
    // arriving late) updates the display without disturbing the timeline.
    private string? _trackTitle;
    private string? _trackArtist;
    private string? _trackAlbum;

    /// <summary>
    /// How long the current track runs, in frames. Zero means "unknown", which is sent as an
    /// open-ended stream rather than as a guess - a receiver stops pulling audio when the
    /// timeline reaches the end we declare, so a wrong length is worse than none.
    /// </summary>
    private uint _trackDurationFrames;

    public AirPlay2Session(string host, int rtspPort, string? password = null)
    {
        _host = host;
        _rtspPort = rtspPort;
        _pairing = new AirPlay2Pairing(password);
    }

    /// <summary>True when the receiver refused our password - the caller should ask for another.</summary>
    public bool PasswordRejected => _pairing.PasswordRejected;

    /// <summary>The level the session opens at. Set before <see cref="ConnectAsync"/>.</summary>
    public float InitialVolume { get; set; } = 0.35f;

    /// <summary>
    /// Adopt the speaker's own level on connect instead of imposing ours. A speaker's
    /// volume belongs to whoever last set it; arriving with a number out of our settings
    /// overrides a deliberate choice someone made in the room.
    /// </summary>
    public bool AdoptReceiverVolume { get; set; } = true;

    /// <summary>
    /// Opens the session in a PAUSED state - for a speaker that has been selected but has no
    /// music yet.
    ///
    /// The stream still runs; it carries silence, which is what keeps the receiver's clock
    /// and buffer alive so the first real note is on time. What changes is what the receiver
    /// is TOLD: paused, so its display says paused rather than showing a track that isn't
    /// playing. <see cref="NotifyPlaybackStartedAsync"/> flips it when audio arrives.
    /// </summary>
    public bool StartPaused { get; set; }

    /// <summary>
    /// How far into the track the LISTENER is - not how far the sender has got.
    ///
    /// Those differ by the receiver's buffer. Audio for a given position goes out labelled
    /// <c>_timestamp + LatencyFrames</c>, and the sync packet tells the receiver that plain
    /// <c>_timestamp</c> is playing now, so what comes out of the speaker at this instant is
    /// what was sent <see cref="LatencyFrames"/> ago. Reporting the send position instead
    /// means the timeline is two seconds into a track that has not made a sound yet.
    ///
    /// The latency is already in the anchor: <see cref="_trackStartTimestamp"/> is a
    /// <see cref="CurrentRtpTime"/>, so the distance from it to the bare <see cref="_timestamp"/>
    /// IS the audible offset. Subtracting the latency a second time reports the track two
    /// seconds behind where the listener is. The difference is taken as a SIGNED modular one
    /// because both operands are a 32-bit sample counter: right after an anchor the send
    /// position is legitimately behind the track start, and an unsigned subtraction turns
    /// that into 27 hours.
    /// </summary>
    public TimeSpan? AudiblePosition
    {
        get
        {
            if (!IsConnected)
            {
                return null;
            }

            var ahead = unchecked((int)(_timestamp - _trackStartTimestamp));
            var audible = ahead > 0 ? (uint)ahead : 0u;
            return TimeSpan.FromSeconds((double)audible / SampleRate);
        }
    }

    public bool IsConnected { get; private set; }

    /// <summary>
    /// True once at least one audio packet has actually gone out.
    ///
    /// Now-playing information is anchored to an RTP timestamp, and a timestamp means nothing
    /// until the stream carrying it exists. Announcing a track into a stream that has not
    /// sent a byte describes a moment the receiver has no way to place.
    /// </summary>
    public bool HasSentAudio { get; private set; }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        var session = (uint)Random.Shared.Next(1, int.MaxValue);
        var sessionId = session.ToString();
        _ssrc = session;
        _sessionUri = null;

        _rtsp = new RtspClient();
        await _rtsp.ConnectAsync(_host, _rtspPort, ct);

        // DACP-ID and Active-Remote must match the control endpoint we actually publish -
        // they are how the receiver finds it. Randomising them here (as this did) meant the
        // receiver browsed mDNS for a name nothing answered to, which is precisely what
        // "Controls Not Available" is on the remote.
        var dacp = DacpControlServer.Instance;
        dacp.Command -= OnDacpCommand;
        dacp.Command += OnDacpCommand;
        dacp.VolumeChanged -= OnDacpVolume;
        dacp.VolumeChanged += OnDacpVolume;

        // The first session up owns the DACP button presses for the whole process - see
        // OnDacpCommand for why exactly one session may forward them.
        Interlocked.CompareExchange(ref _dacpOwner, this, null);

        _rtsp.DefaultHeaders["User-Agent"] = "AirPlay/550.10";
        _rtsp.DefaultHeaders["Client-Instance"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToUpperInvariant();
        _rtsp.DefaultHeaders["DACP-ID"] = dacp.DacpId;
        _rtsp.DefaultHeaders["Active-Remote"] = dacp.ActiveRemote;

        // /info goes FIRST, as a GET, before pairing. Order matters more than it looks:
        // asking for it AFTER pair-setup makes a HomePod drop the connection outright
        // ("closed mid-response"), which is what kept this from ever reaching SETUP.
        var info = await _rtsp.SendAsync("GET", "/info", ct: ct);
        if (!info.IsSuccess)
        {
            _log.Debug("AirPlay 2 /info returned {Status} - continuing to pairing", info.StatusCode);
        }

        // PTP when the receiver advertises it (feature bit 41) and our grandmaster could
        // bind its ports; NTP otherwise, unchanged. The reference sender gates on exactly
        // this bit. PTP is the timing model whose group a receiver actually JOINS - the
        // measured difference between how it treats an iPhone's session and ours.
        var receiverFeatures = info.IsSuccess ? ReadFeatureBits(info.BodyBytes) : 0L;

        // PTP is the AirPlay 2 timing model; NTP is the AirPlay 1 answer. A receiver that
        // advertises PTP gets it, because PTP is the timing group a receiver actually JOINS,
        // and joining is what makes it treat the session as an AirPlay 2 source rather than a
        // legacy stream it merely plays.
        //
        // AirPlay.EnablePtp=false is the escape hatch back to the NTP path, for a receiver
        // whose PTP handling is worse than its NTP handling or a network that blocks 319/320.
        var ptpAllowed = Settings.Get<bool>("AirPlay.EnablePtp", true);
        _ptp = ptpAllowed && (receiverFeatures & (1L << 41)) != 0 ? PtpClock.Instance : null;
        _log.Information("AirPlay 2 timing: {Protocol} (receiver features 0x{Features:X}, ptpEnabled={Enabled})",
            _ptp is null ? "NTP" : "PTP", receiverFeatures, ptpAllowed);

        // Then pairing: until this completes the receiver answers everything with 401.
        await _pairing.PairAsync(_rtsp, ct);
        _cipher = new AirPlay2Cipher(_pairing.AudioKey);

        // The receiver drives the clock exchange, so our timing socket has to exist before
        // SETUP announces its port.
        StartTimingServer();

        // Session SETUP, then the stream SETUP that carries the audio key.
        //
        // The body is the identity keys plus a timing arrangement, and it is split by timing
        // model exactly as the reference sender splits it: the NTP payload names a
        // timingPort, the PTP payload names a timingPeerInfo instead. The identity keys are
        // common to both and are not decoration - "name" is the SENDER's name and is what the
        // remote shows as the source, and the session/group UUIDs are what the receiver files
        // the session under. A trimmed body sets up and streams audio perfectly well while
        // leaving the receiver with nothing to put on the tile.
        //
        // What it must NOT claim is more than we are: the fifteen-key body this once sent
        // said we were an iPhone in a multi-select group, which changes how the receiver
        // presents the sender.
        _sessionUuid = Guid.NewGuid().ToString().ToUpperInvariant();
        _groupUuid = Guid.NewGuid().ToString().ToUpperInvariant();

        var deviceId = DeviceIdFrom(DacpControlServer.Instance.DacpId);
        var sessionSetup = new Dictionary<string, object?>
        {
            ["name"] = Environment.MachineName,
            ["deviceID"] = deviceId,
            ["sessionUUID"] = _sessionUuid,
            ["macAddress"] = deviceId,
            ["groupUUID"] = _groupUuid,

            // TRUE now that PTP works: we ARE the group leader (the grandmaster the pod
            // slaves to). Claiming this before PTP was meaningless (A/B'd, no effect); with
            // the pod now accepting our clock, declaring leadership is what should make it
            // adopt our group (gid flip, igl->0 in its broadcast).
            ["groupContainsGroupLeader"] = _ptp is not null,
        };

        if (_ptp is { } ptp)
        {
            // The PTP arrangement, reference-shaped: WHO the clock is (timingPeerInfo,
            // ClockID as a SIGNED plist integer because that is how iOS writes it) and
            // where to reach it. No timingPort - that key belongs to the NTP model.
            var peerInfo = new Dictionary<string, object?>
            {
                ["ID"] = ptp.PeerUuid,
                ["DeviceType"] = 0L,
                ["ClockID"] = unchecked((long)ptp.ClockId),
                ["SupportsClockPortMatchingOverride"] = false,
                ["Addresses"] = new List<object?> { _rtsp.LocalAddress },
            };

            sessionSetup["timingProtocol"] = "PTP";
            sessionSetup["timingPeerInfo"] = peerInfo;
            sessionSetup["timingPeerList"] = new List<object?> { peerInfo };
        }
        else
        {
            sessionSetup["timingProtocol"] = "NTP";
            sessionSetup["timingPort"] = (long)_timingPort;
        }

        var setupBody = BinaryPlist.Write(sessionSetup);

        _sessionUri = $"rtsp://{_rtsp.LocalAddress}/{sessionId}";
        var setup = await _rtsp.SendAsync("SETUP", _sessionUri,
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

        // The receiver's OWN timing addresses ride the session SETUP response; the first
        // usable one becomes a unicast PTP peer - the receiver never syncs to a master
        // that doesn't talk to it.
        if (_ptp is not null)
        {
            RegisterPtpPeer(setup.BodyBytes);
        }

        // RECORD goes HERE - between the session SETUP and the stream SETUP, which is not
        // where it reads like it belongs. That is the reference sender's order:
        //   SETUP (session) -> RECORD -> [SETPEERS if PTP] -> SETUP (stream) -> volume
        // Doing the stream SETUP first and RECORD last works well enough to get audio out,
        // but it leaves the receiver in a state where it takes the stream and ignores
        // everything we say about what is playing.
        //
        // Bare, with no Range or RTP-Info: this is a realtime stream, not a seekable one.
        var record = await _rtsp.SendAsync("RECORD", $"rtsp://{_rtsp.LocalAddress}/{sessionId}", ct: ct);
        if (!record.IsSuccess)
        {
            throw new InvalidOperationException($"AirPlay 2 RECORD refused ({record.StatusCode} {record.StatusText}).");
        }

        // SETPEERS, PTP only: the timing group's member list - the receiver, then us. The
        // content type is LITERALLY "/peer-list-changed"; that is not a mistake here, it
        // is what iOS puts on the wire and what the reference receiver documents.
        if (_ptp is not null)
        {
            // IP strings only. The first attempt sent the HOSTNAME the session was opened
            // with ("Speaker.local") and the receiver answered 500 - a peer list names
            // addresses, not names.
            var peers = BinaryPlist.Write(new List<object?> { _rtsp.RemoteAddress, _rtsp.LocalAddress });
            var setPeers = await _rtsp.SendAsync("SETPEERS", _sessionUri,
                contentType: "/peer-list-changed", body: peers, ct: ct);
            _log.Information("AirPlay 2 SETPEERS -> {Status}", setPeers.StatusCode);
        }

        StartControlClient();

        var streamBody = BinaryPlist.Write(new Dictionary<string, object?>
        {
            ["streams"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["type"] = 96L,                        // realtime audio
                    ["audioFormat"] = AudioFormatAlac441,
                    ["audioMode"] = "default",
                    // ct=2 is ALAC, and on the realtime stream that is the ONLY option: the
                    // receiver hardcodes an ALAC decoder for type 96 and ignores both ct and
                    // audioFormat. Sending raw PCM here (ct=1) is not refused - it sets up,
                    // streams, and the receiver runs its ALAC decoder over the raw samples,
                    // which comes out as static. The frames are "uncompressed ALAC" from
                    // RaopAlac, so there is still no real encoder in this path.
                    ["ct"] = 2L,
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

        // The receiver names the stream it just created; iOS's stream-scoped TEARDOWN quotes
        // this id back, so remember it for the goodbye.
        _streamId = ExtractStreamPort(streamSetup.BodyBytes, "streamID");

        var dataPort = ExtractDataPort(streamSetup.BodyBytes)
            ?? throw new InvalidOperationException("AirPlay 2 stream SETUP returned no dataPort.");

        var address = (await Dns.GetHostAddressesAsync(_host, ct)).First(a => a.AddressFamily == AddressFamily.InterNetwork);
        _audio = new UdpClient(0, AddressFamily.InterNetwork);

        // Same treatment as the timing and control sockets, and it matters MORE here now that a
        // send failure ends the session: on Windows an ICMP port-unreachable provoked by a
        // datagram we sent is reported as WSAECONNRESET on the next operation on this socket.
        // A receiver that drops one packet mid-song would otherwise look exactly like a
        // receiver that died.
        IgnoreUdpConnectionReset(_audio);
        _audioEndpoint = new IPEndPoint(address, dataPort);

        // The receiver answers with its own control port; sync goes there, not to the one
        // we asked for.
        if (ExtractStreamPort(streamSetup.BodyBytes, "controlPort") is { } receiverControlPort)
        {
            _controlEndpoint = new IPEndPoint(address, receiverControlPort);
        }

        _sequence = (ushort)Random.Shared.Next(ushort.MaxValue);

        // audioMode: a real iPhone POSTs this right after the stream is set up
        // (captured 2026-08-16: {audioMode: "default"}). OrgZ never sent it. It may be part
        // of what marks the session as a proper now-playing audio source to the receiver.
        try
        {
            var audioMode = BinaryPlist.Write(new Dictionary<string, object?> { ["audioMode"] = "default" });
            await _rtsp.SendAsync("POST", "/audioMode", contentType: "application/x-apple-binary-plist", body: audioMode, ct: ct);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "AirPlay 2 audioMode failed");
        }

        // Start 1.5s into the timeline, as the reference sender does - a stream that starts
        // at timestamp 0 has no room for the receiver's own buffering.
        _timestamp = SampleRate + (SampleRate / 2);
        StartSyncLoop();

        _trackStartTimestamp = CurrentRtpTime;

        // VOLUME LAST, and it matters: the reference sender notes that some receivers don't
        // register the level at all unless it is the final request of the start sequence.
        // It is also the hook the whole metadata handshake hangs off - the reference sends
        // now-playing from the volume response handler, which is why progress follows it
        // here rather than preceding RECORD as it used to.
        //
        // The adopt GET runs BEFORE the SET or it is not adoption. This block was moved
        // after session-up once, to keep the start sequence byte-identical to an
        // eyes-verified run - and from then on every "adopted" level was just our own SET
        // read back (the log shows the receiver answering exactly what we had written a
        // moment earlier; a fresh selection wrote full scale). Read first, then the
        // sequence still ends on a volume: theirs when they answer, ours when they don't.
        var level = Math.Clamp(InitialVolume, 0f, 1f);
        if (AdoptReceiverVolume && await GetVolumeAsync(ct) is { } current)
        {
            level = current;
            RemoteVolume?.Invoke(this, current);
        }

        await SetVolumeAsync(level, ct);

        // Now-playing startup: progress first, then the caller's text and artwork via
        // SetTrackInfoAsync once the sink replays what it already knows.
        await SendProgressAsync(ct);

        StartFeedbackLoop(sessionId);
        StartMetadataKeepAliveLoop();

        // NO startup FLUSH. It used to go here carrying the RTP timeline, on the theory that
        // a receiver otherwise holds a live session and plays nothing - which was a real
        // symptom, but of raw PCM on a stream the receiver decodes as ALAC. The reference
        // sender only ever sends FLUSH for a seek or a pause, never to start a stream.
        _streamStart = DateTime.UtcNow;
        _framesSent = 0;
        IsPaused = StartPaused;
        IsHolding = StartPaused;
        IsConnected = true;
        _log.Information("AirPlay 2 session up: {Host} audio->{Port}{Held}", _host, dataPort, StartPaused ? " (holding, paused)" : "");
    }

    private System.Net.Sockets.TcpClient? _events;

    /// <summary>
    /// The sealed stream over the event socket. Held only so it is disposed with the session -
    /// its two ChaCha20-Poly1305 instances are handles, and a session that reconnects often
    /// leaves a pile of them to the finalizer otherwise.
    /// </summary>
    private HapCryptoStream? _eventStream;

    /// <summary>
    /// Connects to the event port the receiver hands back from SETUP, and SERVICES it.
    ///
    /// This channel is the keep-alive. The receiver pushes requests down it (chiefly
    /// <c>POST /command</c> carrying updateInfo) and waits for a 200 on each one; a sender
    /// that opens the socket and then ignores it looks dead, and the receiver fades the
    /// stream out and tears the session down after roughly 25-30 seconds. Holding the socket
    /// open is NOT enough - which is exactly how this presented: a minute of perfect audio
    /// that always stopped at the same point, with RTSP still answering 200 on every
    /// feedback POST right up to the moment it ended.
    ///
    /// It is a REVERSE connection, so the keys are the event pair swapped - see
    /// <see cref="HapCryptoStream.DeriveEventKeys"/>.
    /// </summary>
    private async Task OpenEventChannelAsync(int eventPort, CancellationToken ct)
    {
        try
        {
            _events = new System.Net.Sockets.TcpClient();
            await _events.ConnectAsync(_host, eventPort, ct);

            var stream = new HapCryptoStream(_events.GetStream());
            _eventStream = stream;
            if (_pairing.SessionKey is { } secret)
            {
                var (output, input) = HapCryptoStream.DeriveEventKeys(secret);
                stream.Enable(output, input);
            }

            _servers ??= new CancellationTokenSource();
            StartEventResponder(stream, _servers.Token);

            _log.Debug("AirPlay 2 event channel open on {Port}", eventPort);
        }
        catch (Exception ex)
        {
            // Not fatal to reaching RECORD, but the stream won't outlive ~30s without it.
            _log.Debug(ex, "AirPlay 2 event channel connect failed on {Port}", eventPort);
            _eventStream?.Dispose();
            _eventStream = null;
            _events?.Dispose();
            _events = null;
        }
    }

    /// <summary>
    /// Answers the receiver's event requests with 200 OK, for as long as the session lives.
    ///
    /// A dedicated thread with blocking reads, for the same reason the timing responder has
    /// one: this is a liveness signal on a deadline, and the thread pool in this app is busy
    /// enough with decoding and UI that a delayed reply is a real risk.
    ///
    /// The bodies are not parsed. The receiver is telling us about its own state - volume,
    /// grouping, playback info - none of which this sender acts on. What it needs back is
    /// the acknowledgement, so that is what it gets.
    /// </summary>
    private void StartEventResponder(HapCryptoStream stream, CancellationToken token)
    {
        var thread = new Thread(() =>
        {
            var request = new List<byte>(512);
            var one = new byte[1];

            try
            {
                while (!token.IsCancellationRequested)
                {
                    var read = stream.Read(one, 0, 1);
                    if (read == 0)
                    {
                        // The receiver closed the event channel. It only does that when it is
                        // finished with the session, so the session is over whether or not
                        // anything else has noticed yet.
                        EventChannelEnded(token);
                        return;
                    }

                    request.Add(one[0]);

                    // Requests are HTTP-shaped; the blank line ends the head. Any body is
                    // drained by the next iteration rather than parsed - the receiver does
                    // not wait for us to consume it before expecting the 200.
                    var n = request.Count;
                    if (n >= 4 && request[n - 4] == '\r' && request[n - 3] == '\n' && request[n - 2] == '\r' && request[n - 1] == '\n')
                    {
                        var raw = request.ToArray();
                        var head = System.Text.Encoding.ASCII.GetString(raw);
                        var line = head.Split("\r\n")[0];
                        request.Clear();

                        // Consume the body, then answer with a BARE RTSP/1.0 200.
                        //
                        // Both halves are load-bearing, and both were established by A/B on
                        // real hardware rather than by reading: answering HTTP/1.1 with a
                        // Content-Length (however correct that looks for an HTTP-shaped
                        // request) leaves the remote showing "Controls Not Available", and
                        // the reference sender says the same - Content-Length here corrupts
                        // the receiver's realtime timeline.
                        var length = int.TryParse(ExtractHeader(head, "Content-Length"), out var len) ? len : 0;
                        var body = new byte[length];
                        var offset = 0;
                        while (offset < length)
                        {
                            var got = stream.Read(body, offset, length - offset);
                            if (got == 0)
                            {
                                EventChannelEnded(token);
                                return;
                            }
                            offset += got;
                        }

                        if (length > 0)
                        {
                            DispatchEvent(body);
                        }

                        var cSeq = ExtractHeader(head, "CSeq");
                        var reply = new System.Text.StringBuilder("RTSP/1.0 200 OK\r\n");
                        if (cSeq is not null)
                        {
                            reply.Append($"CSeq: {cSeq}\r\n");
                        }
                        reply.Append("\r\n");

                        var bytes = System.Text.Encoding.ASCII.GetBytes(reply.ToString());
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush();

                        _log.Debug("AirPlay 2 event {Line} -> 200", line);
                    }
                }
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    _log.Debug(ex, "AirPlay 2 event channel ended");
                    Fail(ex);
                }
            }
        })
        {
            IsBackground = true,
            Name = "AirPlay events",
        };
        thread.Start();
    }

    /// <summary>
    /// The event channel reached EOF. That is a normal thing to see while we are tearing the
    /// session down ourselves, and the end of the session when it isn't.
    /// </summary>
    private void EventChannelEnded(CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            return;
        }

        Fail(new IOException("The AirPlay 2 event channel was closed by the receiver."));
    }

    /// <summary>
    /// Raised when the receiver asks us to change playback - the Home app's play/pause and
    /// skip buttons. The argument is the four-character command code ("paus", "play"...).
    /// </summary>
    public event EventHandler<string>? RemoteCommand;

    /// <summary>
    /// The receiver moved its own volume - the Home app slider, or the HomePod's touch
    /// controls. Carries a linear 0-1 level, already converted from AirPlay's decibels.
    /// </summary>
    public event EventHandler<float>? RemoteVolume;

    /// <summary>
    /// The session ended without being told to: the receiver was powered off, the Wi-Fi
    /// dropped, or it tore the session down at its end.
    ///
    /// Without this the loops that notice simply exit, <see cref="IsConnected"/> stays true
    /// and the pump goes on sealing packets to an endpoint that is not there - the app shows
    /// a speaker playing, silently, forever. Raised AT MOST ONCE, and never for an ordinary
    /// teardown, so a listener can treat it as "this session is over, deal with it".
    /// </summary>
    public event EventHandler<Exception>? Died;

    /// <summary>Set once by <see cref="Fail"/>, so the death is announced a single time.</summary>
    private int _died;

    /// <summary>
    /// Declares the session dead and tells whoever is listening. Only for an unasked-for
    /// death - cancellation and teardown go through <see cref="Dispose"/> instead.
    /// </summary>
    private void Fail(Exception ex)
    {
        if (Interlocked.Exchange(ref _died, 1) != 0)
        {
            return;
        }

        IsConnected = false;
        _log.Warning(ex, "AirPlay 2 session died: {Host}", _host);
        Died?.Invoke(this, ex);
    }

    /// <summary>
    /// AirPlay carries volume in dB over a -30..0 range, with -144 meaning muted. This is
    /// the inverse of what <see cref="SetVolumeAsync"/> sends, so a level set on the speaker
    /// and a level set in the app mean the same thing.
    ///
    /// A non-finite dB is treated as muted rather than clamped: Math.Clamp propagates NaN, and
    /// this level is adopted into the app's own volume and persisted to settings, where a NaN
    /// is not something the writer can serialize.
    /// </summary>
    internal static float LinearFromDb(double db)
    {
        if (!double.IsFinite(db) || db <= -144)
        {
            return 0f;
        }

        return (float)Math.Clamp((db + 30.0) / 30.0, 0.0, 1.0);
    }

    /// <summary>
    /// The session that forwards DACP button presses on behalf of the whole process.
    ///
    /// <see cref="DacpControlServer"/> is a singleton - one listener, one DACP id - so an
    /// inbound press is seen by EVERY live session. Commands are edge-triggered verbs, and
    /// running one twice is a bug rather than a duplicate: playpause toggles back to where it
    /// started, nextitem skips two tracks. So one session forwards and the rest stay quiet.
    /// Volume is deliberately NOT owned this way; it is a level, not an edge, and every
    /// selected speaker should land on the level the remote asked for.
    /// </summary>
    private static AirPlay2Session? _dacpOwner;

    /// <summary>
    /// Forwards a DACP button press. Same destination as the event channel's commands - the
    /// receiver uses whichever it feels like, so both roads lead to the same handler.
    /// </summary>
    private void OnDacpCommand(object? sender, string command)
    {
        if (!IsConnected)
        {
            return;
        }

        // Re-claim when the owning session has gone: the ownership exists to stop a second
        // speaker doubling the verb, not to leave the remaining one without controls. The
        // exchange returns the owner it found, and null means we just became it.
        var owner = Interlocked.CompareExchange(ref _dacpOwner, this, null) ?? this;
        if (!ReferenceEquals(owner, this))
        {
            return;
        }

        RemoteCommand?.Invoke(this, command);
    }

    /// <summary>Forwards a volume the remote set over DACP - the Home app's slider.</summary>
    private void OnDacpVolume(object? sender, float level)
    {
        if (IsConnected)
        {
            RemoteVolume?.Invoke(this, level);

            // And APPLY it. A DACP setproperty is the remote asking the SENDER to move the
            // level - unlike the event channel's volume, where the receiver has already moved
            // its own - so without this the app's slider tracks a change the speaker never
            // made.
            Helpers.TaskObserver.FireAndForget(SetVolumeAsync(level), "AirPlay 2 DACP volume");
        }
    }

    /// <summary>
    /// Decodes one event body and, if it is a remote-control request, raises it.
    ///
    /// The receiver sends a binary plist shaped like
    /// <c>{ type: "sendMediaRemoteCommand", value: { ... "paus" ... } }</c>. Rather than
    /// depend on the exact nesting - which is Apple's to change and is not documented
    /// anywhere authoritative - this walks the decoded structure for a string that is a
    /// command we recognise. The other traffic on this channel is "updateInfo" status, which
    /// contains no such string and so falls through untouched.
    /// </summary>
    private void DispatchEvent(byte[] body)
    {
        try
        {
            // The buffer is whatever the mis-framed read handed us, so the plist is somewhere
            // INSIDE it rather than at the front - find the magic and parse from there.
            var start = IndexOfMagic(body);
            if (start < 0)
            {
                return;
            }

            var plist = BinaryPlist.Read(body[start..]);

            // The receiver narrating its own state is the only view of its display we can
            // read from here, so the whole body goes in the log - this is the instrument
            // that replaces "a person looks at the tile".
            _log.Debug("AirPlay 2 event body: {Body}", DescribeNode(plist, 0));

            // Volume first: a volume event carries no command string, so looking for one
            // would just fall through and drop it.
            if (FindVolume(plist) is { } db)
            {
                var linear = LinearFromDb(db);
                _log.Information("AirPlay 2 remote volume: {Db} dB -> {Linear:0.00}", db, linear);
                DacpControlServer.Instance.ReportVolume(linear);
                RemoteVolume?.Invoke(this, linear);
                return;
            }

            // Deliberately NOT gated on the plist carrying "sendMediaRemoteCommand".
            //
            // Requiring that envelope is the obviously-correct reading and it stopped every
            // command getting through - the receiver does not always wrap them the way the
            // one captured example did. The looser scan works; tighten it only with a capture
            // showing the envelope actually present.
            var strings = Strings(plist).ToList();

            foreach (var value in strings)
            {
                if (RemoteCommands.Contains(value))
                {
                    _log.Information("AirPlay 2 remote command: {Command}", value);
                    RemoteCommand?.Invoke(this, value);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "AirPlay 2 event body was not a readable plist");
        }
    }

    /// <summary>
    /// The command codes we act on. Deliberately a small set: an unrecognised code is logged
    /// by <see cref="DispatchEvent"/>'s caller rather than guessed at, because acting on a
    /// misread command is worse than ignoring one.
    /// </summary>
    private static readonly HashSet<string> RemoteCommands = new(StringComparer.Ordinal)
    {
        "play", "paus", "stop", "next", "prev", "nextitem", "previtem", "playpause",
        // The event channel's MODERN spellings, verified from a HomePod capture (2026-08-16):
        // a Home-app pause arrives as value "plps" (toggle), skips as "nitm"/"pitm". Without
        // these in the filter the command is decoded and logged but never raised, so the tile's
        // controls light up and then do nothing. HandleRemoteCommand already maps all three.
        "plps", "nitm", "pitm",
    };

    /// <summary>One line per event plist, bounded, for the log - data payloads as sizes.</summary>
    private static string DescribeNode(object? node, int depth) => depth > 6 ? "…" : node switch
    {
        null => "(null)",
        byte[] data => $"<{data.Length}B>",
        List<object?> list => $"[{string.Join(", ", list.Take(24).Select(item => DescribeNode(item, depth + 1)))}]",
        Dictionary<string, object?> map => $"{{{string.Join(", ", map.Take(24).Select(kv => $"{kv.Key}: {DescribeNode(kv.Value, depth + 1)}"))}}}",
        string text => text.Length > 96 ? $"\"{text[..96]}…\"" : $"\"{text}\"",
        _ => node.ToString() ?? "",
    };

    /// <summary>
    /// The volume, in dB, out of an event plist - or null if this event isn't about volume.
    ///
    /// Keyed by name rather than by position: the receiver nests it differently depending on
    /// what prompted the update, but the key is always "volume".
    /// </summary>
    private static double? FindVolume(object? node)
    {
        switch (node)
        {
            case Dictionary<string, object?> map:
            {
                foreach (var (key, value) in map)
                {
                    if (key.Equals("volume", StringComparison.OrdinalIgnoreCase) && ToDouble(value) is { } db)
                    {
                        return db;
                    }

                    if (FindVolume(value) is { } nested)
                    {
                        return nested;
                    }
                }
            }
            break;

            case List<object?> list:
            {
                foreach (var entry in list)
                {
                    if (FindVolume(entry) is { } nested)
                    {
                        return nested;
                    }
                }
            }
            break;
        }

        return null;
    }

    private static double? ToDouble(object? value) => value switch
    {
        double d => d,
        float f => f,
        long l => l,
        int i => i,
        _ => null,
    };

    /// <summary>Where "bplist00" starts in a buffer, or -1.</summary>
    private static int IndexOfMagic(ReadOnlySpan<byte> buffer)
    {
        ReadOnlySpan<byte> magic = "bplist00"u8;
        for (var i = 0; i + magic.Length <= buffer.Length; i++)
        {
            if (buffer.Slice(i, magic.Length).SequenceEqual(magic))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Every string anywhere in a decoded plist, depth first.</summary>
    private static IEnumerable<string> Strings(object? node)
    {
        switch (node)
        {
            case string s:
            {
                yield return s;
            }
            break;

            case Dictionary<string, object?> map:
            {
                foreach (var (key, value) in map)
                {
                    yield return key;
                    foreach (var found in Strings(value))
                    {
                        yield return found;
                    }
                }
            }
            break;

            case List<object?> list:
            {
                foreach (var entry in list)
                {
                    foreach (var found in Strings(entry))
                    {
                        yield return found;
                    }
                }
            }
            break;
        }
    }

    /// <summary>Pulls one header value out of an HTTP-shaped head block.</summary>
    private static string? ExtractHeader(string head, string name)
    {
        foreach (var line in head.Split("\r\n"))
        {
            var colon = line.IndexOf(':');
            if (colon > 0 && line[..colon].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return line[(colon + 1)..].Trim();
            }
        }

        return null;
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

                    // /feedback ONLY. Progress does not belong on the heartbeat: the
                    // reference sender emits it solely as part of a metadata update, on a
                    // track change. Sending a position every two seconds tells the receiver
                    // a stream is playing whether or not one is, so merely selecting the
                    // speaker made it show as playing - and the receiver derives the running
                    // position from the RTP timeline anyway.
                    await _rtsp.SendAsync("POST", "/feedback", ct: token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // The heartbeat is the one request that goes out whether or not anything
                    // is playing, so a failure here is the earliest honest sign the receiver
                    // has gone - say so rather than exiting quietly and leaving the pump
                    // talking to nobody.
                    _log.Debug(ex, "AirPlay 2 feedback failed");
                    Fail(ex);
                    return;
                }
            }
        }, token);
    }

    /// <summary>
    /// Re-asserts the now-playing metadata on a cadence while a track is actually playing -
    /// the keep-alive that makes the tile paint EVERY time instead of sometimes.
    ///
    /// One push per track races the receiver's timeline lock: send it before the RTP&lt;-&gt;clock
    /// mapping is established and it is dropped, and the tile stays dark for the whole
    /// session. A cadence removes the race - owntone re-sends its metadata as periodic
    /// keep-alives for the same reason.
    ///
    /// Gated hard on PLAYING: during a hold/pause a progress push would tell the receiver a
    /// silent source is sounding, which is the "selecting the speaker shows it as playing"
    /// regression the feedback heartbeat was deliberately kept clear of.
    /// </summary>
    private void StartMetadataKeepAliveLoop()
    {
        if (_servers is null)
        {
            return;
        }

        var token = _servers.Token;
        _ = Task.Run(async () =>
        {
            var tick = 0;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), token);

                    if (_rtsp is null || IsPaused || !_announcedTrack)
                    {
                        continue;
                    }

                    // Progress every second keeps the tile's clock live and re-anchored to the
                    // real timeline. The DMAP text re-asserts less often - enough to win the
                    // startup race and recover from a dropped announce without spamming.
                    await SendProgressAsync(token);

                    if (tick % 3 == 0
                        && (!string.IsNullOrEmpty(_trackTitle) || !string.IsNullOrEmpty(_trackArtist) || !string.IsNullOrEmpty(_trackAlbum)))
                    {
                        var duration = _trackDurationFrames > 0
                            ? TimeSpan.FromSeconds((double)_trackDurationFrames / SampleRate)
                            : (TimeSpan?)null;
                        await SendWithRtpInfoAsync("application/x-dmap-tagged", DmapMetadata.Build(_trackTitle, _trackArtist, _trackAlbum, duration), token);
                    }

                    tick++;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // CONTINUE, not return: one slow reply that trips the RTSP timeout used to
                    // end the keep-alive for the rest of the session, which puts the tile back
                    // on a single raced announce - the exact failure this loop exists to fix.
                    // A receiver that is really gone is caught by the feedback heartbeat.
                    _log.Debug(ex, "AirPlay 2 metadata keep-alive failed");
                    continue;
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
        IgnoreUdpConnectionReset(_timing);
        _servers ??= new CancellationTokenSource();

        var token = _servers.Token;

        // A DEDICATED THREAD with blocking receives, not a thread-pool task.
        //
        // The receiver queries this clock while it is processing SETUP, and withholds its
        // SETUP reply until we answer. Running the responder on the pool meant that in the
        // app - where LibVLC and the UI keep the pool busy - the reply could be delayed past
        // the receiver's patience, so SETUP simply never came back. The same code answered
        // instantly from a test process with an idle pool, which is exactly why this only
        // ever failed inside OrgZ.
        var thread = new Thread(() =>
        {
            var any = new IPEndPoint(IPAddress.Any, 0);
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var query = _timing.Receive(ref any);
                    var received = RaopPackets.NtpNow();
                    if (RaopPackets.IsTimingRequest(query))
                    {
                        var reply = RaopPackets.BuildTimingReply(query, received, RaopPackets.NtpNow());
                        _timing.Send(reply, reply.Length, any);
                    }
                }
                catch (SocketException ex)
                {
                    // One bad datagram is not the end of the clock service. A reply that draws
                    // an ICMP unreachable surfaces on the NEXT receive as a socket error, and
                    // returning here left the receiver with no answer to any later query - in
                    // NTP mode, no clock at all.
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    _log.Debug(ex, "AirPlay 2 timing exchange failed");
                    continue;
                }
                catch (Exception ex)
                {
                    // Anything else (the socket disposed at teardown) really does end it.
                    if (!token.IsCancellationRequested)
                    {
                        _log.Debug(ex, "AirPlay 2 timing exchange failed");
                    }
                    return;
                }
            }
        })
        {
            IsBackground = true,
            Name = "AirPlay timing",
        };
        thread.Start();
    }

    /// <summary>
    /// Stops Windows turning an ICMP "port unreachable" for a datagram we SENT into an error
    /// on the next receive of an unconnected UDP socket - the same WSAECONNRESET (10054)
    /// behaviour <see cref="Sharing.MdnsAdvertiser"/> documents. Winsock-only ioctl;
    /// everywhere else the loops' per-packet resilience is all there is and all that's needed.
    /// </summary>
    private static void IgnoreUdpConnectionReset(UdpClient client)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);
            client.Client.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null);
        }
        catch (SocketException ex)
        {
            _log.Debug(ex, "SIO_UDP_CONNRESET ioctl failed - relying on loop resilience");
        }
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
        IgnoreUdpConnectionReset(_control);
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
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // _timestamp IS the playback position - SendPacketAsync advances it by
                    // 352 for every packet it sends. Adding _framesSent (which counts the
                    // same frames) made this clock run at twice real speed, so the NTP->RTP
                    // mapping the receiver schedules against drifted a second every second
                    // and the audio it had queued was never playable at the time we claimed.
                    var now = _timestamp;
                    var packet = _ptp is { } ptp
                        ? RaopPackets.BuildSyncPtp(now, PtpClock.NowNanoseconds(), ptp.ClockId, _syncSentinel)
                        : RaopPackets.BuildSync(now, RaopPackets.NtpNow(), now + LatencyFrames, _syncSentinel);
                    await _control.SendAsync(packet, packet.Length, _controlEndpoint);
                    _syncSentinel = false;
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

    /// <summary>
    /// The receiver's feature bits out of a /info reply, or 0 when it didn't answer with one
    /// we can read. /info is optional - a receiver that answers 200 with an empty body, an XML
    /// plist or a truncated one is a receiver we pair with anyway, on the NTP path.
    /// </summary>
    private static long ReadFeatureBits(byte[] body)
    {
        try
        {
            return BinaryPlist.Read(body) is Dictionary<string, object?> plist
                && plist.TryGetValue("features", out var featureBits)
                && featureBits is long bits
                    ? bits
                    : 0L;
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "AirPlay 2 /info body was not a readable plist");
            return 0L;
        }
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

        // Coming back from a pause. Three things have to happen together, and leaving any of
        // them out is a different flavour of broken:
        //   - the pacing clock restarts, or the pump believes it owes the receiver every
        //     second the pause lasted and floods it with a burst;
        //   - the next sync packet re-anchors the timeline against the new wall clock;
        //   - the next audio packet is marked, which is how a receiver tells a resumed
        //     stream from a stale one.
        if (_reanchor)
        {
            _reanchor = false;
            _syncSentinel = true;
            _first = true;
            _framesSent = 0;
            _streamStart = DateTime.UtcNow;

            // Audio is moving again, so the tile is too - pin the resume position first so
            // the receiver doesn't extrapolate across the span the pause lasted.
            if (IsPaused)
            {
                await SendTimelinePinAsync(playing: true, ct);
                await SendPlaybackStateAsync(MediaRemotePlaying, ct);
            }
        }

        // NO gap re-anchoring here.
        //
        // Advancing _timestamp across a silent gap is the right idea for pause/resume pacing
        // and it broke metadata: _timestamp is what CurrentRtpTime anchors the now-playing
        // information to, so jumping it moves the track's declared start out from under the
        // announcement the receiver already accepted. Pause pacing needs solving somewhere
        // that doesn't move the metadata timeline.
        //
        // The stream clock starts at the FIRST packet, not at the end of the handshake.
        // Stamping it in ConnectAsync meant any gap before audio arrived (the bus opens the
        // sink ~a second before the decoder produces anything) counted as time already
        // played, so the pacing loop below thought it was that far behind and flushed a
        // second of audio at once before settling.
        if (_first)
        {
            _streamStart = DateTime.UtcNow;
        }

        // The header is built first because its timestamp+ssrc words authenticate the
        // payload as AAD - the receiver checks them, so they can't be filled in afterwards.
        var header = RaopPackets.BuildAudio(_sequence, _timestamp + LatencyFrames, _ssrc, [], _first);

        // The realtime stream carries ALAC, not PCM - the receiver decodes type 96 as ALAC
        // unconditionally. RaopAlac packs the samples into a frame flagged "stored raw", so
        // this is still a bit-shuffle rather than a real encoder, and it is the same packer
        // the classic RAOP path uses.
        //
        // The sealed payload carries its own nonce on the end, so the packet is
        // header + ciphertext + tag + nonce.
        var body = _cipher.SealAudio(RaopAlac.Encode(pcm.Span), header.AsSpan(4, 8));

        var packet = new byte[header.Length + body.Length];
        header.CopyTo(packet, 0);
        body.CopyTo(packet, header.Length);

        _first = false;
        _sequence++;
        _timestamp += FramesPerPacket;
        _framesSent += FramesPerPacket;

        try
        {
            await _audio.SendAsync(packet, packet.Length, _audioEndpoint);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
        {
            // Not a death. A single ICMP port-unreachable - a receiver that blinked, or a
            // router answering for it - surfaces as ECONNRESET on the NEXT send. The ioctl
            // above suppresses this on Windows; other platforms still report it, and one lost
            // datagram in a stream that sends 86 a second is not worth ending a session over.
            _log.Debug(ex, "AirPlay 2 audio datagram refused; continuing");
        }
        catch (SocketException ex)
        {
            // Anything else - host unreachable, network down - means the far end really is
            // gone. Nothing downstream of the pump can tell that from silence, so say it here.
            Fail(ex);
            return;
        }

        HasSentAudio = true;

        var due = _streamStart.AddSeconds((double)_framesSent / SampleRate);
        var wait = due - DateTime.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, ct);
        }
    }

    /// <summary>
    /// Records the track BEFORE the handshake, so the first progress the receiver ever sees
    /// carries the real length.
    ///
    /// The receiver latches the stream's duration from the progress it gets at the start and
    /// does not revisit it. Connecting first and announcing the track a second later meant
    /// the first progress went out with the open-ended fallback, and the remote showed a
    /// one-hour track counting up forever no matter what we sent afterwards.
    ///
    /// Purely local - no network, nothing to await. The text and artwork still go out after
    /// RECORD, which is the only point the receiver accepts them.
    /// </summary>
    public void SeedTrackInfo(string? title, string? artist, string? album, TimeSpan? duration)
    {
        _trackTitle = title;
        _trackArtist = artist;
        _trackAlbum = album;
        _trackDurationFrames = duration is { TotalSeconds: > 0 and < 86400 } d
            ? (uint)(d.TotalSeconds * SampleRate)
            : 0;
    }

    /// <summary>
    /// Tells the receiver what is playing: the text it shows, the cover art, and the real
    /// length of the track.
    ///
    /// The duration matters for more than display. The receiver schedules the end of the
    /// stream from the progress triple, so an accurate end is what keeps it pulling audio
    /// for the whole track, and an accurate "current" is what keeps its idea of the position
    /// in step with ours.
    /// </summary>
    public async Task SetTrackInfoAsync(string? title, string? artist, string? album, TimeSpan? duration, byte[]? artwork, CancellationToken ct = default)
    {
        if (_rtsp is null)
        {
            return;
        }

        // Announcements are SERIALIZED. The bus's track replay and the play-start re-claim
        // are both fire-and-forget and can land together; interleaving the two bundles'
        // requests races the receiver's view of which item owns the display.
        await _announceGate.WaitAsync(ct);
        try
        {
            await AnnounceLockedAsync(title, artist, album, duration, artwork, ct);
        }
        finally
        {
            _announceGate.Release();
        }
    }

    private readonly SemaphoreSlim _announceGate = new(1, 1);

    private async Task AnnounceLockedAsync(string? title, string? artist, string? album, TimeSpan? duration, byte[]? artwork, CancellationToken ct)
    {
        // Shrink the cover ONCE, here, before either path sees it - the DMAP artwork
        // request and the MediaRemote push both carry the same bytes, so a full-size album
        // scan is paid for twice on every track change. The resize is also what turns a PNG
        // cover into the JPEG the MediaRemote push requires - a PNG rides the DMAP path but
        // never reaches an Apple receiver's display without it.
        if (ResizeArtwork && artwork is { Length: > 0 } original)
        {
            if (!ReferenceEquals(original, _artworkSource))
            {
                _artworkSource = original;
                _artworkFitted = AirPlayArtwork.Fit(original);
            }

            artwork = _artworkFitted;
        }

        // Only a genuinely NEW track restarts the timeline.
        //
        // This fires more than once per track - artwork is loaded asynchronously, so a track
        // is typically announced first without a cover and again with one a few seconds
        // later. Restarting the timeline on that second call told the receiver the song had
        // just begun when it was already well into it, which resets the position on the
        // remote's display and makes the whole tile incoherent.
        var isNewTrack = title != _trackTitle || artist != _trackArtist || album != _trackAlbum;
        if (isNewTrack)
        {
            _trackTitle = title;
            _trackArtist = artist;
            _trackAlbum = album;
            _trackStartTimestamp = CurrentRtpTime;
            _trackDurationFrames = duration is { TotalSeconds: > 0 and < 86400 } d
                ? (uint)(d.TotalSeconds * SampleRate)
                : 0;

            // A new track with no cover FORGETS the last one's. The cached bytes are what a
            // hold release re-announces with, so leaving them behind puts the previous song's
            // sleeve on this one - and the cache exists only to avoid resizing the same image
            // twice, not to supply art nobody handed us.
            if (artwork is not { Length: > 0 })
            {
                _artworkSource = null;
                _artworkFitted = null;
            }
        }

        _log.Information("AirPlay 2 track info: title={Title} artist={Artist} album={Album} duration={Duration} art={ArtBytes}B type={ArtType} new={IsNew} held={Held}",
            title ?? "(none)", artist ?? "(none)", album ?? "(none)", duration, artwork?.Length ?? 0,
            artwork is { Length: > 0 } ? ImageContentType(artwork) ?? "unknown" : "none", isNewTrack, IsHolding);

        // NOTHING goes on the wire while HOLDING. The opening sequence has to land whole,
        // from a PLAYING client - the shape the lab tool proves on hardware every time.
        // Announcing the item now, as a paused client, and flipping to playing later is the
        // split this file has already documented as fatal once: the receiver 200s every
        // push and renders nothing when the item finally matters. So a hold only SEEDS what
        // to say - the fields above are recorded - and NotifyPlaybackStartedAsync says all
        // of it, as one sequence, the moment audio actually starts.
        //
        // A PAUSE is not a hold: the receiver already knows this sender and its item by then,
        // so a track selected while paused is announced normally and the resume only has to
        // move the timeline.
        if (IsHolding)
        {
            return;
        }

        try
        {
            // Progress FIRST, then text, then artwork - the reference sender's order. The
            // receiver anchors the item to the progress triple; text arriving ahead of it
            // describes an item the receiver hasn't placed on a timeline yet.
            //
            // Except while paused. The position rides an RTP timestamp that keeps advancing
            // whether or not the music does, so a paused tile fed a fresh triple counts up
            // through a track nobody is playing - which is why the feedback and keep-alive
            // loops both withhold it while paused. The resume re-anchors and sends one then.
            if (!IsPaused)
            {
                await SendProgressAsync(ct);
            }

            // An all-empty announcement CLEARS the receiver's display rather than leaving it
            // as it was, so say nothing instead of saying nothing loudly.
            if (!string.IsNullOrEmpty(title) || !string.IsNullOrEmpty(artist) || !string.IsNullOrEmpty(album))
            {
                await SendWithRtpInfoAsync("application/x-dmap-tagged", DmapMetadata.Build(title, artist, album, duration), ct);
            }

            // Cover art rides the same channel with an image content type. Receivers with no
            // display ignore it, so this is sent regardless of what the speaker is.
            //
            // The type is SNIFFED, not assumed: this said "image/jpeg" for whatever bytes the
            // file happened to embed, and a PNG cover announced as JPEG is a large payload the
            // receiver can only reject. Anything we can't name is not sent at all - a wrong
            // content type is worse than no artwork.
            if (artwork is { Length: > 0 } && ImageContentType(artwork) is { } imageType)
            {
                // Oversized covers are SKIPPED rather than sent.
                //
                // The reference sender resizes art to a fixed size before sending; we ship
                // whatever the file embeds, which has been up to 629KB - 615 chunks through
                // the encrypted control channel, on the same connection carrying the session's
                // keep-alive. This cap is here to establish whether that is what costs us the
                // now-playing display; if it is, the fix is to downscale, not to drop.
                if (artwork.Length <= MaxArtworkBytes)
                {
                    await SendWithRtpInfoAsync(imageType, artwork, ct);
                }
                else
                {
                    _log.Information("AirPlay 2 artwork skipped: {Bytes}B exceeds {Max}B", artwork.Length, MaxArtworkBytes);
                }
            }

            // And the MediaRemote push, which is what lights the transport controls up.
            await SendSupportedCommandsAsync(ct);
        }
        catch (Exception ex)
        {
            // Never fatal: metadata is decoration, and a receiver that rejects it is still
            // perfectly capable of playing the audio.
            _log.Debug(ex, "AirPlay 2 track info failed");
        }
    }

    /// <summary>
    /// The MediaRemote half of an announcement - the lean supported-command list, empty then
    /// populated, over <c>POST /command</c> on this same encrypted RTSP channel.
    ///
    /// That list is ALL that goes down this path. The now-playing tile itself is carried
    /// entirely by the DMAP SET_PARAMETER above, which is what a real iPhone does; the item
    /// and client pushes this used to send are not part of a realtime sender's traffic.
    /// </summary>
    private async Task SendSupportedCommandsAsync(CancellationToken ct)
    {
        if (_rtsp is null)
        {
            return;
        }

        // MIMIC A REAL iPHONE, byte-verified from the working SomaFM session (2026-08-16,
        // DECRYPTED /command capture in realcommand.log): over MediaRemote /command it sends
        // ONLY updateMRSupportedCommands - never updateMRNowPlayingInfo/Client/PlaybackState.
        // The now-playing TILE is carried entirely by the DMAP SET_PARAMETER above. Controls
        // come from this command list, and the EXACT SET is what matters:
        //   - the iPhone advertises SIX transport commands and no more:
        //       5 PreviousTrack, 4 NextTrack, 3 Stop, 2 TogglePlayPause, 1 Pause, 0 Play
        //   - it is sent PROGRESSIVELY: an empty list first, then the populated one, and the
        //     populated list again after each metadata push.
        // OrgZ used to advertise FIFTEEN commands (shuffle 26 / repeat 25 / scrub 24 / skip
        // 18,17 / ...) in one push - capabilities the pod can't reconcile with a realtime
        // stream, and it greyed the whole source out ("Controls Not Available"). Sending the
        // iPhone's exact lean set is the fix; sending MORE was the bug.
        if (!_sentSupportedCommands)
        {
            _sentSupportedCommands = true;
            await SendSupportedCommandsAsync(Array.Empty<(long, bool)>(), ct);   // empty first, as the iPhone does
        }

        await SendSupportedCommandsAsync(TransportCommands, ct);

        _announcedTrack = true;
    }

    /// <summary>The prefix every MediaRemote now-playing key carries, in full.</summary>
    private const string NowPlayingKey = "kMRMediaRemoteNowPlayingInfo";

    /// <summary>The session and group UUIDs announced in SETUP.</summary>
    private string? _sessionUuid;
    private string? _groupUuid;

    /// <summary>The receiver's id for our audio stream, from the stream SETUP reply.</summary>
    private int? _streamId;

    /// <summary>The process grandmaster when this session runs PTP timing; null on the NTP path.</summary>
    private PtpClock? _ptp;

    /// <summary>The receiver timing address registered as a PTP peer, for removal at teardown.</summary>
    private IPAddress? _ptpPeer;

    /// <summary>
    /// Registers the receiver's timing address (from the session SETUP response's
    /// timingPeerInfo) with the grandmaster. First usable IPv4 wins, like the reference.
    ///
    /// A peer is not optional. The grandmaster is unicast: with an empty peer list it sends no
    /// Announce, no Sync and no Follow-Up at all, so a receiver we never registered is left
    /// with a timing protocol it has joined and a master it never hears from. SETPEERS tells
    /// it where we are; it does not make us talk to it. Hence the fallback to the address the
    /// RTSP connection is provably reachable on.
    /// </summary>
    private void RegisterPtpPeer(byte[] responsePlist)
    {
        try
        {
            if (BinaryPlist.Read(responsePlist) is Dictionary<string, object?> plist
                && plist.TryGetValue("timingPeerInfo", out var peerInfoNode)
                && peerInfoNode is Dictionary<string, object?> peerInfo
                && peerInfo.TryGetValue("Addresses", out var addressesNode)
                && addressesNode is List<object?> addresses)
            {
                foreach (var entry in addresses)
                {
                    if (entry is string text
                        && IPAddress.TryParse(text, out var address)
                        && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        _ptpPeer = address;
                        _ptp?.AddPeer(address);
                        return;
                    }
                }
            }

            // No timingPeerInfo, or one that lists only link-local IPv6 - a HomePod puts its
            // fe80:: addresses first and some receivers send nothing at all.
            if (IPAddress.TryParse(_rtsp?.RemoteAddress ?? string.Empty, out var fallback))
            {
                _log.Information("AirPlay 2 PTP: no usable timing address in SETUP response - using the RTSP peer {Address}", fallback);
                _ptpPeer = fallback;
                _ptp?.AddPeer(fallback);
                return;
            }

            _log.Information("AirPlay 2 PTP: no timing peer to register - the receiver will not be sent Sync");
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "AirPlay 2 PTP peer registration failed");
        }
    }

    /// <summary>
    /// Declares which transport controls this sender accepts.
    ///
    /// Without it the receiver has been told what is playing but not that anything can act on
    /// it, and the remote shows "Controls Not Available" - which is exactly what a HomePod
    /// does for us today, while 200-accepting the now-playing push.
    ///
    /// Each entry is a standalone plist embedded as a DATA node, not a nested dictionary; the
    /// numbering is MRMediaRemoteCommand (Play=0, Pause=1...), and the list and its order are
    /// the EXACT set a real iPhone advertises for a live stream, from the decrypted capture:
    /// PreviousTrack, NextTrack, Stop, TogglePlayPause, Pause (disabled - a live stream can't
    /// pause), Play. No shuffle/repeat/scrub/skip and no options dictionaries - those are what
    /// a receiver refuses on a realtime source.
    /// </summary>
    private static readonly (long Command, bool Enabled)[] TransportCommands =
    {
        (5, true),    // PreviousTrack
        (4, true),    // NextTrack
        (3, true),    // Stop
        (2, true),    // TogglePlayPause
        (1, false),   // Pause - disabled, as the iPhone sends it for a stream
        (0, true),    // Play
    };

    private async Task SendSupportedCommandsAsync((long Command, bool Enabled)[] commands, CancellationToken ct)
    {
        if (_rtsp is null)
        {
            return;
        }

        var list = new List<object?>(commands.Length);
        foreach (var (command, enabled) in commands)
        {
            list.Add(CommandInfo(command, enabled, null));
        }

        var body = BinaryPlist.Write(new Dictionary<string, object?>
        {
            ["type"] = "updateMRSupportedCommands",
            ["params"] = new Dictionary<string, object?>
            {
                ["mrSupportedCommandsFromSender"] = list,
            },
        });

        var response = await _rtsp.SendAsync("POST", "/command",
            contentType: "application/x-apple-binary-plist", body: body, ct: ct);

        _log.Information("MediaRemote supported commands ({Count}) -> {Status}", commands.Length, response.StatusCode);
    }

    /// <summary>One MRSupportedCommand, serialized on its own and carried as data.</summary>
    private static byte[] CommandInfo(long command, bool enabled, Dictionary<string, object?>? options)
    {
        var entry = new Dictionary<string, object?>
        {
            ["kCommandInfoCommandKey"] = command,
            ["kCommandInfoEnabledKey"] = enabled,
        };

        if (options is not null)
        {
            entry["kCommandInfoOptionsKey"] = options;
        }

        return BinaryPlist.Write(entry);
    }

    /// <summary>MRPlaybackState: 1 = Playing, 2 = Paused, 3 = Stopped.</summary>
    private const int MediaRemotePlaying = 1;

    /// <summary>MRPlaybackState paused.</summary>
    private const int MediaRemotePaused = 2;

    /// <summary>
    /// Covers are shrunk to 600x600 JPEG before sending, which the reference sender does: a
    /// real iPhone puts a ~43KB baseline JPEG on the wire, not the file's embedded megabytes.
    /// </summary>
    internal const bool ResizeArtwork = true;

    // The last cover we were handed and what it shrank to, so a track announced twice -
    // which is what happens when artwork arrives after the text - is not resized twice.
    private byte[]? _artworkSource;
    private byte[]? _artworkFitted;

    /// <summary>The supported-command list is part of the opening sequence, sent once.</summary>
    private bool _sentSupportedCommands;

    /// <summary>
    /// The content type for an image, read from its magic bytes, or null when it is neither
    /// of the two formats a receiver accepts.
    /// </summary>
    internal static string? ImageContentType(ReadOnlySpan<byte> image)
    {
        if (image.Length >= 3 && image[0] == 0xFF && image[1] == 0xD8 && image[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (image.Length >= 8 && image[0] == 0x89 && image[1] == 0x50 && image[2] == 0x4E && image[3] == 0x47)
        {
            return "image/png";
        }

        return null;
    }

    /// <summary>
    /// Sends the progress triple - track start, current position, track end - in RTP
    /// timestamp units, which is the clock the receiver reports position against.
    /// </summary>
    private async Task SendProgressAsync(CancellationToken ct)
    {
        if (_rtsp is null)
        {
            return;
        }

        // Say NOTHING until the track's length is known.
        //
        // With no duration there is no honest value for the end: naming a far-future one had
        // the receiver show a one-hour track counting up, and using the current position -
        // verified on a real receiver - announces pos == end, a zero-length track that has
        // already finished. SetTrackInfoAsync sends a correct triple as soon as it knows one.
        if (_trackDurationFrames == 0)
        {
            return;
        }

        // The three fields are NOT start/now/end, which is the obvious reading and the one
        // this had. Matching the reference sender:
        //   display = the track's start MINUS a lead-in delay
        //   pos     = the current position, never before the start
        //   end     = start + the track's length
        // The lead-in is what gives the receiver a moment to have the metadata in hand
        // before the audio it belongs to arrives.
        //
        // "Never before the start" is a MODULAR comparison: the RTP timestamp is a 32-bit
        // sample counter that wraps every ~27 hours, and a session holding a speaker with
        // silence runs for days. A plain unsigned compare across the wrap reads as "behind the
        // start" and pins the position to the track's beginning, once a second, for the rest
        // of that track.
        var display = _trackStartTimestamp - MetadataLeadInFrames;
        var ahead = unchecked((int)(CurrentRtpTime - _trackStartTimestamp));
        var pos = ahead > 0 ? CurrentRtpTime : _trackStartTimestamp;
        var end = _trackStartTimestamp + _trackDurationFrames;

        var body = System.Text.Encoding.ASCII.GetBytes($"progress: {display}/{pos}/{end}\r\n");
        await _rtsp.SendAsync("SET_PARAMETER", _sessionUri ?? $"rtsp://{_rtsp.LocalAddress}/stream",
            contentType: "text/parameters", body: body, ct: ct);
    }

    /// <summary>
    /// SET_PARAMETER carrying an RTP-Info header, which is what ties a metadata or artwork
    /// update to the point in the timeline it belongs to - without it a receiver can show
    /// the new title against audio that hasn't reached the speaker yet.
    ///
    /// It goes to the SESSION uri, not to /stream. A HomePod answers 200 to either, and then
    /// silently ignores anything sent to /stream - which is a thoroughly convincing way to
    /// look like it worked. Volume is the exception that misleads here: that one really does
    /// belong on /stream, so the two are not interchangeable.
    /// </summary>
    private Task<RtspResponse> SendWithRtpInfoAsync(string contentType, byte[] body, CancellationToken ct)
        => _rtsp!.SendAsync("SET_PARAMETER", _sessionUri ?? $"rtsp://{_rtsp.LocalAddress}/stream",
            new Dictionary<string, string> { ["RTP-Info"] = $"rtptime={_trackStartTimestamp}" },
            contentType, body, ct);

    /// <summary>
    /// There is no PAUSE method in AirPlay. FLUSH names the first frame that survives, the
    /// sender stops transmitting, and the timeline is NOT reset: sequence and timestamp carry
    /// on from where they were, because they are a sample counter rather than a clock. What
    /// has to be re-established on resume is the mapping between that counter and the wall
    /// clock, which is what the sentinel sync packet is for.
    /// </summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        if (_rtsp is null || _sessionUri is null || !IsConnected)
        {
            return;
        }

        // The two-field RTP-Info form, which is unique to FLUSH: everything strictly before
        // this frame is discarded and this one is kept, so it names the NEXT frame rather
        // than the last one sent.
        var headers = new Dictionary<string, string>
        {
            ["RTP-Info"] = $"seq={_sequence};rtptime={CurrentRtpTime}",
        };

        _reanchor = true;

        try
        {
            await _rtsp.SendAsync("FLUSH", _sessionUri, headers, ct: ct);

            // And SAY so. The receiver has been told what is playing and that transport
            // controls exist; if nothing ever tells it the stream stopped, its tile goes on
            // counting up through a pause - the position extrapolates from ElapsedTime and a
            // playback rate we last declared as 1.
            //
            // The timeline pin goes FIRST: a bare state flip leaves the receiver showing the
            // last literal ElapsedTime it was given, so the pin freezes the position (rate 0,
            // fresh Timestamp) and THEN the state change lands on a coherent tile. The
            // reference Apple sender pushes info-then-state at both transitions.
            await SendTimelinePinAsync(playing: false, ct);
            await SendPlaybackStateAsync(MediaRemotePaused, ct);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "AirPlay 2 flush failed");
        }
    }

    /// <summary>
    /// Pins the receiver's timeline to the actual position and rate - a
    /// <c>mergePolicy: update</c> push carrying ONLY the timeline fields, sent before a
    /// playback-state change so the tile freezes (pause) or resumes (play) from the right
    /// position instead of extrapolating across the transition.
    ///
    /// Timeline fields only: an update push merges into the item the receiver already has,
    /// and carrying the artwork bytes through every correction makes it re-decode the cover
    /// each time. Gated on a track having been announced, because an update into a display
    /// we never claimed merges our timeline into someone else's item.
    /// </summary>
    private async Task SendTimelinePinAsync(bool playing, CancellationToken ct)
    {
        if (_rtsp is null || !_announcedTrack)
        {
            return;
        }

        var body = BinaryPlist.Write(new Dictionary<string, object?>
        {
            ["type"] = "updateMRNowPlayingInfo",
            ["params"] = new Dictionary<string, object?>
            {
                ["type"] = "npi-text",
                ["mergePolicy"] = "update",
                ["params"] = new Dictionary<string, object?>
                {
                    [NowPlayingKey + "ElapsedTime"] = (AudiblePosition ?? TimeSpan.Zero).TotalSeconds,
                    [NowPlayingKey + "PlaybackRate"] = playing ? 1.0 : 0.0,
                    [NowPlayingKey + "DefaultPlaybackRate"] = 1.0,
                    [NowPlayingKey + "Timestamp"] = DateTime.UtcNow,
                },
            },
        });

        await _rtsp.SendAsync("POST", "/command",
            contentType: "application/x-apple-binary-plist", body: body, ct: ct);
    }

    /// <summary>Tells the receiver whether the stream is running, for its tile and its buttons.</summary>
    private async Task SendPlaybackStateAsync(int state, CancellationToken ct)
    {
        if (_rtsp is null)
        {
            return;
        }

        IsPaused = state == MediaRemotePaused;

        var body = BinaryPlist.Write(new Dictionary<string, object?>
        {
            ["type"] = "updateMRPlaybackState",
            ["params"] = new Dictionary<string, object?>
            {
                ["mrPlaybackState"] = (long)state,
            },
        });

        await _rtsp.SendAsync("POST", "/command",
            contentType: "application/x-apple-binary-plist", body: body, ct: ct);
    }

    /// <summary>Whether the receiver has been told the stream is paused - the remote asks.</summary>
    public bool IsPaused { get; private set; }

    /// <summary>
    /// Whether this session is HOLDING the speaker: opened by a selection, streaming silence,
    /// never yet handed a note of real audio.
    ///
    /// Separate from <see cref="IsPaused"/> on purpose, even though a hold is announced as a
    /// pause. A user pause sets IsPaused too, and treating that as a hold sends the resume
    /// down the hold-release path - which re-anchors the track to the resume instant, so the
    /// tile restarts at 0:00 halfway through the song, and re-announces the item instead of
    /// declaring it playing again. The hold is released exactly once, by
    /// <see cref="NotifyPlaybackStartedAsync"/>; every later pause/resume is a pause.
    /// </summary>
    public bool IsHolding { get; private set; }

    /// <summary>Set after a FLUSH: the next packet has to re-anchor the timeline.</summary>
    private bool _reanchor;

    /// <summary>Whether the next sync packet carries the new-anchor sentinel.</summary>
    private volatile bool _syncSentinel = true;

    /// <summary>A track announcement has claimed the receiver's display this session.</summary>
    private bool _announcedTrack;

    /// <summary>
    /// Says the music has started, for a session that was HOLDING the speaker with silence.
    /// Does nothing once the hold has been released - a later pause and resume is a pause,
    /// and goes out as a timeline pin plus a playing state from the send path.
    /// </summary>
    public async Task NotifyPlaybackStartedAsync(CancellationToken ct = default)
    {
        if (IsConnected && IsHolding)
        {
            _log.Information("AirPlay 2 playback started (was holding): announcing {Title}", _trackTitle ?? "(no track seeded)");

            // The hold carried silence, and silence advanced the RTP clock without playing
            // any of the track: the music starts NOW. Re-anchor the track to this moment,
            // or a speaker held for two minutes opens its tile two minutes into the song.
            _trackStartTimestamp = CurrentRtpTime;

            // Flip LOCALLY, with no state push of its own. The receiver has heard nothing
            // on the MediaRemote path during the hold, and the first thing it ever hears
            // must be the same whole sequence the lab tool sends - progress, DMAP, artwork
            // and the supported-command list. A state push here would front-run that
            // sequence and split it, which is the documented way to a tile that never paints.
            IsHolding = false;
            IsPaused = false;

            if (!string.IsNullOrEmpty(_trackTitle) || !string.IsNullOrEmpty(_trackArtist) || !string.IsNullOrEmpty(_trackAlbum))
            {
                var duration = _trackDurationFrames > 0
                    ? TimeSpan.FromSeconds((double)_trackDurationFrames / SampleRate)
                    : (TimeSpan?)null;
                await SetTrackInfoAsync(_trackTitle, _trackArtist, _trackAlbum, duration, _artworkSource, ct);
            }
            else
            {
                // Nothing seeded to announce - the bus's track info will arrive on its own.
                // Say only that the stream runs, so the receiver isn't left believing a
                // paused source is making sound.
                await SendPlaybackStateAsync(MediaRemotePlaying, ct);
            }
        }
    }

    /// <summary>
    /// Asks the receiver what volume IT is at, in the same text/parameters shape volume is
    /// set with. Null when the receiver won't say - some won't, and that is not an error,
    /// it just means our level stands.
    /// </summary>
    public async Task<float?> GetVolumeAsync(CancellationToken ct = default)
    {
        if (_rtsp is null)
        {
            return null;
        }

        try
        {
            var response = await _rtsp.SendAsync("GET_PARAMETER", $"rtsp://{_rtsp.LocalAddress}/stream",
                contentType: "text/parameters", body: "volume\r\n"u8.ToArray(), ct: ct);

            if (!response.IsSuccess)
            {
                return null;
            }

            foreach (var line in response.Body.Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries))
            {
                var colon = line.IndexOf(':');
                if (colon > 0
                    && line[..colon].Trim().Equals("volume", StringComparison.OrdinalIgnoreCase)
                    && double.TryParse(line[(colon + 1)..].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var db))
                {
                    _log.Information("AirPlay 2 receiver is at {Db} dB", db);
                    return LinearFromDb(db);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "AirPlay 2 volume read failed");
        }

        return null;
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

            // The receiver polls our DACP endpoint for this value as a health check, so what
            // it reads back should be what we just set.
            DacpControlServer.Instance.ReportVolume(linear);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "AirPlay 2 volume set failed");
        }
    }

    private bool _disposed;

    /// <summary>Set once the goodbye has gone out, so it is never sent twice.</summary>
    private int _toreDown;

    /// <summary>
    /// The goodbye, awaitable, so a caller on a UI thread never blocks on it.
    ///
    /// TEARDOWN even when the handshake FAILED and no Session header was ever seen. A
    /// receiver holds the session it began building until told otherwise, so a string of
    /// failed attempts fills it up and it eventually stops answering anything - including
    /// /info on a fresh connection. Leaving without saying goodbye is what turns one bad
    /// attempt into a dead speaker.
    /// </summary>
    public async Task TeardownAsync()
    {
        // Said ONCE, whoever says it: a caller that tears the session down cleanly and then
        // disposes it must not send the receiver a second goodbye for a session it has
        // already closed.
        if (_rtsp is null || _sessionUri is null || Interlocked.Exchange(ref _toreDown, 1) != 0)
        {
            return;
        }

        // A receiver that has stopped answering must not hold the caller up: the RTSP client's
        // own receive timeout is ten seconds per request, which is far longer than a goodbye
        // is worth.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        try
        {
            // iOS's goodbye is TWO teardowns, in order: one naming the stream - the
            // receiver stops that stream and keeps the connection - then one with an
            // empty plist dict, which ends the session (a bare bodyless TEARDOWN is the
            // AirPlay 1 shape, and this path is not that). The stream-scoped one goes
            // first whenever a stream was actually set up, so the receiver winds down
            // the way its own senders wind it down.
            if (_audio is not null)
            {
                var streamTeardown = BinaryPlist.Write(new Dictionary<string, object?>
                {
                    ["streams"] = new List<object?>
                    {
                        new Dictionary<string, object?>
                        {
                            ["streamID"] = (long)(_streamId ?? 0),
                            ["type"] = 96L,
                        },
                    },
                });

                await _rtsp.SendAsync("TEARDOWN", _sessionUri,
                    contentType: "application/x-apple-binary-plist", body: streamTeardown, ct: deadline.Token);
            }

            await _rtsp.SendAsync("TEARDOWN", _sessionUri,
                contentType: "application/x-apple-binary-plist", body: BinaryPlist.Write(new Dictionary<string, object?>()), ct: deadline.Token);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "AirPlay 2 teardown failed");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        IsConnected = false;
        IsHolding = false;

        DacpControlServer.Instance.Command -= OnDacpCommand;
        DacpControlServer.Instance.VolumeChanged -= OnDacpVolume;
        Interlocked.CompareExchange(ref _dacpOwner, null, this);

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
            // The goodbye runs on a POOL thread and Dispose waits on that.
            //
            // Dispose is reached synchronously from the speaker flyout, i.e. on the UI thread
            // with a synchronization context. Awaiting the RTSP gate here - which a keep-alive
            // or feedback request holds a good part of every second - posts the continuation
            // back to the very thread that is blocked waiting for it, so both teardowns time
            // out and the receiver never hears the goodbye at all. Off the context there is
            // nothing to deadlock against, and the wait is only a backstop.
            Task.Run(TeardownAsync).Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "AirPlay 2 teardown failed");
        }

        // The grandmaster outlives sessions; only this session's peer registration ends.
        if (_ptpPeer is { } peer)
        {
            try
            {
                _ptp?.RemovePeer(peer);
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "AirPlay 2 PTP peer removal failed");
            }
        }

        _cipher?.Dispose();
        _audio?.Dispose();
        _timing?.Dispose();
        _control?.Dispose();
        _eventStream?.Dispose();
        _events?.Dispose();
        _servers?.Dispose();
        _announceGate.Dispose();
        _rtsp?.Dispose();
    }
}
