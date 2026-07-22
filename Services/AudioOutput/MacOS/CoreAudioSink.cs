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

    // 24 × 100ms = 2.4s of buffering - deep enough to absorb macOS audio
    // thread scheduling jitter (especially in unsigned dev builds where the
    // process doesn't get a real-time audio priority lease) without ever
    // starving the AudioQueue.
    private const int PoolSize = 24;

    private readonly string? _deviceUid;
    private readonly object _lifecycle = new();
    private readonly object _poolLock = new();
    private readonly Queue<IntPtr> _freeBuffers = new();
    private readonly List<IntPtr> _allBuffers = new(PoolSize);
    private readonly CoreAudioNative.AudioQueueOutputCallback _callback;

    private IntPtr _queue;
    private GCHandle _selfHandle;
    private float _volume = 1f;
    private bool _muted;
    private bool _disposed;
    private long _droppedBuffers;
    private long _lastDropLogTicks;

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
                        _allBuffers.Add(bufPtr);
                    }
                }
            }

            // Defer AudioQueueStart until enough real audio has been queued.
            // Starting the queue with zero or one buffer causes an audible
            // transient on macOS (hardware ramp / device-wake click). We let
            // Write() flip _started=true once we've buffered >=3 packets.
            CurrentFormat = format;
            _log.Information("CoreAudioSink opened: uid={Uid} {Rate}Hz {Channels}ch", _deviceUid ?? "default", format.SampleRate, format.Channels);
        }
    }

    private int _bufferedSinceStart;
    private bool _started;

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

        // Slice the incoming PCM into pool-buffer-sized chunks so an LVC
        // callback larger than our 100ms allocation no longer drops the tail.
        // Empirically LibVLC's S16 callback can deliver up to ~4096 frames
        // (≈93ms) but spikes higher on format transitions.
        int written = 0;
        while (written < pcm.Length)
        {
            IntPtr bufPtr;
            lock (_poolLock)
            {
                if (_freeBuffers.Count == 0)
                {
                    bufPtr = IntPtr.Zero;
                }
                else
                {
                    bufPtr = _freeBuffers.Dequeue();
                }
            }

            // Wait for the AudioQueue callback to return a buffer. SetAudioCallbacks
            // works on backpressure - the LibVLC decoder thread is *meant* to
            // block here while playback drains the pool. Dropping samples on
            // timeout caused audible speed wobble (skipped samples sped up,
            // late samples slowed down). 5 s ceiling keeps us from hanging
            // forever if the AudioQueue truly stopped responding.
            if (bufPtr == IntPtr.Zero)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (bufPtr == IntPtr.Zero && sw.ElapsedMilliseconds < 5000 && !_disposed)
                {
                    System.Threading.Thread.Sleep(2);
                    lock (_poolLock)
                    {
                        if (_freeBuffers.Count > 0)
                        {
                            bufPtr = _freeBuffers.Dequeue();
                        }
                    }
                }

                if (bufPtr == IntPtr.Zero)
                {
                    // AudioQueue stopped returning buffers for 5 s - something
                    // is genuinely wrong (queue stopped, hardware unplugged).
                    var dropped = System.Threading.Interlocked.Increment(ref _droppedBuffers);
                    var nowTicks = Environment.TickCount64;
                    var lastTicks = System.Threading.Interlocked.Read(ref _lastDropLogTicks);
                    if (nowTicks - lastTicks > 1000)
                    {
                        System.Threading.Interlocked.Exchange(ref _lastDropLogTicks, nowTicks);
                        _log.Warning("CoreAudioSink {Id}: AudioQueue pool wedged for 5 s, dropping {Bytes} byte(s) (total drops: {Total})", Id, pcm.Length - written, dropped);
                    }
                    return;
                }
            }

            var buf = Marshal.PtrToStructure<CoreAudioNative.AudioQueueBuffer>(bufPtr);
            int chunkLen = Math.Min(pcm.Length - written, (int)buf.mAudioDataBytesCapacity);
            var chunk = pcm.Slice(written, chunkLen);

            if (_muted)
            {
                new Span<byte>((void*)buf.mAudioData, chunkLen).Clear();
            }
            else if (_volume < 0.999f)
            {
                ScaleS16(chunk, new Span<byte>((void*)buf.mAudioData, chunkLen), _volume);
            }
            else
            {
                chunk.CopyTo(new Span<byte>((void*)buf.mAudioData, chunkLen));
            }

            // Patch the mAudioDataByteSize field in place on the native buffer.
            Marshal.WriteInt32(bufPtr, (int)Marshal.OffsetOf<CoreAudioNative.AudioQueueBuffer>(nameof(CoreAudioNative.AudioQueueBuffer.mAudioDataByteSize)), chunkLen);

            var rc = CoreAudioNative.AudioQueueEnqueueBuffer(_queue, bufPtr, 0, IntPtr.Zero);
            if (rc != 0)
            {
                // Enqueue failed - return the buffer to the pool so we don't
                // leak it permanently. Most common cause is the queue having
                // been Stop()'d underneath us; the next Open/Resume cycle
                // will get it going again.
                lock (_poolLock)
                {
                    _freeBuffers.Enqueue(bufPtr);
                }
                _log.Warning("CoreAudioSink {Id}: AudioQueueEnqueueBuffer failed: OSStatus {Rc}", Id, rc);
                return;
            }

            written += chunkLen;
            _bufferedSinceStart++;
        }

        // Delay AudioQueueStart until we've buffered enough audio that the
        // hardware never sees an immediate underrun. Three pool buffers ≈
        // 300ms - well past the device wake-up transient and any LibVLC
        // first-callback variance, but short enough that play feels instant.
        if (!_started && _bufferedSinceStart >= 3)
        {
            _started = true;
            CoreAudioNative.AudioQueueStart(_queue, IntPtr.Zero);
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
        // AudioQueuePause: instant pause without draining buffered audio.
        // AudioQueueStop(immediate=false) used to play out the entire ~1.2s
        // of pool buffering before honoring the pause - audibly laggy.
        CoreAudioNative.AudioQueuePause(_queue);
    }

    public void Resume()
    {
        if (_queue == IntPtr.Zero) return;
        CoreAudioNative.AudioQueueStart(_queue, IntPtr.Zero);
    }

    public void Flush()
    {
        if (_queue == IntPtr.Zero) return;
        // AudioQueueReset clears pending audio without stopping the queue;
        // playback can resume immediately on the next AudioQueueEnqueueBuffer.
        // Stop(immediate=true) followed by Start used to leave some buffers
        // stranded - neither in our free list nor being played - because the
        // OnBufferComplete callback isn't guaranteed to fire for them.
        CoreAudioNative.AudioQueueReset(_queue);
        lock (_poolLock)
        {
            _freeBuffers.Clear();
            foreach (var ptr in _allBuffers)
            {
                _freeBuffers.Enqueue(ptr);
            }
        }
        // After a flush we re-enter the buffer-up-before-Start state so the
        // next track's beginning also pre-rolls and doesn't pop.
        _bufferedSinceStart = 0;
        _started = false;
    }

    public void Drain()
    {
        if (_queue == IntPtr.Zero)
        {
            return;
        }

        // A short track can end before Write ever reached the 3-buffer
        // pre-roll threshold - start the queue now or the tail never plays.
        if (!_started && _bufferedSinceStart > 0)
        {
            _started = true;
            CoreAudioNative.AudioQueueStart(_queue, IntPtr.Zero);
        }

        // AudioQueueFlush pushes any partially-filled internal buffer toward
        // the hardware; then wait for OnBufferComplete to hand every pool
        // buffer back, which means all enqueued audio has actually played.
        CoreAudioNative.AudioQueueFlush(_queue);
        var deadline = Environment.TickCount64 + 5000;
        while (!_disposed)
        {
            lock (_poolLock)
            {
                if (_freeBuffers.Count >= _allBuffers.Count)
                {
                    return;
                }
            }
            if (Environment.TickCount64 > deadline)
            {
                _log.Warning("CoreAudioSink {Id}: drain timed out with audio still queued", Id);
                return;
            }
            System.Threading.Thread.Sleep(5);
        }
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
                _allBuffers.Clear();
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
