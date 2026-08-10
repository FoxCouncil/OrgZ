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
/// <summary>
/// One output that stopped working, and why - phrased for the status bar.
/// <paramref name="NeedsPassword"/> marks the one failure the user can actually fix from
/// here, so the UI can offer a password prompt instead of just reporting it.
/// </summary>
public sealed record SinkFailure(string SinkId, string DisplayName, string Reason, bool NeedsPassword = false);

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

    // Immutable snapshot of _sinks, swapped on every mutation. The fan-out reads it
    // without taking a lock or allocating - it used to ToArray() under _lock ~20×/second
    // for the life of playback, and the flyout took three more snapshots per slider tick.
    private volatile IAudioSink[] _sinkSnapshot = [];

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

    public IReadOnlyList<IAudioSink> Sinks => _sinkSnapshot;

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
            _sinkSnapshot = [.. _sinks];
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
                    _sinkSnapshot = [.. _sinks];
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
        // COPY the tail inside the lock rather than handing _accum out: Write reuses
        // that same array from offset 0 the moment the fill is reset, so a concurrent
        // writer (the FLAC engine's pump drains while VLC's thread can still write)
        // would overwrite bytes the fan-out was still reading and scaling.
        byte[]? tail = null;
        int tailLength = 0;
        lock (_accumLock)
        {
            if (_accumFill > 0)
            {
                tailLength = _accumFill;
                tail = System.Buffers.ArrayPool<byte>.Shared.Rent(tailLength);
                _accum.AsSpan(0, tailLength).CopyTo(tail);
                _accumFill = 0;
            }
        }

        if (tail != null)
        {
            try
            {
                FanOut(tail.AsSpan(0, tailLength));
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(tail);
            }
        }

        ForEachSink(s => s.Drain());
    }

    private void ForEachSink(Action<IAudioSink> action)
    {
        foreach (var sink in _sinkSnapshot)
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
            _sinkSnapshot = [];
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
        // The snapshot is immutable and swapped whole on mutation, so the fan-out
        // neither locks nor allocates - it runs on the audio thread ~20×/second.
        var sinks = _sinkSnapshot;

        // One read of the nullable format for the whole call: a torn read across an
        // engine swap could otherwise pick the wrong scaling branch for one buffer.
        var format = _format;

        // Before the early-out: with no open sink there is no clock, and that has to be
        // supplied here or playback races (see PaceIfUnclocked).
        PaceIfUnclocked(pcm.Length, format, sinks);

        if (sinks.Length == 0)
        {
            return;
        }

        // Apply master volume × normalization gain if the product isn't unity.  Scaling is done
        // on a per-call scratch array so sinks receive the adjusted data and can still apply their
        // own per-sink volumes on top.  The combined gain can be >1 (a quiet track boosted by
        // ReplayGain); ScaleS16 clamps so that can't overflow the sample range.
        ReadOnlySpan<byte> buffer = pcm;
        byte[]? scratch = null;
        var effectiveGain = _masterVolume * _normalizationGain;
        if (Math.Abs(effectiveGain - 1f) > 0.001f && format is { Encoding: AudioSampleEncoding.PcmSigned, BitsPerSample: 16 or 32 })
        {
            scratch = System.Buffers.ArrayPool<byte>.Shared.Rent(pcm.Length);
            if (format is { BitsPerSample: 32 })
            {
                PcmMath.ScaleS32(pcm, scratch.AsSpan(0, pcm.Length), effectiveGain);
            }
            else
            {
                PcmMath.ScaleS16(pcm, scratch.AsSpan(0, pcm.Length), effectiveGain);
            }
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

    // Wall-clock state for PaceIfUnclocked. Only touched from the audio thread.
    private long _paceStart;
    private long _pacedBytes;

    /// <summary>
    /// Supplies a playback clock when no sink is open to provide one.
    ///
    /// Normally an output paces the decoder: WaveOut blocks inside Write until its buffer
    /// drains, and the AirPlay sink back-pressures once it's streaming. LibVLC's audio
    /// callback has no clock of its own - it decodes exactly as fast as we accept buffers.
    /// So when every sink is shut (an AirPlay receiver that refused to pair, a device that
    /// vanished, or simply nothing selected) the track rips past at whatever speed the disk
    /// can feed it, which is heard as playback running at many times normal speed.
    ///
    /// Sleeping here is correct rather than wasteful: this IS the decoder thread, and
    /// holding it to real time is exactly what an output would have done. The wait is
    /// capped so a format glitch can't park playback indefinitely, and the accumulator
    /// resets whenever a real sink takes over so the two clocks never fight.
    /// </summary>
    private void PaceIfUnclocked(int byteCount, AudioFormat? format, IAudioSink[] sinks)
    {
        foreach (var sink in sinks)
        {
            if (sink.IsOpen)
            {
                _pacedBytes = 0;   // a real output owns the clock again
                return;
            }
        }

        if (format is not { SampleRate: > 0, Channels: > 0, BitsPerSample: > 0 } clock)
        {
            return;
        }

        var frameBytes = clock.Channels * (clock.BitsPerSample / 8);
        if (frameBytes <= 0)
        {
            return;
        }

        if (_pacedBytes == 0)
        {
            _paceStart = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        _pacedBytes += byteCount;

        var due = (double)(_pacedBytes / frameBytes) / clock.SampleRate;
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(_paceStart).TotalSeconds;
        var wait = due - elapsed;

        if (wait > 0)
        {
            Thread.Sleep(TimeSpan.FromSeconds(Math.Min(wait, 1.0)));
        }
    }

    /// <summary>Drops the fallback clock's accumulator - call on seek/stop so it restarts clean.</summary>
    internal void ResetPacing() => _pacedBytes = 0;

    // A sink that just refused to open must not be retried on every buffer. The bus calls
    // TryOpen ~20x/second, which turned one locked-out AirPlay receiver into a flood of
    // identical warnings - and, worse, kept restarting the receiver's own lockout timer.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _openFailedAt = new(StringComparer.Ordinal);
    private const int ReopenCooldownMs = 5000;

    // Instance, not static: a failed open raises SinkFailed so the UI can say why.
    private void TryOpen(IAudioSink sink, AudioFormat format)
    {
        if (!sink.IsOpen
            && _openFailedAt.TryGetValue(sink.Id, out var failedAt)
            && Environment.TickCount64 - failedAt < ReopenCooldownMs)
        {
            return;
        }

        try
        {
            // Native-rate playback: the format changes per source (a 44.1 kHz
            // CD rip followed by a 192 kHz master), so a sink open at the old
            // rate must be reopened - devices can't change rate in place.
            if (sink.IsOpen)
            {
                if (sink.CurrentFormat == format)
                {
                    return;
                }
                sink.Close();
            }
            sink.Open(format);
            _openFailedAt.TryRemove(sink.Id, out _);
        }
        catch (Exception ex)
        {
            _openFailedAt[sink.Id] = Environment.TickCount64;
            _log.Warning(ex, "AudioSinkBus: failed to open {Id}", sink.Id);
            // A sink that can't open produces silence on that output. The log alone left
            // the user staring at a playing track with no sound and no explanation.
            SinkFailed?.Invoke(this, new SinkFailure(sink.Id, sink.DisplayName, ex.Message,
                sink is AirPlay.AirPlayRaopSink { NeedsPassword: true }));
        }
    }

    /// <summary>
    /// Raised when a sink can't be opened or its transport dies - carries a reason fit to
    /// show the user, because silence with no message is indistinguishable from a bug.
    /// </summary>
    public event EventHandler<SinkFailure>? SinkFailed;

    /// <summary>Lets a sink report an asynchronous failure (e.g. a network handshake that fails after Open).</summary>
    internal void ReportSinkFailure(string id, string displayName, string reason, bool needsPassword = false)
        => SinkFailed?.Invoke(this, new SinkFailure(id, displayName, reason, needsPassword));

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
