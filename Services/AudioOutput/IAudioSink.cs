// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Services.AudioOutput;

/// <summary>
/// A single opened audio output.  Created by an <see cref="IAudioSinkProvider"/>
/// for a specific device; fed PCM via <see cref="Write"/> from the
/// <see cref="AudioSinkBus"/>.
/// </summary>
/// <remarks>
/// <para>
/// Sinks are not thread-safe by default — <see cref="Write"/> is called from
/// a single audio worker thread (LibVLC's audio callback), so concurrent
/// writes aren't a concern.  <see cref="Volume"/> and <see cref="IsMuted"/>
/// may be read/written from the UI thread; implementations should handle
/// that safely (typically via atomic field reads — the audio-thread write
/// loop sees the latest value on its next buffer).
/// </para>
/// <para>
/// Disposal is synchronous and closes the underlying resource (device handle,
/// network socket, etc.).  Calling <see cref="Write"/> after disposal is a
/// no-op.
/// </para>
/// </remarks>
public interface IAudioSink : IDisposable
{
    /// <summary>
    /// Stable identifier from <see cref="AudioDeviceInfo.QualifiedId"/>.
    /// </summary>
    string Id { get; }

    string DisplayName { get; }

    AudioFormat? CurrentFormat { get; }

    /// <summary>
    /// Per-sink volume, 0.0–1.0.  Applied either by the underlying API
    /// (e.g., <c>waveOutSetVolume</c>) or by scaling samples before write —
    /// either way the caller only sees a linear gain knob.
    /// </summary>
    float Volume { get; set; }

    bool IsMuted { get; set; }

    bool IsOpen { get; }

    /// <summary>
    /// Prepares the sink for the given stream format.  For devices with a
    /// fixed mixer format, the sink may insert format conversion internally.
    /// </summary>
    void Open(AudioFormat format);

    /// <summary>
    /// Queues PCM samples for playback in the format declared by the most
    /// recent <see cref="Open"/>.  Returns quickly — the underlying OS or
    /// network transport buffers async.
    /// </summary>
    void Write(ReadOnlySpan<byte> pcm);

    /// <summary>
    /// Releases the device/socket but keeps the sink reusable — subsequent
    /// <see cref="Open"/> can re-acquire.  Dispose closes for good.
    /// </summary>
    void Close();

    /// <summary>
    /// Pauses playback at the hardware level so any already-queued audio
    /// stops immediately.  Called from LibVLC's pause callback so the user's
    /// pause click is audible instantly instead of after the forwarder's
    /// buffer queue drains.
    /// </summary>
    void Pause();

    /// <summary>
    /// Resumes playback after <see cref="Pause"/>.  Picks up where pause
    /// left off without needing a re-open.
    /// </summary>
    void Resume();

    /// <summary>
    /// Discards all queued / pending audio at the hardware level.  Called on
    /// seek and on stop so the listener doesn't hear the last half-second of
    /// the previous position after jumping.
    /// </summary>
    void Flush();
}
