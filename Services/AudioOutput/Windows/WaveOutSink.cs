// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

#if WINDOWS
using System.Runtime.InteropServices;
using Serilog;

namespace OrgZ.Services.AudioOutput.Windows;

/// <summary>
/// <see cref="IAudioSink"/> backed by <c>waveOutWrite</c> on a specific
/// Win32 wave output device (selected by deviceId so we can play the same
/// PCM to multiple devices simultaneously via an <see cref="AudioSinkBus"/>).
/// </summary>
internal sealed class WaveOutSink : IAudioSink
{
    private static readonly ILogger _log = Logging.For("WaveOutSink");

    // 4 slots × ≥50ms per bus fan-out (AudioSinkBus.AggregateTargetMs) ≥ 200ms
    // standing latency.  Smaller is better for pause/seek responsiveness, but
    // we need enough headroom that occasional scheduler hiccups don't cause
    // underruns.  The bus-side aggregation is what makes the per-write size
    // trustworthy - raw LibVLC callbacks can be as small as ~6ms for hi-res
    // sources, which starved this ring and underran into audible garbage.
    private const int RingSize = 4;

    private readonly uint _deviceId;
    private readonly object _lifecycle = new();
    private IntPtr _handle;
    private readonly GCHandle[] _bufferHandles = new GCHandle[RingSize];
    private readonly byte[][] _buffers = new byte[RingSize][];

    // Native WAVEHDRs live as long as the ring, not as long as one write. They used to be
    // AllocHGlobal'd and freed per buffer (~20 allocs/second for the whole session);
    // now each slot's block is allocated on first use and released only in Close.
    private readonly IntPtr[] _headerPtrs = new IntPtr[RingSize];
    private readonly bool[] _slotInFlight = new bool[RingSize];
    private static readonly int HeaderSize = Marshal.SizeOf<WaveNative.WAVEHDR>();
    private static readonly int DwFlagsOffset = (int)Marshal.OffsetOf<WaveNative.WAVEHDR>(nameof(WaveNative.WAVEHDR.dwFlags));

    private int _next;
    private float _volume = 1f;
    private bool _muted;
    private bool _disposed;

    // Set by the UI thread, applied by whoever can take the lifecycle lock without
    // waiting - see ApplyVolumeToDevice.
    private volatile bool _volumeDirty;

    public WaveOutSink(uint deviceId, string displayName, string qualifiedId)
    {
        _deviceId = deviceId;
        DisplayName = displayName;
        Id = qualifiedId;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public AudioFormat? CurrentFormat { get; private set; }
    public bool IsOpen => _handle != IntPtr.Zero;

    // The size of the last buffer handed over, so latency can be reported in time rather
    // than in slots - the same four slots are 200ms of 50ms writes and 372ms of 93ms ones.
    private volatile int _lastWriteBytes;

    /// <summary>
    /// Write to audible: the ring, which sits full while anything is playing because Write
    /// blocks for a slot rather than growing.
    ///
    /// Small, but not nothing - and it is subtracted from the delay the bus applies to hold
    /// this output back for a slower one. Reporting zero here would leave the local speaker
    /// running a fifth of a second behind an AirPlay receiver we had otherwise lined up
    /// exactly.
    ///
    /// Before the first buffer arrives the size of a write can only be guessed at (the bus's
    /// aggregation target, which a 44.1kHz FLAC block overshoots by nearly half), so the bus
    /// treats a sink's first write as an alignment event and lines everything up again once
    /// this figure is measured rather than assumed.
    /// </summary>
    public TimeSpan OutputLatency
    {
        get
        {
            if (!IsOpen || CurrentFormat is not { } format || format.BytesPerSecond <= 0)
            {
                return TimeSpan.Zero;
            }

            var bytes = _lastWriteBytes > 0
                ? _lastWriteBytes
                : format.BytesPerSecond * AudioSinkBus.AggregateTargetMs / 1000;

            return TimeSpan.FromSeconds((double)bytes * RingSize / format.BytesPerSecond);
        }
    }

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            ApplyVolumeToDevice();
        }
    }

    public bool IsMuted
    {
        get => _muted;
        set
        {
            _muted = value;
            ApplyVolumeToDevice();
        }
    }

    public void Open(AudioFormat format)
    {
        lock (_lifecycle)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(WaveOutSink));
            }
            if (_handle != IntPtr.Zero)
            {
                return;
            }

            var fmt = new WaveNative.WAVEFORMATEX
            {
                wFormatTag = format.BitsPerSample > 16 ? WaveNative.WAVE_FORMAT_EXTENSIBLE : WaveNative.WAVE_FORMAT_PCM,
                nChannels = (ushort)format.Channels,
                nSamplesPerSec = (uint)format.SampleRate,
                wBitsPerSample = (ushort)format.BitsPerSample,
                nBlockAlign = (ushort)(format.Channels * format.BitsPerSample / 8),
                nAvgBytesPerSec = (uint)(format.SampleRate * format.Channels * format.BitsPerSample / 8),
                cbSize = 0,
            };

            uint rc;
            if (format.BitsPerSample > 16)
            {
                // Hi-res PCM (the bit-perfect 24-in-32 path) needs the
                // extensible form - plain PCM tags above 16 bits are rejected
                // by many drivers.
                fmt.cbSize = 22;
                var ext = new WaveNative.WAVEFORMATEXTENSIBLE
                {
                    Format = fmt,
                    wValidBitsPerSample = (ushort)format.BitsPerSample,
                    dwChannelMask = WaveNative.SPEAKER_FRONT_LEFT | WaveNative.SPEAKER_FRONT_RIGHT,
                    SubFormat = WaveNative.KSDATAFORMAT_SUBTYPE_PCM,
                };
                rc = WaveNative.waveOutOpen(out _handle, _deviceId, ref ext, IntPtr.Zero, IntPtr.Zero, WaveNative.CALLBACK_NULL);
            }
            else
            {
                rc = WaveNative.waveOutOpen(out _handle, _deviceId, ref fmt, IntPtr.Zero, IntPtr.Zero, WaveNative.CALLBACK_NULL);
            }
            if (rc != WaveNative.MMSYSERR_NOERROR)
            {
                _handle = IntPtr.Zero;
                throw new InvalidOperationException($"waveOutOpen(device={_deviceId}) failed: MMRESULT {rc}");
            }

            CurrentFormat = format;
            // Already under _lifecycle - apply directly rather than going through the
            // try-lock path (which would see the lock held and merely mark it pending).
            _volumeDirty = true;
            ApplyPendingVolumeLocked();
            _log.Information("WaveOutSink opened: device={Device} name={Name} {Rate}Hz {Channels}ch {Bits}bit", _deviceId, DisplayName, format.SampleRate, format.Channels, format.BitsPerSample);
        }
    }

    public void Write(ReadOnlySpan<byte> pcm)
    {
        // The lifecycle lock makes Write and Close mutually exclusive. Without it,
        // swapping output devices mid-playback disposed the sink under a Write in
        // flight: Close freed the pinned buffers and closed the handle between
        // Write's entry check and its waveOutWrite - a null-deref or a dead-handle
        // call on the audio callback thread, i.e. a process crash dressed as a
        // deadlock (Write's slot-wait spins against a device that stopped consuming
        // first, THEN the dispose lands). Found swapping sources on real hardware.
        lock (_lifecycle)
        {
            WriteLocked(pcm);
        }
    }

    private void WriteLocked(ReadOnlySpan<byte> pcm)
    {
        if (_disposed || pcm.Length == 0 || _handle == IntPtr.Zero)
        {
            return;
        }

        // A volume change queued by the UI thread rides in on the audio thread, which
        // already holds the lock - so the setter never has to wait for it.
        ApplyPendingVolumeLocked();

        var slot = _next;
        _next = (_next + 1) % RingSize;

        if (_slotInFlight[slot])
        {
            var deadline = Environment.TickCount64 + 1000;
            while (!IsSlotDone(slot))
            {
                if (Environment.TickCount64 > deadline)
                {
                    _log.Warning("WaveOutSink {Id} slot {Slot} never completed — skipping", Id, slot);
                    return;
                }
                System.Threading.Thread.SpinWait(200);
            }
            UnprepareSlot(slot);
        }

        // The IsAllocated check matters after a Close/Open cycle (native-rate
        // format change): Close frees the pinned handles, so a stale buffer
        // must be re-pinned or AddrOfPinnedObject throws on every write -
        // silent playback while the UI thinks the track is playing.
        if (_buffers[slot] == null || _buffers[slot].Length < pcm.Length || !_bufferHandles[slot].IsAllocated)
        {
            if (_bufferHandles[slot].IsAllocated)
            {
                _bufferHandles[slot].Free();
            }
            _buffers[slot] = new byte[pcm.Length];
            _bufferHandles[slot] = GCHandle.Alloc(_buffers[slot], GCHandleType.Pinned);
        }

        pcm.CopyTo(_buffers[slot]);
        _lastWriteBytes = pcm.Length;

        // The slot's header block is reused; only its two live fields are rewritten.
        var headerPtr = _headerPtrs[slot];
        if (headerPtr == IntPtr.Zero)
        {
            headerPtr = Marshal.AllocHGlobal(HeaderSize);
            _headerPtrs[slot] = headerPtr;
        }

        var header = new WaveNative.WAVEHDR
        {
            lpData = _bufferHandles[slot].AddrOfPinnedObject(),
            dwBufferLength = (uint)pcm.Length,
        };
        Marshal.StructureToPtr(header, headerPtr, fDeleteOld: false);

        var prep = WaveNative.waveOutPrepareHeader(_handle, headerPtr, (uint)HeaderSize);
        if (prep != WaveNative.MMSYSERR_NOERROR)
        {
            return;
        }

        var wr = WaveNative.waveOutWrite(_handle, headerPtr, (uint)HeaderSize);
        if (wr != WaveNative.MMSYSERR_NOERROR)
        {
            WaveNative.waveOutUnprepareHeader(_handle, headerPtr, (uint)HeaderSize);
            return;
        }

        _slotInFlight[slot] = true;
    }

    private bool IsSlotDone(int slot)
    {
        if (!_slotInFlight[slot] || _headerPtrs[slot] == IntPtr.Zero)
        {
            return true;
        }
        // Only dwFlags matters, and the spin-wait reads it thousands of times a second -
        // no reason to marshal the whole 8-field struct for one int.
        return (Marshal.ReadInt32(_headerPtrs[slot], DwFlagsOffset) & WaveNative.WHDR_DONE) != 0;
    }

    private void UnprepareSlot(int slot)
    {
        if (!_slotInFlight[slot] || _headerPtrs[slot] == IntPtr.Zero)
        {
            return;
        }
        WaveNative.waveOutUnprepareHeader(_handle, _headerPtrs[slot], (uint)HeaderSize);
        _slotInFlight[slot] = false;
    }

    /// <summary>
    /// Pushes a pending volume/mute to the device. Caller must hold <c>_lifecycle</c>.
    /// </summary>
    private void ApplyPendingVolumeLocked()
    {
        if (!_volumeDirty || _handle == IntPtr.Zero)
        {
            return;
        }
        _volumeDirty = false;

        // waveOutSetVolume packs left-right as two uint16 halves; we set
        // both to the same value for stereo.  When muted, send 0 to both.
        ushort amp = _muted ? (ushort)0 : (ushort)(_volume * 0xFFFF);
        uint stereo = (uint)amp | ((uint)amp << 16);
        WaveNative.waveOutSetVolume(_handle, stereo);
    }

    /// <summary>
    /// Never blocks the caller. The lifecycle lock is held by the audio thread across
    /// its slot-wait (up to 1 s) and by Drain (up to 2 s), so a volume drag - which
    /// fires this per slider tick from the UI thread - used to freeze the window for
    /// as long as end-of-track drain took. If the lock is free we apply immediately;
    /// otherwise the audio thread picks the change up on its next buffer (~50 ms).
    /// </summary>
    private void ApplyVolumeToDevice()
    {
        _volumeDirty = true;

        if (!System.Threading.Monitor.TryEnter(_lifecycle))
        {
            return;
        }

        try
        {
            ApplyPendingVolumeLocked();
        }
        finally
        {
            System.Threading.Monitor.Exit(_lifecycle);
        }
    }

    // Every handle-touching operation shares the lifecycle lock with Close: winmm
    // calls on a handle that a concurrent Close just freed are undefined behaviour.
    // The blocking ones (Drain up to 2 s, Write's slot-wait up to 1 s) bound how
    // long a Close can stall - a fair price for never crashing the audio thread.

    public void Pause()
    {
        lock (_lifecycle)
        {
            if (_handle == IntPtr.Zero) return;
            WaveNative.waveOutPause(_handle);
        }
    }

    public void Resume()
    {
        lock (_lifecycle)
        {
            if (_handle == IntPtr.Zero) return;
            WaveNative.waveOutRestart(_handle);
        }
    }

    public void Flush()
    {
        lock (_lifecycle)
        {
            if (_handle == IntPtr.Zero) return;
            // waveOutReset marks every queued header as done, so the next Write
            // can reuse them without waiting.  The audio stops instantly at
            // the hardware level - this is what makes Pause / Seek feel crisp.
            WaveNative.waveOutReset(_handle);
        }
    }

    public void Drain()
    {
        lock (_lifecycle)
        {
            if (_handle == IntPtr.Zero)
            {
                return;
            }

            // End-of-track: let every queued header finish playing instead of
            // waveOutReset-ing it away (which cut the last ~ring-depth of audio).
            // The ring holds well under a second; 2s is a generous stall ceiling.
            var deadline = Environment.TickCount64 + 2000;
            for (int i = 0; i < RingSize; i++)
            {
                while (!IsSlotDone(i))
                {
                    if (Environment.TickCount64 > deadline)
                    {
                        _log.Warning("WaveOutSink {Id}: drain timed out with audio still queued", Id);
                        return;
                    }
                    System.Threading.Thread.Sleep(5);
                }
            }
        }
    }

    public void Close()
    {
        lock (_lifecycle)
        {
            if (_handle == IntPtr.Zero)
            {
                return;
            }

            WaveNative.waveOutReset(_handle);
            for (int i = 0; i < RingSize; i++)
            {
                UnprepareSlot(i);
                // The header blocks outlive individual writes but not the open handle;
                // Close is the one place they're released.
                if (_headerPtrs[i] != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_headerPtrs[i]);
                    _headerPtrs[i] = IntPtr.Zero;
                }
                if (_bufferHandles[i].IsAllocated)
                {
                    _bufferHandles[i].Free();
                }
                _buffers[i] = null!;
            }
            _next = 0;

            // The measured write size belongs to the format that just closed. Carrying it into
            // the next Open makes OutputLatency report a 44.1 kHz buffer's worth of time for a
            // 192 kHz one, and the bus primes its delay lines off exactly that number.
            _lastWriteBytes = 0;
            WaveNative.waveOutClose(_handle);
            _handle = IntPtr.Zero;
            CurrentFormat = null;
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
#endif
