// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Services.AudioOutput;

/// <summary>
/// A sink whose device can drive playback back the other way - an AirPlay receiver whose
/// Home-app buttons, or a speaker's own controls, act on the app.
///
/// Separate from <see cref="IAudioSink"/> because it is genuinely rare: a sound card has no
/// opinion about what plays next. The bus surfaces this so the view-model subscribes once
/// instead of knowing which sink types can talk back.
/// </summary>
public interface IRemoteControllableSink
{
    /// <summary>The four-character command code the device sent ("play", "paus", "next"...).</summary>
    event EventHandler<string>? RemoteCommand;

    /// <summary>The device changed its own volume; carries a linear 0-1 level.</summary>
    event EventHandler<float>? RemoteVolume;
}

/// <summary>
/// A single opened audio output.  Created by an <see cref="IAudioSinkProvider"/>
/// for a specific device; fed PCM via <see cref="Write"/> from the
/// <see cref="AudioSinkBus"/>.
/// </summary>
/// <remarks>
/// <para>
/// Sinks are not thread-safe by default - <see cref="Write"/> is called from
/// a single audio worker thread (LibVLC's audio callback), so concurrent
/// writes aren't a concern.  <see cref="Volume"/> and <see cref="IsMuted"/>
/// may be read/written from the UI thread; implementations should handle
/// that safely (typically via atomic field reads - the audio-thread write
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
    /// Per-sink volume, 0.0-1.0.  Applied either by the underlying API
    /// (e.g., <c>waveOutSetVolume</c>) or by scaling samples before write -
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
    /// Absorbs a format change in place, returning false if the sink must be reopened.
    ///
    /// Reopening is cheap for a local device and expensive for a network one: an AirPlay
    /// receiver treats it as a whole new session, complete with pairing. Since the first
    /// buffer of a track can arrive at a different bit depth than the format the sink was
    /// opened with, a sink that converts internally should say so here rather than have a
    /// working session torn down mid-handshake.
    /// </summary>
    bool TryAdaptFormat(AudioFormat format) => false;

    /// <summary>
    /// Whether this sink is currently consuming audio in real time, and so acts as the
    /// playback clock.
    ///
    /// Distinct from <see cref="IsOpen"/> on purpose: a network sink can be open while its
    /// handshake is still running or has quietly failed, accepting buffers and discarding
    /// them. That paces nothing, and if the bus mistakes it for a clock the decoder free-runs
    /// and the track races through at many times speed.
    /// </summary>
    bool ProvidesClock => IsOpen;

    /// <summary>
    /// Whether this output wants to be opened the moment it is SELECTED, rather than waiting
    /// for audio.
    ///
    /// False for a sound card, which costs nothing to open late. True for a network receiver,
    /// where opening means pairing, a handshake, and filling a two-second buffer - about a
    /// second and a half of work that would otherwise happen after the user presses play, and
    /// be heard as the beginning of the song arriving late and ragged.
    ///
    /// Holding the session also means the speaker is CLAIMED, exactly as selecting a speaker
    /// in Music claims it, and that there is something for a volume change to reach while
    /// nothing is playing. The stream is kept alive with silence, and the receiver is told
    /// the state is paused - see AirPlay2Session - so a held session does not masquerade as
    /// playback.
    /// </summary>
    bool HoldsSessionWhenIdle => false;

    /// <summary>
    /// How long it takes a sample handed to <see cref="Write"/> to reach the listener.
    ///
    /// Zero - the default - means "as good as instant", which is what a sound card is. A
    /// network receiver is emphatically not: AirPlay hands the receiver a full two seconds of
    /// audio before the moment it is to be played, on purpose, so that a lossy Wi-Fi link
    /// still arrives in time. Nothing can shorten that, and nothing should try.
    ///
    /// The bus uses this to line the outputs up with each other: the same instant of music
    /// reaches every speaker at once because the fast ones are held back to match the slowest.
    /// Without it, ticking both laptop speakers and an AirPlay receiver plays the same song
    /// twice, three seconds apart, which is worse than either alone.
    ///
    /// A sink whose latency varies should report its STEADY-STATE figure and hold it: this
    /// number changing mid-track costs a resync, and being approximately right and stable
    /// beats being exactly right and jittery.
    /// </summary>
    TimeSpan OutputLatency => TimeSpan.Zero;

    /// <summary>
    /// How far into the track this output has actually REACHED THE LISTENER, or null when
    /// the sink has no better idea than the decoder does.
    ///
    /// A local device is close enough to instant that the decoder's position is the truth.
    /// A network receiver holds seconds of buffer, so the two are genuinely different
    /// numbers and only this one matches what is coming out of the speaker.
    /// </summary>
    TimeSpan? PlaybackPosition => null;

    /// <summary>
    /// Tells the sink what is playing, for outputs that show it.
    ///
    /// A local device has nothing to do with this, hence the no-op default. A network
    /// receiver does: an AirPlay speaker displays the title and cover, and needs the real
    /// duration to know when the stream ends - without it, it stops pulling audio partway
    /// through every track.
    /// </summary>
    void SetTrackInfo(string? title, string? artist, string? album, TimeSpan? duration, byte[]? artwork) { }

    /// <summary>
    /// Queues PCM samples for playback in the format declared by the most
    /// recent <see cref="Open"/>.  Returns quickly - the underlying OS or
    /// network transport buffers async.
    /// </summary>
    void Write(ReadOnlySpan<byte> pcm);

    /// <summary>
    /// Releases the device/socket but keeps the sink reusable - subsequent
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

    /// <summary>
    /// Plays out all queued audio before returning - the natural end-of-track
    /// counterpart to <see cref="Flush"/>.  Blocks (bounded) until the queue
    /// has reached the speakers so the last few hundred milliseconds of a
    /// track aren't discarded.  Called from LibVLC's drain callback, which
    /// expects exactly this blocking behavior.  The sink stays open and
    /// usable for the next track.
    /// </summary>
    void Drain();
}
