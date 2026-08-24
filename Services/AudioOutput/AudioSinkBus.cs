// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Serilog;

namespace OrgZ.Services.AudioOutput;

/// <summary>
/// One output that stopped working, and why - phrased for the status bar.
/// <paramref name="NeedsPassword"/> marks the one failure the user can actually fix from
/// here, so the UI can offer a password prompt instead of just reporting it.
/// </summary>
public sealed record SinkFailure(string SinkId, string DisplayName, string Reason, bool NeedsPassword = false);

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
            // What the delay lines hold is in the OLD format - a different rate or depth, so
            // the same bytes are a different length of music. Start them again.
            if (_format != format)
            {
                ResetDelays();
                Realign();
            }

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
            Realign();

            // A sink that joins live audio opens now. So does one that asks to be opened on
            // selection - a network receiver, which wants its pairing and its buffer done
            // before the first note rather than after it.
            //
            // The old rule opened neither while stopped, because running the handshake made
            // a HomePod show itself as playing with nothing playing. That is now handled
            // where it belongs: the session announces a paused state until audio actually
            // arrives, so it can hold the speaker without lying about it.
            if (IsAudioFlowing || sink.HoldsSessionWhenIdle)
            {
                // A receiver that wants an early open may be selected before anything has
                // ever played, so there is no format to inherit. AirPlay is 44.1kHz stereo
                // whatever the source, which makes CD the honest default rather than a guess.
                TryOpen(sink, _format ?? AudioFormat.CdDaStereo16);
            }
        }

        HookRemoteControl(sink);

        // A sink usually joins mid-track - the user picks a speaker while something is
        // already playing - and SetTrackInfo has long since been and gone. Replay it, or the
        // receiver shows nothing until the next track and never learns this one's length.
        if (_nowPlaying is { } track)
        {
            TrySetTrackInfo(sink, track);
        }

        _log.Information("AudioSinkBus: added sink {Id} ({Name})", sink.Id, sink.DisplayName);
    }

    /// <summary>
    /// A device asked us to change playback - the play/pause and skip buttons on an AirPlay
    /// receiver's Home-app tile. Carries the four-character command code.
    /// </summary>
    public event EventHandler<string>? RemoteCommand;

    /// <summary>
    /// A device changed its own volume - carries which output it was and a linear 0-1 level.
    ///
    /// The id matters: a speaker's volume is ITS volume. Without knowing which one moved,
    /// the only thing a listener can do is guess, and with two receivers selected the guess
    /// is wrong half the time.
    /// </summary>
    public event EventHandler<(string SinkId, float Level)>? RemoteVolume;

    private void HookRemoteControl(IAudioSink sink)
    {
        if (sink is IRemoteControllableSink remote)
        {
            remote.RemoteCommand += OnSinkRemoteCommand;
            remote.RemoteVolume += OnSinkRemoteVolume;
        }
    }

    private void UnhookRemoteControl(IAudioSink sink)
    {
        if (sink is IRemoteControllableSink remote)
        {
            remote.RemoteCommand -= OnSinkRemoteCommand;
            remote.RemoteVolume -= OnSinkRemoteVolume;
        }
    }

    private void OnSinkRemoteCommand(object? sender, string command) => RemoteCommand?.Invoke(this, command);

    private void OnSinkRemoteVolume(object? sender, float level)
    {
        if (sender is IAudioSink sink)
        {
            RemoteVolume?.Invoke(this, (sink.Id, level));
        }
    }

    /// <summary>What is playing now, kept so a sink added mid-track can be told.</summary>
    private (string? Title, string? Artist, string? Album, TimeSpan? Duration, byte[]? Artwork)? _nowPlaying;

    /// <summary>
    /// Passes the current track to every open sink. Local outputs ignore it; a network
    /// receiver uses it for its display and, more importantly, for the stream's length.
    /// </summary>
    public void SetTrackInfo(string? title, string? artist, string? album, TimeSpan? duration, byte[]? artwork)
    {
        var track = (title, artist, album, duration, artwork);
        _nowPlaying = track;

        _log.Information("AudioSinkBus: track info {Title} -> {Count} sink(s)", title ?? "(none)", _sinkSnapshot.Length);

        foreach (var sink in _sinkSnapshot)
        {
            TrySetTrackInfo(sink, track);
        }
    }

    private void TrySetTrackInfo(IAudioSink sink, (string? Title, string? Artist, string? Album, TimeSpan? Duration, byte[]? Artwork) track)
    {
        try
        {
            sink.SetTrackInfo(track.Title, track.Artist, track.Album, track.Duration, track.Artwork);
        }
        catch (Exception ex)
        {
            // Decoration must never take down playback.
            _log.Warning(ex, "AudioSinkBus: sink {Id} rejected track info", sink.Id);
        }
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
                    Realign();
                    break;
                }
            }
        }

        if (removed != null)
        {
            _delays.TryRemove(removed.Id, out _);
            _unmeasured.TryRemove(removed.Id, out _);
            UnhookRemoteControl(removed);
            _log.Information("AudioSinkBus: removed sink {Id}", removed.Id);
            removed.Dispose();
        }
    }

    /// <summary>
    /// Pauses every active sink at the hardware level.  Called from LibVLC's
    /// pause callback so the user's click is audible immediately rather than
    /// after the per-sink buffer queue drains.
    /// </summary>
    public void PauseAll()
    {
        // A paused network receiver drops the seconds it was holding, so an aligned local
        // output has to drop the same seconds or resume ahead of it.
        ResetDelays();
        ForEachSink(s => s.Pause());
    }

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

        ResetDelays();
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
            _delays.Clear();
            _unmeasured.Clear();
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

        Interlocked.Exchange(ref _lastWriteTicks, Environment.TickCount64);

        // Audio is arriving, so any sink still shut is one that was picked while nothing was
        // playing. THIS is where it opens.
        //
        // Add deliberately refuses to open a sink while the app is stopped - opening an
        // AirPlay session runs the handshake through RECORD, and RECORD tells a receiver a
        // stream is starting, so merely ticking a HomePod in the picker made it show itself
        // as playing. But refusing there and nowhere else meant the speaker then stayed silent
        // for the whole session: nothing ever came back to open it. A write is the one moment
        // that is unambiguously "audio is flowing now".
        OpenIdleSinks(sinks, format);

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

        // The slowest open output sets the beat everything else marches to.
        var slowest = SlowestLatency(sinks);
        byte[]? delayScratch = null;
        var measured = false;

        try
        {
            foreach (var sink in sinks)
            {
                try
                {
                    var delayed = Align(sink, buffer, format, slowest, ref delayScratch);
                    sink.Write(delayed);

                    if (!_unmeasured.IsEmpty && _unmeasured.TryRemove(sink.Id, out _))
                    {
                        measured = true;
                    }
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
            if (delayScratch != null)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(delayScratch);
            }
        }

        // Something now knows how deep it really is, so line the outputs up again against the
        // measurement instead of the opening guess - see _unmeasured. Done after the loop so
        // the sinks in this one buffer are all aligned the same way.
        if (measured)
        {
            Realign();
        }
    }

    /// <summary>
    /// Outputs that have been opened but never written to, and so cannot yet say how far
    /// behind they run.
    ///
    /// A local sink sizes its queue in BUFFERS, and the size of a buffer is whatever the
    /// decoder hands over - 50ms of hi-res slivers aggregated, 93ms of a 44.1kHz FLAC block.
    /// Until the first write it can only quote the bus's aggregation target, which for FLAC is
    /// out by nearly half. A second sound card ticked mid-track was therefore lined up against
    /// that guess and, since alignment is worked out once, stayed a sixth of a second away from
    /// the first one for the rest of the session. The first write is where the real figure
    /// appears, so it counts as an alignment event.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _unmeasured = new(StringComparer.Ordinal);

    /// <summary>
    /// One output's delay line, kept for as long as the sink is on the bus. Keyed by id
    /// because the sink itself is replaced on a format change and the alignment isn't.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SinkDelayLine> _delays = new(StringComparer.Ordinal);

    /// <summary>
    /// Bumped whenever the set of outputs or the stream format changes - the only things
    /// that can genuinely move the alignment. Delay lines re-prime when it moves and at no
    /// other time.
    /// </summary>
    private int _alignment;

    /// <summary>Marks the alignment stale, so every line re-primes on the next buffer.</summary>
    private void Realign() => Interlocked.Increment(ref _alignment);

    /// <summary>
    /// How far into the track the LISTENER is, when that differs from the decoder - or null
    /// when it doesn't.
    ///
    /// It differs when every output is a distant one. A sound card plays what it is given
    /// almost at once, so with any local output selected the decoder's clock IS what the room
    /// hears. With only an AirPlay receiver, the room is a couple of seconds behind, and a
    /// timeline that ignores that is a timeline that disagrees with the music playing in it.
    ///
    /// The slowest reporting output wins, since that is the one still to finish.
    /// </summary>
    public TimeSpan? ListenerPosition
    {
        get
        {
            TimeSpan? position = null;

            foreach (var sink in _sinkSnapshot)
            {
                if (!sink.IsOpen)
                {
                    continue;
                }

                // A local output anywhere in the mix means the decoder's clock is honest;
                // there is nothing to correct and nothing to guess at.
                if (sink.OutputLatency < TimeSpan.FromMilliseconds(500))
                {
                    return null;
                }

                if (sink.PlaybackPosition is { } reported && (position is null || reported > position))
                {
                    position = reported;
                }
            }

            return position;
        }
    }

    /// <summary>The deepest latency among the outputs actually playing.</summary>
    private static TimeSpan SlowestLatency(IAudioSink[] sinks)
    {
        var slowest = TimeSpan.Zero;
        foreach (var sink in sinks)
        {
            if (sink.IsOpen && sink.OutputLatency > slowest)
            {
                slowest = sink.OutputLatency;
            }
        }

        return slowest;
    }

    /// <summary>
    /// Holds one sink back by the difference between its latency and the slowest sink's, so
    /// the same instant of music leaves every speaker at once.
    ///
    /// The common case - one output, or several equally quick ones - allocates nothing and
    /// touches no buffer: there is nothing to align, so the caller's own span goes straight
    /// through.
    /// </summary>
    private ReadOnlySpan<byte> Align(IAudioSink sink, ReadOnlySpan<byte> pcm, AudioFormat? format, TimeSpan slowest, ref byte[]? scratch)
    {
        if (format is not { BytesPerSecond: > 0 } clock || !sink.IsOpen)
        {
            return pcm;
        }

        // A distant output that reports nothing has not told us how far ahead it needs its
        // audio - only that it has not been asked, or does not know yet. Taking that zero at
        // face value inverts the whole idea: the far speaker gets held back to match a local
        // ring, on top of the seconds it already buffers. Leave it alone until it says.
        if (sink.HoldsSessionWhenIdle && sink.OutputLatency == TimeSpan.Zero)
        {
            return pcm;
        }

        var behind = slowest - sink.OutputLatency;
        var delayBytes = behind > TimeSpan.Zero ? (int)(behind.TotalSeconds * clock.BytesPerSecond) : 0;
        delayBytes -= delayBytes % Math.Max(clock.BytesPerFrame, 1);

        if (delayBytes <= 0 && !_delays.ContainsKey(sink.Id))
        {
            return pcm;
        }

        var line = _delays.GetOrAdd(sink.Id, _ => new SinkDelayLine());

        // Aligned ONCE, and then left alone until something really changes.
        //
        // Re-priming a line costs that sink a delay's worth of silence, so it must happen on
        // events - a speaker joining or leaving, a format change - and never on measurement
        // noise. Comparing the reported latency against the configured one looked equivalent
        // and was not: a sink reports its depth in whole buffers, so an output holding four
        // 50ms buffers reports 200ms or 150ms depending on which side of a callback it is
        // asked. Any threshold fine enough to catch a real change is finer than that step,
        // so the line was re-primed on almost every write and the output it was meant to
        // align played nothing but its own priming silence.
        if (line.Generation != _alignment)
        {
            line.Generation = _alignment;
            _log.Information("AudioSinkBus: {Id} aligned {Ms}ms behind the slowest output", sink.Id, (int)behind.TotalMilliseconds);
            line.Configure(delayBytes, pcm.Length);
        }

        if (line.DelayBytes <= 0)
        {
            return pcm;
        }

        scratch ??= System.Buffers.ArrayPool<byte>.Shared.Rent(pcm.Length);
        if (scratch.Length < pcm.Length)
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(scratch);
            scratch = System.Buffers.ArrayPool<byte>.Shared.Rent(pcm.Length);
        }

        var output = scratch.AsSpan(0, pcm.Length);
        line.Process(pcm, output);
        return output;
    }

    /// <summary>
    /// Drops what the delay lines hold. Called for seek and pause, where the audio in them
    /// belongs to a moment the listener has just left - and never for a track change, where
    /// it is simply the end of the song still on its way to the speaker.
    /// </summary>
    private void ResetDelays()
    {
        foreach (var line in _delays.Values)
        {
            line.RequestReset();
        }
    }

    /// <summary>
    /// Opens any sink that isn't. Cheap in the common case - every sink is already open and
    /// this is a field read each - and rate-limited for the one that isn't by TryOpen's own
    /// cooldown, so a receiver that refuses to pair is retried every five seconds rather than
    /// twenty times a second.
    /// </summary>
    private void OpenIdleSinks(IAudioSink[] sinks, AudioFormat? format)
    {
        if (format is not { } current)
        {
            return;
        }

        foreach (var sink in sinks)
        {
            if (!sink.IsOpen)
            {
                TryOpen(sink, current);
            }
        }
    }

    // Wall-clock state for PaceIfUnclocked. Only touched from the audio thread.
    private long _paceStart;
    private long _pacedBytes;

    /// <summary>When audio last reached the bus - the difference between stopped and playing.</summary>
    private long _lastWriteTicks;

    /// <summary>
    /// True when audio has arrived recently enough that playback is genuinely running.
    ///
    /// The window is generous on purpose: a decoder gap between tracks is not "stopped", and
    /// treating it as such would tear a network session down and pay for the handshake again
    /// on the next track.
    /// </summary>
    private bool IsAudioFlowing => Environment.TickCount64 - Interlocked.Read(ref _lastWriteTicks) < 3000;

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
            if (sink.ProvidesClock)
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
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long At, string Reason)> _openFailedAt = new(StringComparer.Ordinal);
    private const int ReopenCooldownMs = 5000;

    // Instance, not static: a failed open raises SinkFailed so the UI can say why.
    private void TryOpen(IAudioSink sink, AudioFormat format)
    {
        var previous = _openFailedAt.TryGetValue(sink.Id, out var failure) ? failure : default;

        if (!sink.IsOpen && previous.At != 0 && Environment.TickCount64 - previous.At < ReopenCooldownMs)
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

                // Give the sink a chance to absorb the change first - see TryAdaptFormat.
                if (sink.TryAdaptFormat(format))
                {
                    return;
                }

                // For a network sink this is a whole new session: pairing, handshake, and a
                // display that goes blank in between. Worth saying which change caused it.
                _log.Information("AudioSinkBus: reopening {Id} - {OldRate}Hz/{OldBits}-bit cannot become {NewRate}Hz/{NewBits}-bit",
                    sink.Id, sink.CurrentFormat?.SampleRate, sink.CurrentFormat?.BitsPerSample, format.SampleRate, format.BitsPerSample);

                sink.Close();
            }
            sink.Open(format);
            _openFailedAt.TryRemove(sink.Id, out _);
            _unmeasured[sink.Id] = 0;

            // An output that has just opened has a latency it did not have a moment ago -
            // an AirPlay receiver goes from nothing to two seconds - so everything else has
            // to be lined up against it again.
            Realign();
        }
        catch (Exception ex)
        {
            _openFailedAt[sink.Id] = (Environment.TickCount64, ex.Message);

            // ONCE per reason, not once per attempt.
            //
            // A sink that can't open produces silence on that output, and the log alone left
            // the user staring at a playing track with no sound and no explanation. But the
            // retry runs for as long as the track does, and telling someone the same thing
            // every five seconds is its own kind of broken - the message is news the first
            // time and noise afterwards.
            if (previous.Reason != ex.Message)
            {
                _log.Warning(ex, "AudioSinkBus: failed to open {Id}", sink.Id);
                SinkFailed?.Invoke(this, new SinkFailure(sink.Id, sink.DisplayName, ex.Message,
                    sink is AirPlay.AirPlayRaopSink { NeedsPassword: true }));
            }
        }
    }

    /// <summary>
    /// Raised when a sink can't be opened or its transport dies - carries a reason fit to
    /// show the user, because silence with no message is indistinguishable from a bug.
    /// </summary>
    public event EventHandler<SinkFailure>? SinkFailed;

    /// <summary>Lets a sink report an asynchronous failure (e.g. a network handshake that fails after Open).</summary>
    internal void ReportSinkFailure(string id, string displayName, string reason, bool needsPassword = false)
    {
        // A sink dropping out changes which output is the slowest, and the delay lines were
        // primed for the old answer. Without this the local speakers keep running two seconds
        // behind an AirPlay receiver that is no longer in the mix - silence for two seconds,
        // then permanently late audio, for a fault the user never sees explained.
        ResetDelays();
        Realign();

        SinkFailed?.Invoke(this, new SinkFailure(id, displayName, reason, needsPassword));
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
