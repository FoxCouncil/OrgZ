// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Services.AudioOutput;

public enum AudioSampleEncoding
{
    PcmSigned,
    PcmUnsigned,
    IeeeFloat,
}

/// <summary>
/// Describes a PCM audio stream's format.  Used to negotiate what a sink can
/// accept at <see cref="IAudioSink.Open"/> time and what a provider receives
/// through <see cref="IAudioSink.Write"/>.
/// </summary>
public readonly record struct AudioFormat
{
    public required int SampleRate { get; init; }
    public required int Channels { get; init; }
    public required int BitsPerSample { get; init; }
    public required AudioSampleEncoding Encoding { get; init; }

    public int BytesPerFrame => Channels * BitsPerSample / 8;
    public int BytesPerSecond => SampleRate * BytesPerFrame;

    /// <summary>
    /// Canonical CD-DA format: 44.1 kHz stereo signed 16-bit - what LibVLC
    /// delivers to our audio tap and what every consumer platform handles
    /// natively without resampling.
    /// </summary>
    public static AudioFormat CdDaStereo16 => new()
    {
        SampleRate = 44100,
        Channels = 2,
        BitsPerSample = 16,
        Encoding = AudioSampleEncoding.PcmSigned,
    };
}
