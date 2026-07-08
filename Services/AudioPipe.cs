// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

namespace OrgZ.Services;

/// <summary>
/// Bounded in-process audio conduit between a <see cref="StreamSession"/> pump and LibVLC.
/// The pump writes clean audio chunks; VLC reads them through <see cref="PipeMediaInput"/>.
/// The bound (~1 MB ≈ a minute of 128 kbps) gives the pump natural backpressure: when VLC
/// stops consuming, writes block, and upstream slows via TCP flow control instead of the
/// session buffering the station forever.
/// </summary>
public sealed class AudioPipe
{
    private readonly BlockingCollection<byte[]> _chunks = new(boundedCapacity: 256);
    private long _totalWritten;

    public long TotalWritten => Interlocked.Read(ref _totalWritten);
    public bool IsCompleted => _chunks.IsAddingCompleted;

    /// <summary>Blocks when the pipe is full. Returns false once the pipe is completed or cancelled - the pump should stop.</summary>
    public bool Write(byte[] chunk, CancellationToken ct)
    {
        try
        {
            _chunks.Add(chunk, ct);
            Interlocked.Add(ref _totalWritten, chunk.Length);
            return true;
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>Marks end-of-stream; blocked and future reads drain the queue then report EOF.</summary>
    public void Complete()
    {
        try
        {
            _chunks.CompleteAdding();
        }
        catch (ObjectDisposedException)
        {
            // Raced a dispose - same outcome.
        }
    }

    /// <summary>Blocking read with timeout; false with a null chunk means "nothing yet", false with IsCompleted means EOF.</summary>
    public bool TryRead(out byte[]? chunk, int millisecondsTimeout)
    {
        try
        {
            return _chunks.TryTake(out chunk, millisecondsTimeout);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            chunk = null;
            return false;
        }
    }
}

/// <summary>
/// LibVLC <see cref="MediaInput"/> over an <see cref="AudioPipe"/> - the in-process
/// replacement for VLC's own network access. Live stream semantics: unknown size, no
/// seeking, reads block (in short polls, so a completed pipe unblocks promptly) until
/// audio arrives or the pipe reports EOF.
/// </summary>
public sealed class PipeMediaInput(AudioPipe pipe) : MediaInput
{
    private byte[]? _pending;
    private int _pendingOffset;

    public override bool Open(out ulong size)
    {
        size = ulong.MaxValue;
        CanSeek = false;
        return true;
    }

    public override int Read(IntPtr buf, uint len)
    {
        try
        {
            while (_pending == null)
            {
                if (pipe.TryRead(out _pending, millisecondsTimeout: 500))
                {
                    _pendingOffset = 0;
                    break;
                }
                if (pipe.IsCompleted)
                {
                    return 0; // upstream ended → EOF to VLC
                }
            }

            var count = Math.Min((int)len, _pending!.Length - _pendingOffset);
            Marshal.Copy(_pending, _pendingOffset, buf, count);
            _pendingOffset += count;
            if (_pendingOffset >= _pending.Length)
            {
                _pending = null;
            }
            return count;
        }
        catch
        {
            return -1;
        }
    }

    public override bool Seek(ulong offset) => false;

    public override void Close()
    {
        // The session owns upstream lifetime; VLC closing its end doesn't end the pull.
    }
}
