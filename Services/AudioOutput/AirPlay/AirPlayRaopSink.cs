// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Threading.Channels;
using Serilog;

namespace OrgZ.Services.AudioOutput.AirPlay;

/// <summary>
/// Streams the audio bus to an AirPlay receiver over RAOP.
///
/// The bus calls <see cref="Write"/> from LibVLC's audio thread and expects it to return
/// immediately, but RAOP has to be paced in real time - so writes land in a bounded
/// channel and a pump task drains it into 352-frame packets. The channel is bounded and
/// drops the OLDEST buffer when full: a stalled network must never back-pressure the
/// decoder thread (that stutters every other output too).
///
/// Failure is loud by design. If the handshake fails the sink refuses to open, so the
/// picker reports the receiver as unusable rather than swallowing the stream.
/// </summary>
internal sealed class AirPlayRaopSink : IAudioSink, IRemoteControllableSink
{
    private static readonly ILogger _log = Logging.For("AirPlaySink");

    private readonly string _host;
    private readonly int _port;

    private RaopSession? _session;
    private AirPlay2Session? _airplay2;

    // Handshake backoff, so a failing receiver isn't hammered (see Open).
    private DateTime _retryNotBefore = DateTime.MinValue;
    private int _failureCount;
    private string? _lastFailure;
    private Channel<byte[]>? _queue;
    private Task? _pump;
    private CancellationTokenSource? _cts;

    // Carries the remainder between writes: the bus hands over arbitrary buffer sizes,
    // RAOP wants exactly 352 frames per packet.
    private readonly List<byte> _partial = new(RaopAlac.PcmBytesPerPacket * 2);

    // Non-null when the stream isn't already 44.1 kHz.
    private AudioResampler? _resampler;

    private float _volume = 1f;
    private bool _muted;
    private bool _paused;
    private bool _disposed;

    // Open and Close race each other: the picker opens on the UI thread while the bus's
    // fan-out reopens idle sinks from the audio thread. Without this both can build a
    // session and the loser's pump is orphaned - still streaming, no longer cancellable.
    private readonly Lock _lifecycle = new();

    /// <summary>
    /// The previous session's teardown, still running on a background thread.
    ///
    /// Close and FailStream hand the goodbye off so the UI thread isn't held for seconds, which
    /// means a reopen - a format change is <c>Close(); Open(format);</c> back to back, and so is
    /// a quick untick/retick - can start a new handshake while the old session is still sending
    /// its TEARDOWN. An AirPlay 2 receiver handed a SETUP and a TEARDOWN at once is exactly how
    /// one ends up wedged, holding a session it will not give back. So the next session waits
    /// for this, on its own thread, where waiting costs nobody anything.
    /// </summary>
    private Task? _teardown;

    // Guards the PCM staging buffers. Write runs on the decoder thread while Flush/Pause
    // arrive from whoever asked for the pause, and List<byte> is not thread-safe.
    private readonly Lock _pcm = new();

    // Bumped by every session, so a pump that outlived its session can tell that the state
    // it is about to write belongs to somebody else now.
    private int _generation;

    // Bumped by every flush. A Write already past the buffer work must not push the audio
    // it just packed into a queue that was drained behind its back.
    private volatile int _flushGeneration;

    // Packets packed by the current Write, handed to Enqueue after the PCM lock is released.
    private readonly List<byte[]> _ready = new(8);

    // True once this sink has hooked the process-wide control endpoint, so Close can let go
    // of it without starting one for a sink that never used it.
    private bool _dacpHooked;

    private readonly string? _password;

    public AirPlayRaopSink(AudioDeviceInfo device, string host, int port, AudioSinkBus? bus = null, string? password = null)
    {
        Id = device.QualifiedId;
        DisplayName = device.DisplayName;
        _host = host;
        _port = port;
        _password = password;

        // The handshake finishes long after Open returns, so a failure can't be thrown to
        // a caller - it goes back through the bus, which the UI listens to.
        if (bus is not null)
        {
            ConnectFailed += (_, reason) => bus.ReportSinkFailure(Id, DisplayName, reason, NeedsPassword);
        }
    }

    /// <summary>Set when the receiver rejected our credentials, so the UI knows to prompt.</summary>
    public bool NeedsPassword { get; private set; }

    /// <summary>Only a session that's actually streaming paces the decoder - see IAudioSink.</summary>
    /// <summary>
    /// A PAUSED sink is not a clock.
    ///
    /// Write discards everything while paused, so nothing back-pressures the decoder - and if
    /// the bus still believes this sink is pacing playback, it won't pace either. The decoder
    /// then runs as fast as it can read, which is heard as the track tearing past at many
    /// times speed the moment playback resumes locally.
    /// </summary>
    public bool ProvidesClock => _streaming && !_paused;

    public string Id { get; }
    public string DisplayName { get; }
    public AudioFormat? CurrentFormat { get; private set; }
    public bool IsOpen { get; private set; }

    /// <summary>
    /// The receiver's two-second buffer plus our own queue - see IAudioSink.OutputLatency.
    ///
    /// Reported as a FIXED figure from the moment the sink is open, because the bus aligns
    /// on events rather than continuously: it primes its delay lines when a sink opens, and
    /// a number that grows as the queue fills would prime them against a depth the sink no
    /// longer has. Without it the bus reads this output as instant, decides the sound card
    /// is the slow one, and delays the wrong speaker.
    /// </summary>
    public TimeSpan OutputLatency => IsOpen ? TimeSpan.FromSeconds(2) + TimeSpan.FromSeconds((double)QueuePackets * RaopAlac.FramesPerPacket / 44100) : TimeSpan.Zero;

    /// <summary>
    /// What the room is hearing, which is seconds behind what the decoder has produced -
    /// the session knows the difference, and the app's timeline follows this one.
    /// </summary>
    public TimeSpan? PlaybackPosition => _airplay2?.AudiblePosition;

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            PushVolume();
        }
    }

    public bool IsMuted
    {
        get => _muted;
        set
        {
            _muted = value;
            PushVolume();
        }
    }

    public void Open(AudioFormat format)
    {
        // Serialised, because the picker (UI thread) and the bus's reopen-idle-sinks pass
        // (audio thread) can both land here for the same sink within the same millisecond.
        lock (_lifecycle)
        {
            OpenLocked(format);
        }
    }

    private void OpenLocked(AudioFormat format)
    {
        if (IsOpen)
        {
            return;
        }

        // The bus reopens a closed sink on every playback tick, so a failed handshake was
        // being retried ~3x/second. Against a HomeKit receiver that trips its brute-force
        // lockout within seconds - we were the reason pairing kept getting refused. Back
        // off instead, and answer from the cached reason without touching the network.
        if (DateTime.UtcNow < _retryNotBefore)
        {
            throw new InvalidOperationException(_lastFailure ?? $"“{DisplayName}” is not available right now.");
        }

        // RAOP is fixed at 44.1 kHz 16-bit stereo. The bus hands us whatever the decoder
        // produced - in practice 32-bit float - so the sink converts depth itself (the
        // IAudioSink contract expects exactly that). Rate and channel count it cannot fix:
        // there's no resampler here, so those still refuse.
        // RAOP is 44.1 kHz stereo, full stop. Rate is converted here (a hi-res library is
        // mostly NOT 44.1); channel count isn't, because a downmix is a mixing decision
        // rather than a transport one.
        if (format.Channels != 2)
        {
            throw new NotSupportedException($"“{DisplayName}” needs stereo, but this track is {format.Channels}-channel.");
        }

        var resampler = format.SampleRate == 44100 ? null : new AudioResampler(format.SampleRate, 44100, 2);
        if (resampler is not null)
        {
            _log.Information("AirPlay: resampling {Rate} Hz to 44.1 kHz for {Name}", format.SampleRate, DisplayName);
        }

        if (!CanConvert(format))
        {
            throw new NotSupportedException($"AirPlay can't convert {format.BitsPerSample}-bit {format.Encoding} to the 16-bit PCM RAOP requires.");
        }

        _log.Debug("AirPlay sink accepting {Rate}Hz {Bits}-bit {Encoding}", format.SampleRate, format.BitsPerSample, format.Encoding);

        CurrentFormat = format;

        lock (_pcm)
        {
            _resampler = resampler;
            _partial.Clear();
            _converted.Clear();
        }

        // ~1s of audio in flight. Wait (not DropOldest) so a full queue is visible to
        // Enqueue, which decides between pacing and dropping - see Enqueue for why that
        // distinction is what keeps playback running at 1x.
        //
        // Depth is latency, directly. Back-pressure only engages once the queue is FULL, so
        // in steady state it IS full and the decoder runs a full queue-length ahead of what
        // has actually been sent - which is what put the app's timeline four seconds ahead of
        // the receiver's. It does not need to be deep: the receiver holds its own two-second
        // buffer and absorbs network jitter, so all this has to cover is decoder scheduling,
        // and the FLAC pump delivers in ~50ms chunks.
        _queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(QueuePackets)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

        _cts = new CancellationTokenSource();

        // Marked open BEFORE the pump exists, so a second caller already inside Open (or the
        // audio thread's next fan-out) sees a claimed sink rather than building a second
        // session to the same speaker.
        IsOpen = true;
        var generation = ++_generation;

        // Whatever the last session is still doing, this one waits for it before touching the
        // receiver. Read here under _lifecycle; awaited inside the pump.
        var previousTeardown = _teardown;

        // The handshake is a network round trip and Open runs on the UI thread (the speaker
        // flyout calls straight through) - so connect inside the pump instead of blocking
        // here. A failure is reported by ConnectFailed rather than thrown, because by then
        // the caller is long gone.
        _pump = Task.Factory.StartNew(() => RunAsync(_cts.Token, generation, previousTeardown), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

        _log.Information("AirPlay sink opening: {Name} ({Host}:{Port})", DisplayName, _host, _port);
    }

    /// <summary>
    /// Takes a new bit depth or sample rate without dropping the session.
    ///
    /// Everything except channel count is handled on the way in - depth by ConvertToS16,
    /// rate by the resampler - so there is nothing about those the receiver needs to know.
    /// Tearing down instead meant a track that started 16-bit and continued 32-bit killed a
    /// freshly paired session mid-SETUP, and the receiver wouldn't accept a replacement
    /// while the old one was still half-open.
    /// </summary>
    public bool TryAdaptFormat(AudioFormat format)
    {
        if (!IsOpen || format.Channels != 2 || !CanConvert(format))
        {
            return false;
        }

        lock (_pcm)
        {
            _resampler = format.SampleRate == 44100 ? null : new AudioResampler(format.SampleRate, 44100, 2);
            _partial.Clear();
            _converted.Clear();
            CurrentFormat = format;
        }

        _log.Information("AirPlay adapted to {Rate}Hz {Bits}-bit for {Name} without reconnecting", format.SampleRate, format.BitsPerSample, DisplayName);
        return true;
    }

    /// <summary>
    /// Hands the current track to the live AirPlay 2 session - title/artist/album for the
    /// display, cover art, and the duration the receiver schedules the end of stream from.
    ///
    /// Held even when no session is up yet, because the bus announces the track while the
    /// handshake is still running; RunAsync replays it once the session comes up.
    /// </summary>
    public void SetTrackInfo(string? title, string? artist, string? album, TimeSpan? duration, byte[]? artwork)
    {
        _pendingTrack = (title, artist, album, duration, artwork);

        _log.Debug("AirPlay sink track info: {Title} (session={State})", title ?? "(none)",
            _airplay2 is null ? "none" : _airplay2.IsConnected ? "connected" : "connecting");

        if (_airplay2 is { IsConnected: true } session)
        {
            Helpers.TaskObserver.FireAndForget(
                session.SetTrackInfoAsync(title, artist, album, duration, artwork),
                "AirPlay track info");
        }
    }

    private (string? Title, string? Artist, string? Album, TimeSpan? Duration, byte[]? Artwork)? _pendingTrack;

    /// <summary>
    /// Waits for the first audio packet to reach the wire, then announces the track.
    ///
    /// Bounded: if audio never starts there is nothing to describe, and the announcement is
    /// simply dropped rather than left pending forever.
    /// </summary>
    private async Task AnnounceWhenStreamingAsync(AirPlay2Session session, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (!session.HasSentAudio && Environment.TickCount64 < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(50, ct);
        }

        if (!session.HasSentAudio || ct.IsCancellationRequested || _pendingTrack is not { } track)
        {
            return;
        }

        await session.SetTrackInfoAsync(track.Title, track.Artist, track.Album, track.Duration, track.Artwork, ct);
    }

    /// <summary>
    /// Selecting this speaker opens the session immediately and holds it with silence - see
    /// IAudioSink.HoldsSessionWhenIdle. Pairing, the handshake and the receiver's two-second
    /// buffer are then all done before the first note instead of after it.
    /// </summary>
    public bool HoldsSessionWhenIdle => HoldSessionOnSelect;

    /// <summary>
    /// Whether selecting a speaker connects to it straight away and holds it with silence.
    ///
    /// ON, because the volume model depends on it: the session ADOPTS the speaker's own
    /// level as part of its start sequence, so with no session at select the picker shows
    /// the sink's default - a speaker sitting at 50% read as 100% until the first note
    /// played. Connecting on select is also what pays for pairing, the handshake and the
    /// receiver's two-second buffer before the music starts instead of after.
    /// </summary>
    internal static bool HoldSessionOnSelect { get; set; } = true;

    /// <summary>True once real audio - not the pump's silence - has passed through.</summary>
    private volatile bool _audioArrived;

    public void Write(ReadOnlySpan<byte> pcm)
    {
        if (!IsOpen || _paused || _queue is null)
        {
            return;
        }

        // Remembered for the StartPaused decision when a session (re)connects. The actual
        // hold release lives in the pump, which can tell real packets from its own silence
        // and awaits the notification IN ORDER before the first real packet goes out.
        _audioArrived = true;

        // The staging buffers are shared with Flush, which runs on whichever thread asked
        // for the pause or the seek - the FLAC engine pauses without waiting for its pump to
        // park, and a List cleared underneath GetRange throws or hands back garbage. Held
        // only around the buffer work: Enqueue can park the decoder for two seconds, and a
        // pause that had to wait that long to be heard is not a pause.
        int generation;

        _ready.Clear();

        lock (_pcm)
        {
            generation = _flushGeneration;

            // _partial always holds S16LE - convert on the way in, so the packer and the
            // 352-frame chunking below only ever deal with one layout.
            AppendAsS16(pcm);

            while (_partial.Count >= RaopAlac.PcmBytesPerPacket)
            {
                var packet = _partial.GetRange(0, RaopAlac.PcmBytesPerPacket).ToArray();
                _partial.RemoveRange(0, RaopAlac.PcmBytesPerPacket);
                ApplyGain(packet);
                _ready.Add(packet);
            }
        }

        // One back-pressure budget for the whole buffer, not one per packet: a 50ms hand-off
        // is six packets, and six two-second waits is twelve seconds of a decoder thread that
        // every other output shares.
        var budget = Environment.TickCount64 + BackPressureMs;

        foreach (var packet in _ready)
        {
            // A flush landed while these were being packed: they are pre-seek, or pre-pause,
            // audio and queueing them now plays them back after the user asked for silence.
            if (_flushGeneration != generation || _paused)
            {
                break;
            }

            Enqueue(packet, budget);
        }

        _ready.Clear();
    }

    /// <summary>
    /// Packets held between the decoder and the wire - 128 x 352 frames is ~1.02s at 44.1kHz.
    /// This is the app-side share of end-to-end latency; see Open for why it is kept short.
    /// </summary>
    private const int QueuePackets = 128;

    /// <summary>How long a full queue may stall the decoder before we drop instead.</summary>
    private const int BackPressureMs = 2000;

    // True once a session is up and the pump is draining in real time.
    private volatile bool _streaming;

    /// <summary>
    /// Hands one packet to the pump, applying back-pressure once we're actually streaming.
    ///
    /// This is what keeps playback at 1x. A local sink paces the decoder by blocking in
    /// its own write; AirPlay has no such clock, so when it is the ONLY output an
    /// always-accepting Write let LibVLC decode as fast as it could read the file - the
    /// track raced to the end in seconds while the speaker played the first few. Making a
    /// full queue block borrows the receiver's real-time drain as the clock.
    ///
    /// Bounded, though: a wedged receiver must never hang the decoder (that would stall
    /// every other output too), so past the deadline we drop the oldest packet and move
    /// on. Before the session is up we always drop, so the ~1s handshake window doesn't
    /// stall playback or deliver a backlog of stale audio when it completes.
    ///
    /// <paramref name="deadline"/> is the caller's whole budget, shared by every packet in
    /// one buffer - see Write.
    /// </summary>
    private void Enqueue(byte[] packet, long deadline)
    {
        var queue = _queue;
        if (queue is null || queue.Writer.TryWrite(packet))
        {
            return;
        }

        if (_streaming)
        {
            while (Environment.TickCount64 < deadline)
            {
                if (queue.Writer.TryWrite(packet))
                {
                    return;
                }

                Thread.Sleep(5);
            }
        }

        queue.Reader.TryRead(out _);
        queue.Writer.TryWrite(packet);
    }

    /// <summary>Depths this sink can reduce to the 16-bit PCM RAOP requires.</summary>
    internal static bool CanConvert(AudioFormat format) => format switch
    {
        { Encoding: AudioSampleEncoding.PcmSigned, BitsPerSample: 16 or 24 or 32 } => true,
        { Encoding: AudioSampleEncoding.IeeeFloat, BitsPerSample: 32 } => true,
        _ => false,
    };

    /// <summary>
    /// Converts one buffer to S16LE and appends it to <see cref="_partial"/>.
    /// Exposed as a static for tests; the instance path just forwards its format.
    /// </summary>
    internal static void ConvertToS16(ReadOnlySpan<byte> source, AudioFormat format, List<byte> destination)
    {
        switch (format.Encoding, format.BitsPerSample)
        {
            case (AudioSampleEncoding.PcmSigned, 16):
            {
                destination.AddRange(source);
            }
            break;

            case (AudioSampleEncoding.IeeeFloat, 32):
            {
                // LibVLC's tap hands us normalized floats; clamp before scaling so an
                // inter-sample peak above 1.0 wraps to full scale instead of to silence.
                for (var i = 0; i + 4 <= source.Length; i += 4)
                {
                    var value = BitConverter.ToSingle(source[i..(i + 4)]);
                    var scaled = (int)(Math.Clamp(value, -1f, 1f) * short.MaxValue);
                    destination.Add((byte)(scaled & 0xFF));
                    destination.Add((byte)((scaled >> 8) & 0xFF));
                }
            }
            break;

            case (AudioSampleEncoding.PcmSigned, 32):
            {
                for (var i = 0; i + 4 <= source.Length; i += 4)
                {
                    // Keep the top 16 bits.
                    destination.Add(source[i + 2]);
                    destination.Add(source[i + 3]);
                }
            }
            break;

            case (AudioSampleEncoding.PcmSigned, 24):
            {
                for (var i = 0; i + 3 <= source.Length; i += 3)
                {
                    destination.Add(source[i + 1]);
                    destination.Add(source[i + 2]);
                }
            }
            break;
        }
    }

    // Reused across writes so the depth conversion doesn't allocate per buffer; the
    // resampler reads from it and appends 44.1 kHz frames into _partial.
    private readonly List<byte> _converted = new(RaopAlac.PcmBytesPerPacket * 2);

    private void AppendAsS16(ReadOnlySpan<byte> source)
    {
        if (CurrentFormat is not { } format)
        {
            return;
        }

        if (_resampler is null)
        {
            ConvertToS16(source, format, _partial);
            return;
        }

        // Depth first, then rate - the resampler works in 16-bit frames.
        _converted.Clear();
        ConvertToS16(source, format, _converted);
        _resampler.Process(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_converted), _partial);
    }

    /// <summary>
    /// Volume rides the RTSP channel, but mute is applied to the samples too: a receiver
    /// takes a moment to act on SET_PARAMETER and the user's mute must be instant.
    /// </summary>
    private void ApplyGain(Span<byte> pcm)
    {
        if (!_muted)
        {
            return;
        }

        pcm.Clear();
    }

    /// <summary>
    /// Connects, then streams until cancelled. Owns the session's whole lifetime so the
    /// handshake never blocks a caller.
    /// </summary>
    private async Task RunAsync(CancellationToken ct, int generation, Task? previousTeardown = null)
    {
        // Captured, not read from the field: a pump that outlives its Close would otherwise
        // start reading the NEXT session's queue, and the channel is single-reader.
        var queue = _queue;
        if (queue is null)
        {
            return;
        }

        // Let the previous session finish saying goodbye before introducing this one. Bounded,
        // because a receiver that never answers a TEARDOWN must not also cost us the next
        // session - four seconds is past the teardown's own three-second pump wait.
        if (previousTeardown is not null)
        {
            try
            {
                await previousTeardown.WaitAsync(TimeSpan.FromSeconds(4), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "AirPlay: previous session teardown did not finish before the reopen");
            }
        }

        // Two protocol generations answer on the same discovery record, and nothing in the
        // mDNS advertisement reliably says which. So: try classic RAOP, and when the
        // receiver answers 401 (an AirPlay 2 device demanding pairing) retry that way.
        RaopSession? raop = null;
        AirPlay2Session? airplay2 = null;

        try
        {
            try
            {
                // A receiver we hold a password for is an AirPlay 2 device, so don't probe
                // classic RAOP first. That probe opens and drops a second RTSP connection,
                // and a HomePod left the follow-up session hanging with no reply at all.
                if (!string.IsNullOrEmpty(_password))
                {
                    throw new InvalidOperationException("401 - receiver requires AirPlay 2 pairing");
                }

                raop = new RaopSession(_host, _port);
                await raop.ConnectAsync(ct);
                _session = raop;
                _failureCount = 0;   // a good connection clears the backoff ladder

                // A classic receiver reaches us the same way an AirPlay 2 one does: it
                // browses for the DACP endpoint named by the headers the session sends, and
                // posts its transport keys there. The session has no control channel of its
                // own, so the sink is what turns those posts into commands.
                // Never fatal to the stream: DacpControlServer.Instance builds the singleton on
                // first use, which binds a port and publishes an mDNS record, and either can
                // throw. Losing the control endpoint costs the speaker's buttons; letting that
                // throw escape here would cost the audio.
                try
                {
                    HookDacp();
                }
                catch (Exception hook)
                {
                    _log.Debug(hook, "AirPlay: DACP control endpoint unavailable for {Name}", DisplayName);
                }

                _log.Information("AirPlay streaming to {Name} (classic RAOP)", DisplayName);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && NeedsPairing(ex))
            {
                raop?.Dispose();
                raop = null;

                _log.Information("{Name} wants AirPlay 2 pairing - retrying with the paired path", DisplayName);
                airplay2 = new AirPlay2Session(_host, _port, _password)
                {
                    InitialVolume = _muted ? 0f : _volume,

                    // The speaker's level wins on select - EXCEPT over a mute, which is a
                    // deliberate choice made here that adoption must not quietly undo.
                    AdoptReceiverVolume = !_muted,

                    // Opened because the speaker was SELECTED, with nothing playing yet:
                    // hold it with silence, and say so rather than showing a phantom track.
                    StartPaused = !_audioArrived,
                };

                // Seed the track BEFORE connecting. The receiver takes the stream's length
                // from the first progress it sees, so it has to be right the first time -
                // the bus told us what was playing before the handshake even started.
                if (_pendingTrack is { } seed)
                {
                    airplay2.SeedTrackInfo(seed.Title, seed.Artist, seed.Album, seed.Duration);
                }

                airplay2.RemoteCommand += (_, command) => RemoteCommand?.Invoke(this, command);

                // Track the level locally too, so the app's own volume changes don't fight
                // the one the user just set on the speaker. Subscribed BEFORE the connect:
                // the handshake itself raises this when it adopts the speaker's level, and
                // a handler attached afterwards misses exactly that event - the app slider
                // then pushes the stale level right back over the adoption.
                airplay2.RemoteVolume += (_, level) => ApplyRemoteVolume(level);

                // A receiver that goes away takes the whole session with it. Nothing else
                // notices: the pump can sit on silence forever, so the sink would stay open
                // and "playing" to a speaker that has been off for ten minutes.
                airplay2.Died += (_, died) => FailStream(died, generation, queue);

                try
                {
                    await airplay2.ConnectAsync(ct);
                }
                finally
                {
                    NeedsPassword = airplay2.PasswordRejected;
                }
                _airplay2 = airplay2;
                _failureCount = 0;
                _log.Information("AirPlay streaming to {Name} (AirPlay 2)", DisplayName);
            }

            PushVolume();

            // The bus announced the track while the handshake was still running, so send it
            // now - but only once audio is genuinely on the wire.
            //
            // Enqueue DROPS every packet until _streaming is set below, so announcing here
            // describes an RTP timestamp in a stream that has never transmitted a byte. This
            // runs alongside the pump rather than before it: awaiting the first packet here
            // would block the very loop that has to run for one to be sent.
            if (_pendingTrack is not null && airplay2 is not null)
            {
                Helpers.TaskObserver.FireAndForget(AnnounceWhenStreamingAsync(airplay2, ct), "AirPlay track announce");
            }

            // The sink was closed (and possibly reopened) while this handshake was in flight,
            // so this session belongs to nobody: claiming to stream would leave the sink
            // pacing the decoder off a pump that is about to stop.
            if (generation != _generation)
            {
                raop?.Dispose();
                airplay2?.Dispose();
                return;
            }

            _streaming = true;
        }
        catch (OperationCanceledException)
        {
            raop?.Dispose();
            airplay2?.Dispose();
            return;
        }
        catch (Exception ex)
        {
            raop?.Dispose();
            airplay2?.Dispose();
            // Loud, not silent: the old placeholder sink swallowed the stream, which is the
            // failure mode this whole path exists to avoid.
            _log.Error(ex, "AirPlay handshake failed for {Name} ({Host}:{Port})", DisplayName, _host, _port);

            if (generation != _generation)
            {
                // Somebody closed this sink and opened it again while we were connecting;
                // the state below belongs to that session now, not to this one.
                return;
            }

            IsOpen = false;
            _streaming = false;

            // Write filled the queue while the handshake was running, and nothing will ever
            // read it. Left in place, its depth is what Drain waits five seconds for at the
            // end of every track - on every other output too, since the drain is one call.
            DrainQueue(queue);

            // A receiver that's rate-limiting needs real time, not another attempt: HomeKit
            // lockouts clear on a timer and every retry restarts it. Everything else backs
            // off gently so a flaky network doesn't turn into a connection storm either.
            var lockedOut = ex.Message.Contains("too many attempts", StringComparison.OrdinalIgnoreCase);
            _failureCount++;

            // A rejected password won't fix itself, and retrying it is what trips the
            // receiver's brute-force lockout. Sit out until the user supplies a new one,
            // which clears the gate via SetPassword.
            var backoff = NeedsPassword
                ? TimeSpan.FromHours(1)
                : lockedOut
                    ? TimeSpan.FromMinutes(5)
                    : TimeSpan.FromSeconds(Math.Min(60, 5 * Math.Pow(2, _failureCount - 1)));
            _retryNotBefore = DateTime.UtcNow.Add(backoff);
            _lastFailure = Explain(ex);

            _log.Information("AirPlay: not retrying {Name} for {Seconds:0}s", DisplayName, backoff.TotalSeconds);
            ConnectFailed?.Invoke(this, _lastFailure);
            return;
        }

        try
        {
            // A REALTIME stream, which means it never stops.
            //
            // RAOP is a constant-rate transport: the receiver maps each packet's timestamp
            // onto a wall clock and plays it at that instant. So the timeline has to keep
            // moving whether or not the decoder has anything to say. Between two tracks it
            // does not - there is a gap while the next file is opened - and simply waiting
            // there stalls the timeline, then floods the receiver with a burst whose
            // timestamps have already passed. It drops those, and the join between two songs
            // becomes a hole with a click in it. It is also what a held session IS: a
            // speaker selected with nothing playing yet is kept alive by exactly this
            // silence, which is what lets the announcement and the volume adoption land.
            //
            // So when the queue runs dry, silence goes out instead.
            var silence = new byte[RaopAlac.PcmBytesPerPacket];

            while (!ct.IsCancellationRequested)
            {
                if (!queue.Reader.TryRead(out var packet))
                {
                    // Give the decoder the length of about one packet to produce something
                    // before filling in for it.
                    var ready = queue.Reader.WaitToReadAsync(ct).AsTask();

                    // IsCompletedSuccessfully before Result: an ordinary Close cancels this
                    // wait, and reading Result on a cancelled task throws an AggregateException
                    // that is NOT an OperationCanceledException - so it would miss the cancel
                    // handler below and log every normal speaker close as a pump failure.
                    if (await Task.WhenAny(ready, Task.Delay(8, ct)) == ready && ready.IsCompletedSuccessfully && ready.Result)
                    {
                        if (!queue.Reader.TryRead(out packet))
                        {
                            packet = silence;
                        }
                    }
                    else
                    {
                        packet = silence;
                    }
                }

                if (airplay2 is not null)
                {
                    // A paused session has told the receiver to stop; keeping the stream
                    // alive through that would be talking over the silence the user asked
                    // for. The HOLD is different: there the session itself is marked paused
                    // but the sink is not, and the silence is the point.
                    if (!_paused)
                    {
                        // The hold ends HERE, on the first packet that is real music rather
                        // than our own generated silence - awaited, in order, so no real
                        // audio can reach the receiver while the session still claims to be
                        // paused. This used to be an edge-triggered latch in Write, which
                        // could be consumed before the session existed and then never fire
                        // again; the pump checks every packet and the check is free once
                        // the session is playing.
                        //
                        // The HOLD, specifically - not the pause state, which a user pause
                        // sets too. Reading that one meant resuming from a pause re-announced
                        // the track from 0:00 instead of taking the session's own resume path.
                        if (!ReferenceEquals(packet, silence) && airplay2.IsHolding)
                        {
                            _log.Information("AirPlay hold released: first real audio for {Name}, announcing as playing", DisplayName);
                            await airplay2.NotifyPlaybackStartedAsync(ct);
                        }

                        await airplay2.SendPacketAsync(packet, ct);
                    }
                    else
                    {
                        await Task.Delay(20, ct);
                    }
                }
                else if (raop is not null)
                {
                    await raop.SendPacketAsync(packet, ct);
                }
                else
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "AirPlay send pump stopped for {Id}", Id);
            FailStream(ex, generation, queue);
        }
    }

    /// <summary>
    /// Ends a session that died under us - a receiver switched off, a socket that stopped
    /// answering - and puts the sink back where a reopen can find it.
    ///
    /// Leaving the sink "open" with nothing reading the queue is the expensive failure: the
    /// queue stays full, so every Enqueue spends its whole two-second back-pressure budget
    /// before dropping, and that happens on the decoder thread, several times per fan-out.
    /// One silent AirPlay receiver was enough to stall the local speakers as well.
    /// </summary>
    private void FailStream(Exception ex, int generation, Channel<byte[]> queue)
    {
        RaopSession? session;
        AirPlay2Session? airplay2;
        CancellationTokenSource? cts;
        Task? pump;

        lock (_lifecycle)
        {
            if (generation != _generation || (!IsOpen && !_streaming))
            {
                return;
            }

            IsOpen = false;
            _streaming = false;
            _generation++;

            session = _session;
            airplay2 = _airplay2;
            cts = _cts;
            pump = _pump;
            _session = null;
            _airplay2 = null;
            _cts = null;
            _pump = null;
        }

        // Nothing is going to read what is left in there, and its depth is what Drain waits
        // on at the end of every track.
        DrainQueue(queue);
        UnhookDacp();

        try
        {
            cts?.Cancel();
        }
        catch (Exception cancelled)
        {
            _log.Debug(cancelled, "AirPlay pump cancel failed");
        }

        // Off this thread: the event that told us the session is gone is raised by the
        // session itself, and disposing it sends a TEARDOWN nobody is going to answer.
        var teardown = Task.Run(() =>
        {
            try
            {
                pump?.Wait(TimeSpan.FromSeconds(3));
            }
            catch (Exception waited)
            {
                _log.Debug(waited, "AirPlay pump shutdown");
            }

            session?.Dispose();
            airplay2?.Dispose();
            cts?.Dispose();
        });

        lock (_lifecycle)
        {
            _teardown = teardown;
        }

        Helpers.TaskObserver.FireAndForget(teardown, "AirPlay session teardown");

        _lastFailure = Explain(ex);

        // Short, unlike a refused handshake: this receiver was working a moment ago, so the
        // bus's next reopen pass is welcome to try it again rather than sitting out minutes.
        _retryNotBefore = DateTime.UtcNow.AddSeconds(5);

        _log.Warning("AirPlay session to {Name} ended: {Reason}", DisplayName, _lastFailure);
        ConnectFailed?.Invoke(this, _lastFailure);
    }

    /// <summary>Empties a queue nothing is going to read, so Drain has nothing to wait for.</summary>
    private static void DrainQueue(Channel<byte[]> queue)
    {
        while (queue.Reader.TryRead(out _))
        {
        }
    }

    /// <summary>
    /// Listens to the process-wide DACP endpoint - the classic path's only way of hearing
    /// from the receiver. The AirPlay 2 session hooks it itself and reaches us through its
    /// own RemoteCommand, so this is only wired for the RAOP branch.
    /// </summary>
    private void HookDacp()
    {
        var dacp = DacpControlServer.Instance;
        dacp.Command -= OnDacpCommand;
        dacp.Command += OnDacpCommand;
        dacp.VolumeChanged -= OnDacpVolume;
        dacp.VolumeChanged += OnDacpVolume;
        _dacpHooked = true;
    }

    private void UnhookDacp()
    {
        if (!_dacpHooked)
        {
            return;
        }

        _dacpHooked = false;
        var dacp = DacpControlServer.Instance;
        dacp.Command -= OnDacpCommand;
        dacp.VolumeChanged -= OnDacpVolume;
    }

    private void OnDacpCommand(object? sender, string command) => RemoteCommand?.Invoke(this, command);

    private void OnDacpVolume(object? sender, float level) => ApplyRemoteVolume(level);

    /// <summary>
    /// Takes the level the receiver set, without letting it corrupt ours.
    ///
    /// The field is assigned rather than the property: this level came FROM the receiver, so
    /// pushing it back is an echo. It arrives as decibels converted to linear, and a receiver
    /// reporting the muted sentinel (or nothing at all) can produce a value that is not a
    /// number, which then poisons every slider and comparison downstream of it.
    /// </summary>
    private void ApplyRemoteVolume(float level)
    {
        if (!float.IsFinite(level))
        {
            _log.Debug("AirPlay: ignoring an unusable remote volume from {Name}", DisplayName);
            return;
        }

        _volume = Math.Clamp(level, 0f, 1f);
        RemoteVolume?.Invoke(this, _volume);
    }

    /// <summary>A 401 from the classic handshake means the receiver is an AirPlay 2 device.</summary>
    internal static bool NeedsPairing(Exception ex) => ex.Message.Contains("401", StringComparison.Ordinal);

    /// <summary>Raised when the handshake fails after Open returned - carries a user-facing reason.</summary>
    public event EventHandler<string>? ConnectFailed;

    /// <inheritdoc />
    public event EventHandler<string>? RemoteCommand;

    /// <inheritdoc />
    public event EventHandler<float>? RemoteVolume;

    /// <summary>
    /// Turns a handshake failure into something that tells the user what to do about it.
    /// A bare "401 Unauthorized" reads like a bug; the real meaning is that this receiver
    /// wants the AirPlay 2 pairing handshake, which OrgZ doesn't implement.
    /// </summary>
    private string Explain(Exception ex) => ex.Message switch
    {
        var m when m.Contains("too many attempts", StringComparison.OrdinalIgnoreCase) =>
            $"“{DisplayName}” has locked out pairing attempts — wait a few minutes, or restart the speaker, then try again.",
        var m when m.Contains("401", StringComparison.Ordinal) =>
            $"“{DisplayName}” refused the connection ({DisplayName} may be in use by another sender).",
        _ => $"Couldn't start AirPlay to “{DisplayName}”: {ex.Message}",
    };

    private void PushVolume()
    {
        var level = _muted ? 0f : _volume;

        if (_airplay2 is { IsConnected: true } modern)
        {
            Helpers.TaskObserver.FireAndForget(modern.SetVolumeAsync(level), "AirPlay volume");
        }
        else if (_session is { IsConnected: true } session)
        {
            Helpers.TaskObserver.FireAndForget(session.SetVolumeAsync(level), "AirPlay volume");
        }
    }

    public void Close()
    {
        Task? pump;
        CancellationTokenSource? cts;
        RaopSession? session;
        AirPlay2Session? airplay2;

        lock (_lifecycle)
        {
            IsOpen = false;
            _paused = false;
            _streaming = false;

            // Anything the old pump still has to say - a handshake failure, a dead session -
            // is about a session this sink no longer has.
            _generation++;

            pump = _pump;
            cts = _cts;
            session = _session;
            airplay2 = _airplay2;

            _queue?.Writer.TryComplete();

            _session = null;
            _airplay2 = null;
            _cts = null;
            _queue = null;
            _pump = null;
            CurrentFormat = null;

            // A reopened sink is a NEW session, and the new session's StartPaused/first-audio
            // logic has to run from scratch. This latched forever once, so a close-then-reopen
            // (a format change, or a session death) started the next session claiming audio had
            // already arrived - and the one-shot playing notification in Write could never fire
            // again for this sink object.
            _audioArrived = false;
        }

        lock (_pcm)
        {
            _partial.Clear();
        }

        UnhookDacp();

        try
        {
            cts?.Cancel();
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "AirPlay pump cancel failed");
        }

        if (pump is null && session is null && airplay2 is null && cts is null)
        {
            return;
        }

        // Off the caller's thread, because Close runs on the UI thread (unticking a speaker)
        // and the teardown it has to do is all waiting: the pump has to notice the cancel,
        // and disposing the session sends TEARDOWN and waits for a reply the receiver we are
        // letting go of is the least likely of any to send. Every one of those waits ran to
        // its timeout, and the window froze for seconds.
        //
        // The pump owns the session; disposing here too is safe (RaopSession.Dispose is
        // idempotent) and covers a Close that lands mid-handshake.
        var teardown = Task.Run(() =>
        {
            try
            {
                pump?.Wait(TimeSpan.FromSeconds(3));
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "AirPlay pump shutdown");
            }

            session?.Dispose();
            airplay2?.Dispose();
            cts?.Dispose();
        });

        lock (_lifecycle)
        {
            _teardown = teardown;
        }

        Helpers.TaskObserver.FireAndForget(teardown, "AirPlay sink teardown");
    }

    /// <summary>
    /// Stops feeding the receiver and flushes what it holds, so a pause is audible now
    /// rather than after its ~2s buffer drains.
    /// </summary>
    public void Pause()
    {
        _paused = true;
        Flush();

        // Only a PAUSE tells the receiver to stop. There is no pause method in AirPlay -
        // FLUSH is it - and this is the one caller that means it.
        //
        // Logged either way, because "pause didn't reach the speaker" and "the speaker
        // ignored our pause" are different bugs and the log has to say which.
        var session = _airplay2;
        _log.Information("AirPlay pause: {Name} (session={State})", DisplayName,
            session is null ? "none" : session.IsConnected ? "connected" : "connecting");

        if (session is { IsConnected: true })
        {
            Helpers.TaskObserver.FireAndForget(session.FlushAsync(), "AirPlay 2 pause");
        }
    }

    public void Resume()
    {
        _log.Information("AirPlay resume: {Name}", DisplayName);
        _paused = false;
    }

    public void Flush()
    {
        var queue = _queue;

        lock (_pcm)
        {
            _partial.Clear();
            // Drop the filter's retained tail too, or a seek bleeds pre-jump audio into the
            // first packets after it.
            _resampler?.Reset();

            // Tells a Write that is already past the buffers, on the decoder thread, that the
            // packets in its hand are from before the flush and must not be queued.
            _flushGeneration++;
        }

        if (queue is not null)
        {
            // Drop everything queued locally before telling the receiver to do the same.
            DrainQueue(queue);
        }

        if (_session is { IsConnected: true } session)
        {
            Helpers.TaskObserver.FireAndForget(session.FlushAsync(), "AirPlay flush");
        }
    }

    /// <summary>
    /// Lets the queue drain so the tail of a track isn't cut. Bounded - a wedged receiver
    /// must not hang LibVLC's drain callback forever.
    /// </summary>
    public void Drain()
    {
        // Only a session that is actually pulling packets can empty the queue. After a failed
        // handshake nothing reads it, so waiting is waiting for the full ceiling - and this
        // is the drain at the end of a track, which every other output waits through too.
        if (!IsOpen || !_streaming || _paused)
        {
            return;
        }

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_queue is not null && _queue.Reader.Count > 0 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(50);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Close();
    }
}
