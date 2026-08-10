// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Services.AudioOutput;

/// <summary>
/// Band-limited sample-rate conversion for sinks with a fixed rate - AirPlay/RAOP takes
/// 44.1 kHz only, and a hi-res library is mostly not that.
///
/// Windowed-sinc interpolation rather than linear: downsampling 96 kHz to 44.1 kHz has to
/// remove everything above 22.05 kHz first, or that content folds back down as audible
/// aliasing. Linear interpolation does no such filtering, so it turns a clean master into
/// a gritty one. The sinc kernel's cutoff tracks the ratio, so it band-limits and
/// interpolates in one pass.
///
/// Filter state carries across <see cref="Process"/> calls - the audio thread hands over
/// small buffers continuously, and resetting per buffer would click at every boundary.
/// </summary>
public sealed class AudioResampler
{
    /// <summary>Taps either side of the sample point. 32 is a good quality/CPU trade for real-time audio.</summary>
    private const int HalfTaps = 32;

    private readonly int _channels;
    private readonly double _ratio;      // output rate / input rate
    private readonly double _cutoff;     // normalized to the INPUT Nyquist
    private readonly float[] _window;    // precomputed Blackman window over the kernel span

    // Source frames kept between calls: the tail the next output positions still need.
    private float[] _history = [];
    private double _position;            // fractional read position, in source frames

    public AudioResampler(int sourceRate, int targetRate, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);

        _channels = channels;
        _ratio = (double)targetRate / sourceRate;

        // Downsampling: cut at the OUTPUT Nyquist so nothing folds back. Upsampling: the
        // source is already band-limited, so cut at its own Nyquist.
        _cutoff = Math.Min(1.0, _ratio);

        // Blackman window over the full kernel - tames the sinc truncation ripple that
        // would otherwise show up as pre/post-echo on transients.
        var span = (HalfTaps * 2) + 1;
        _window = new float[span];
        for (var i = 0; i < span; i++)
        {
            var x = (double)i / (span - 1);
            _window[i] = (float)(0.42 - (0.5 * Math.Cos(2 * Math.PI * x)) + (0.08 * Math.Cos(4 * Math.PI * x)));
        }
    }

    /// <summary>True when the rates match and conversion is a copy.</summary>
    public bool IsPassthrough => Math.Abs(_ratio - 1.0) < 1e-12;

    /// <summary>
    /// Resamples interleaved 16-bit LE PCM and appends the result to
    /// <paramref name="destination"/> as interleaved 16-bit LE PCM.
    /// </summary>
    public void Process(ReadOnlySpan<byte> input, List<byte> destination)
    {
        if (input.Length < 2)
        {
            return;
        }

        if (IsPassthrough)
        {
            destination.AddRange(input);
            return;
        }

        // Build one contiguous float buffer: retained history, then the new frames.
        var incomingSamples = input.Length / 2;
        var buffer = new float[_history.Length + incomingSamples];
        _history.CopyTo(buffer, 0);
        for (var i = 0; i < incomingSamples; i++)
        {
            buffer[_history.Length + i] = (short)(input[i * 2] | (input[(i * 2) + 1] << 8)) / 32768f;
        }

        var frames = buffer.Length / _channels;
        var step = 1.0 / _ratio;   // source frames consumed per output frame

        // Only emit while the whole kernel is inside the buffer; the rest waits for more input.
        while (_position + HalfTaps + 1 < frames)
        {
            var center = (int)Math.Floor(_position);
            var fraction = _position - center;

            for (var ch = 0; ch < _channels; ch++)
            {
                double sum = 0;
                for (var tap = -HalfTaps; tap <= HalfTaps; tap++)
                {
                    var index = center + tap;
                    if (index < 0 || index >= frames)
                    {
                        continue;
                    }

                    // Distance from the exact (fractional) read position to this source sample.
                    var distance = tap - fraction;
                    sum += buffer[(index * _channels) + ch] * Sinc(distance * _cutoff) * _window[tap + HalfTaps];
                }

                // The kernel's gain scales with its cutoff; normalize so level is preserved.
                var value = (int)Math.Round(sum * _cutoff * 32767.0);
                var clamped = (short)Math.Clamp(value, short.MinValue, short.MaxValue);
                destination.Add((byte)(clamped & 0xFF));
                destination.Add((byte)((clamped >> 8) & 0xFF));
            }

            _position += step;
        }

        // Retain everything the next output position still reads back into, and rebase.
        var keepFrom = Math.Max(0, (int)Math.Floor(_position) - HalfTaps);
        var keepFrames = frames - keepFrom;
        _history = new float[keepFrames * _channels];
        Array.Copy(buffer, keepFrom * _channels, _history, 0, _history.Length);
        _position -= keepFrom;
    }

    /// <summary>Drops retained state - call on seek so pre-jump audio can't bleed through.</summary>
    public void Reset()
    {
        _history = [];
        _position = 0;
    }

    private static double Sinc(double x)
    {
        if (Math.Abs(x) < 1e-9)
        {
            return 1.0;
        }

        var pix = Math.PI * x;
        return Math.Sin(pix) / pix;
    }
}
