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

    public AirPlayRaopSink(AudioDeviceInfo device, string host, int port)
    {
        Id = device.QualifiedId;
        DisplayName = device.DisplayName;
        _host = host;
        _port = port;
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

        // RAOP is 44.1k/16/stereo, full stop - the bus negotiates that format for us, and
        // anything else would need a resampler we don't have here.
        if (format.SampleRate != 44100 || format.Channels != 2 || format.BitsPerSample != 16)
        {
            throw new NotSupportedException($"AirPlay needs 44100 Hz 16-bit stereo; the stream is {format.SampleRate} Hz {format.BitsPerSample}-bit x{format.Channels}.");
        }

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

        _partial.AddRange(pcm);

        while (_partial.Count >= RaopAlac.PcmBytesPerPacket)
        {
            var packet = _partial.GetRange(0, RaopAlac.PcmBytesPerPacket).ToArray();
            _partial.RemoveRange(0, RaopAlac.PcmBytesPerPacket);
            ApplyGain(packet);
            _queue.Writer.TryWrite(packet);
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
            ConnectFailed?.Invoke(this, $"Couldn't start AirPlay to “{DisplayName}”: {ex.Message}");
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
