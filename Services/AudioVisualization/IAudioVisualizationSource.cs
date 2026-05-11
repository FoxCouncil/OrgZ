// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Services.AudioVisualization;

/// <summary>
/// Real-time audio data source consumed by visualizations (the mini-player's
/// LCD VU meter today; scripted / shader-based visualizers in the future).
/// Consumers pull the latest frame on their own clock - typically a UI render
/// timer at 25-60 fps.
/// </summary>
/// <remarks>
/// <para>
/// Data is populated from a LibVLC native worker thread as audio decodes.
/// Reads are thread-safe via a lock on the per-band snapshot - any staleness
/// between a "just populated" native frame and a UI read is bounded by a few
/// milliseconds and is invisible to the eye.
/// </para>
/// <para>
/// The <see cref="RawFrameAvailable"/> hook exists so future visualizer plugins
/// (Winamp AVS / MilkDrop-style shader hosts) can observe raw PCM samples for
/// beat detection / waveform rendering instead of the pre-bucketed spectrum.
/// Today nothing subscribes to it.
/// </para>
/// </remarks>
public interface IAudioVisualizationSource
{
    /// <summary>
    /// Number of frequency bands in <see cref="CopyBandLevels"/>.  Stable for
    /// the lifetime of the source.
    /// </summary>
    int BandCount { get; }

    /// <summary>
    /// Whether the source is currently receiving audio frames.  Consumers can
    /// use this to show a "waiting" placeholder before playback starts.
    /// </summary>
    bool IsActive { get; }

    /// <summary>
    /// Copies the most recent normalized band levels (0..1) into
    /// <paramref name="destination"/>.  If the destination is shorter than
    /// <see cref="BandCount"/>, only the first <c>destination.Length</c> bands
    /// are written.  Missing (no-audio-yet) data returns zeros.
    /// </summary>
    void CopyBandLevels(Span<float> destination);

    /// <summary>
    /// Copies separate left and right channel band levels for stereo
    /// visualizations.  For mono or unknown-channel sources, both spans get
    /// the same mixed data.  Both spans bounded by <see cref="BandCount"/>.
    /// </summary>
    void CopyBandLevelsStereo(Span<float> left, Span<float> right);

    /// <summary>
    /// Raw PCM frame observer for future script / shader visualizers.  Fires
    /// from a native audio thread - subscribers must not block and should
    /// treat the supplied span as read-only / valid only for the duration of
    /// the callback.
    /// </summary>
    event AudioFrameHandler? RawFrameAvailable;
}

/// <summary>
/// Callback signature for <see cref="IAudioVisualizationSource.RawFrameAvailable"/>.
/// Uses a custom delegate because <see cref="AudioFrame"/> is a ref struct and
/// cannot be a generic type argument to <see cref="Action{T}"/>.
/// </summary>
public delegate void AudioFrameHandler(AudioFrame frame);

/// <summary>
/// One chunk of decoded PCM samples as delivered by LibVLC's audio tap.
/// Samples are interleaved little-endian 32-bit floats at 44.1 kHz stereo
/// (the format requested at tap attach time).
/// </summary>
/// <remarks>
/// The <see cref="Samples"/> span's memory is only valid for the duration of
/// the <see cref="IAudioVisualizationSource.RawFrameAvailable"/> callback.
/// Copy if you need to retain.
/// </remarks>
public readonly ref struct AudioFrame
{
    public ReadOnlySpan<float> Samples { get; }
    public int SampleRate { get; }
    public int Channels { get; }
    public long PresentationTimeUs { get; }

    public AudioFrame(ReadOnlySpan<float> samples, int sampleRate, int channels, long pts)
    {
        Samples = samples;
        SampleRate = sampleRate;
        Channels = channels;
        PresentationTimeUs = pts;
    }
}
