// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Runtime.InteropServices;
using Serilog;

namespace OrgZ.Services.AudioOutput.Linux;

/// <summary>
/// <see cref="IAudioSink"/> backed by PulseAudio's <c>pa_simple</c> API.
/// PulseAudio is the near-universal audio server on modern Linux desktops
/// (GNOME, KDE) and PipeWire exposes a PulseAudio-compatible socket, so
/// this one implementation covers both.  ALSA-only / JACK-only users fall
/// back to the "default" device when PulseAudio isn't installed.
/// </summary>
/// <remarks>
/// <para>
/// Per-sink volume is applied by scaling samples before <c>pa_simple_write</c>
/// - the simple API doesn't expose stream volume control, and going through
/// the full async API for volume alone isn't worth the complexity.
/// </para>
/// <para>
/// <c>pa_simple</c> writes are blocking - they return when PulseAudio has
/// accepted the data into its buffer.  This is fine on our audio worker
/// thread (LibVLC's audio callback) as long as we don't starve LibVLC, and
/// the buffer is sized for ~500ms so normal playback never sees a block.
/// </para>
/// </remarks>
internal sealed class PulseAudioSink : IAudioSink
{
    private static readonly ILogger _log = Logging.For("PulseAudioSink");

    private readonly object _lifecycle = new();
    private readonly string? _deviceName;
    private IntPtr _stream;
    private float _volume = 1f;
    // User-controlled mute (Settings dialog checkbox) - only the IsMuted
    // setter touches this. Stays sticky across track changes. Previously this
    // field was shared with the LibVLC Pause() soft-mute, which meant a missed
    // OnAudioResume callback would silently flip the user-facing mute on.
    private bool _userMuted;
    private bool _disposed;
    private byte[]? _scratch;

    public PulseAudioSink(string qualifiedId, string displayName, string? deviceName)
    {
        Id = qualifiedId;
        DisplayName = displayName;
        _deviceName = deviceName;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public AudioFormat? CurrentFormat { get; private set; }
    public bool IsOpen => _stream != IntPtr.Zero;

    public float Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0f, 1f);
    }

    public bool IsMuted
    {
        get => _userMuted;
        set => _userMuted = value;
    }

    public void Open(AudioFormat format)
    {
        lock (_lifecycle)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(PulseAudioSink));
            }
            if (_stream != IntPtr.Zero)
            {
                return;
            }

            var spec = new PulseNative.pa_sample_spec
            {
                format = PulseNative.PA_SAMPLE_S16LE,
                rate = (uint)format.SampleRate,
                channels = (byte)format.Channels,
            };

            int err;
            _stream = PulseNative.pa_simple_new(
                IntPtr.Zero,
                "OrgZ",
                PulseNative.PA_STREAM_PLAYBACK,
                _deviceName,
                "Music",
                ref spec,
                IntPtr.Zero,
                IntPtr.Zero,
                out err);

            if (_stream == IntPtr.Zero)
            {
                throw new InvalidOperationException($"pa_simple_new failed: pa error {err}");
            }

            CurrentFormat = format;
            _log.Information("PulseAudioSink opened: device={Device} {Rate}Hz {Channels}ch", _deviceName ?? "default", format.SampleRate, format.Channels);
        }
    }

    public void Write(ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length == 0)
        {
            return;
        }

        // Hold _lifecycle across the native call. VLC's audio thread races
        // with view-switch teardown (Close → pa_simple_free); without the lock
        // the worker reads _stream as non-zero, Close frees it, then pa_simple_write
        // hits the freed pa_stream and libpulse asserts (process abort).
        lock (_lifecycle)
        {
            if (_disposed || _stream == IntPtr.Zero)
            {
                return;
            }

            ReadOnlySpan<byte> output = pcm;
            bool silence = _userMuted;

            if (silence || _volume < 0.999f)
            {
                if (_scratch == null || _scratch.Length < pcm.Length)
                {
                    _scratch = new byte[pcm.Length];
                }

                if (silence)
                {
                    _scratch.AsSpan(0, pcm.Length).Clear();
                }
                else
                {
                    ScaleS16(pcm, _scratch.AsSpan(0, pcm.Length), _volume);
                }
                output = _scratch.AsSpan(0, pcm.Length);
            }

            unsafe
            {
                fixed (byte* p = output)
                {
                    PulseNative.pa_simple_write(_stream, (IntPtr)p, (UIntPtr)output.Length, out _);
                }
            }
        }
    }

    private static void ScaleS16(ReadOnlySpan<byte> source, Span<byte> dest, float gain)
    {
        var src = MemoryMarshal.Cast<byte, short>(source);
        var dst = MemoryMarshal.Cast<byte, short>(dest);
        for (int i = 0; i < src.Length; i++)
        {
            dst[i] = (short)Math.Clamp(src[i] * gain, short.MinValue, short.MaxValue);
        }
    }

    public void Pause()
    {
        // pa_simple doesn't expose cork/pause. Drop whatever is queued in the
        // server-side buffer so the listener doesn't hear the next ~50ms after
        // they hit pause. LibVLC also stops calling OnAudioPlay until Resume,
        // so flushing is sufficient - we don't need a "paused" flag that gates
        // Write (the old design did, and a missed Resume left audio stuck off).
        lock (_lifecycle)
        {
            if (_stream != IntPtr.Zero)
            {
                PulseNative.pa_simple_flush(_stream, out _);
            }
        }
    }

    public void Resume()
    {
        // No-op: Pause() flushes the queue, and LibVLC resumes calling
        // OnAudioPlay on its own. Kept on the interface for parity with other
        // sinks (Core Audio, WaveOut) where Resume does real work.
    }

    public void Flush()
    {
        // pa_simple_flush drops whatever audio is queued in the server buffer
        // so the listener doesn't hear the tail of the previous track after a
        // seek / source switch. Earlier this method latched _muted=true and
        // relied on Resume() to clear it - but seek/track-switch paths don't
        // call Resume(), so the "Default" sink could end up permanently silent
        // while a freshly-Open()ed sink (selected by hand) still worked.
        lock (_lifecycle)
        {
            if (_stream == IntPtr.Zero)
            {
                return;
            }

            if (PulseNative.pa_simple_flush(_stream, out var err) < 0)
            {
                _log.Warning("pa_simple_flush failed: pa error {Error}", err);
            }
        }
    }

    public void Drain()
    {
        // pa_simple_drain blocks until the server has played everything we
        // wrote - exactly the end-of-track semantic. Hold _lifecycle for the
        // same freed-stream race as Write.
        lock (_lifecycle)
        {
            if (_disposed || _stream == IntPtr.Zero)
            {
                return;
            }

            if (PulseNative.pa_simple_drain(_stream, out var err) < 0)
            {
                _log.Warning("pa_simple_drain failed: pa error {Error}", err);
            }
        }
    }

    public void Close()
    {
        lock (_lifecycle)
        {
            if (_stream != IntPtr.Zero)
            {
                PulseNative.pa_simple_free(_stream);
                _stream = IntPtr.Zero;
                CurrentFormat = null;
            }
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
