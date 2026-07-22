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
/// <b>Sample format:</b> native-rate S16.  The format callback accepts
/// whatever sample rate the source decodes at (44.1 kHz CD rips through
/// 192 kHz hi-res masters) so VLC never inserts its resampler - the OS
/// audio stack gets the source's own timeline.  Sample format stays
/// <c>S16N</c> because libvlc 3's amem output module rejects everything
/// else; when a libvlc with S32N/FL32 amem support ships, widening the
/// accepted format here (and letting <see cref="AudioFormat"/> carry it
/// downstream) is the only change needed for bit-depth passthrough.
/// The FFT path pays a cheap divide-by-32768 to get floats.
/// </para>
/// <para>
/// <b>VU / visualizer feed:</b> the analyzer's band mapping is FFT-bin
/// based and tuned for ~44.1 kHz input, so the tap decimates the analyzer
/// feed by round(rate / 44100) - a 192 kHz source feeds every 4th frame
/// and the meter behaves identically at every source rate.  Sinks always
/// receive the full undecimated stream; <see cref="RawFrameAvailable"/>
/// consumers get full-rate samples tagged with the true source rate.
/// </para>
/// <para>
/// <b>Lifecycle:</b> delegates are held as fields because LibVLC stores
/// raw function pointers into native memory - if the delegates get GC'd,
/// native code calls into freed memory and the app crashes.
/// </para>
/// </remarks>
public sealed class AudioTap : IAudioVisualizationSource, IDisposable
{
    public const uint TapChannels = 2;
    private const uint AnalyzerBaseRate = 44100;

    private static readonly ILogger _log = Logging.For("AudioTap");

    private readonly AudioAnalyzer _analyzer;
    private readonly AudioSinkBus _bus;

    private readonly MediaPlayer.LibVLCAudioSetupCb _setupCb;
    private readonly MediaPlayer.LibVLCAudioCleanupCb _cleanupCb;
    private readonly MediaPlayer.LibVLCAudioPlayCb _playCb;
    private readonly MediaPlayer.LibVLCAudioPauseCb _pauseCb;
    private readonly MediaPlayer.LibVLCAudioResumeCb _resumeCb;
    private readonly MediaPlayer.LibVLCAudioFlushCb _flushCb;
    private readonly MediaPlayer.LibVLCAudioDrainCb _drainCb;

    // Negotiated per-source in OnAudioSetup (libvlc's aout open).  _sourceRate
    // is what the sinks play at; the analyzer sees _sourceRate / _decimation
    // (kept near 44.1 kHz so the meter's band mapping stays rate-independent).
    private uint _sourceRate = AnalyzerBaseRate;
    private int _decimation = 1;
    private uint _analyzerRate = AnalyzerBaseRate;
    private int _decimPhase;

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

        _setupCb = OnAudioSetup;
        _cleanupCb = OnAudioCleanup;
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

        player.SetAudioFormatCallback(_setupCb, _cleanupCb);
        player.SetAudioCallbacks(_playCb, _pauseCb, _resumeCb, _flushCb, _drainCb);
        _attachedPlayer = player;
        // Sane default so sinks are open before the first setup callback;
        // OnAudioSetup re-negotiates per source.
        _bus.SetFormat(AudioFormat.CdDaStereo16);

        _lastDrainTicks = Environment.TickCount64;
        _drainTimer ??= new System.Threading.Timer(OnDrainTick, null, DrainPeriodMs, DrainPeriodMs);

        _log.Information("AudioTap attached to MediaPlayer (S16N, native-rate via format callback)");
    }

    // -- Native-engine (non-VLC) feed -------------------------------------
    // The bit-perfect FLAC engine bypasses libvlc entirely, so it announces
    // its format and pushes decoded PCM here to drive the same analyzer /
    // ring / AudioStarted machinery the VLC callbacks use.  The engine owns
    // the sink bus writes; the tap only powers visualization + the
    // audio-started signal on this path.

    private int _externalBits = 16;

    /// <summary>Arms the tap for PCM pushed by the native engine in <paramref name="format"/>.</summary>
    public void BeginExternalSession(AudioFormat format)
    {
        _sourceRate = (uint)format.SampleRate;
        _decimation = Math.Max(1, (int)Math.Round(format.SampleRate / (double)AnalyzerBaseRate));
        _analyzerRate = (uint)(format.SampleRate / _decimation);
        _externalBits = format.BitsPerSample;
        ClearRing();
        _analyzer.Reset();
        _lastDrainTicks = Environment.TickCount64;
        _log.Information("AudioTap external session: {Rate} Hz S{Bits} (analyzer feed {AnalyzerRate} Hz, decimation {Decimation})", format.SampleRate, format.BitsPerSample, _analyzerRate, _decimation);
    }

    /// <summary>Interleaved-stereo PCM from the native engine (S16 or S32 per the session format).</summary>
    public void OnExternalAudio(ReadOnlySpan<byte> pcm, long pts)
    {
        if (pcm.Length == 0)
        {
            return;
        }

        if (System.Threading.Interlocked.Exchange(ref _audioStartedNotified, 1) == 0)
        {
            AudioStarted?.Invoke();
        }
        _active = true;

        int frames;
        int totalFloats;
        if (_externalBits > 16)
        {
            var ints = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(pcm);
            totalFloats = ints.Length;
            frames = ints.Length / (int)TapChannels;
            if (_floatScratch.Length < totalFloats)
            {
                _floatScratch = new float[totalFloats];
            }
            const float Inv2p31 = 1f / 2147483648f;
            for (int i = 0; i < ints.Length; i++)
            {
                _floatScratch[i] = ints[i] * Inv2p31;
            }
        }
        else
        {
            var shorts = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(pcm);
            totalFloats = shorts.Length;
            frames = shorts.Length / (int)TapChannels;
            if (_floatScratch.Length < totalFloats)
            {
                _floatScratch = new float[totalFloats];
            }
            const float Inv32k = 1f / 32768f;
            for (int i = 0; i < shorts.Length; i++)
            {
                _floatScratch[i] = shorts[i] * Inv32k;
            }
        }

        var floatSpan = new ReadOnlySpan<float>(_floatScratch, 0, totalFloats);
        WriteToRing(floatSpan, frames);

        var handler = RawFrameAvailable;
        if (handler != null)
        {
            var frame = new AudioFrame(floatSpan, (int)_sourceRate, (int)TapChannels, pts);
            handler(frame);
        }
    }

    /// <summary>Pause/resume from the native engine - mirrors the VLC pause callback minus the bus control (the engine owns the bus).</summary>
    public void SetExternalPaused(bool paused)
    {
        if (paused)
        {
            _active = false;
            ClearRing();
            _analyzer.Reset();
        }
        else
        {
            _active = true;
            _lastDrainTicks = Environment.TickCount64;
        }
    }

    /// <summary>Ends the native-engine session (stop / end-of-track) - VU decays to zero.</summary>
    public void EndExternalSession()
    {
        _active = false;
        ClearRing();
        _analyzer.Reset();
    }

    // -- LibVLC callbacks ------------------------------------------------

    /// <summary>
    /// libvlc calls this when it opens the audio output for a source, proposing
    /// the decoded format.  We accept the source's own sample rate - that is
    /// the whole point of native-rate playback; VLC inserts no resampler - and
    /// force stereo.  The format fourcc is left untouched: libvlc 3's amem
    /// module proposes and only accepts <c>S16N</c>.
    /// </summary>
    private int OnAudioSetup(ref IntPtr opaque, ref IntPtr format, ref uint rate, ref uint channels)
    {
        if (rate == 0)
        {
            return -1;
        }

        channels = TapChannels;

        _sourceRate = rate;
        _decimation = Math.Max(1, (int)Math.Round(rate / (double)AnalyzerBaseRate));
        _analyzerRate = rate / (uint)_decimation;
        _decimPhase = 0;

        ClearRing();
        _analyzer.Reset();
        _bus.SetFormat(new AudioFormat
        {
            SampleRate = (int)rate,
            Channels = (int)TapChannels,
            BitsPerSample = 16,
            Encoding = AudioSampleEncoding.PcmSigned,
        });

        _log.Information("AudioTap negotiated native-rate output: {Rate} Hz S16 stereo (analyzer feed {AnalyzerRate} Hz, decimation {Decimation})", rate, _analyzerRate, _decimation);
        return 0;
    }

    private void OnAudioCleanup(IntPtr opaque)
    {
        // Per-source aout teardown - nothing to release; the next setup
        // callback re-negotiates everything.
    }

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
            var frame = new AudioFrame(floatSpan, (int)_sourceRate, (int)TapChannels, pts);
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
            // Decimate hi-res sources down to ~44.1 kHz for the analyzer -
            // its band mapping is bin-based and expects that ballpark.  The
            // phase carries across chunks so the kept-frame cadence is exact.
            int stored = 0;
            int i = _decimPhase;
            for (; i < frames; i += _decimation)
            {
                int dst = _ringWriteIdx * 2;
                _ringInterleaved[dst]     = interleaved[i * 2];
                _ringInterleaved[dst + 1] = interleaved[i * 2 + 1];
                _ringWriteIdx = (_ringWriteIdx + 1) % RingCapacityFrames;
                stored++;
            }
            _decimPhase = i - frames;

            _ringFillFrames = Math.Min(RingCapacityFrames, _ringFillFrames + stored);
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

            // Target frames = analyzer-feed rate * dt. Cap at one second of
            // audio so a long system pause doesn't dump a giant block into
            // the analyzer in one tick (which would model a discontinuity).
            var analyzerRate = _analyzerRate;
            int targetFrames = (int)Math.Min(dtMs * analyzerRate / 1000, analyzerRate);

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
            _decimPhase = 0;
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
