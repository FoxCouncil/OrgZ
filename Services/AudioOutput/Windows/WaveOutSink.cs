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

    // 4 slots × ~25ms per LibVLC callback ≈ 100ms standing latency.  Smaller
    // is better for pause/seek responsiveness, but we need enough headroom
    // that occasional scheduler hiccups don't cause underruns.
    private const int RingSize = 4;

    private readonly uint _deviceId;
    private readonly object _lifecycle = new();
    private IntPtr _handle;
    private readonly GCHandle[] _bufferHandles = new GCHandle[RingSize];
    private readonly byte[][] _buffers = new byte[RingSize][];
    private readonly IntPtr[] _headerPtrs = new IntPtr[RingSize];
    private int _next;
    private float _volume = 1f;
    private bool _muted;
    private bool _disposed;

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
                wFormatTag = WaveNative.WAVE_FORMAT_PCM,
                nChannels = (ushort)format.Channels,
                nSamplesPerSec = (uint)format.SampleRate,
                wBitsPerSample = (ushort)format.BitsPerSample,
                nBlockAlign = (ushort)(format.Channels * format.BitsPerSample / 8),
                nAvgBytesPerSec = (uint)(format.SampleRate * format.Channels * format.BitsPerSample / 8),
                cbSize = 0,
            };

            var rc = WaveNative.waveOutOpen(out _handle, _deviceId, ref fmt, IntPtr.Zero, IntPtr.Zero, WaveNative.CALLBACK_NULL);
            if (rc != WaveNative.MMSYSERR_NOERROR)
            {
                _handle = IntPtr.Zero;
                throw new InvalidOperationException($"waveOutOpen(device={_deviceId}) failed: MMRESULT {rc}");
            }

            CurrentFormat = format;
            ApplyVolumeToDevice();
            _log.Information("WaveOutSink opened: device={Device} name={Name} {Rate}Hz {Channels}ch {Bits}bit", _deviceId, DisplayName, format.SampleRate, format.Channels, format.BitsPerSample);
        }
    }

    public void Write(ReadOnlySpan<byte> pcm)
    {
        if (_disposed || pcm.Length == 0 || _handle == IntPtr.Zero)
        {
            return;
        }

        var slot = _next;
        _next = (_next + 1) % RingSize;

        if (_headerPtrs[slot] != IntPtr.Zero)
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

        if (_buffers[slot] == null || _buffers[slot].Length < pcm.Length)
        {
            if (_bufferHandles[slot].IsAllocated)
            {
                _bufferHandles[slot].Free();
            }
            _buffers[slot] = new byte[pcm.Length];
            _bufferHandles[slot] = GCHandle.Alloc(_buffers[slot], GCHandleType.Pinned);
        }

        pcm.CopyTo(_buffers[slot]);

        var header = new WaveNative.WAVEHDR
        {
            lpData = _bufferHandles[slot].AddrOfPinnedObject(),
            dwBufferLength = (uint)pcm.Length,
        };

        var headerPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WaveNative.WAVEHDR>());
        Marshal.StructureToPtr(header, headerPtr, fDeleteOld: false);

        var prep = WaveNative.waveOutPrepareHeader(_handle, headerPtr, (uint)Marshal.SizeOf<WaveNative.WAVEHDR>());
        if (prep != WaveNative.MMSYSERR_NOERROR)
        {
            Marshal.FreeHGlobal(headerPtr);
            return;
        }

        var wr = WaveNative.waveOutWrite(_handle, headerPtr, (uint)Marshal.SizeOf<WaveNative.WAVEHDR>());
        if (wr != WaveNative.MMSYSERR_NOERROR)
        {
            WaveNative.waveOutUnprepareHeader(_handle, headerPtr, (uint)Marshal.SizeOf<WaveNative.WAVEHDR>());
            Marshal.FreeHGlobal(headerPtr);
            return;
        }

        _headerPtrs[slot] = headerPtr;
    }

    private bool IsSlotDone(int slot)
    {
        var ptr = _headerPtrs[slot];
        if (ptr == IntPtr.Zero)
        {
            return true;
        }
        var hdr = Marshal.PtrToStructure<WaveNative.WAVEHDR>(ptr);
        return (hdr.dwFlags & WaveNative.WHDR_DONE) != 0;
    }

    private void UnprepareSlot(int slot)
    {
        var ptr = _headerPtrs[slot];
        if (ptr == IntPtr.Zero)
        {
            return;
        }
        WaveNative.waveOutUnprepareHeader(_handle, ptr, (uint)Marshal.SizeOf<WaveNative.WAVEHDR>());
        Marshal.FreeHGlobal(ptr);
        _headerPtrs[slot] = IntPtr.Zero;
    }

    private void ApplyVolumeToDevice()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        // waveOutSetVolume packs left-right as two uint16 halves; we set
        // both to the same value for stereo.  When muted, send 0 to both.
        ushort amp = _muted ? (ushort)0 : (ushort)(_volume * 0xFFFF);
        uint stereo = (uint)amp | ((uint)amp << 16);
        WaveNative.waveOutSetVolume(_handle, stereo);
    }

    public void Pause()
    {
        if (_handle == IntPtr.Zero) return;
        WaveNative.waveOutPause(_handle);
    }

    public void Resume()
    {
        if (_handle == IntPtr.Zero) return;
        WaveNative.waveOutRestart(_handle);
    }

    public void Flush()
    {
        if (_handle == IntPtr.Zero) return;
        // waveOutReset marks every queued header as done, so the next Write
        // can reuse them without waiting.  The audio stops instantly at
        // the hardware level - this is what makes Pause / Seek feel crisp.
        WaveNative.waveOutReset(_handle);
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
                if (_bufferHandles[i].IsAllocated)
                {
                    _bufferHandles[i].Free();
                }
            }
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
