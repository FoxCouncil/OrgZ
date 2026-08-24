// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Serilog;

namespace OrgZ.Services.AudioOutput.AirPlay;

/// <summary>
/// The PTP grandmaster an AirPlay 2 receiver synchronizes to when a session says
/// <c>timingProtocol: PTP</c> - the timing model every real Apple sender uses, and the one
/// whose group a receiver actually JOINS (the gid/igl flip in its mDNS broadcast).
///
/// This is not IEEE-1588. It is the AirPlay subset, modelled on the reference sender's
/// implementation: a self-appointed grandmaster that never runs a master election - it
/// announces itself as a primary reference and ignores every rival announce - speaks
/// UNICAST only, and only to peers explicitly registered from a session's timing-peer
/// exchange. Multicast PTP on the LAN is left completely alone.
///
/// One per process, like the DACP endpoint: the clock identity is an identity of this
/// SENDER, shared by however many receivers are being driven.
/// </summary>
internal sealed class PtpClock : IDisposable
{
    private static readonly ILogger _log = Logging.For("Ptp");

    /// <summary>PTP event port - Sync and everything timestamp-critical.</summary>
    private const int EventPort = 319;

    /// <summary>PTP general port - Announce, Follow-Up, Delay-Resp, Signaling.</summary>
    private const int GeneralPort = 320;

    // Message types (IEEE-1588 numbering, the subset AirPlay uses).
    private const byte MsgSync = 0x00;
    private const byte MsgDelayReq = 0x01;
    private const byte MsgPdelayReq = 0x02;
    private const byte MsgPdelayResp = 0x03;
    private const byte MsgFollowUp = 0x08;
    private const byte MsgDelayResp = 0x09;
    private const byte MsgPdelayRespFollowUp = 0x0A;
    private const byte MsgAnnounce = 0x0B;
    private const byte MsgSignaling = 0x0C;

    // Flags: iOS sends 0x0408 on Announce - UNICAST | TIMESCALE.
    private const ushort FlagTimescale = 1 << 3;
    private const ushort FlagTwoStep = 1 << 9;
    private const ushort FlagUnicast = 1 << 10;

    // Cadences and the log-intervals that advertise them, reference-matched.
    private static readonly TimeSpan AnnounceInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SyncInterval = TimeSpan.FromMilliseconds(125);
    private static readonly TimeSpan SignalingInterval = TimeSpan.FromSeconds(1);
    // Announce logint -2 (254 on the wire) - the exact value a real macOS sender emits, per
    // the build-mac->Speaker capture. Was 0.
    private const sbyte LogIntervalAnnounce = -2;
    private const sbyte LogIntervalSync = -3;
    private const sbyte LogIntervalSignaling = -128;
    private const sbyte LogIntervalDelayResp = -3;

    private readonly UdpClient _event;
    private readonly UdpClient _general;
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _peersGate = new();
    private readonly List<IPEndPoint> _peerBases = [];

    /// <summary>Each peer's requesting port identity, so the send loop can keep re-granting it like the Mac does.</summary>
    private readonly Dictionary<IPAddress, byte[]> _peerPortIds = [];

    // Sequence ids are bumped from the send loop, the receive loop and a session's connect
    // thread at once, so they are int + Interlocked: a ushort++ is a read-modify-write and
    // two messages sharing a sequenceId read as a duplicate to the receiver.
    private int _announceSeq;
    private int _syncSeq;
    private int _signalingSeq;

    /// <summary>This sender's PTP clock identity - stable for the install, derived from the DACP id.</summary>
    public ulong ClockId { get; }

    /// <summary>The timing-peer UUID announced in SETUP; fresh per launch, like the reference sender's.</summary>
    public string PeerUuid { get; } = Guid.NewGuid().ToString().ToUpperInvariant();

    private static readonly Lock _instanceGate = new();
    private static PtpClock? _instance;
    private static bool _failed;

    /// <summary>
    /// The process-wide grandmaster, started on first use - or null when the PTP ports
    /// can't be bound (another PTP daemon owns them), in which case sessions fall back to
    /// NTP timing exactly as before.
    /// </summary>
    public static PtpClock? Instance
    {
        get
        {
            lock (_instanceGate)
            {
                if (_instance is not null || _failed)
                {
                    return _instance;
                }

                try
                {
                    _instance = new PtpClock();
                }
                catch (SocketException ex)
                {
                    // Windows lets an unprivileged process bind 319/320; the POSIX systems
                    // reserve everything below 1024 to root, so a plain user launch there is
                    // AccessDenied rather than a port conflict. Remember the refusal -
                    // retrying every session would log the same failure forever.
                    _failed = true;
                    switch (ex.SocketErrorCode)
                    {
                        case SocketError.AccessDenied:
                        {
                            _log.Information(ex, "PTP ports need elevated privileges on this OS - AirPlay sessions will use NTP timing");
                        }
                        break;

                        case SocketError.AddressAlreadyInUse:
                        {
                            _log.Information(ex, "another PTP stack owns ports {Event}/{General} - AirPlay sessions will use NTP timing", EventPort, GeneralPort);
                        }
                        break;

                        default:
                        {
                            _log.Information(ex, "PTP ports unavailable - AirPlay sessions will use NTP timing");
                        }
                        break;
                    }
                }
                catch (Exception ex)
                {
                    _failed = true;
                    _log.Information(ex, "PTP grandmaster failed to start - AirPlay sessions will use NTP timing");
                }

                return _instance;
            }
        }
    }

    private PtpClock()
    {
        // The clock identity has to be stable across sessions AND launches - a receiver
        // re-syncing to "a different clock" every play is a re-anchor it never needed.
        var dacp = Convert.FromHexString(DacpControlServer.Instance.DacpId);
        ClockId = BinaryPrimitives.ReadUInt64BigEndian(dacp);

        // Both ports or neither: a half-built clock that kept 319 bound would hold the port
        // for the life of the process while Instance reports PTP unavailable, locking every
        // other PTP tool on the box out of it too.
        try
        {
            _event = new UdpClient(new IPEndPoint(IPAddress.Any, EventPort));
            _general = new UdpClient(new IPEndPoint(IPAddress.Any, GeneralPort));
        }
        catch
        {
            _event?.Dispose();
            _general?.Dispose();
            _cts.Dispose();
            throw;
        }

        var token = _cts.Token;
        _ = Task.Run(() => ReceiveLoopAsync(_event, isEventPort: true, token), token);
        _ = Task.Run(() => ReceiveLoopAsync(_general, isEventPort: false, token), token);
        _ = Task.Run(() => SendLoopAsync(token), token);

        _log.Information("PTP grandmaster up: clock {ClockId:X16} on {Event}/{General}", ClockId, EventPort, GeneralPort);
    }

    /// <summary>
    /// Registers a receiver's timing address (from its SETUP response) as a unicast peer.
    /// Announce and signaling go out immediately - a receiver that just asked for PTP
    /// should not wait out a timer tick to learn its master exists.
    /// </summary>
    public void AddPeer(IPAddress address)
    {
        lock (_peersGate)
        {
            if (_peerBases.Any(p => p.Address.Equals(address)))
            {
                return;
            }

            _peerBases.Add(new IPEndPoint(address, 0));
        }

        _warnedNoReqs = false;
        _log.Information("PTP peer added: {Address}", address);

        try
        {
            // Announce immediately so the peer sees a master at once. No signalling here -
            // signalling is only ever a GRANT, and we have nothing to grant until the peer
            // sends its request.
            SendToPeers(BuildAnnounce(), GeneralPort, _general);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "PTP immediate announce failed");
        }
    }

    public void RemovePeer(IPAddress address)
    {
        lock (_peersGate)
        {
            _peerBases.RemoveAll(p => p.Address.Equals(address));
            _peerPortIds.Remove(address);
        }

        _log.Information("PTP peer removed: {Address}", address);
    }

    /// <summary>Nanoseconds on this sender's PTP timescale - the clock the 0xD7 sync packets and Follow-Ups share.</summary>
    public static ulong NowNanoseconds()
    {
        // Only internal consistency matters: the receiver disciplines itself to whatever
        // this clock says, it never compares it to wall time. Stopwatch is the steadiest
        // clock the runtime offers.
        return (ulong)((double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency * 1_000_000_000.0);
    }

    // ---- send side ---------------------------------------------------------------------

    /// <summary>Total Delay-Reqs the receiver has sent us - the count that proves it slaves.</summary>
    private long _delayReqCount;
    private long _lastReportedReqs;

    /// <summary>Latches the "nobody has slaved to us" warning to once per peer registration.</summary>
    private volatile bool _warnedNoReqs;

    private async Task SendLoopAsync(CancellationToken ct)
    {
        var lastAnnounce = DateTime.MinValue;
        var lastSignaling = DateTime.MinValue;
        var lastReport = DateTime.MinValue;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;

                // A live heartbeat that the pod is SLAVING to us: Delay-Req is a slave-only
                // message, so a rising count is the receiver actively measuring its offset
                // to our clock. Zero means it hasn't accepted us as master.
                if (now - lastReport >= TimeSpan.FromSeconds(5))
                {
                    lastReport = now;
                    var reqs = System.Threading.Interlocked.Read(ref _delayReqCount);
                    if (reqs > _lastReportedReqs)
                    {
                        _log.Debug("PTP SLAVING: receiver has sent {Total} Delay-Reqs (+{Delta} in 5s) - it is synchronized to OrgZ as master",
                            reqs, reqs - _lastReportedReqs);
                        _lastReportedReqs = reqs;
                    }
                    else if (reqs == 0 && !_warnedNoReqs)
                    {
                        // Only worth saying while a peer is actually registered, and only
                        // once per registration - the loop outlives every session.
                        bool hasPeers;
                        lock (_peersGate)
                        {
                            hasPeers = _peerBases.Count > 0;
                        }

                        if (hasPeers)
                        {
                            _warnedNoReqs = true;
                            _log.Information("PTP: no Delay-Reqs yet - receiver has not accepted us as master");
                        }
                    }
                }

                if (now - lastAnnounce >= AnnounceInterval)
                {
                    lastAnnounce = now;
                    SendToPeers(BuildAnnounce(), GeneralPort, _general);
                }

                if (now - lastSignaling >= SignalingInterval)
                {
                    lastSignaling = now;
                    // A GRANT to each peer that has requested service - NOT a zeroed
                    // signalling. The Mac re-grants continuously; that steady renewal is
                    // part of why its receiver stays committed.
                    KeyValuePair<IPAddress, byte[]>[] grantees;
                    lock (_peersGate)
                    {
                        grantees = [.. _peerPortIds];
                    }

                    foreach (var (address, portId) in grantees)
                    {
                        var grant = BuildGrant(portId);
                        try
                        {
                            _general.Send(grant, grant.Length, new IPEndPoint(address, GeneralPort));
                        }
                        catch (SocketException ex)
                        {
                            _log.Debug(ex, "PTP grant renewal to {Peer} failed", address);
                        }
                    }
                }

                // Two-step sync: the Sync itself carries a ZERO timestamp; the Follow-Up
                // carries the precise time the Sync left. That split is the whole point of
                // two-step PTP - the timestamp doesn't have to be known while the first
                // packet is being built.
                var sync = BuildSync(out var syncSeq);
                SendToPeers(sync, EventPort, _event);
                SendToPeers(BuildFollowUp(syncSeq, NowNanoseconds()), GeneralPort, _general);

                await Task.Delay(SyncInterval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "PTP send loop error");
            }
        }
    }

    private readonly HashSet<byte> _dumped = [];

    /// <summary>Logs the full hex of the first packet of each message type - for a byte-exact diff against a real capture.</summary>
    private void DumpOnce(byte[] message)
    {
        if (message.Length < 2)
        {
            return;
        }

        var type = (byte)(message[0] & 0x0F);
        lock (_dumped)
        {
            if (!_dumped.Add(type))
            {
                return;
            }
        }

        _log.Debug("PTP-DUMP type=0x{Type:X2} len={Len}: {Hex}", type, message.Length, Convert.ToHexString(message));
    }

    private void SendToPeers(byte[] message, int port, UdpClient socket)
    {
        DumpOnce(message);

        IPEndPoint[] peers;
        lock (_peersGate)
        {
            if (_peerBases.Count == 0)
            {
                return;
            }
            peers = [.. _peerBases];
        }

        foreach (var peer in peers)
        {
            try
            {
                socket.Send(message, message.Length, new IPEndPoint(peer.Address, port));
            }
            catch (SocketException ex)
            {
                _log.Debug(ex, "PTP send to {Peer} failed", peer.Address);
            }
        }
    }

    // ---- receive side ------------------------------------------------------------------

    private async Task ReceiveLoopAsync(UdpClient socket, bool isEventPort, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try
            {
                packet = await socket.ReceiveAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }
                _log.Debug(ex, "PTP receive error");
                continue;
            }

            try
            {
                Handle(packet.Buffer, packet.RemoteEndPoint);
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "PTP packet handling failed");
            }
        }
    }

    /// <summary>First sighting per (peer, message type) - the receiver's PTP behavior, readable from a log.</summary>
    private readonly HashSet<(IPAddress, byte)> _heardFrom = [];

    private void Handle(byte[] data, IPEndPoint from)
    {
        if (data.Length < HeaderSize)
        {
            return;
        }

        var type = (byte)(data[0] & 0x0F);

        // First contact per peer AND type at INF: an Announce says the receiver talks to
        // us; a Delay-Req says it accepted our mastership and is measuring - those are
        // different milestones and the log has to distinguish them.
        lock (_heardFrom)
        {
            if (_heardFrom.Add((from.Address, type)))
            {
                if (type == MsgAnnounce && data.Length >= HeaderSize + 30)
                {
                    // Whose clock does IT believe in? Its own identity here means it has
                    // not accepted ours; ours here means it has. Priority1 is logged
                    // because BMCA compares it BEFORE clock class - a receiver announcing
                    // priority1 below ours outranks us no matter how good our clock claims
                    // to be.
                    var grandmaster = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(HeaderSize + 19));
                    var priority1 = data[HeaderSize + 13];
                    var clockClass = data[HeaderSize + 14];
                    var priority2 = data[HeaderSize + 18];
                    var domain = data[4];
                    _log.Information("PTP first {Type} from {Peer}: grandmaster {Gm:X16} p1={P1} class={Class} p2={P2} domain={Domain}{Ours}",
                        "Announce", from.Address, grandmaster, priority1, clockClass, priority2, domain, grandmaster == ClockId ? " (US - accepted)" : " (not us)");
                }
                else
                {
                    _log.Information("PTP first contact from {Peer}: message type 0x{Type:X2}", from.Address, type);
                }
            }
        }
        switch (type)
        {
            // The receiver measuring its path delay to us - the one exchange that has to
            // be answered or the sync never converges.
            case MsgDelayReq when data.Length >= HeaderSize + 10:
            {
                System.Threading.Interlocked.Increment(ref _delayReqCount);
                var response = BuildDelayResp(data);
                DumpOnce(response);
                _general.Send(response, response.Length, new IPEndPoint(from.Address, GeneralPort));
            }
            break;

            case MsgPdelayReq:
            {
                var resp = BuildPdelayResp(data, NowNanoseconds());
                _event.Send(resp, resp.Length, new IPEndPoint(from.Address, EventPort));

                var followUp = BuildPdelayRespFollowUp(data, NowNanoseconds());
                _general.Send(followUp, followUp.Length, new IPEndPoint(from.Address, GeneralPort));
            }
            break;

            // The pod REQUESTING unicast service - the exchange that decides everything.
            // It sends this signaling and waits for a GRANT before it will accept us as
            // master and start sending Delay-Req. Ignoring it (which we used to do) is why
            // the pod never slaved: no grant, no service, no delay measurement, no join.
            // Apple's grant is a proprietary ORGANIZATION_EXTENSION TLV (OUI 00:0D:93), not
            // the standard 0x0005 GRANT - decoded from the build-mac->Speaker capture.
            case MsgSignaling when data.Length >= HeaderSize + 10:
            {
                // Only a peer a session actually registered is granted service. A grant is a
                // standing one: the send loop then aims a 1 Hz unicast stream at that
                // address until the session ends, so granting to whatever host reaches port
                // 320 would let one packet (with a source address we never verified) point
                // that stream anywhere for the life of the process.
                var portId = data.AsSpan(20, 10).ToArray();
                var registered = false;
                lock (_peersGate)
                {
                    if (_peerBases.Any(p => p.Address.Equals(from.Address)))
                    {
                        registered = true;

                        // Remember the requester's port identity so the send loop can KEEP
                        // granting it - the Mac never sends a zeroed signalling, every one
                        // of its ~1/s signallings is a fresh grant. Reactively granting once
                        // was half the job.
                        _peerPortIds[from.Address] = portId;
                    }
                }

                if (!registered)
                {
                    _log.Debug("PTP signaling from unregistered {Peer} ignored", from.Address);
                    break;
                }

                var grant = BuildGrant(portId);
                DumpOnce(grant);
                _general.Send(grant, grant.Length, new IPEndPoint(from.Address, GeneralPort));
                lock (_heardFrom)
                {
                    if (_heardFrom.Add((from.Address, 0xEE)))
                    {
                        _log.Information("PTP granted unicast service to {Peer}", from.Address);
                    }
                }
            }
            break;

            // We are the grandmaster by fiat: rival announces are not contested, they are
            // ignored - the reference sender does exactly this and Apple's receivers accept
            // it.
            case MsgAnnounce:
            case MsgSync:
            case MsgFollowUp:
            {
            }
            break;
        }
    }

    /// <summary>
    /// Builds the GRANT reply to a pod's unicast-service request, byte-modelled on the
    /// macOS sender's grant in the capture. Two Apple org TLVs: subtype 1 granting a
    /// 3-message set (mask 0x0007), subtype 5 granting a 5-message set (mask 0x001F), each
    /// with the reference intervals (0x2710) and duration (0x00203BCC). targetPortIdentity
    /// echoes the requester's port so it knows the grant is for it.
    /// </summary>
    private byte[] BuildGrant(ReadOnlySpan<byte> targetPortId)
    {
        // 34 header + 10 targetPortIdentity + TLV1 (4 hdr + 22) + TLV2 (4 hdr + 32) = 106,
        // exactly the macOS grant length.
        var message = new byte[HeaderSize + 10 + 26 + 36];
        WriteHeader(message, MsgSignaling, (ushort)message.Length, (ushort)System.Threading.Interlocked.Increment(ref _signalingSeq), LogIntervalSignaling, FlagUnicast | FlagTimescale);

        var body = message.AsSpan(HeaderSize);

        // targetPortIdentity = the requester's sourcePortIdentity (its clock + port).
        targetPortId[..10].CopyTo(body);

        // The two grant TLVs, VERBATIM from the macOS sender's grant in the capture (only
        // the targetPortIdentity above differs per peer). Type 0x0003, then the Apple OUI
        // 00:0D:93 org value. Reproduced byte-for-byte rather than parametrized - the tail
        // structure (interval fields vs duration) is Apple-proprietary and not worth
        // guessing when the exact bytes are in hand.
        GrantTlv1.CopyTo(body[10..]);
        GrantTlv2.CopyTo(body[(10 + GrantTlv1.Length)..]);

        return message;
    }

    // Each literal has to be exactly 4 + its own declared length (bytes 2..3), or the grant
    // ships a value that is shorter than it claims and every field after it is read at the
    // wrong offset.

    // TLV 1: 0x0003, len 22, Apple OUI, subtype 1, mask 0x0007, intervals + duration 0x00203BCC.
    private static readonly byte[] GrantTlv1 =
    [
        0x00, 0x03, 0x00, 0x16,
        0x00, 0x0D, 0x93, 0x00, 0x00, 0x01, 0x00, 0x07, 0x00, 0x00, 0x00, 0x00,
        0x27, 0x10, 0x00, 0x00, 0x27, 0x10, 0x00, 0x20, 0x3B, 0xCC,
    ];

    // TLV 2: 0x0003, len 32, Apple OUI, subtype 5, mask 0x001F, intervals + duration
    // 0x00203BCC, then the two trailing zero bytes that pad the value out to its declared
    // 32 - written out rather than left to the buffer so the literal and the length agree.
    private static readonly byte[] GrantTlv2 =
    [
        0x00, 0x03, 0x00, 0x20,
        0x00, 0x0D, 0x93, 0x00, 0x00, 0x05, 0x00, 0x1F, 0x00, 0x00, 0x00, 0x00,
        0x27, 0x10, 0x00, 0x00, 0x27, 0x10, 0x00, 0x00, 0x27, 0x10, 0x00, 0x00,
        0x27, 0x10, 0x00, 0x20, 0x3B, 0xCC, 0x00, 0x00,
    ];

    // ---- message building --------------------------------------------------------------

    private const int HeaderSize = 34;

    /// <summary>The common 34-byte header. Everything in PTP is big-endian.</summary>
    private void WriteHeader(Span<byte> buffer, byte type, ushort length, ushort sequence, sbyte logInterval, ushort flags)
    {
        buffer[..HeaderSize].Clear();
        // transportSpecific / majorSdoId = 1 in the upper nibble: this is the 802.1AS
        // (gPTP) profile marker. Without it a HomePod sees a generic-PTP node and never
        // joins its timeline - the capture shows every Apple message carries it (follow-up
        // byte0 = 0x18, announce = 0x1B, sync = 0x10).
        buffer[0] = (byte)(0x10 | type);
        buffer[1] = 0x02;                                                 // PTP version 2
        BinaryPrimitives.WriteUInt16BigEndian(buffer[2..], length);
        // domain 0, reserved
        // TIMESCALE is set on EVERY Apple PTP message (the capture confirms it on sync,
        // follow-up and delay-resp too, not just announce). Without it the pod treats our
        // timeline as arbitrary rather than PTP and won't lock to it - it was the reason
        // the pod started a delay exchange but never fully committed.
        BinaryPrimitives.WriteUInt16BigEndian(buffer[6..], (ushort)(flags | FlagTimescale));
        // correction 0, reserved
        BinaryPrimitives.WriteUInt64BigEndian(buffer[20..], ClockId);     // sourcePortIdentity: clock
        BinaryPrimitives.WriteUInt16BigEndian(buffer[28..], 0x8004);      // sourcePortIdentity: port - the Mac's IPv4 value
        BinaryPrimitives.WriteUInt16BigEndian(buffer[30..], sequence);
        buffer[32] = ControlFieldFor(type);
        buffer[33] = unchecked((byte)logInterval);
    }

    /// <summary>The legacy control field, kept accurate because old parsers still read it.</summary>
    private static byte ControlFieldFor(byte type) => type switch
    {
        MsgSync => 0x00,
        MsgDelayReq => 0x01,
        MsgFollowUp => 0x02,
        MsgDelayResp => 0x03,
        _ => 0x05,
    };

    private static void WriteTimestamp(Span<byte> buffer, ulong nanoseconds)
    {
        var seconds = nanoseconds / 1_000_000_000;
        var nanos = (uint)(nanoseconds % 1_000_000_000);
        BinaryPrimitives.WriteUInt16BigEndian(buffer, (ushort)(seconds >> 32));
        BinaryPrimitives.WriteUInt32BigEndian(buffer[2..], (uint)seconds);
        BinaryPrimitives.WriteUInt32BigEndian(buffer[6..], nanos);
    }

    internal byte[] BuildAnnounce()
    {
        // header + originTimestamp(10) + utcOffset(2) + reserved(1) + priority1(1) +
        // clockQuality(4) + priority2(1) + grandmasterIdentity(8) + stepsRemoved(2) +
        // timeSource(1) + PATH_TRACE TLV (4 + 8)
        var message = new byte[HeaderSize + 30 + 12];
        WriteHeader(message, MsgAnnounce, (ushort)message.Length, (ushort)System.Threading.Interlocked.Increment(ref _announceSeq), LogIntervalAnnounce, FlagUnicast | FlagTimescale);

        // IEEE-1588 Announce body layout, offsets from the end of the 34-byte header:
        //   [0..9] originTimestamp  [10..11] currentUtcOffset  [12] reserved
        //   [13] priority1  [14..17] clockQuality  [18] priority2
        //   [19..26] grandmasterIdentity  [27..28] stepsRemoved  [29] timeSource
        // Values are the EXACT ones a real macOS sender announces (build-mac->Speaker capture),
        // not the textbook-superior clock OrgZ used to claim. A HomePod does not pick its
        // master by clock quality - Apple wins with a deliberately mediocre clock - so
        // looking like Apple matters more than looking good.
        var body = message.AsSpan(HeaderSize);
        // originTimestamp stays ZERO in an announce - the Mac sends 0, not a live time.
        BinaryPrimitives.WriteInt16BigEndian(body[10..], 37);            // currentUtcOffset = 37 (capture)
        body[13] = 248;                                                   // priority1 (was 128)
        BinaryPrimitives.WriteUInt32BigEndian(body[14..], 0xF821436A);    // class 248, accuracy 0x21, variance 0x436A
        body[18] = 235;                                                   // priority2 - the Mac's IPv4 value. At 247 we announced WORSE than the pod's own 235, so it judged itself the better clock and never slaved.
        BinaryPrimitives.WriteUInt64BigEndian(body[19..], ClockId);       // grandmaster = us
        // stepsRemoved 0 (body[27..28])
        body[29] = 0xA0;                                                  // timeSource: INTERNAL_OSCILLATOR (was 0x20 GPS)

        // The reference notes iOS repeats the clock id as a PATH_TRACE TLV.
        BinaryPrimitives.WriteUInt16BigEndian(body[30..], 0x0008);        // PATH_TRACE
        BinaryPrimitives.WriteUInt16BigEndian(body[32..], 8);
        BinaryPrimitives.WriteUInt64BigEndian(body[34..], ClockId);

        return message;
    }

    internal byte[] BuildSync(out ushort sequence)
    {
        sequence = (ushort)System.Threading.Interlocked.Increment(ref _syncSeq);
        var message = new byte[HeaderSize + 10];
        WriteHeader(message, MsgSync, (ushort)message.Length, sequence, LogIntervalSync, FlagUnicast | FlagTwoStep);
        // originTimestamp stays ZERO - two-step: the Follow-Up carries the real time.
        return message;
    }

    internal byte[] BuildFollowUp(ushort syncSequence, ulong nanoseconds)
    {
        // header(34) + preciseOriginTimestamp(10) + 802.1AS Follow_Up TLV(4+28) +
        // Apple TLV(4+16) = 96, exactly the macOS follow-up. The gPTP Follow_Up
        // information TLV is what a receiver reads to lock to our timeline; a follow-up
        // without it (our old two-zeroed-Apple-TLV version) is not a gPTP follow-up.
        var message = new byte[HeaderSize + 10 + 32 + 20];
        WriteHeader(message, MsgFollowUp, (ushort)message.Length, syncSequence, LogIntervalSync, FlagUnicast);

        var body = message.AsSpan(HeaderSize);
        WriteTimestamp(body, nanoseconds);

        // 802.1AS Follow_Up information TLV: ORG_EXT, OUI 00:80:C2, subtype 000001, then
        // cumulativeScaledRateOffset/gmTimeBaseIndicator/lastGmPhaseChange/scaledLastGmFreq
        // - all zero (we make no rate correction), which the capture also ships zeroed.
        var tlv1 = body[10..];
        BinaryPrimitives.WriteUInt16BigEndian(tlv1, 0x0003);
        BinaryPrimitives.WriteUInt16BigEndian(tlv1[2..], 28);
        tlv1[4] = 0x00;                                                   // OUI 00:80:C2 (IEEE 802.1)
        tlv1[5] = 0x80;
        tlv1[6] = 0xC2;
        tlv1[7] = 0x00;                                                   // subType 00:00:01
        tlv1[8] = 0x00;
        tlv1[9] = 0x01;
        // remaining 22 bytes stay zero

        // Apple TLV: ORG_EXT, OUI 00:0D:93, subtype 000004, carrying our clock id.
        var tlv2 = body[(10 + 32)..];
        BinaryPrimitives.WriteUInt16BigEndian(tlv2, 0x0003);
        BinaryPrimitives.WriteUInt16BigEndian(tlv2[2..], 16);
        tlv2[4] = 0x00;                                                   // OUI 00:0D:93
        tlv2[5] = 0x0D;
        tlv2[6] = 0x93;
        tlv2[7] = 0x00;                                                   // subType 00:00:04
        tlv2[8] = 0x00;
        tlv2[9] = 0x04;
        BinaryPrimitives.WriteUInt64BigEndian(tlv2[10..], ClockId);

        return message;
    }

    internal byte[] BuildDelayResp(byte[] request)
    {
        var message = new byte[HeaderSize + 10 + 10];
        var requestSequence = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(30));
        WriteHeader(message, MsgDelayResp, (ushort)message.Length, requestSequence, LogIntervalDelayResp, FlagUnicast);

        var body = message.AsSpan(HeaderSize);
        WriteTimestamp(body, NowNanoseconds());
        request.AsSpan(20, 10).CopyTo(body[10..]);                        // requestingPortIdentity = their sourcePortIdentity

        return message;
    }

    private byte[] BuildPdelayResp(byte[] request, ulong receiptNanoseconds)
    {
        var message = new byte[HeaderSize + 10 + 10];
        var requestSequence = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(30));
        WriteHeader(message, MsgPdelayResp, (ushort)message.Length, requestSequence, 0x7F, FlagUnicast | FlagTwoStep);

        var body = message.AsSpan(HeaderSize);
        WriteTimestamp(body, receiptNanoseconds);
        request.AsSpan(20, 10).CopyTo(body[10..]);

        return message;
    }

    private byte[] BuildPdelayRespFollowUp(byte[] request, ulong nanoseconds)
    {
        var message = new byte[HeaderSize + 10 + 10];
        var requestSequence = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(30));
        WriteHeader(message, MsgPdelayRespFollowUp, (ushort)message.Length, requestSequence, 0x7F, FlagUnicast);

        var body = message.AsSpan(HeaderSize);
        WriteTimestamp(body, nanoseconds);
        request.AsSpan(20, 10).CopyTo(body[10..]);

        return message;
    }

    /// <summary>
    /// Tears the process-wide grandmaster down at exit, so the sockets close and the
    /// receive loops stop instead of riding the process out.
    /// </summary>
    internal static void Shutdown()
    {
        PtpClock? instance;
        lock (_instanceGate)
        {
            instance = _instance;
            _instance = null;
        }

        instance?.Dispose();
    }

    public void Dispose()
    {
        _cts.Cancel();

        try
        {
            _event.Dispose();
        }
        catch
        {
            // teardown
        }

        try
        {
            _general.Dispose();
        }
        catch
        {
            // teardown
        }

        _cts.Dispose();

        lock (_instanceGate)
        {
            if (ReferenceEquals(_instance, this))
            {
                _instance = null;
            }
        }
    }
}
