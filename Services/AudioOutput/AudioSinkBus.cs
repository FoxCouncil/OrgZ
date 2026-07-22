// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Serilog;

namespace OrgZ.Services.AudioOutput;

/// <summary>
/// Composite audio output - holds a collection of active <see cref="IAudioSink"/>s
/// and fans every <see cref="Write"/> out to all of them simultaneously.
/// That's what lets the user tick multiple output devices in Settings and
/// hear music on all of them at once (laptop speakers + desk speakers +
/// AirPlay receiver in the kitchen, for example).
/// </summary>
/// <remarks>
/// <para>
/// The bus owns its sinks - removing a sink calls <see cref="IDisposable.Dispose"/>
/// on it.  The set of active sinks is mutated from the UI thread
/// (Settings dialog) while <see cref="Write"/> runs on LibVLC's audio worker
/// thread, so sink list access is guarded by a lock; the lock is only held
/// across reference copies (quick) and never while actually writing PCM.
/// </para>
/// <para>
/// Per-sink volume is applied by each sink in its own <see cref="IAudioSink.Write"/>;
/// master volume lives on the bus and is applied via sample scaling here
/// before fan-out - that way even sinks without native gain support honor it.
/// </para>
/// <para>
/// The bus aggregates incoming PCM to at least <see cref="AggregateTargetMs"/>
/// per fan-out.  LibVLC's audio callback delivers whatever block size its
/// decode chain produces: 44.1 kHz FLAC arrives in comfortable 4096-frame
/// (~93 ms) blocks, but hi-res sources (24-bit/192 kHz) come out of VLC's
/// resampler as 264-frame (~6 ms) slivers.  Platform sinks size their
/// hardware queues in buffers, not milliseconds - waveOut's 4-slot ring held
/// a mere 24 ms of audio for such slivers and underran into audible garbage.
/// Aggregating here makes sink queue depth independent of the source's
/// chunking, for every platform sink at once.
/// </para>
/// </remarks>
public sealed class AudioSinkBus : IDisposable
{
    /// <summary>
    /// Minimum audio per fan-out, in milliseconds.  Blocks at or above this
    /// size pass through unbuffered (44.1 kHz FLAC's ~93 ms blocks behave
    /// exactly as before); smaller blocks accumulate until they add up to it.
    /// </summary>
    public const int AggregateTargetMs = 50;

    private static readonly ILogger _log = Logging.For("AudioSinkBus");

    private readonly object _lock = new();
    private readonly List<IAudioSink> _sinks = [];
    private AudioFormat? _format;
    private float _masterVolume = 1f;
    private float _normalizationGain = 1f;
    private bool _disposed;

    // PCM aggregation state.  Write is called from LibVLC's single audio
    // worker thread; SetFormat / FlushAll can arrive from other threads, so
    // the accumulator has its own lock (never held across sink writes).
    private readonly object _accumLock = new();
    private byte[] _accum = [];
    private int _accumFill;
    private int _accumTarget;

    /// <summary>
    /// Master volume applied to all sinks in [0, 1].  Values above 1 are
    /// clamped - no amplification to avoid clipping.
    /// </summary>
    public float MasterVolume
    {
        get => _masterVolume;
        set => _masterVolume = Math.Clamp(value, 0f, 1f);
    }

    /// <summary>
    /// Automatic loudness-normalization gain (ReplayGain / "Sound Check"), applied on top of
    /// <see cref="MasterVolume"/>.  Unlike master volume this MAY exceed 1 to bring a quiet track
    /// up to the reference; the scaling pass clamps samples so an over-boost can't overflow.
    /// Set to 1 for no normalization.  Takes effect on the next PCM buffer, so it can be changed
    /// mid-track and the listener hears it immediately.
    /// </summary>
    public float NormalizationGain
    {
        get => _normalizationGain;
        set => _normalizationGain = Math.Clamp(value, 0f, 4f);
    }

    public IReadOnlyList<IAudioSink> Sinks
    {
        get { lock (_lock) { return [.. _sinks]; } }
    }

    public AudioFormat? Format => _format;

    public void SetFormat(AudioFormat format)
    {
        lock (_accumLock)
        {
            var target = format.BytesPerSecond * AggregateTargetMs / 1000;
            target -= target % format.BytesPerFrame;
            _accumTarget = Math.Max(target, format.BytesPerFrame);
            if (_accum.Length < _accumTarget * 2)
            {
                _accum = new byte[_accumTarget * 2];
            }
            _accumFill = 0;
        }

        lock (_lock)
        {
            _format = format;
            foreach (var sink in _sinks)
            {
                TryOpen(sink, format);
            }
        }
    }

    public void Add(IAudioSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_lock)
        {
            if (_sinks.Any(s => s.Id == sink.Id))
            {
                return;
            }

            _sinks.Add(sink);
            if (_format.HasValue)
            {
                TryOpen(sink, _format.Value);
            }
        }

        _log.Information("AudioSinkBus: added sink {Id} ({Name})", sink.Id, sink.DisplayName);
    }

    public void Remove(string sinkId)
    {
        IAudioSink? removed = null;
        lock (_lock)
        {
            for (int i = 0; i < _sinks.Count; i++)
            {
                if (_sinks[i].Id == sinkId)
                {
                    removed = _sinks[i];
                    _sinks.RemoveAt(i);
                    break;
                }
            }
        }

        if (removed != null)
        {
            _log.Information("AudioSinkBus: removed sink {Id}", removed.Id);
            removed.Dispose();
        }
    }

    /// <summary>
    /// Pauses every active sink at the hardware level.  Called from LibVLC's
    /// pause callback so the user's click is audible immediately rather than
    /// after the per-sink buffer queue drains.
    /// </summary>
    public void PauseAll() => ForEachSink(s => s.Pause());

    public void ResumeAll() => ForEachSink(s => s.Resume());

    /// <summary>
    /// Flushes every active sink's queued audio - called on seek / track
    /// change so the listener doesn't hear the tail of the previous position.
    /// Also drops any PCM still accumulating toward the next fan-out.
    /// </summary>
    public void FlushAll()
    {
        lock (_accumLock)
        {
            _accumFill = 0;
        }
        ForEachSink(s => s.Flush());
    }

    /// <summary>
    /// Plays out every sink's queued audio - the natural end-of-track
    /// counterpart to <see cref="FlushAll"/>.  Any PCM still accumulating
    /// toward the next fan-out is written first (it's the very end of the
    /// track), then each sink blocks until its hardware queue has actually
    /// reached the speakers.  Called from LibVLC's drain callback, which
    /// expects this to block until playback is audibly finished.
    /// </summary>
    public void DrainAll()
    {
        byte[]? tail = null;
        int tailLength = 0;
        lock (_accumLock)
        {
            if (_accumFill > 0)
            {
                tail = _accum;
                tailLength = _accumFill;
                _accumFill = 0;
            }
        }

        if (tail != null)
        {
            FanOut(tail.AsSpan(0, tailLength));
        }

        ForEachSink(s => s.Drain());
    }

    private void ForEachSink(Action<IAudioSink> action)
    {
        IAudioSink[] sinks;
        lock (_lock)
        {
            if (_sinks.Count == 0)
            {
                return;
            }
            sinks = _sinks.ToArray();
        }

        foreach (var sink in sinks)
        {
            try
            {
                action(sink);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "AudioSinkBus: sink {Id} control op failed", sink.Id);
            }
        }
    }

    public void Clear()
    {
        List<IAudioSink> drained;
        lock (_lock)
        {
            drained = [.. _sinks];
            _sinks.Clear();
        }

        foreach (var sink in drained)
        {
            try { sink.Dispose(); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Queues <paramref name="pcm"/> for fan-out to every active sink.
    /// Blocks of at least <see cref="AggregateTargetMs"/> fan out immediately;
    /// smaller blocks accumulate until they add up to it, so sinks always see
    /// hardware-queue-friendly buffer sizes regardless of how the decoder
    /// chunked the stream.
    /// </summary>
    public void Write(ReadOnlySpan<byte> pcm)
    {
        if (_disposed || pcm.Length == 0)
        {
            return;
        }

        byte[]? aggregated = null;
        int aggregatedLength = 0;
        lock (_accumLock)
        {
            // _accumTarget == 0 means Write before SetFormat - pass through.
            // A block already at/above target with nothing pending skips the
            // copy entirely (the pre-aggregation fast path).
            if (_accumTarget > 0 && (pcm.Length < _accumTarget || _accumFill > 0))
            {
                var needed = _accumFill + pcm.Length;
                if (_accum.Length < needed)
                {
                    Array.Resize(ref _accum, needed);
                }
                pcm.CopyTo(_accum.AsSpan(_accumFill));
                _accumFill = needed;
                if (_accumFill < _accumTarget)
                {
                    return;
                }
                aggregated = _accum;
                aggregatedLength = _accumFill;
                _accumFill = 0;
            }
        }

        FanOut(aggregated != null ? aggregated.AsSpan(0, aggregatedLength) : pcm);
    }

    /// <summary>
    /// Fans <paramref name="pcm"/> out to every active sink.  Returns quickly
    /// - sinks queue the buffer internally for their own playback thread.
    /// A master-volume scaling pass is applied into a scratch buffer when
    /// <see cref="MasterVolume"/> &lt; 1 so sinks see the attenuated bytes.
    /// </summary>
    private void FanOut(ReadOnlySpan<byte> pcm)
    {
        // Snapshot the sink list so we don't hold the lock across the
        // potentially-slow sink writes.
        IAudioSink[] sinks;
        lock (_lock)
        {
            if (_sinks.Count == 0)
            {
                return;
            }
            sinks = _sinks.ToArray();
        }

        // Apply master volume × normalization gain if the product isn't unity.  Scaling is done
        // on a per-call scratch array so sinks receive the adjusted data and can still apply their
        // own per-sink volumes on top.  The combined gain can be >1 (a quiet track boosted by
        // ReplayGain); ScaleS16 clamps so that can't overflow the sample range.
        ReadOnlySpan<byte> buffer = pcm;
        byte[]? scratch = null;
        var effectiveGain = _masterVolume * _normalizationGain;
        if (Math.Abs(effectiveGain - 1f) > 0.001f && _format is { BitsPerSample: 16, Encoding: AudioSampleEncoding.PcmSigned })
        {
            scratch = System.Buffers.ArrayPool<byte>.Shared.Rent(pcm.Length);
            ScaleS16(pcm, scratch.AsSpan(0, pcm.Length), effectiveGain);
            buffer = scratch.AsSpan(0, pcm.Length);
        }

        try
        {
            foreach (var sink in sinks)
            {
                try
                {
                    sink.Write(buffer);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "AudioSinkBus: sink {Id} write failed", sink.Id);
                }
            }
        }
        finally
        {
            if (scratch != null)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(scratch);
            }
        }
    }

    private static void ScaleS16(ReadOnlySpan<byte> source, Span<byte> dest, float gain)
    {
        var src = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(source);
        var dst = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(dest);
        for (int i = 0; i < src.Length; i++)
        {
            dst[i] = (short)Math.Clamp(src[i] * gain, short.MinValue, short.MaxValue);
        }
    }

    private static void TryOpen(IAudioSink sink, AudioFormat format)
    {
        try
        {
            if (!sink.IsOpen)
            {
                sink.Open(format);
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "AudioSinkBus: failed to open {Id}", sink.Id);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Clear();
    }
}
