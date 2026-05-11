// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Serilog;

namespace OrgZ.Services.AudioOutput;

/// <summary>
/// Composite audio output — holds a collection of active <see cref="IAudioSink"/>s
/// and fans every <see cref="Write"/> out to all of them simultaneously.
/// That's what lets the user tick multiple output devices in Settings and
/// hear music on all of them at once (laptop speakers + desk speakers +
/// AirPlay receiver in the kitchen, for example).
/// </summary>
/// <remarks>
/// <para>
/// The bus owns its sinks — removing a sink calls <see cref="IDisposable.Dispose"/>
/// on it.  The set of active sinks is mutated from the UI thread
/// (Settings dialog) while <see cref="Write"/> runs on LibVLC's audio worker
/// thread, so sink list access is guarded by a lock; the lock is only held
/// across reference copies (quick) and never while actually writing PCM.
/// </para>
/// <para>
/// Per-sink volume is applied by each sink in its own <see cref="IAudioSink.Write"/>;
/// master volume lives on the bus and is applied via sample scaling here
/// before fan-out — that way even sinks without native gain support honor it.
/// </para>
/// </remarks>
public sealed class AudioSinkBus : IDisposable
{
    private static readonly ILogger _log = Logging.For("AudioSinkBus");

    private readonly object _lock = new();
    private readonly List<IAudioSink> _sinks = [];
    private AudioFormat? _format;
    private float _masterVolume = 1f;
    private bool _disposed;

    /// <summary>
    /// Master volume applied to all sinks in [0, 1].  Values above 1 are
    /// clamped — no amplification to avoid clipping.
    /// </summary>
    public float MasterVolume
    {
        get => _masterVolume;
        set => _masterVolume = Math.Clamp(value, 0f, 1f);
    }

    public IReadOnlyList<IAudioSink> Sinks
    {
        get { lock (_lock) { return [.. _sinks]; } }
    }

    public AudioFormat? Format => _format;

    public void SetFormat(AudioFormat format)
    {
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
    /// Flushes every active sink's queued audio — called on seek / track
    /// change so the listener doesn't hear the tail of the previous position.
    /// </summary>
    public void FlushAll() => ForEachSink(s => s.Flush());

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
    /// Fans <paramref name="pcm"/> out to every active sink.  Returns quickly
    /// — sinks queue the buffer internally for their own playback thread.
    /// A master-volume scaling pass is applied into a scratch buffer when
    /// <see cref="MasterVolume"/> &lt; 1 so sinks see the attenuated bytes.
    /// </summary>
    public void Write(ReadOnlySpan<byte> pcm)
    {
        if (_disposed || pcm.Length == 0)
        {
            return;
        }

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

        // Apply master volume if needed.  Scaling is done on a per-call
        // scratch array so sinks receive the attenuated data and can still
        // apply their own per-sink volumes on top.
        ReadOnlySpan<byte> buffer = pcm;
        byte[]? scratch = null;
        if (_masterVolume < 0.999f && _format is { BitsPerSample: 16, Encoding: AudioSampleEncoding.PcmSigned })
        {
            scratch = System.Buffers.ArrayPool<byte>.Shared.Rent(pcm.Length);
            ScaleS16(pcm, scratch.AsSpan(0, pcm.Length), _masterVolume);
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
