// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using OrgZ.Services.AudioOutput.AirPlay;

namespace OrgZ.Tests;

/// <summary>
/// A software AirPlay 2 receiver that runs inside the test process, on loopback, silently.
///
/// This exists because the AirPlay sender could only ever be checked by playing something at
/// a real speaker and looking at it - which is slow, needs a person in the room, and reports
/// "the display is blank" rather than which byte was wrong. Roughly forty of those cycles
/// established less than one run of this does.
///
/// It speaks the parts of the protocol the sender actually uses: transient pair-setup, the
/// HAP-encrypted RTSP channel, both SETUPs, RECORD, SET_PARAMETER in all its content types,
/// POST /command and /feedback, FLUSH and TEARDOWN, the reverse event channel, and all three
/// UDP ports - it decrypts the audio, decodes it back to PCM, answers the sender's NTP clock,
/// and records the lot.
///
/// What it is NOT: proof that our crypto agrees with Apple's. Both ends of the pairing and
/// the framing here are ours, so a matched pair of mistakes would pass. That half is pinned
/// separately by <see cref="AirPlayPairingTests"/>'s external vectors, and by the fact that
/// audio plays on real hardware. What this proves is everything above the transport - order,
/// framing, timing and content - which is where every metadata failure actually lived.
/// </summary>
internal sealed class FakeAirPlayReceiver : IDisposable
{
    private const int FramesPerPacket = 352;

    private readonly TcpListener _rtspListener;
    private readonly TcpListener _eventListener;
    private readonly UdpClient _data;
    private readonly UdpClient _control;
    private readonly UdpClient _timingClient;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Thread> _threads = [];
    private readonly ReferenceSrpServer _srp;
    private readonly string? _password;

    private readonly Lock _gate = new();
    private readonly List<Request> _requests = [];
    private readonly List<CommandMessage> _commands = [];
    private readonly List<SyncPacket> _syncs = [];
    private readonly List<AudioPacket> _audio = [];
    private readonly List<string> _timingFaults = [];
    private readonly List<MetadataUpdate> _metadata = [];
    private readonly List<ArtworkUpdate> _artwork = [];
    private readonly List<ProgressUpdate> _progress = [];
    private readonly List<double> _volumes = [];
    private readonly List<string> _faults = [];

    private byte[]? _sessionKey;
    private ChaCha20Poly1305? _audioCipher;
    private Stream? _eventStream;
    private int _eventCSeq;

    public FakeAirPlayReceiver(string? password = null)
    {
        _password = password;
        _srp = new ReferenceSrpServer(password);

        // Loopback only, always. A receiver bound to every interface would advertise this
        // machine as an AirPlay speaker to the LAN for as long as the suite runs.
        _rtspListener = new TcpListener(IPAddress.Loopback, 0);
        _rtspListener.Start();
        Port = ((IPEndPoint)_rtspListener.LocalEndpoint).Port;

        _eventListener = new TcpListener(IPAddress.Loopback, 0);
        _eventListener.Start();
        EventPort = ((IPEndPoint)_eventListener.LocalEndpoint).Port;

        _data = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        DataPort = ((IPEndPoint)_data.Client.LocalEndPoint!).Port;

        _control = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        ControlPort = ((IPEndPoint)_control.Client.LocalEndPoint!).Port;

        _timingClient = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        TimingPort = ((IPEndPoint)_timingClient.Client.LocalEndPoint!).Port;

        Start(AcceptRtspLoop, "fake receiver rtsp");
        Start(AcceptEventLoop, "fake receiver events");
        Start(AudioLoop, "fake receiver audio");
        Start(ControlLoop, "fake receiver control");
        Start(TimingLoop, "fake receiver timing");
    }

    public string Host => "127.0.0.1";
    public int Port { get; }
    public int EventPort { get; }
    public int DataPort { get; }
    public int ControlPort { get; }
    public int TimingPort { get; }

    // ── What the sender told us ─────────────────────────────

    /// <summary>Every RTSP request received, in order.</summary>
    public IReadOnlyList<Request> Requests { get { lock (_gate) { return [.. _requests]; } } }

    /// <summary>The methods in order, with the /path for the HTTP-style endpoints.</summary>
    public IReadOnlyList<string> Sequence
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests.Select(r => r.Uri.StartsWith('/') ? $"{r.Method} {r.Uri}" : r.Method)];
            }
        }
    }

    public IReadOnlyList<MetadataUpdate> Metadata { get { lock (_gate) { return [.. _metadata]; } } }
    public IReadOnlyList<ArtworkUpdate> Artwork { get { lock (_gate) { return [.. _artwork]; } } }
    public IReadOnlyList<ProgressUpdate> Progress { get { lock (_gate) { return [.. _progress]; } } }
    public IReadOnlyList<double> Volumes { get { lock (_gate) { return [.. _volumes]; } } }
    public IReadOnlyList<CommandMessage> Commands { get { lock (_gate) { return [.. _commands]; } } }
    public IReadOnlyList<SyncPacket> Syncs { get { lock (_gate) { return [.. _syncs]; } } }
    public IReadOnlyList<AudioPacket> Audio { get { lock (_gate) { return [.. _audio]; } } }

    /// <summary>Anything wrong with the sender's answers to our clock queries.</summary>
    public IReadOnlyList<string> TimingFaults { get { lock (_gate) { return [.. _timingFaults]; } } }

    /// <summary>
    /// Anything that threw inside this receiver while serving a connection.
    ///
    /// A session thread that dies on an unhandled exception takes the whole test host with
    /// it and every result in the run goes with it - which is the opposite of a harness
    /// whose job is to name the byte that was wrong. So the throw is caught, recorded here,
    /// and reported by whatever <see cref="WaitFor"/> is waiting on the reply that never came.
    /// </summary>
    public IReadOnlyList<string> Faults { get { lock (_gate) { return [.. _faults]; } } }

    public int FeedbackCount { get; private set; }
    public bool TornDown { get; private set; }

    /// <summary>The shape of each TEARDOWN, in order: "streams" (stop the stream) or "session" (empty plist, end it).</summary>
    public IReadOnlyList<string> Teardowns { get { lock (_gate) { return [.. _teardowns]; } } }

    private readonly List<string> _teardowns = [];

    /// <summary>
    /// The level this speaker is already set to, in AirPlay's decibels - what a person left
    /// it at before any sender turned up. -15 dB is half scale.
    /// </summary>
    public double CurrentVolumeDb { get; set; } = -15.0;

    /// <summary>How many times the sender asked what our volume is.</summary>
    public int VolumeReads { get; private set; }

    /// <summary>
    /// Whether this receiver implements GET_PARAMETER at all. Plenty don't, and a sender has
    /// to cope with the ones that just say no.
    /// </summary>
    public bool AnswerVolumeReads { get; init; } = true;

    /// <summary>The sender's identity headers, as the receiver would use them to find its control endpoint.</summary>
    public string? DacpId { get; private set; }
    public string? ActiveRemote { get; private set; }

    /// <summary>The session SETUP body, decoded.</summary>
    public Dictionary<string, object?>? SessionSetup { get; private set; }

    /// <summary>The stream SETUP body's first stream entry, decoded.</summary>
    public Dictionary<string, object?>? StreamSetup { get; private set; }

    /// <summary>The sender's own ports, as announced in the SETUP bodies.</summary>
    public int SenderTimingPort { get; private set; }
    public int SenderControlPort { get; private set; }

    /// <summary>How many timing exchanges the sender answered correctly.</summary>
    public int TimingRepliesAccepted { get; private set; }

    // ── Driving the sender ──────────────────────────────────

    /// <summary>Whether the sender has connected to the reverse event channel yet.</summary>
    public bool EventChannelOpen => _eventStream is not null;

    /// <summary>
    /// Pushes an event down the reverse channel and returns the sender's reply line.
    ///
    /// This is how a HomePod tells a sender its volume moved or a button was pressed, and the
    /// sender's answer matters as much as its action: a receiver that doesn't get one treats
    /// the sender as gone and fades the stream out.
    /// </summary>
    public string SendEvent(object? plist, string uri = "/command", string contentType = "application/x-apple-binary-plist")
    {
        var stream = _eventStream ?? throw new InvalidOperationException("The sender has not opened the event channel.");
        var body = BinaryPlist.Write(plist);

        var head = new StringBuilder();
        head.Append($"POST {uri} RTSP/1.0\r\n");
        head.Append($"CSeq: {++_eventCSeq}\r\n");
        head.Append($"Content-Type: {contentType}\r\n");
        head.Append($"Content-Length: {body.Length}\r\n\r\n");

        lock (stream)
        {
            var bytes = Encoding.ASCII.GetBytes(head.ToString());
            stream.Write(bytes, 0, bytes.Length);
            stream.Write(body, 0, body.Length);
            stream.Flush();

            return ReadHead(stream) ?? throw new IOException("The sender closed the event channel without replying.");
        }
    }

    /// <summary>The volume-changed event a receiver sends when its own control is used.</summary>
    public string SendVolumeEvent(double db) => SendEvent(new Dictionary<string, object?>
    {
        ["type"] = "updateInfo",
        ["params"] = new Dictionary<string, object?> { ["volume"] = db },
    });

    /// <summary>
    /// The transport-command event: two string keys at the root, "type" and "value". The
    /// value is a four-character code, and the ones a receiver actually sends are "play",
    /// "paus", "plps" (toggle), "nitm" (next) and "pitm" (previous).
    /// </summary>
    public string SendCommandEvent(string command) => SendEvent(new Dictionary<string, object?>
    {
        ["type"] = "sendMediaRemoteCommand",
        ["value"] = command,
    });

    /// <summary>The stream handle this receiver hands out in the stream SETUP reply.</summary>
    public const long StreamId = 424242L;

    /// <summary>
    /// Calls the sender's DACP endpoint the way a receiver does: a bare HTTP GET carrying
    /// the Active-Remote token it was given.
    /// </summary>
    public DacpReply SendDacp(int port, string path) => SendDacpSequence(port, path)[0];

    /// <summary>
    /// The same calls, all down ONE connection, which is how a receiver really uses this
    /// endpoint: it keeps the socket and polls dmcp.volume about once a second as a health
    /// check. A sender that answers the first request and then closes starts the count of
    /// bad answers that ends in "Controls Not Available".
    /// </summary>
    public IReadOnlyList<DacpReply> SendDacpSequence(int port, params string[] paths)
    {
        using var client = new TcpClient();
        client.Connect(IPAddress.Loopback, port);
        using var stream = client.GetStream();

        var replies = new List<DacpReply>(paths.Length);
        foreach (var path in paths)
        {
            // Exactly what a receiver sends: a bare GET with a Host and the token it was
            // given. No User-Agent, no session id, no body.
            var request = Encoding.ASCII.GetBytes(
                $"GET {path} HTTP/1.1\r\nHost: {Host}\r\nActive-Remote: {ActiveRemote}\r\n\r\n");
            stream.Write(request, 0, request.Length);
            stream.Flush();

            replies.Add(ReadDacpReply(stream));
        }

        return replies;
    }

    private static DacpReply ReadDacpReply(Stream stream)
    {
        var received = new List<byte>(4096);
        var buffer = new byte[2048];
        while (true)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                break;
            }

            received.AddRange(buffer.AsSpan(0, read));

            // Stop once the declared body has arrived - a remote that keeps reading past the
            // end waits for its own timeout and calls the command failed.
            var all = received.ToArray();
            var split = FindHeadEnd(all);
            if (split < 0)
            {
                continue;
            }

            var head = Encoding.ASCII.GetString(all, 0, split);
            var length = int.TryParse(HeaderIn(head, "Content-Length"), out var n) ? n : -1;
            if (length >= 0 && all.Length - split - 4 >= length)
            {
                return new DacpReply(head, all[(split + 4)..(split + 4 + length)]);
            }
        }

        var final = received.ToArray();
        var end = FindHeadEnd(final);
        return end < 0
            ? new DacpReply(Encoding.ASCII.GetString(final), [])
            : new DacpReply(Encoding.ASCII.GetString(final, 0, end), final[(end + 4)..]);
    }

    /// <summary>
    /// One answer from the sender's control endpoint. The body is kept as BYTES - a DMAP
    /// reply is big-endian binary, and reading it as text turns every value over 127 into a
    /// question mark.
    /// </summary>
    internal sealed record DacpReply(string Head, byte[] Body)
    {
        public int Status
        {
            get
            {
                var parts = Head.Split("\r\n")[0].Split(' ');
                return parts.Length > 1 && int.TryParse(parts[1], out var code) ? code : 0;
            }
        }

        public string? Header(string name) => HeaderIn(Head, name);
    }

    private static int FindHeadEnd(byte[] data)
    {
        for (var i = 0; i + 4 <= data.Length; i++)
        {
            if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
            {
                return i;
            }
        }

        return -1;
    }

    private static string? HeaderIn(string head, string name)
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

    // ── Waiting ─────────────────────────────────────────────

    /// <summary>
    /// Blocks until a condition holds, or fails the test with what the receiver DID see.
    /// Polling rather than signalling on purpose: every one of these is waiting on a network
    /// event that either arrives in milliseconds or reveals a bug by never arriving.
    /// </summary>
    public void WaitFor(Func<bool> condition, string what, int timeoutMs = 10000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return;
            }
            Thread.Sleep(20);
        }

        var faults = Faults;
        var blame = faults.Count == 0 ? "" : $" Receiver faults: {string.Join("; ", faults)}";
        throw new TimeoutException($"Timed out waiting for {what}. Requests so far: {string.Join(", ", Sequence)}.{blame}");
    }

    // ── Records ─────────────────────────────────────────────

    internal sealed record Request(string Method, string Uri, Dictionary<string, string> Headers, byte[] Body)
    {
        public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
        public string? ContentType => Header("Content-Type");
        public string Text => Encoding.UTF8.GetString(Body);
    }

    /// <summary>One now-playing announcement, with the timeline anchor it carried.</summary>
    internal sealed record MetadataUpdate(DmapReader Dmap, string? RtpInfo, string Uri)
    {
        public string? Title => Dmap.Text("minm");
        public string? Artist => Dmap.Text("asar");
        public string? Album => Dmap.Text("asal");
        public long? DurationMs => Dmap.Number("astm");
        public uint? RtpTime => ParseRtpTime(RtpInfo);
    }

    internal sealed record ArtworkUpdate(byte[] Bytes, string ContentType, string? RtpInfo)
    {
        public uint? RtpTime => ParseRtpTime(RtpInfo);
    }

    internal sealed record ProgressUpdate(uint Display, uint Position, uint End)
    {
        public double DurationSeconds => (End - Display) / 44100.0;
    }

    /// <summary>A POST /command body: the envelope type and the decoded plist.</summary>
    internal sealed record CommandMessage(string? Type, Dictionary<string, object?>? Plist, byte[] Raw)
    {
        /// <summary>The nested params dictionary, which is where every payload actually lives.</summary>
        public Dictionary<string, object?>? Params
            => Plist?.TryGetValue("params", out var value) == true ? value as Dictionary<string, object?> : null;
    }

    internal sealed record SyncPacket(uint NowTimestamp, ulong Ntp, uint NextTimestamp, bool First, long ArrivedAt);

    internal sealed record AudioPacket(ushort Sequence, uint Timestamp, uint Ssrc, bool Marker, byte[]? Pcm, string? Fault, long ArrivedAt);

    private static uint? ParseRtpTime(string? rtpInfo)
    {
        if (rtpInfo is null)
        {
            return null;
        }

        foreach (var part in rtpInfo.Split(';'))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && pair[0].Trim().Equals("rtptime", StringComparison.OrdinalIgnoreCase)
                && uint.TryParse(pair[1].Trim(), out var value))
            {
                return value;
            }
        }

        return null;
    }

    // ── RTSP ────────────────────────────────────────────────

    private void AcceptRtspLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = _rtspListener.AcceptTcpClient();
            }
            catch
            {
                return;
            }

            // Each connection gets its own thread and its own pairing state: the sink probes
            // classic RAOP on a separate connection before falling back, so more than one is
            // normal rather than exceptional.
            //
            // Guarded like the other loops: an unhandled exception on a dedicated thread ends
            // the test HOST, which loses every result in the run instead of reddening one test.
            var thread = new Thread(() =>
            {
                try
                {
                    Serve(client);
                }
                catch (Exception ex)
                {
                    lock (_gate)
                    {
                        _faults.Add($"session thread: {ex}");
                    }
                }
            })
            {
                IsBackground = true,
                Name = "fake receiver session",
            };
            thread.Start();
        }
    }

    private void Serve(TcpClient client)
    {
        using (client)
        {
            var stream = new HapCryptoStream(client.GetStream());
            var paired = false;
            byte[]? clientPublicKey = null;

            while (!_cts.IsCancellationRequested)
            {
                var request = ReadRequest(stream);
                if (request is null)
                {
                    return;
                }

                lock (_gate)
                {
                    _requests.Add(request);
                }

                if (request.Header("DACP-ID") is { } dacp)
                {
                    DacpId = dacp;
                }
                if (request.Header("Active-Remote") is { } remote)
                {
                    ActiveRemote = remote;
                }

                var cSeq = request.Header("CSeq") ?? "1";
                var enableEncryptionAfterReply = false;

                switch (request.Method, request.Uri)
                {
                    case ("GET", "/info"):
                    {
                        WriteResponse(stream, cSeq, body: InfoPlist(), contentType: "application/x-apple-binary-plist");
                    }
                    break;

                    case ("POST", "/pair-pin-start"):
                    {
                        WriteResponse(stream, cSeq);
                    }
                    break;

                    case ("POST", "/pair-setup"):
                    {
                        var tlv = Tlv8.Decode(request.Body);
                        var state = tlv.TryGetValue(Tlv8.State, out var s) && s.Length > 0 ? s[0] : (byte)0;

                        if (state == 0x01)
                        {
                            WriteResponse(stream, cSeq, contentType: "application/octet-stream", body: Tlv8.Encode(
                                (Tlv8.State, [0x02]),
                                (Tlv8.PublicKey, _srp.PublicKey),
                                (Tlv8.Salt, _srp.Salt)));
                        }
                        else if (state == 0x03)
                        {
                            if (!tlv.TryGetValue(Tlv8.PublicKey, out clientPublicKey) || !tlv.TryGetValue(Tlv8.Proof, out var proof))
                            {
                                Reject("a pair-setup M3 with no public key or proof");
                                WriteResponse(stream, cSeq, 400, "Bad Request");
                                break;
                            }

                            if (!_srp.VerifyClientProof(clientPublicKey, proof))
                            {
                                // What a receiver with the wrong password answers: HTTP 200
                                // carrying a TLV error, not an HTTP failure.
                                WriteResponse(stream, cSeq, contentType: "application/octet-stream", body: Tlv8.Encode(
                                    (Tlv8.State, [0x04]),
                                    (Tlv8.Error, [0x02])));
                                break;
                            }

                            _sessionKey = _srp.ComputeSessionKey(clientPublicKey);
                            WriteResponse(stream, cSeq, contentType: "application/octet-stream", body: Tlv8.Encode(
                                (Tlv8.State, [0x04]),
                                (Tlv8.Proof, _srp.ComputeServerProof(clientPublicKey, proof))));

                            paired = true;
                            enableEncryptionAfterReply = true;
                        }
                        else
                        {
                            WriteResponse(stream, cSeq, 400, "Bad Request");
                        }
                    }
                    break;

                    case ("SETUP", _):
                    {
                        HandleSetup(stream, cSeq, request, paired);
                    }
                    break;

                    case ("RECORD", _) when paired:
                    {
                        // A receiver answers RECORD with the latency it will actually apply -
                        // the sender needs it to know how far ahead of the speaker it is.
                        WriteResponse(stream, cSeq, extra: [("Audio-Latency", "88200")]);
                    }
                    break;

                    case ("SET_PARAMETER", _) when paired:
                    {
                        HandleSetParameter(request);
                        WriteResponse(stream, cSeq);
                    }
                    break;

                    // A sender asking what this speaker is set to. Answering is how it can
                    // adopt the level someone chose on the speaker itself rather than
                    // overriding it the moment it connects.
                    case ("GET_PARAMETER", _) when paired && request.Text.Contains("volume", StringComparison.OrdinalIgnoreCase):
                    {
                        VolumeReads++;

                        if (AnswerVolumeReads)
                        {
                            WriteResponse(stream, cSeq, contentType: "text/parameters",
                                body: Encoding.ASCII.GetBytes(FormattableString.Invariant($"volume: {CurrentVolumeDb:0.000000}\r\n")));
                        }
                        else
                        {
                            WriteResponse(stream, cSeq, 400, "Bad Request");
                        }
                    }
                    break;

                    case ("POST", "/feedback") when paired:
                    {
                        FeedbackCount++;
                        WriteResponse(stream, cSeq);
                    }
                    break;

                    case ("POST", "/command") when paired:
                    {
                        var status = HandleCommand(request);
                        WriteResponse(stream, cSeq, status, status == 200 ? "OK" : "Bad Request");
                    }
                    break;

                    case ("FLUSH", _) when paired:
                    case ("OPTIONS", _) when paired:
                    {
                        WriteResponse(stream, cSeq);
                    }
                    break;

                    case ("TEARDOWN", _):
                    {
                        // shairport-sync's handle_teardown_2, faithfully: a body naming
                        // streams stops the stream and LEAVES THE CONNECTION OPEN; the
                        // empty plist ends the session. iOS sends both, in that order, and
                        // a receiver that conflated them could never pin the two-step
                        // goodbye.
                        Dictionary<string, object?>? teardown = null;
                        try
                        {
                            teardown = request.Body.Length > 0 ? BinaryPlist.Read(request.Body) as Dictionary<string, object?> : null;
                        }
                        catch
                        {
                            // an unreadable body reads as the empty form, like a bare AP1 TEARDOWN
                        }

                        var streamsOnly = teardown is not null && teardown.ContainsKey("streams");
                        lock (_gate)
                        {
                            _teardowns.Add(streamsOnly ? "streams" : "session");
                        }

                        WriteResponse(stream, cSeq);

                        if (!streamsOnly)
                        {
                            TornDown = true;
                            return;
                        }
                    }
                    break;

                    default:
                    {
                        // Everything else before pairing is 401 - which is exactly how a real
                        // AirPlay 2 device turns a classic RAOP sender away.
                        if (!paired)
                        {
                            Reject($"{request.Method} {request.Uri} before pair-setup");
                        }

                        WriteResponse(stream, cSeq, paired ? 200 : 401, paired ? "OK" : "Unauthorized");
                    }
                    break;
                }

                if (enableEncryptionAfterReply && _sessionKey is { } secret)
                {
                    // The receiver's halves are the sender's, swapped: what it writes we read.
                    var (senderWrite, senderRead) = HapCryptoStream.DeriveControlKeys(secret);
                    stream.Enable(senderRead, senderWrite);
                }
            }
        }
    }

    private void HandleSetup(Stream stream, string cSeq, Request request, bool paired)
    {
        if (!paired)
        {
            WriteResponse(stream, cSeq, 401, "Unauthorized");
            return;
        }

        Dictionary<string, object?>? body;
        try
        {
            body = BinaryPlist.Read(request.Body) as Dictionary<string, object?>;
        }
        catch (InvalidDataException)
        {
            // A SETUP whose body isn't a plist is a sender bug worth naming, not a crash.
            Reject("SETUP with a body that is not a readable plist");
            WriteResponse(stream, cSeq, 400, "Bad Request");
            return;
        }

        if (body is not null && body.TryGetValue("streams", out var streamsValue) && streamsValue is List<object?> { Count: > 0 } streams)
        {
            StreamSetup = streams[0] as Dictionary<string, object?>;

            if (StreamSetup?.TryGetValue("shk", out var key) == true && key is byte[] { Length: 32 } shk)
            {
                _audioCipher = new ChaCha20Poly1305(shk);
            }

            if (StreamSetup?.TryGetValue("controlPort", out var senderControl) == true && senderControl is long port)
            {
                SenderControlPort = (int)port;
            }

            WriteResponse(stream, cSeq, contentType: "application/x-apple-binary-plist", body: BinaryPlist.Write(new Dictionary<string, object?>
            {
                ["streams"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = 96L,
                        ["dataPort"] = (long)DataPort,
                        ["controlPort"] = (long)ControlPort,
                        ["audioBufferSize"] = 8388608L,

                        // The receiver's own handle for this stream. The raw MediaRemote
                        // tunnel has to quote it back in a header.
                        ["streamID"] = StreamId,
                    },
                },
            }));
            return;
        }

        SessionSetup = body;
        if (body?.TryGetValue("timingPort", out var timing) == true && timing is long timingPort)
        {
            SenderTimingPort = (int)timingPort;
        }

        WriteResponse(stream, cSeq, contentType: "application/x-apple-binary-plist",
            body: BinaryPlist.Write(new Dictionary<string, object?>
            {
                ["eventPort"] = (long)EventPort,
                ["timingPort"] = (long)TimingPort,
            }),
            extra: [("Session", "1")]);
    }

    private void HandleSetParameter(Request request)
    {
        var contentType = request.ContentType ?? "";
        var rtpInfo = request.Header("RTP-Info");

        if (contentType.StartsWith("application/x-dmap-tagged", StringComparison.OrdinalIgnoreCase))
        {
            var dmap = DmapReader.Parse(request.Body);
            lock (_gate)
            {
                _metadata.Add(new MetadataUpdate(dmap, rtpInfo, request.Uri));
            }
            return;
        }

        if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            lock (_gate)
            {
                _artwork.Add(new ArtworkUpdate(request.Body, contentType, rtpInfo));
            }
            return;
        }

        foreach (var line in request.Text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();

            if (name.Equals("volume", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var db))
            {
                lock (_gate)
                {
                    _volumes.Add(db);
                }
            }
            else if (name.Equals("progress", StringComparison.OrdinalIgnoreCase))
            {
                var parts = value.Split('/');
                if (parts.Length == 3
                    && uint.TryParse(parts[0], out var display)
                    && uint.TryParse(parts[1], out var position)
                    && uint.TryParse(parts[2], out var end))
                {
                    lock (_gate)
                    {
                        _progress.Add(new ProgressUpdate(display, position, end));
                    }
                }
            }
        }
    }

    /// <summary>
    /// The command types a modern Apple receiver recognises on this endpoint. Anything else
    /// is a 400 - so this list is the difference between a push that lands and one that is
    /// refused, and it is not extensible by wishing.
    /// </summary>
    private static readonly HashSet<string> KnownCommandTypes = new(StringComparer.Ordinal)
    {
        "APValeria", "duckAudio", "unduckAudio", "mute", "unmute", "changeRelativeVolume",
        "performPWDKeyExchange", "updateMRNowPlayingInfo", "updateMRSupportedCommands",
        "setMRInfo", "setSenderDisplayLatencyMs", "updateMRPlaybackState",
        "updateMRNowPlayingClient",
    };

    /// <summary>
    /// What MediaRemote is holding: the now-playing state as the receiver would actually
    /// render it, built only from keys it recognises.
    ///
    /// This is the whole point of the harness. A receiver answers 200 to a push whose keys it
    /// cannot name and then displays nothing, so "the request succeeded" proves nothing at
    /// all - only what ends up in here does.
    ///
    /// Written on a session thread and read from the test thread, so it lives behind the same
    /// lock the request log does and is handed out as a snapshot: a Dictionary cleared and
    /// repopulated while a WaitFor polls it is free to throw, or to show a half-applied tile -
    /// a title with the artwork not yet back.
    /// </summary>
    public IReadOnlyDictionary<string, object?> NowPlaying { get { lock (_gate) { return new Dictionary<string, object?>(_nowPlaying); } } }

    /// <summary>The transport controls the sender says it accepts, by MediaRemote id.</summary>
    public IReadOnlyList<long> SupportedCommands { get { lock (_gate) { return [.. _supportedCommands]; } } }

    /// <summary>The playback state last pushed - 1 playing, 2 paused, 3 stopped.</summary>
    public long? PlaybackState { get { lock (_gate) { return _playbackState; } } }

    /// <summary>The serialized NowPlayingClient the sender attributed the item to.</summary>
    public byte[]? NowPlayingClient { get { lock (_gate) { return _nowPlayingClient; } } }

    private readonly Dictionary<string, object?> _nowPlaying = [];
    private readonly List<long> _supportedCommands = [];
    private readonly List<string> _rejected = [];
    private long? _playbackState;
    private byte[]? _nowPlayingClient;

    /// <summary>
    /// Seeds the now-playing state with somebody else's item - a phone, another app - which
    /// is what a speaker in a real house is always holding when a new sender arrives.
    /// </summary>
    public void SeedForeignNowPlaying(string title)
    {
        lock (_gate)
        {
            _nowPlaying[NowPlayingKey + "Title"] = title;
            _nowPlaying[NowPlayingKey + "Artist"] = "Someone Else";
            _nowPlaying[NowPlayingKey + "ArtworkData"] = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0xFF, 0xD9 };
        }
    }

    /// <summary>Requests this receiver refused, and why - a 400 the sender may have ignored.</summary>
    public IReadOnlyList<string> Rejected { get { lock (_gate) { return [.. _rejected]; } } }

    private void Reject(string reason)
    {
        lock (_gate)
        {
            _rejected.Add(reason);
        }
    }

    private const string NowPlayingKey = "kMRMediaRemoteNowPlayingInfo";

    /// <summary>The title MediaRemote would show, or null if nothing legible ever arrived.</summary>
    public string? DisplayTitle => Displayed<string>(NowPlayingKey + "Title");
    public string? DisplayArtist => Displayed<string>(NowPlayingKey + "Artist");
    public string? DisplayAlbum => Displayed<string>(NowPlayingKey + "Album");
    public byte[]? DisplayArtwork => Displayed<byte[]>(NowPlayingKey + "ArtworkData");

    private T? Displayed<T>(string key) where T : class
    {
        lock (_gate)
        {
            return _nowPlaying.TryGetValue(key, out var value) ? value as T : null;
        }
    }

    /// <summary>Handles POST /command, and returns the status to answer with.</summary>
    private int HandleCommand(Request request)
    {
        Dictionary<string, object?>? plist = null;
        try
        {
            plist = BinaryPlist.Read(request.Body) as Dictionary<string, object?>;
        }
        catch (InvalidDataException)
        {
            // Recorded raw so a test can say exactly what arrived instead of "nothing did".
        }

        var type = plist?.TryGetValue("type", out var value) == true ? value as string : null;
        var parameters = plist?.TryGetValue("params", out var p) == true ? p as Dictionary<string, object?> : null;

        lock (_gate)
        {
            _commands.Add(new CommandMessage(type, plist, request.Body));
        }

        if (plist is null || request.Body.Length == 0)
        {
            Reject("POST /command with no readable plist body");
            return 400;
        }

        if (type is null)
        {
            // The typeless shape is the raw MediaRemote tunnel, and it is the one that has to
            // say which stream it belongs to.
            if (request.Header("X-Apple-StreamID") is null)
            {
                Reject("a typeless POST /command with no X-Apple-StreamID");
                return 400;
            }

            return parameters?.ContainsKey("data") == true ? 200 : 400;
        }

        if (!KnownCommandTypes.Contains(type))
        {
            Reject($"POST /command with unknown type '{type}'");
            return 400;
        }

        if (parameters is null)
        {
            Reject($"POST /command '{type}' with no params");
            return 400;
        }

        switch (type)
        {
            case "updateMRNowPlayingInfo":
            {
                ApplyNowPlaying(parameters);
            }
            break;

            case "updateMRSupportedCommands":
            {
                // Applied as ONE list under the lock: a poller that catches the gap between
                // the clear and the repopulate reads "this sender has no controls".
                var accepted = new List<long>();
                var faults = new List<string>();

                if (parameters.TryGetValue("mrSupportedCommandsFromSender", out var list) && list is List<object?> commands)
                {
                    foreach (var entry in commands)
                    {
                        // Each element is a standalone plist carried as data, not a nested
                        // dictionary - a receiver deserializes each one on its own.
                        if (entry is byte[] blob && BinaryPlist.Read(blob) is Dictionary<string, object?> command
                            && command.TryGetValue("kCommandInfoCommandKey", out var id) && id is long number)
                        {
                            accepted.Add(number);
                        }
                        else
                        {
                            faults.Add("a supported-command entry that was not a plist blob");
                        }
                    }
                }

                lock (_gate)
                {
                    _supportedCommands.Clear();
                    _supportedCommands.AddRange(accepted);
                    _rejected.AddRange(faults);
                }
            }
            break;

            case "updateMRPlaybackState":
            {
                lock (_gate)
                {
                    _playbackState = parameters.TryGetValue("mrPlaybackState", out var state) && state is long s ? s : null;
                }
            }
            break;

            case "updateMRNowPlayingClient":
            {
                lock (_gate)
                {
                    _nowPlayingClient = parameters.TryGetValue("mrNowPlayingClient", out var client) ? client as byte[] : null;
                }
            }
            break;
        }

        return 200;
    }

    /// <summary>
    /// Applies a now-playing push the way MediaRemote does: only keys it knows, and a
    /// "replace" policy that discards everything the push left out - artwork included.
    /// </summary>
    private void ApplyNowPlaying(Dictionary<string, object?> parameters)
    {
        if (!parameters.TryGetValue("params", out var inner) || inner is not Dictionary<string, object?> info)
        {
            Reject("updateMRNowPlayingInfo with no inner params");
            return;
        }

        var replace = (parameters.TryGetValue("mergePolicy", out var policy) ? policy as string : null) == "replace";

        var accepted = new Dictionary<string, object?>();
        var faults = new List<string>();

        foreach (var (key, value) in info)
        {
            if (!key.StartsWith(NowPlayingKey, StringComparison.Ordinal))
            {
                // Silently dropped by the real thing, which is exactly why it is worth
                // recording: a push of "Title" is accepted, answered 200, and shows nothing.
                faults.Add($"now-playing key '{key}' is not a MediaRemote key and was discarded");
                continue;
            }

            if (TypeFault(key, value) is { } fault)
            {
                faults.Add(fault);
                continue;
            }

            accepted[key] = value;
        }

        // The whole push lands at once. Applied key by key, a poller can see the new title
        // with the artwork of the push still missing, which reads as "the cover was lost".
        lock (_gate)
        {
            if (replace)
            {
                _nowPlaying.Clear();
            }

            foreach (var (key, value) in accepted)
            {
                _nowPlaying[key] = value;
            }

            _rejected.AddRange(faults);
        }
    }

    /// <summary>The value types MediaRemote insists on, checked as it checks them.</summary>
    private static string? TypeFault(string key, object? value) => key[NowPlayingKey.Length..] switch
    {
        "Duration" or "ElapsedTime" or "PlaybackRate" or "DefaultPlaybackRate" when value is not double
            => $"{key} must be a real, got {value?.GetType().Name ?? "null"}",
        "Timestamp" when value is not DateTime
            => $"{key} must be a date, got {value?.GetType().Name ?? "null"}",
        "UniqueIdentifier" when value is not long
            => $"{key} must be an integer, got {value?.GetType().Name ?? "null"}",
        "ArtworkIdentifier" when value is not string
            => $"{key} must be a string, got {value?.GetType().Name ?? "null"}",
        "ArtworkData" when value is not byte[]
            => $"{key} must be data, got {value?.GetType().Name ?? "null"}",
        "Title" or "Artist" or "Album" or "MediaType" or "ArtworkMIMEType" when value is not string
            => $"{key} must be a string, got {value?.GetType().Name ?? "null"}",
        _ => null,
    };

    private byte[] InfoPlist() => BinaryPlist.Write(new Dictionary<string, object?>
    {
        ["deviceid"] = "AA:BB:CC:11:22:33",
        // Real-device features MINUS bit 41 (SupportsPTP): a fake advertising PTP would
        // make every offline test session start a real PTP grandmaster on the test box's
        // ports 319/320. The PTP path is verified on hardware via the group-join oracle;
        // this receiver models the NTP path deterministically.
        ["features"] = 496155769145856L & ~(1L << 41),
        ["model"] = "AudioAccessory5,1",
        ["name"] = "Fake Receiver",
        ["protovers"] = "1.1",
        ["srcvers"] = "550.10",
        ["statusFlags"] = _password is null ? 4L : 0x84L,
        ["audioLatencies"] = new List<object?>
        {
            new Dictionary<string, object?>
            {
                ["type"] = 100L,
                ["audioType"] = "default",
                ["inputLatencyMicros"] = 0L,
                ["outputLatencyMicros"] = 0L,
            },
        },
    });

    // ── The reverse event channel ───────────────────────────

    private void AcceptEventLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = _eventListener.AcceptTcpClient();
            }
            catch
            {
                return;
            }

            var stream = new HapCryptoStream(client.GetStream());
            if (_sessionKey is { } secret)
            {
                var (senderWrite, senderRead) = HapCryptoStream.DeriveEventKeys(secret);
                stream.Enable(senderRead, senderWrite);
            }

            _eventStream = stream;
        }
    }

    // ── UDP ─────────────────────────────────────────────────

    private void AudioLoop()
    {
        var any = new IPEndPoint(IPAddress.Any, 0);
        while (!_cts.IsCancellationRequested)
        {
            byte[] packet;
            try
            {
                packet = _data.Receive(ref any);
            }
            catch
            {
                return;
            }

            var arrived = Environment.TickCount64;
            if (packet.Length < 12)
            {
                lock (_gate)
                {
                    _audio.Add(new AudioPacket(0, 0, 0, false, null, $"packet is {packet.Length} bytes", arrived));
                }
                continue;
            }

            var marker = (packet[1] & 0x80) != 0;
            var sequence = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2));
            var timestamp = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4));
            var ssrc = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(8));

            byte[]? pcm = null;
            string? fault = null;

            try
            {
                if ((packet[1] & 0x7F) != 0x60)
                {
                    throw new InvalidDataException($"payload type is 0x{packet[1] & 0x7F:X2}, expected 0x60");
                }

                if (_audioCipher is null)
                {
                    throw new InvalidDataException("audio arrived before the stream SETUP carried a key");
                }

                // ciphertext | 16-byte tag | 8-byte nonce tail, with the RTP header's
                // timestamp and ssrc words authenticated alongside.
                var body = packet.AsSpan(12);
                if (body.Length < 24)
                {
                    throw new InvalidDataException($"sealed payload is only {body.Length} bytes");
                }

                var cipherLength = body.Length - 24;
                var nonce = new byte[12];
                body[^8..].CopyTo(nonce.AsSpan(4));

                var plain = new byte[cipherLength];
                _audioCipher.Decrypt(nonce, body[..cipherLength], body.Slice(cipherLength, 16), plain, packet.AsSpan(4, 8));

                pcm = AlacRaw.Decode(plain, FramesPerPacket);
            }
            catch (Exception ex)
            {
                fault = ex.Message;
            }

            lock (_gate)
            {
                _audio.Add(new AudioPacket(sequence, timestamp, ssrc, marker, pcm, fault, arrived));
            }
        }
    }

    /// <summary>
    /// Asks the sender to send a run of packets again, as a receiver does after losing one.
    /// </summary>
    public void RequestResend(ushort first, ushort count)
    {
        var request = new byte[8];
        request[0] = 0x80;
        request[1] = 0x55 | 0x80;
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), first);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(6), count);

        _control.Send(request, request.Length, new IPEndPoint(IPAddress.Loopback, SenderControlPort));
    }

    /// <summary>Packets the sender sent again on request, by sequence number.</summary>
    public List<ushort> Resends { get; } = [];

    private void ControlLoop()
    {
        var any = new IPEndPoint(IPAddress.Any, 0);
        while (!_cts.IsCancellationRequested)
        {
            byte[] packet;
            try
            {
                packet = _control.Receive(ref any);
            }
            catch
            {
                return;
            }

            // A retransmission: four bytes of wrapper, then the original audio packet exactly
            // as it first went out.
            if (packet.Length > 16 && (packet[1] & 0x7F) == 0x56)
            {
                lock (_gate)
                {
                    Resends.Add(BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2)));
                }
                continue;
            }

            if (packet.Length < 20 || (packet[1] & 0x7F) != 0x54)
            {
                continue;
            }

            lock (_gate)
            {
                _syncs.Add(new SyncPacket(
                    BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4)),
                    BinaryPrimitives.ReadUInt64BigEndian(packet.AsSpan(8)),
                    BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(16)),
                    (packet[0] & 0x10) != 0,
                    Environment.TickCount64));
            }
        }
    }

    /// <summary>
    /// Queries the sender's clock the way a receiver does, and checks the answer.
    ///
    /// A receiver asks before it will start a realtime stream at all, so an unanswered query
    /// is not a missing nicety - it is the stream never starting.
    /// </summary>
    private void TimingLoop()
    {
        _timingClient.Client.ReceiveTimeout = 1000;
        var sequence = (ushort)0;

        while (!_cts.IsCancellationRequested)
        {
            if (SenderTimingPort == 0)
            {
                Thread.Sleep(20);
                continue;
            }

            var origin = RaopPackets.NtpNow();
            var request = new byte[32];
            request[0] = 0x80;
            request[1] = 0x52 | 0x80;
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), ++sequence);
            BinaryPrimitives.WriteUInt64BigEndian(request.AsSpan(24), origin);

            try
            {
                _timingClient.Send(request, request.Length, new IPEndPoint(IPAddress.Loopback, SenderTimingPort));

                var any = new IPEndPoint(IPAddress.Any, 0);
                var reply = _timingClient.Receive(ref any);

                var fault = ValidateTimingReply(reply, origin);
                if (fault is null)
                {
                    TimingRepliesAccepted++;
                }
                else
                {
                    lock (_gate)
                    {
                        _timingFaults.Add(fault);
                    }
                }
            }
            catch (SocketException)
            {
                lock (_gate)
                {
                    _timingFaults.Add("the sender did not answer a clock query within 1s");
                }
            }
            catch
            {
                return;
            }

            Thread.Sleep(500);
        }
    }

    private static string? ValidateTimingReply(byte[] reply, ulong origin)
    {
        if (reply.Length < 32)
        {
            return $"timing reply is {reply.Length} bytes, expected 32";
        }

        if ((reply[1] & 0x7F) != 0x53)
        {
            return $"timing reply payload type is 0x{reply[1] & 0x7F:X2}, expected 0x53";
        }

        var echoed = BinaryPrimitives.ReadUInt64BigEndian(reply.AsSpan(8));
        if (echoed != origin)
        {
            return $"timing reply echoed {echoed:X16} instead of our transmit stamp {origin:X16}";
        }

        var received = BinaryPrimitives.ReadUInt64BigEndian(reply.AsSpan(16));
        var transmit = BinaryPrimitives.ReadUInt64BigEndian(reply.AsSpan(24));
        if (received == 0 || transmit == 0)
        {
            return "timing reply carried a zero clock stamp";
        }

        if (transmit < received)
        {
            return "timing reply transmitted before it received";
        }

        return null;
    }

    // ── Plumbing ────────────────────────────────────────────

    private void Start(Action loop, string name)
    {
        var thread = new Thread(() =>
        {
            try
            {
                loop();
            }
            catch (Exception)
            {
                // A socket closed under a blocked read during teardown - not a failure.
            }
        })
        {
            IsBackground = true,
            Name = name,
        };

        _threads.Add(thread);
        thread.Start();
    }

    private static Request? ReadRequest(Stream stream)
    {
        var head = ReadHead(stream);
        if (head is null)
        {
            return null;
        }

        var lines = head.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            // A bare blank line: nothing to serve, and reading it as a request line would
            // take the session thread down with an index out of range.
            return null;
        }

        var parts = lines[0].Split(' ');

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0)
            {
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        var length = headers.TryGetValue("Content-Length", out var value) && int.TryParse(value, out var n) ? n : 0;
        var body = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = stream.Read(body, offset, length - offset);
            if (read == 0)
            {
                return null;
            }
            offset += read;
        }

        return new Request(parts[0], parts.Length > 1 ? parts[1] : "", headers, body);
    }

    /// <summary>Reads to the blank line that ends an HTTP-shaped head, or null at EOF.</summary>
    private static string? ReadHead(Stream stream)
    {
        var head = new List<byte>(512);
        var one = new byte[1];

        while (true)
        {
            int read;
            try
            {
                read = stream.Read(one, 0, 1);
            }
            catch
            {
                return null;
            }

            if (read == 0)
            {
                return null;
            }

            head.Add(one[0]);
            var n = head.Count;
            if (n >= 4 && head[n - 4] == '\r' && head[n - 3] == '\n' && head[n - 2] == '\r' && head[n - 1] == '\n')
            {
                return Encoding.ASCII.GetString(head.ToArray());
            }
        }
    }

    private static void WriteResponse(
        Stream stream,
        string cSeq,
        int status = 200,
        string statusText = "OK",
        string? contentType = null,
        byte[]? body = null,
        IEnumerable<(string Name, string Value)>? extra = null)
    {
        var head = new StringBuilder();
        head.Append($"RTSP/1.0 {status} {statusText}\r\n");
        head.Append($"CSeq: {cSeq}\r\n");
        head.Append("Server: AirTunes/550.10\r\n");

        foreach (var (name, value) in extra ?? [])
        {
            head.Append($"{name}: {value}\r\n");
        }

        if (contentType is not null)
        {
            head.Append($"Content-Type: {contentType}\r\n");
        }

        head.Append($"Content-Length: {body?.Length ?? 0}\r\n\r\n");

        var bytes = Encoding.ASCII.GetBytes(head.ToString());
        stream.Write(bytes, 0, bytes.Length);
        if (body is { Length: > 0 })
        {
            stream.Write(body, 0, body.Length);
        }
        stream.Flush();
    }

    public void Dispose()
    {
        _cts.Cancel();

        try { _rtspListener.Stop(); } catch { /* already down */ }
        try { _eventListener.Stop(); } catch { /* already down */ }

        _data.Dispose();
        _control.Dispose();
        _timingClient.Dispose();
        _audioCipher?.Dispose();
        _eventStream?.Dispose();
        _cts.Dispose();
    }
}
