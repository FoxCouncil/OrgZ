// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Runtime.InteropServices;
using Serilog;

namespace OrgZ.Services.AudioOutput.MacOS;

/// <summary>
/// <see cref="IAudioSink"/> backed by macOS AudioToolbox's AudioQueue.  Each
/// sink owns one AudioQueue configured for signed-16-bit interleaved stereo
/// and optionally pinned to a specific device UID via
/// <c>kAudioQueueProperty_CurrentDevice</c>.
/// </summary>
/// <remarks>
/// <para>
/// Buffer model: AudioQueue requires us to allocate a pool of AudioQueueBuffers
/// and enqueue them as they come back through the callback.  We pre-allocate
/// <see cref="PoolSize"/> buffers; on each <see cref="Write"/> we copy the
/// incoming PCM into the next available one and enqueue.  A free-list
/// populated by the AudioQueue callback keeps us from enqueuing the same
/// buffer twice.
/// </para>
/// </remarks>
internal sealed class CoreAudioSink : IAudioSink
{
    private static readonly ILogger _log = Logging.For("CoreAudioSink");

    private const int PoolSize = 12;

    private readonly string? _deviceUid;
    private readonly object _lifecycle = new();
    private readonly object _poolLock = new();
    private readonly Queue<IntPtr> _freeBuffers = new();
    private readonly CoreAudioNative.AudioQueueOutputCallback _callback;

    private IntPtr _queue;
    private GCHandle _selfHandle;
    private float _volume = 1f;
    private bool _muted;
    private bool _disposed;

    public CoreAudioSink(string qualifiedId, string displayName, string? deviceUid)
    {
        Id = qualifiedId;
        DisplayName = displayName;
        _deviceUid = deviceUid;
        _callback = OnBufferComplete;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public AudioFormat? CurrentFormat { get; private set; }
    public bool IsOpen => _queue != IntPtr.Zero;

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            ApplyVolume();
        }
    }

    public bool IsMuted
    {
        get => _muted;
        set
        {
            _muted = value;
            ApplyVolume();
        }
    }

    public void Open(AudioFormat format)
    {
        lock (_lifecycle)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(CoreAudioSink));
            }
            if (_queue != IntPtr.Zero)
            {
                return;
            }

            var asbd = new CoreAudioNative.AudioStreamBasicDescription
            {
                mSampleRate = format.SampleRate,
                mFormatID = CoreAudioNative.kAudioFormatLinearPCM,
                mFormatFlags = CoreAudioNative.kLinearPCMFormatFlagIsSignedInteger | CoreAudioNative.kLinearPCMFormatFlagIsPacked,
                mBytesPerPacket = (uint)(format.BytesPerFrame),
                mFramesPerPacket = 1,
                mBytesPerFrame = (uint)(format.BytesPerFrame),
                mChannelsPerFrame = (uint)format.Channels,
                mBitsPerChannel = (uint)format.BitsPerSample,
                mReserved = 0,
            };

            _selfHandle = GCHandle.Alloc(this);
            var rc = CoreAudioNative.AudioQueueNewOutput(ref asbd, _callback, GCHandle.ToIntPtr(_selfHandle), IntPtr.Zero, IntPtr.Zero, 0, out _queue);
            if (rc != 0)
            {
                _selfHandle.Free();
                _queue = IntPtr.Zero;
                throw new InvalidOperationException($"AudioQueueNewOutput failed: OSStatus {rc}");
            }

            if (!string.IsNullOrEmpty(_deviceUid))
            {
                PinToDevice(_deviceUid);
            }

            ApplyVolume();

            // Pre-allocate the pool.
            int bufferBytes = format.SampleRate / 10 * format.BytesPerFrame; // ~100ms each
            lock (_poolLock)
            {
                for (int i = 0; i < PoolSize; i++)
                {
                    if (CoreAudioNative.AudioQueueAllocateBuffer(_queue, (uint)bufferBytes, out var bufPtr) == 0)
                    {
                        _freeBuffers.Enqueue(bufPtr);
                    }
                }
            }

            CoreAudioNative.AudioQueueStart(_queue, IntPtr.Zero);
            CurrentFormat = format;
            _log.Information("CoreAudioSink opened: uid={Uid} {Rate}Hz {Channels}ch", _deviceUid ?? "default", format.SampleRate, format.Channels);
        }
    }

    private unsafe void PinToDevice(string uid)
    {
        // kAudioQueueProperty_CurrentDevice takes a CFStringRef to the device
        // UID.  Create one, pass its pointer-sized value through AudioQueueSetProperty,
        // and release - AudioQueue copies the reference internally.
        var cf = CoreAudioNative.CFStringCreateWithCString(IntPtr.Zero, uid, CoreAudioNative.kCFStringEncodingUTF8);
        if (cf == IntPtr.Zero)
        {
            _log.Warning("CoreAudioSink: CFStringCreateWithCString failed for uid={Uid}; falling back to default device", uid);
            return;
        }

        try
        {
            var ptrHolder = Marshal.AllocHGlobal(IntPtr.Size);
            try
            {
                Marshal.WriteIntPtr(ptrHolder, cf);
                var rc = CoreAudioNative.AudioQueueSetProperty(
                    _queue,
                    CoreAudioNative.kAudioQueueProperty_CurrentDevice,
                    ptrHolder,
                    (uint)IntPtr.Size);

                if (rc != 0)
                {
                    _log.Warning("CoreAudioSink: AudioQueueSetProperty(CurrentDevice={Uid}) failed: OSStatus {Rc}", uid, rc);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ptrHolder);
            }
        }
        finally
        {
            CoreAudioNative.CFRelease(cf);
        }
    }

    public unsafe void Write(ReadOnlySpan<byte> pcm)
    {
        if (_disposed || pcm.Length == 0 || _queue == IntPtr.Zero)
        {
            return;
        }

        IntPtr bufPtr;
        lock (_poolLock)
        {
            if (_freeBuffers.Count == 0)
            {
                return; // drop frame rather than block
            }
            bufPtr = _freeBuffers.Dequeue();
        }

        var buf = Marshal.PtrToStructure<CoreAudioNative.AudioQueueBuffer>(bufPtr);
        if (buf.mAudioDataBytesCapacity < pcm.Length)
        {
            // Dropped: buffer too small for this packet.  Shouldn't happen
            // with our 100ms allocation, but guard anyway.
            lock (_poolLock)
            {
                _freeBuffers.Enqueue(bufPtr);
            }
            return;
        }

        if (_muted)
        {
            new Span<byte>((void*)buf.mAudioData, pcm.Length).Clear();
        }
        else if (_volume < 0.999f)
        {
            ScaleS16(pcm, new Span<byte>((void*)buf.mAudioData, pcm.Length), _volume);
        }
        else
        {
            pcm.CopyTo(new Span<byte>((void*)buf.mAudioData, pcm.Length));
        }

        // Patch the mAudioDataByteSize field in place on the native buffer.
        Marshal.WriteInt32(bufPtr, (int)Marshal.OffsetOf<CoreAudioNative.AudioQueueBuffer>(nameof(CoreAudioNative.AudioQueueBuffer.mAudioDataByteSize)), pcm.Length);

        CoreAudioNative.AudioQueueEnqueueBuffer(_queue, bufPtr, 0, IntPtr.Zero);
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

    private void ApplyVolume()
    {
        if (_queue == IntPtr.Zero)
        {
            return;
        }
        CoreAudioNative.AudioQueueSetParameter(_queue, CoreAudioNative.kAudioQueueParam_Volume, _muted ? 0f : _volume);
    }

    private static void OnBufferComplete(IntPtr userData, IntPtr audioQueue, IntPtr buffer)
    {
        if (userData == IntPtr.Zero)
        {
            return;
        }

        var handle = GCHandle.FromIntPtr(userData);
        if (handle.Target is CoreAudioSink sink)
        {
            lock (sink._poolLock)
            {
                sink._freeBuffers.Enqueue(buffer);
            }
        }
    }

    public void Pause()
    {
        if (_queue == IntPtr.Zero) return;
        CoreAudioNative.AudioQueueStop(_queue, false); // false = don't flush, just pause
    }

    public void Resume()
    {
        if (_queue == IntPtr.Zero) return;
        CoreAudioNative.AudioQueueStart(_queue, IntPtr.Zero);
    }

    public void Flush()
    {
        if (_queue == IntPtr.Zero) return;
        // AudioQueueStop(..., immediate=true) flushes all pending buffers;
        // Start again so we're ready for the next Write.
        CoreAudioNative.AudioQueueStop(_queue, true);
        CoreAudioNative.AudioQueueStart(_queue, IntPtr.Zero);
    }

    public void Close()
    {
        lock (_lifecycle)
        {
            if (_queue != IntPtr.Zero)
            {
                CoreAudioNative.AudioQueueStop(_queue, true);
                CoreAudioNative.AudioQueueDispose(_queue, true);
                _queue = IntPtr.Zero;
            }
            if (_selfHandle.IsAllocated)
            {
                _selfHandle.Free();
            }
            lock (_poolLock)
            {
                _freeBuffers.Clear();
            }
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
