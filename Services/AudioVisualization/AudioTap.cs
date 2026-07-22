// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using LibVLCSharp.Shared;
using OrgZ.Services.AudioOutput;
using Serilog;

namespace OrgZ.Services.AudioVisualization;

/// <summary>
/// Attaches to the main <see cref="MediaPlayer"/> via
/// <see cref="MediaPlayer.SetAudioCallbacks"/>, receives decoded PCM samples
/// from LibVLC, and fans them out two ways:
/// <list type="bullet">
///   <item>To <see cref="AudioAnalyzer"/> for FFT / band-level extraction
///   (the VU meter consumes this).</item>
///   <item>To the <see cref="AudioSinkBus"/> so the user's selected output
///   devices (single or multiple, local or network) play the audio.
///   SetAudioCallbacks replaces LibVLC's own audio output, so we route to
///   the sink bus from here instead.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Sample format:</b> we ask LibVLC for <c>S16N</c> - signed-16-bit,
/// native-endian, interleaved stereo at 44.1 kHz.  Most output sinks
/// (waveOut, CoreAudio, PulseAudio) consume S16 directly without extra
/// conversion; the FFT path pays a cheap divide-by-32768 to get floats.
/// </para>
/// <para>
/// <b>Lifecycle:</b> delegates are held as fields because LibVLC stores
/// raw function pointers into native memory - if the delegates get GC'd,
/// native code calls into freed memory and the app crashes.
/// </para>
/// </remarks>
public sealed class AudioTap : IAudioVisualizationSource, IDisposable
{
    public const uint TapSampleRate = 44100;
    public const uint TapChannels = 2;
    private const string TapFormat = "S16N";

    private static readonly ILogger _log = Logging.For("AudioTap");

    private readonly AudioAnalyzer _analyzer;
    private readonly AudioSinkBus _bus;

    private readonly MediaPlayer.LibVLCAudioPlayCb _playCb;
    private readonly MediaPlayer.LibVLCAudioPauseCb _pauseCb;
    private readonly MediaPlayer.LibVLCAudioResumeCb _resumeCb;
    private readonly MediaPlayer.LibVLCAudioFlushCb _flushCb;
    private readonly MediaPlayer.LibVLCAudioDrainCb _drainCb;

    private MediaPlayer? _attachedPlayer;
    private bool _active;
    private bool _disposed;
    private long _buffersReceived;
    private bool _qosBumped;

    private float[] _floatScratch = new float[4096];

    // --- Realtime-paced analyzer feed ------------------------------------
    // libvlc's audio callback fires in bursts whose size depends on the
    // decoder (MP3 frames ~1152, FLAC blocks ~4096), which makes the FFT
    // pipeline see one big lump every 25ms or one bigger lump every ~93ms.
    // To decouple visualization pacing from delivery pacing, the callback
    // just stashes samples into this ring buffer; a periodic timer drains
    // it at realtime rate (one frame per 1/sampleRate seconds) and feeds
    // the analyzer in steady ~16ms chunks. The meter looks identical
    // regardless of whether the source is a 26ms-callback radio stream or
    // a 93ms-callback FLAC file.
    private const int RingCapacityFrames = 8192;  // ~186ms @ 44.1 kHz
    private readonly float[] _ringInterleaved = new float[RingCapacityFrames * 2];
    private int _ringWriteIdx;
    private int _ringFillFrames;
    private readonly object _ringLock = new();
    private System.Threading.Timer? _drainTimer;
    private long _lastDrainTicks;
    private int _drainBusy;
    private const int DrainPeriodMs = 16;
    private float[] _drainScratch = new float[2048 * 2];

    // --- Shared band snapshot (multi-consumer) ---------------------------
    // CopyBandLevels* drain the analyzer's max-since-read buffers. With two LCDs
    // (main window + mini-player) each reading on its own ~60 fps clock, the first
    // reader emptied the buffer and the second saw silence - the mini-player VU
    // sat dead. Instead, drain into this snapshot at most once per SnapRefreshMs;
    // every consumer in that window reads the same cached frame, so any number of
    // LCDs/visualizers render the meter from a single drain per frame.
    private float[] _snapMono = [];
    private float[] _snapLeft = [];
    private float[] _snapRight = [];
    private long _snapTicks;
    private readonly object _snapLock = new();
    private const int SnapRefreshMs = 14;

    public AudioTap(AudioSinkBus bus, AudioAnalyzer? analyzer = null)
    {
        ArgumentNullException.ThrowIfNull(bus);
        _bus = bus;
        _analyzer = analyzer ?? new AudioAnalyzer();
        _snapMono = new float[_analyzer.BandCount];
        _snapLeft = new float[_analyzer.BandCount];
        _snapRight = new float[_analyzer.BandCount];

        _playCb = OnAudioPlay;
        _pauseCb = OnAudioPause;
        _resumeCb = OnAudioResume;
        _flushCb = OnAudioFlush;
        _drainCb = OnAudioDrain;
    }

    public int BandCount => _analyzer.BandCount;

    public bool IsActive => _active;

    public event AudioFrameHandler? RawFrameAvailable;

    /// <summary>
    /// Fires once per playback session the first time libvlc actually delivers
    /// PCM samples to the tap. The viewmodel uses this to know "playback is
    /// truly under way" -- a more reliable signal than libvlc's Playing event,
    /// which can fire before any audio reaches the output.
    /// </summary>
    public event Action? AudioStarted;

    private int _audioStartedNotified;

    /// <summary>
    /// Arms <see cref="AudioStarted"/> to fire on the next OnAudioPlay buffer.
    /// Called by the viewmodel at the start of every Play call so the LCD's
    /// loading state can be cleared exactly when sound begins.
    /// </summary>
    public void ResetAudioStartTracking()
    {
        System.Threading.Interlocked.Exchange(ref _audioStartedNotified, 0);
    }

    public void CopyBandLevels(Span<float> destination)
    {
        lock (_snapLock)
        {
            RefreshSnapshotIfStale();
            var n = Math.Min(destination.Length, _snapMono.Length);
            _snapMono.AsSpan(0, n).CopyTo(destination);
        }
    }

    public void CopyBandLevelsStereo(Span<float> left, Span<float> right)
    {
        lock (_snapLock)
        {
            RefreshSnapshotIfStale();
            var n = Math.Min(Math.Min(left.Length, right.Length), _snapLeft.Length);
            _snapLeft.AsSpan(0, n).CopyTo(left);
            _snapRight.AsSpan(0, n).CopyTo(right);
        }
    }

    /// <summary>
    /// Drains the analyzer's max-since-read band buffers into the shared snapshot, but at most
    /// once per <see cref="SnapRefreshMs"/>. Consumers reading on their own ~60 fps clocks
    /// therefore share one drain per frame instead of racing to empty the buffer (which left the
    /// second LCD - the mini-player - reading zeros). Caller holds <see cref="_snapLock"/>.
    /// </summary>
    private void RefreshSnapshotIfStale()
    {
        var now = Environment.TickCount64;
        if (now - _snapTicks < SnapRefreshMs)
        {
            return;
        }
        _snapTicks = now;
        _analyzer.CopyBands(_snapMono);
        _analyzer.CopyBandsStereo(_snapLeft, _snapRight);
    }

    public void Attach(MediaPlayer player)
    {
        ArgumentNullException.ThrowIfNull(player);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (ReferenceEquals(_attachedPlayer, player))
        {
            return;
        }

        player.SetAudioFormat(TapFormat, TapSampleRate, TapChannels);
        player.SetAudioCallbacks(_playCb, _pauseCb, _resumeCb, _flushCb, _drainCb);
        _attachedPlayer = player;
        _bus.SetFormat(AudioFormat.CdDaStereo16);

        _lastDrainTicks = Environment.TickCount64;
        _drainTimer ??= new System.Threading.Timer(OnDrainTick, null, DrainPeriodMs, DrainPeriodMs);

        _log.Information("AudioTap attached to MediaPlayer (format={Format} rate={Rate} ch={Ch})", TapFormat, TapSampleRate, TapChannels);
    }

    // -- LibVLC callbacks ------------------------------------------------

    private unsafe void OnAudioPlay(IntPtr data, IntPtr samples, uint count, long pts)
    {
        if (count == 0 || samples == IntPtr.Zero)
        {
            return;
        }

        // On the very first callback, ask the OS to bump this thread's QoS to
        // USER_INTERACTIVE. We can't get hard real-time scheduling without
        // joining coreaudiod's workgroup (Mach time-constraint policy is
        // gated), but USER_INTERACTIVE puts us at the top of the user-space
        // priority bands and makes preemption from background work far less
        // likely. No entitlements / code signing required.
        if (!_qosBumped)
        {
            _qosBumped = true;
            ThreadQos.BumpToUserInteractive(_log);
        }

        var prev = System.Threading.Interlocked.Increment(ref _buffersReceived);

        if (prev == 1)
        {
            _log.Information("AudioTap receiving samples: first buffer {Count} frames @ pts={Pts}", count, pts);
        }

        if (System.Threading.Interlocked.Exchange(ref _audioStartedNotified, 1) == 0)
        {
            AudioStarted?.Invoke();
        }

        _active = true;

        int totalShorts = checked((int)(count * TapChannels));
        int byteLen = totalShorts * sizeof(short);

        // (a) Forward to the sink bus so audio plays through whatever devices
        //     the user has selected - fans out to multiple outputs when applicable.
        var byteSpan = new ReadOnlySpan<byte>((void*)samples, byteLen);
        _bus.Write(byteSpan);

        // (b) Convert S16 → float32 for FFT.  Scratch grows once.
        if (_floatScratch.Length < totalShorts)
        {
            _floatScratch = new float[totalShorts];
        }
        var shortSpan = new ReadOnlySpan<short>((void*)samples, totalShorts);
        const float Inv32k = 1f / 32768f;
        for (int i = 0; i < totalShorts; i++)
        {
            _floatScratch[i] = shortSpan[i] * Inv32k;
        }
        var floatSpan = new ReadOnlySpan<float>(_floatScratch, 0, totalShorts);

        // Stash into the ring instead of feeding the analyzer directly --
        // the drain timer delivers samples at a steady realtime cadence
        // regardless of how libvlc batches them.
        WriteToRing(floatSpan, (int)count);

        var handler = RawFrameAvailable;
        if (handler != null)
        {
            var frame = new AudioFrame(floatSpan, (int)TapSampleRate, (int)TapChannels, pts);
            handler(frame);
        }
    }

    private void WriteToRing(ReadOnlySpan<float> interleaved, int frames)
    {
        if (frames <= 0)
        {
            return;
        }

        lock (_ringLock)
        {
            for (int i = 0; i < frames; i++)
            {
                int dst = _ringWriteIdx * 2;
                _ringInterleaved[dst]     = interleaved[i * 2];
                _ringInterleaved[dst + 1] = interleaved[i * 2 + 1];
                _ringWriteIdx = (_ringWriteIdx + 1) % RingCapacityFrames;
            }

            _ringFillFrames = Math.Min(RingCapacityFrames, _ringFillFrames + frames);
        }
    }

    /// <summary>
    /// Drains realtime-paced frames from the ring buffer into the analyzer.
    /// Wakes every <see cref="DrainPeriodMs"/> ms and computes the target
    /// drain amount from wall-clock dt so the analyzer always receives audio
    /// at the same rate as playback regardless of timer drift or how libvlc
    /// chose to chunk the source.
    /// </summary>
    private void OnDrainTick(object? state)
    {
        // Skip if a previous tick is still running. Drain work is <1ms in
        // practice so this is just belt-and-braces for system pauses.
        if (System.Threading.Interlocked.Exchange(ref _drainBusy, 1) != 0)
        {
            return;
        }

        try
        {
            long now = Environment.TickCount64;
            long dtMs = now - _lastDrainTicks;
            _lastDrainTicks = now;

            if (dtMs <= 0)
            {
                return;
            }

            // Target frames = sample-rate * dt. Cap at one second of audio
            // so a long system pause doesn't dump a giant block into the
            // analyzer in one tick (which would model a discontinuity).
            int targetFrames = (int)Math.Min(dtMs * TapSampleRate / 1000, TapSampleRate);

            int frames;
            lock (_ringLock)
            {
                frames = Math.Min(targetFrames, _ringFillFrames);
                if (frames <= 0)
                {
                    return;
                }

                int neededFloats = frames * 2;
                if (_drainScratch.Length < neededFloats)
                {
                    _drainScratch = new float[neededFloats];
                }

                int readIdx = (_ringWriteIdx - _ringFillFrames + RingCapacityFrames) % RingCapacityFrames;
                for (int i = 0; i < frames; i++)
                {
                    int src = readIdx * 2;
                    _drainScratch[i * 2]     = _ringInterleaved[src];
                    _drainScratch[i * 2 + 1] = _ringInterleaved[src + 1];
                    readIdx = (readIdx + 1) % RingCapacityFrames;
                }

                _ringFillFrames -= frames;
            }

            var span = new ReadOnlySpan<float>(_drainScratch, 0, frames * 2);
            _analyzer.FeedInterleavedStereo(span);
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _drainBusy, 0);
        }
    }

    private void OnAudioPause(IntPtr data, long pts)
    {
        _active = false;
        _bus.PauseAll();
        // Clear analyzer bands so the VU meter has a zero target to decay
        // toward.  Without this, paused playback leaves the bars pinned at
        // the last level they were showing, which looks broken.
        ClearRing();
        _analyzer.Reset();
    }

    private void OnAudioResume(IntPtr data, long pts)
    {
        _active = true;
        _lastDrainTicks = Environment.TickCount64;
        _bus.ResumeAll();
    }

    private void OnAudioFlush(IntPtr data, long pts)
    {
        // Seek / track change - drop both the analyzer's windowed state and
        // every sink's pending hardware queue so the user hears the new
        // position right away instead of the last buffered chunk of the old.
        ClearRing();
        _analyzer.Reset();
        _bus.FlushAll();
    }

    private void OnAudioDrain(IntPtr data)
    {
        // Natural end-of-track: play out what's queued instead of flushing it.
        // FlushAll here used to cut the last few hundred milliseconds of every
        // song - the ring depth's worth of already-queued audio. libvlc's
        // drain callback expects to block until playback is audibly finished.
        _active = false;
        ClearRing();
        _analyzer.Reset();
        _bus.DrainAll();
    }

    private void ClearRing()
    {
        lock (_ringLock)
        {
            _ringFillFrames = 0;
            _ringWriteIdx = 0;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _drainTimer?.Dispose();
        _drainTimer = null;
        _attachedPlayer = null;
    }
}
