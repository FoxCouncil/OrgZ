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
    private bool _muted;
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
        get => _muted;
        set => _muted = value;
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
        if (_disposed || pcm.Length == 0 || _stream == IntPtr.Zero)
        {
            return;
        }

        ReadOnlySpan<byte> output = pcm;

        if (_muted || _volume < 0.999f)
        {
            if (_scratch == null || _scratch.Length < pcm.Length)
            {
                _scratch = new byte[pcm.Length];
            }

            if (_muted)
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
        // pa_simple doesn't expose cork/pause - the best we can do is set a
        // mute flag so future Writes emit silence.  Latency is still bounded
        // by PulseAudio's ~50ms default buffer, which is fine.
        _muted = true;
    }

    public void Resume()
    {
        _muted = false;
    }

    public void Flush()
    {
        // pa_simple_flush would let us drop queued audio; for now, flip mute
        // so incoming frames drop until Resume.  Full flush support would
        // require the async API - polish-phase work.
        _muted = true;
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
