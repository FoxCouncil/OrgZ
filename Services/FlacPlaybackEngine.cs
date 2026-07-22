// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Diagnostics;
using OrgZ.Services.AudioOutput;
using OrgZ.Services.AudioVisualization;
using Serilog;

namespace OrgZ.Services;

/// <summary>
/// Bit-perfect playback engine for local FLAC files.  libvlc 3's amem output
/// only accepts 16-bit samples, so this path bypasses VLC entirely: the
/// reference <c>flac</c> decoder (already a rip-pipeline dependency, resolved
/// via <see cref="ExecutableResolver"/>) streams raw PCM at the file's native
/// bit depth and sample rate, the engine widens it losslessly to S32
/// (16-bit &lt;&lt; 16, 24-bit &lt;&lt; 8), and the <see cref="AudioSinkBus"/> fans it
/// out to the hardware at native rate.  At unity gain every source bit
/// reaches the OS audio stack untouched.
/// </summary>
/// <remarks>
/// <para>
/// <b>Seek</b> re-spawns the decoder with <c>--skip=&lt;sample&gt;</c> - flac
/// seeks are sample-accurate and process start is a few milliseconds, the
/// same strategy the CD rip path uses for its tooling.  <b>Pause</b> gates
/// the pump thread and pauses the sinks at the hardware level.  <b>End of
/// stream</b> drains the sinks (so the tail plays out) and raises
/// <see cref="EndReached"/>.
/// </para>
/// <para>
/// The engine feeds the shared <see cref="AudioTap"/> so the VU meter and
/// visualizers behave identically to the VLC path.  All events are raised on
/// pump/worker threads - callers marshal to the UI thread themselves, same
/// contract as LibVLC's events.
/// </para>
/// </remarks>
public sealed class FlacPlaybackEngine : IDisposable
{
    private static readonly ILogger _log = Logging.For("FlacPlaybackEngine");

    private readonly AudioSinkBus _bus;
    private readonly AudioTap _tap;
    private readonly object _lifecycle = new();
    private readonly ManualResetEventSlim _resumeGate = new(initialState: true);

    private Process? _decoder;
    private Thread? _pumpThread;
    private CancellationTokenSource? _pumpCts;
    private int _sessionId;

    private string? _filePath;
    private int _sampleRate;
    private int _channels;
    private int _bitsPerSample;
    private long _baseSample;
    private long _samplesDelivered;
    private volatile bool _isPlaying;
    private volatile bool _isPaused;

    public FlacPlaybackEngine(AudioSinkBus bus, AudioTap tap)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(tap);
        _bus = bus;
        _tap = tap;
    }

    /// <summary>True while a track is loaded and not paused (mirrors MediaPlayer.IsPlaying).</summary>
    public bool IsPlaying => _isPlaying && !_isPaused;

    /// <summary>True while a track is loaded, playing or paused.</summary>
    public bool IsActive => _isPlaying;

    public bool IsPaused => _isPaused;

    /// <summary>Current position in milliseconds (base seek offset + samples pumped).</summary>
    public long TimeMs => _sampleRate > 0 ? (_baseSample + Interlocked.Read(ref _samplesDelivered)) * 1000 / _sampleRate : 0;

    /// <summary>Raised roughly four times a second from the pump thread with the current position.</summary>
    public event Action<long>? TimeChanged;

    /// <summary>Raised after the decoder reaches end of stream and the sinks have drained.</summary>
    public event Action? EndReached;

    public event Action<string>? EncounteredError;

    /// <summary>
    /// True when this engine can own <paramref name="item"/>: a local FLAC
    /// file, mono or stereo, with a resolvable flac decoder.  Anything else
    /// (streams, lossy formats, multichannel) stays on the VLC path.
    /// </summary>
    public static bool CanPlay(MediaItem item)
    {
        return item.Kind is MediaKind.Music or MediaKind.Audiobook
            && item.Source == null
            && !string.IsNullOrEmpty(item.FilePath)
            && string.Equals(item.Extension, ".flac", StringComparison.OrdinalIgnoreCase)
            && item.AudioChannels is null or 1 or 2
            && ExecutableResolver.Find("flac") != null;
    }

    public void Play(string filePath, int sampleRate, int channels, int bitsPerSample, long startMs = 0)
    {
        lock (_lifecycle)
        {
            StopLocked(flushSinks: true);

            _filePath = filePath;
            _sampleRate = sampleRate > 0 ? sampleRate : 44100;
            _channels = channels is 1 or 2 ? channels : 2;
            _bitsPerSample = bitsPerSample is 16 or 24 or 32 ? bitsPerSample : 16;
            _baseSample = Math.Max(0, startMs) * _sampleRate / 1000;
            Interlocked.Exchange(ref _samplesDelivered, 0);
            _isPaused = false;
            _resumeGate.Set();

            var format = new AudioFormat
            {
                SampleRate = _sampleRate,
                Channels = 2,
                BitsPerSample = 32,
                Encoding = AudioSampleEncoding.PcmSigned,
            };
            _bus.SetFormat(format);
            _tap.ResetAudioStartTracking();
            _tap.BeginExternalSession(format);

            StartPumpLocked();
            _isPlaying = true;
        }
    }

    public void Pause()
    {
        lock (_lifecycle)
        {
            if (!_isPlaying || _isPaused)
            {
                return;
            }
            _isPaused = true;
            _resumeGate.Reset();
            _bus.PauseAll();
            _tap.SetExternalPaused(true);
        }
    }

    public void Resume()
    {
        lock (_lifecycle)
        {
            if (!_isPlaying || !_isPaused)
            {
                return;
            }
            _isPaused = false;
            _tap.SetExternalPaused(false);
            _bus.ResumeAll();
            _resumeGate.Set();
        }
    }

    /// <summary>Sample-accurate seek: re-spawns the decoder at the target sample.</summary>
    public void SeekMs(long ms)
    {
        lock (_lifecycle)
        {
            if (!_isPlaying || _filePath == null)
            {
                return;
            }

            KillPumpLocked();
            _bus.FlushAll();
            _baseSample = Math.Max(0, ms) * _sampleRate / 1000;
            Interlocked.Exchange(ref _samplesDelivered, 0);
            StartPumpLocked();

            if (_isPaused)
            {
                // Stay paused at the new position - the pump fills the sink
                // queue and blocks; audio resumes on Resume().
                _resumeGate.Reset();
                _bus.PauseAll();
            }
        }
    }

    public void Stop()
    {
        lock (_lifecycle)
        {
            StopLocked(flushSinks: true);
        }
    }

    private void StopLocked(bool flushSinks)
    {
        KillPumpLocked();
        if (flushSinks)
        {
            _bus.FlushAll();
        }
        if (_isPlaying)
        {
            _tap.EndExternalSession();
        }
        _isPlaying = false;
        _isPaused = false;
        _resumeGate.Set();
    }

    private void StartPumpLocked()
    {
        var flacExe = ExecutableResolver.Find("flac") ?? throw new InvalidOperationException("flac decoder not found on PATH or in bundled tools");

        var psi = new ProcessStartInfo
        {
            FileName = flacExe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("-s");
        psi.ArgumentList.Add("--force-raw-format");
        psi.ArgumentList.Add("--endian=little");
        psi.ArgumentList.Add("--sign=signed");
        if (_baseSample > 0)
        {
            psi.ArgumentList.Add($"--skip={_baseSample}");
        }
        psi.ArgumentList.Add(_filePath!);

        _decoder = Process.Start(psi) ?? throw new InvalidOperationException("failed to start flac decoder");
        _decoder.ErrorDataReceived += (_, _) => { };
        _decoder.BeginErrorReadLine();

        _pumpCts = new CancellationTokenSource();
        var ct = _pumpCts.Token;
        var proc = _decoder;
        var session = ++_sessionId;

        _pumpThread = new Thread(() => Pump(proc, session, ct))
        {
            IsBackground = true,
            Name = "FlacEnginePump",
            Priority = ThreadPriority.AboveNormal,
        };
        _pumpThread.Start();

        _log.Information("FlacPlaybackEngine pump started: {File} @ {Rate} Hz {Bits}-bit ch={Ch} skip={Skip}", Path.GetFileName(_filePath), _sampleRate, _bitsPerSample, _channels, _baseSample);
    }

    private void KillPumpLocked()
    {
        _pumpCts?.Cancel();
        var proc = _decoder;
        _decoder = null;
        if (proc != null)
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill();
                }
                proc.Dispose();
            }
            catch
            {
                // Already gone - fine.
            }
        }
        // The pump exits promptly once the process dies and the token is
        // cancelled; don't join from lock scope (the pump takes _lifecycle
        // for its end-of-stream transition).
        _pumpThread = null;
        _pumpCts = null;
    }

    private void Pump(Process proc, int session, CancellationToken ct)
    {
        try
        {
            var stdout = proc.StandardOutput.BaseStream;
            int srcBytesPerSample = _bitsPerSample / 8;
            int srcFrameBytes = srcBytesPerSample * _channels;

            // ~50ms of source audio per read, frame-aligned.
            int readBytes = Math.Max(srcFrameBytes, _sampleRate / 20 * srcFrameBytes);
            var raw = new byte[readBytes];
            var wide = new byte[readBytes / srcFrameBytes * 8];

            int leftover = 0;
            long lastTimeRaise = 0;

            while (!ct.IsCancellationRequested)
            {
                _resumeGate.Wait(ct);

                int read = stdout.Read(raw, leftover, raw.Length - leftover);
                if (read <= 0)
                {
                    break;
                }

                int available = leftover + read;
                int frames = available / srcFrameBytes;
                int consumed = frames * srcFrameBytes;
                if (frames > 0)
                {
                    int wideBytes = WidenToS32Stereo(raw.AsSpan(0, consumed), wide, _bitsPerSample, _channels);
                    var chunk = wide.AsSpan(0, wideBytes);

                    _bus.Write(chunk);
                    _tap.OnExternalAudio(chunk, TimeMs * 1000);
                    Interlocked.Add(ref _samplesDelivered, frames);

                    var now = Environment.TickCount64;
                    if (now - lastTimeRaise >= 250)
                    {
                        lastTimeRaise = now;
                        TimeChanged?.Invoke(TimeMs);
                    }
                }

                leftover = available - consumed;
                if (leftover > 0)
                {
                    Array.Copy(raw, consumed, raw, 0, leftover);
                }
            }

            if (!ct.IsCancellationRequested)
            {
                // Natural end of stream: play the queued tail out, then tell
                // the owner - mirrors the VLC drain → EndReached sequence.
                _bus.DrainAll();
                lock (_lifecycle)
                {
                    if (session == _sessionId)
                    {
                        _isPlaying = false;
                        _isPaused = false;
                        _tap.EndExternalSession();
                    }
                }
                if (session == _sessionId)
                {
                    TimeChanged?.Invoke(TimeMs);
                    EndReached?.Invoke();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Seek / stop - expected.
        }
        catch (Exception ex)
        {
            _log.Error(ex, "FlacPlaybackEngine pump failed");
            if (session == _sessionId)
            {
                EncounteredError?.Invoke(ex.Message);
            }
        }
    }

    /// <summary>
    /// Losslessly widens raw little-endian signed PCM to interleaved-stereo
    /// S32: 16-bit &lt;&lt; 16, 24-bit &lt;&lt; 8, 32-bit verbatim; mono duplicates to
    /// both channels.  Returns the number of output bytes.  Internal-static
    /// for unit tests - this is the exactness-critical code.
    /// </summary>
    internal static int WidenToS32Stereo(ReadOnlySpan<byte> raw, Span<byte> dest, int bitsPerSample, int channels)
    {
        int srcBytes = bitsPerSample / 8;
        int frames = raw.Length / (srcBytes * channels);
        var dst = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(dest);

        int di = 0;
        int si = 0;
        for (int f = 0; f < frames; f++)
        {
            for (int c = 0; c < channels; c++)
            {
                int sample = bitsPerSample switch
                {
                    16 => (short)(raw[si] | (raw[si + 1] << 8)) << 16,
                    24 => ((raw[si] << 8) | (raw[si + 1] << 16) | (raw[si + 2] << 24)),
                    _ => raw[si] | (raw[si + 1] << 8) | (raw[si + 2] << 16) | (raw[si + 3] << 24),
                };
                si += srcBytes;

                dst[di++] = sample;
                if (channels == 1)
                {
                    dst[di++] = sample;
                }
            }
        }

        return di * sizeof(int);
    }

    public void Dispose()
    {
        lock (_lifecycle)
        {
            StopLocked(flushSinks: false);
        }
        _resumeGate.Dispose();
    }
}
