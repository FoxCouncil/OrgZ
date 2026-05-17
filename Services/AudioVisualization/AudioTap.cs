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
/// <b>Sample format:</b> we ask LibVLC for <c>S16N</c> — signed-16-bit,
/// native-endian, interleaved stereo at 44.1 kHz.  Most output sinks
/// (waveOut, CoreAudio, PulseAudio) consume S16 directly without extra
/// conversion; the FFT path pays a cheap divide-by-32768 to get floats.
/// </para>
/// <para>
/// <b>Lifecycle:</b> delegates are held as fields because LibVLC stores
/// raw function pointers into native memory — if the delegates get GC'd,
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

    public AudioTap(AudioSinkBus bus, AudioAnalyzer? analyzer = null)
    {
        ArgumentNullException.ThrowIfNull(bus);
        _bus = bus;
        _analyzer = analyzer ?? new AudioAnalyzer();

        _playCb = OnAudioPlay;
        _pauseCb = OnAudioPause;
        _resumeCb = OnAudioResume;
        _flushCb = OnAudioFlush;
        _drainCb = OnAudioDrain;
    }

    public int BandCount => _analyzer.BandCount;

    public bool IsActive => _active;

    public event AudioFrameHandler? RawFrameAvailable;

    public void CopyBandLevels(Span<float> destination) => _analyzer.CopyBands(destination);

    public void CopyBandLevelsStereo(Span<float> left, Span<float> right) => _analyzer.CopyBandsStereo(left, right);

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

        _active = true;

        int totalShorts = checked((int)(count * TapChannels));
        int byteLen = totalShorts * sizeof(short);

        // (a) Forward to the sink bus so audio plays through whatever devices
        //     the user has selected — fans out to multiple outputs when applicable.
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
        _analyzer.FeedInterleavedStereo(floatSpan);

        var handler = RawFrameAvailable;
        if (handler != null)
        {
            var frame = new AudioFrame(floatSpan, (int)TapSampleRate, (int)TapChannels, pts);
            handler(frame);
        }
    }

    private void OnAudioPause(IntPtr data, long pts)
    {
        _active = false;
        _bus.PauseAll();
        // Clear analyzer bands so the VU meter has a zero target to decay
        // toward.  Without this, paused playback leaves the bars pinned at
        // the last level they were showing, which looks broken.
        _analyzer.Reset();
    }

    private void OnAudioResume(IntPtr data, long pts)
    {
        _active = true;
        _bus.ResumeAll();
    }

    private void OnAudioFlush(IntPtr data, long pts)
    {
        // Seek / track change — drop both the analyzer's windowed state and
        // every sink's pending hardware queue so the user hears the new
        // position right away instead of the last buffered chunk of the old.
        _analyzer.Reset();
        _bus.FlushAll();
    }

    private void OnAudioDrain(IntPtr data)
    {
        _active = false;
        _analyzer.Reset();
        _bus.FlushAll();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _attachedPlayer = null;
    }
}
