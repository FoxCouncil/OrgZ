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
    private Channel<byte[]>? _queue;
    private Task? _pump;
    private CancellationTokenSource? _cts;

    // Carries the remainder between writes: the bus hands over arbitrary buffer sizes,
    // RAOP wants exactly 352 frames per packet.
    private readonly List<byte> _partial = new(RaopAlac.PcmBytesPerPacket * 2);

    private float _volume = 1f;
    private bool _muted;
    private bool _paused;
    private bool _disposed;

    public AirPlayRaopSink(AudioDeviceInfo device, string host, int port, AudioSinkBus? bus = null)
    {
        Id = device.QualifiedId;
        DisplayName = device.DisplayName;
        _host = host;
        _port = port;

        // The handshake finishes long after Open returns, so a failure can't be thrown to
        // a caller - it goes back through the bus, which the UI listens to.
        if (bus is not null)
        {
            ConnectFailed += (_, reason) => bus.ReportSinkFailure(Id, DisplayName, reason);
        }
    }

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

        // RAOP is fixed at 44.1 kHz 16-bit stereo. The bus hands us whatever the decoder
        // produced - in practice 32-bit float - so the sink converts depth itself (the
        // IAudioSink contract expects exactly that). Rate and channel count it cannot fix:
        // there's no resampler here, so those still refuse.
        if (format.SampleRate != 44100 || format.Channels != 2)
        {
            // No resampler here, so a hi-res track simply can't go out over RAOP. Say that
            // in the user's terms - "96000 Hz x2" explains nothing to someone hearing silence.
            throw new NotSupportedException(
                $"“{DisplayName}” only takes 44.1 kHz stereo, but this track is {format.SampleRate / 1000.0:0.#} kHz — AirPlay can't play hi-res audio in OrgZ yet.");
        }

        if (!CanConvert(format))
        {
            throw new NotSupportedException($"AirPlay can't convert {format.BitsPerSample}-bit {format.Encoding} to the 16-bit PCM RAOP requires.");
        }

        _log.Debug("AirPlay sink accepting {Rate}Hz {Bits}-bit {Encoding}", format.SampleRate, format.BitsPerSample, format.Encoding);

        CurrentFormat = format;
        _partial.Clear();

        // ~4s of audio in flight; DropOldest so a network stall costs latency, never the
        // decoder - and so the handshake window discards stale audio instead of playing it late.
        _queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(512)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

        _cts = new CancellationTokenSource();

        // The handshake is a network round trip and Open runs on the UI thread (the speaker
        // flyout calls straight through) - so connect inside the pump instead of blocking
        // here. A failure is reported by ConnectFailed rather than thrown, because by then
        // the caller is long gone.
        _pump = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);

        IsOpen = true;
        _log.Information("AirPlay sink opening: {Name} ({Host}:{Port})", DisplayName, _host, _port);
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
            _queue.Writer.TryWrite(packet);
        }
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

    private void AppendAsS16(ReadOnlySpan<byte> source)
    {
        if (CurrentFormat is { } format)
        {
            ConvertToS16(source, format, _partial);
        }
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

        var session = new RaopSession(_host, _port);
        try
        {
            await session.ConnectAsync(ct);
            _session = session;
            PushVolume();
            _log.Information("AirPlay streaming to {Name}", DisplayName);
        }
        catch (OperationCanceledException)
        {
            session.Dispose();
            return;
        }
        catch (Exception ex)
        {
            session.Dispose();
            // Loud, not silent: the old placeholder sink swallowed the stream, which is the
            // failure mode this whole path exists to avoid.
            _log.Error(ex, "AirPlay handshake failed for {Name} ({Host}:{Port})", DisplayName, _host, _port);
            IsOpen = false;
            ConnectFailed?.Invoke(this, Explain(ex));
            return;
        }

        try
        {
            await foreach (var packet in _queue.Reader.ReadAllAsync(ct))
            {
                await session.SendPacketAsync(packet, ct);
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

    /// <summary>Raised when the handshake fails after Open returned - carries a user-facing reason.</summary>
    public event EventHandler<string>? ConnectFailed;

    /// <summary>
    /// Turns a handshake failure into something that tells the user what to do about it.
    /// A bare "401 Unauthorized" reads like a bug; the real meaning is that this receiver
    /// wants the AirPlay 2 pairing handshake, which OrgZ doesn't implement.
    /// </summary>
    private string Explain(Exception ex) => ex.Message.Contains("401", StringComparison.Ordinal)
        ? $"“{DisplayName}” needs AirPlay 2 pairing, which OrgZ can't do yet — it only supports older AirPlay speakers."
        : $"Couldn't start AirPlay to “{DisplayName}”: {ex.Message}";

    private void PushVolume()
    {
        if (_session is { IsConnected: true } session)
        {
            Helpers.TaskObserver.FireAndForget(session.SetVolumeAsync(_muted ? 0f : _volume), "AirPlay volume");
        }
    }

    public void Close()
    {
        IsOpen = false;
        _paused = false;

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
