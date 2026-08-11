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
internal sealed class AirPlayRaopSink : IAudioSink
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
    public bool ProvidesClock => _streaming;

    public string Id { get; }
    public string DisplayName { get; }
    public AudioFormat? CurrentFormat { get; private set; }
    public bool IsOpen { get; private set; }

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

        _resampler = format.SampleRate == 44100 ? null : new AudioResampler(format.SampleRate, 44100, 2);
        if (_resampler is not null)
        {
            _log.Information("AirPlay: resampling {Rate} Hz to 44.1 kHz for {Name}", format.SampleRate, DisplayName);
        }

        if (!CanConvert(format))
        {
            throw new NotSupportedException($"AirPlay can't convert {format.BitsPerSample}-bit {format.Encoding} to the 16-bit PCM RAOP requires.");
        }

        _log.Debug("AirPlay sink accepting {Rate}Hz {Bits}-bit {Encoding}", format.SampleRate, format.BitsPerSample, format.Encoding);

        CurrentFormat = format;
        _partial.Clear();

        // ~4s of audio in flight. Wait (not DropOldest) so a full queue is visible to
        // Enqueue, which decides between pacing and dropping - see Enqueue for why that
        // distinction is what keeps playback running at 1x.
        _queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(512)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

        _cts = new CancellationTokenSource();

        // The handshake is a network round trip and Open runs on the UI thread (the speaker
        // flyout calls straight through) - so connect inside the pump instead of blocking
        // here. A failure is reported by ConnectFailed rather than thrown, because by then
        // the caller is long gone.
        _pump = Task.Factory.StartNew(() => RunAsync(_cts.Token), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

        IsOpen = true;
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

        _resampler = format.SampleRate == 44100 ? null : new AudioResampler(format.SampleRate, 44100, 2);
        _partial.Clear();
        _converted.Clear();
        CurrentFormat = format;

        _log.Information("AirPlay adapted to {Rate}Hz {Bits}-bit for {Name} without reconnecting", format.SampleRate, format.BitsPerSample, DisplayName);
        return true;
    }

    public void Write(ReadOnlySpan<byte> pcm)
    {
        if (!IsOpen || _paused || _queue is null)
        {
            return;
        }

        // _partial always holds S16LE - convert on the way in, so the packer and the
        // 352-frame chunking below only ever deal with one layout.
        AppendAsS16(pcm);

        while (_partial.Count >= RaopAlac.PcmBytesPerPacket)
        {
            var packet = _partial.GetRange(0, RaopAlac.PcmBytesPerPacket).ToArray();
            _partial.RemoveRange(0, RaopAlac.PcmBytesPerPacket);
            ApplyGain(packet);
            Enqueue(packet);
        }
    }

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
    /// </summary>
    private void Enqueue(byte[] packet)
    {
        var queue = _queue;
        if (queue is null || queue.Writer.TryWrite(packet))
        {
            return;
        }

        if (_streaming)
        {
            var deadline = Environment.TickCount64 + BackPressureMs;
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
    private async Task RunAsync(CancellationToken ct)
    {
        if (_queue is null)
        {
            return;
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
                _log.Information("AirPlay streaming to {Name} (classic RAOP)", DisplayName);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && NeedsPairing(ex))
            {
                raop?.Dispose();
                raop = null;

                _log.Information("{Name} wants AirPlay 2 pairing - retrying with the paired path", DisplayName);
                airplay2 = new AirPlay2Session(_host, _port, _password) { InitialVolume = _muted ? 0f : _volume };
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
            IsOpen = false;
            _streaming = false;

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
            await foreach (var packet in _queue.Reader.ReadAllAsync(ct))
            {
                if (airplay2 is not null)
                {
                    await airplay2.SendPacketAsync(packet, ct);
                }
                else if (raop is not null)
                {
                    await raop.SendPacketAsync(packet, ct);
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
        }
    }

    /// <summary>A 401 from the classic handshake means the receiver is an AirPlay 2 device.</summary>
    internal static bool NeedsPairing(Exception ex) => ex.Message.Contains("401", StringComparison.Ordinal);

    /// <summary>Raised when the handshake fails after Open returned - carries a user-facing reason.</summary>
    public event EventHandler<string>? ConnectFailed;

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
        IsOpen = false;
        _paused = false;
        _streaming = false;

        try
        {
            _cts?.Cancel();
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "AirPlay pump cancel failed");
        }

        _queue?.Writer.TryComplete();
        try { _pump?.Wait(TimeSpan.FromSeconds(3)); } catch (Exception ex) { _log.Debug(ex, "AirPlay pump shutdown"); }

        // The pump owns the session; disposing here too is safe (RaopSession.Dispose is
        // idempotent) and covers a Close that lands mid-handshake.
        _session?.Dispose();
        _session = null;
        _airplay2?.Dispose();
        _airplay2 = null;
        _cts?.Dispose();
        _cts = null;
        _queue = null;
        _pump = null;
        _partial.Clear();
        CurrentFormat = null;
    }

    /// <summary>
    /// Stops feeding the receiver and flushes what it holds, so a pause is audible now
    /// rather than after its ~2s buffer drains.
    /// </summary>
    public void Pause()
    {
        _paused = true;
        Flush();
    }

    public void Resume() => _paused = false;

    public void Flush()
    {
        _partial.Clear();
        // Drop the filter's retained tail too, or a seek bleeds pre-jump audio into the
        // first packets after it.
        _resampler?.Reset();
        if (_queue is not null)
        {
            while (_queue.Reader.TryRead(out _))
            {
                // Drop everything queued locally before telling the receiver to do the same.
            }
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
