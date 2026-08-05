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

    // Lock discipline for the Write-vs-Close race (WaveOutSink and PulseAudioSink each fixed
    // their own version of it): every native AudioQueue call happens under _lifecycle with a
    // fresh _queue check, so Close can't dispose the queue between another thread's check and
    // its call. The pool wait happens outside _lifecycle - only the enqueue takes the lock -
    // so Volume/Pause from the UI thread don't stall behind the audio thread's backpressure
    // wait, which is the stall WaveOutSink's coarser locking has.
    // Order when both are needed: _lifecycle, then _poolLock - never the reverse.
    private readonly object _lifecycle = new();
    private readonly object _poolLock = new();
    private readonly Queue<IntPtr> _freeBuffers = new();
    private readonly List<IntPtr> _allBuffers = new(PoolSize);
    // AudioQueueBuffer's data pointer and capacity are fixed at allocation - read them once
    // there instead of reflect-marshaling the whole struct on every chunk of every Write.
    private readonly Dictionary<IntPtr, IntPtr> _bufferData = new(PoolSize);
    private int _bufferCapacity;
    private static readonly int ByteSizeOffset = (int)Marshal.OffsetOf<CoreAudioNative.AudioQueueBuffer>(nameof(CoreAudioNative.AudioQueueBuffer.mAudioDataByteSize));
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
                        var buf = Marshal.PtrToStructure<CoreAudioNative.AudioQueueBuffer>(bufPtr);
                        _bufferData[bufPtr] = buf.mAudioData;
                        _bufferCapacity = (int)buf.mAudioDataBytesCapacity;
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
            // Wait for the AudioQueue callback to return a buffer. SetAudioCallbacks works
            // on backpressure: the LibVLC decoder thread is supposed to block here while
            // playback drains the pool. Dropping samples on timeout caused audible speed
            // wobble. Monitor.Wait (pulsed by OnBufferComplete) replaces a 2ms poll that
            // burned ~500 lock acquisitions a second on the audio thread in steady state.
            // The 5 s ceiling stops us hanging forever if the AudioQueue stops responding.
            IntPtr bufPtr = IntPtr.Zero;
            lock (_poolLock)
            {
                var deadline = Environment.TickCount64 + 5000;
                while (_freeBuffers.Count == 0 && !_disposed && _queue != IntPtr.Zero && Environment.TickCount64 < deadline)
                {
                    System.Threading.Monitor.Wait(_poolLock, 250);
                }
                if (_freeBuffers.Count > 0)
                {
                    bufPtr = _freeBuffers.Dequeue();
                }
            }

            if (bufPtr == IntPtr.Zero)
            {
                if (_disposed || _queue == IntPtr.Zero)
                {
                    return;   // closed underneath us mid-wait - not a wedge, nothing to report
                }

                // No buffers back for 5 s: the queue stopped, or the hardware was unplugged.
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

            lock (_lifecycle)
            {
                // Close can win the race between our entry check and here: the queue and this
                // buffer are gone, so there's nothing left to enqueue into.
                if (_disposed || _queue == IntPtr.Zero)
                {
                    return;
                }

                var dataPtr = _bufferData[bufPtr];
                int chunkLen = Math.Min(pcm.Length - written, _bufferCapacity);
                var chunk = pcm.Slice(written, chunkLen);

                if (_muted)
                {
                    new Span<byte>((void*)dataPtr, chunkLen).Clear();
                }
                else if (_volume < 0.999f && CurrentFormat is { BitsPerSample: > 16 })
                {
                    PcmMath.ScaleS32(chunk, new Span<byte>((void*)dataPtr, chunkLen), _volume);
                }
                else if (_volume < 0.999f)
                {
                    PcmMath.ScaleS16(chunk, new Span<byte>((void*)dataPtr, chunkLen), _volume);
                }
                else
                {
                    chunk.CopyTo(new Span<byte>((void*)dataPtr, chunkLen));
                }

                // Patch the mAudioDataByteSize field in place on the native buffer.
                Marshal.WriteInt32(bufPtr, ByteSizeOffset, chunkLen);

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
                        System.Threading.Monitor.Pulse(_poolLock);
                    }
                    _log.Warning("CoreAudioSink {Id}: AudioQueueEnqueueBuffer failed: OSStatus {Rc}", Id, rc);
                    return;
                }

                written += chunkLen;
                _bufferedSinceStart++;
            }
        }

        // Delay AudioQueueStart until we've buffered enough audio that the
        // hardware never sees an immediate underrun. Three pool buffers ≈
        // 300ms - well past the device wake-up transient and any LibVLC
        // first-callback variance, but short enough that play feels instant.
        if (!_started && _bufferedSinceStart >= 3)
        {
            lock (_lifecycle)
            {
                if (!_disposed && _queue != IntPtr.Zero && !_started)
                {
                    _started = true;
                    CoreAudioNative.AudioQueueStart(_queue, IntPtr.Zero);
                }
            }
        }
    }


    private void ApplyVolume()
    {
        // Fast native call, so holding _lifecycle is safe from the UI thread - Write only
        // holds it per-enqueue, never across its backpressure wait.
        lock (_lifecycle)
        {
            if (_disposed || _queue == IntPtr.Zero)
            {
                return;
            }
            CoreAudioNative.AudioQueueSetParameter(_queue, CoreAudioNative.kAudioQueueParam_Volume, _muted ? 0f : _volume);
        }
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
                System.Threading.Monitor.Pulse(sink._poolLock);   // wake a Write waiting on backpressure
            }
        }
    }

    public void Pause()
    {
        lock (_lifecycle)
        {
            if (_disposed || _queue == IntPtr.Zero) return;
            // AudioQueuePause: instant pause without draining buffered audio.
            // AudioQueueStop(immediate=false) used to play out the entire ~1.2s
            // of pool buffering before honoring the pause - audibly laggy.
            CoreAudioNative.AudioQueuePause(_queue);
        }
    }

    public void Resume()
    {
        lock (_lifecycle)
        {
            if (_disposed || _queue == IntPtr.Zero) return;
            CoreAudioNative.AudioQueueStart(_queue, IntPtr.Zero);
        }
    }

    public void Flush()
    {
        lock (_lifecycle)
        {
            if (_disposed || _queue == IntPtr.Zero) return;
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
                System.Threading.Monitor.PulseAll(_poolLock);   // every waiter can proceed
            }
            // After a flush we re-enter the buffer-up-before-Start state so the
            // next track's beginning also pre-rolls and doesn't pop.
            _bufferedSinceStart = 0;
            _started = false;
        }
    }

    public void Drain()
    {
        lock (_lifecycle)
        {
            if (_disposed || _queue == IntPtr.Zero)
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
            // the hardware.
            CoreAudioNative.AudioQueueFlush(_queue);
        }

        // Then wait (outside the lock, so Pause/Volume stay responsive) for
        // OnBufferComplete to hand every pool buffer back, which means all
        // enqueued audio has actually played. The callback pulses on each return.
        var deadline = Environment.TickCount64 + 5000;
        lock (_poolLock)
        {
            while (!_disposed && _queue != IntPtr.Zero && _freeBuffers.Count < _allBuffers.Count)
            {
                if (Environment.TickCount64 > deadline)
                {
                    _log.Warning("CoreAudioSink {Id}: drain timed out with audio still queued", Id);
                    return;
                }
                System.Threading.Monitor.Wait(_poolLock, 100);
            }
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
                _bufferData.Clear();
                // Wake any Write blocked on backpressure so it can observe the queue is
                // gone and bail instead of sitting out its full 5 s deadline.
                System.Threading.Monitor.PulseAll(_poolLock);
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
