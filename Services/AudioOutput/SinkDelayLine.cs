// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Services.AudioOutput;

/// <summary>
/// Holds one output back so it lines up with a slower one.
///
/// The bus writes the same instant of music to every sink at the same moment, which is only
/// right when they all play it at the same moment - and they don't. An AirPlay receiver is
/// handed audio a full two seconds before it plays a note, by design; a sound card plays it
/// almost at once. Ticking both means hearing the same song twice, seconds apart, from
/// different rooms. So the fast output is fed through this: a fixed-length FIFO that returns
/// what was written to it a set time ago, primed with silence so the delay exists from the
/// first sample rather than building up.
///
/// It rides ACROSS track boundaries on purpose. The end of a track is where the audio it
/// still holds belongs, and flushing there - which is what the drain callback would otherwise
/// invite - would drop a two-second hole into every gapless transition.
/// </summary>
internal sealed class SinkDelayLine
{
    private byte[] _ring = [];
    private int _head;
    private int _held;
    private int _delayBytes;
    private volatile bool _resetRequested;

    /// <summary>How far behind this line is currently running, in bytes of the bus format.</summary>
    public int DelayBytes => _delayBytes;

    /// <summary>
    /// Which alignment this line was primed for. The bus bumps its own counter when the set
    /// of outputs or the format changes; a line whose generation is behind re-primes once and
    /// is then left alone, however much the reported latencies wobble in between.
    /// </summary>
    public int Generation { get; set; } = -1;

    /// <summary>
    /// Sets the delay, keeping the music the line is carrying.
    ///
    /// What it holds is not spare silence, it is the next second or two of the song. Starting
    /// again from scratch would cost that sink twice over - the held audio never plays (a hole
    /// where the music should be) and a fresh delay's worth of silence takes its place (a
    /// second one) - so only the DIFFERENCE between the old alignment and the new one is made
    /// up here. A realign that lands on the same delay, which is most of them (a third quick
    /// output joining, a receiver reconnecting after a blip), then costs nothing at all.
    /// </summary>
    public void Configure(int delayBytes, int chunkBytes)
    {
        _delayBytes = Math.Max(0, delayBytes);
        Grow(_delayBytes + Math.Max(chunkBytes, 1));

        var delta = _delayBytes - _held;

        if (delta > 0)
        {
            // Further behind than before: everything held is still owed to the sink, so it
            // keeps its order and is led by just enough silence to make up the extra.
            var rebuilt = new byte[_ring.Length];
            for (var i = 0; i < _held; i++)
            {
                rebuilt[delta + i] = _ring[(_head + i) % _ring.Length];
            }

            _ring = rebuilt;
            _head = 0;
        }
        else if (delta < 0)
        {
            // Less far behind: the oldest bytes are the ones that have fallen into the past,
            // so those are the ones to drop. Priming silence here would push the output
            // further back, which is the opposite of what was asked for.
            _head = (_head - delta) % _ring.Length;
        }

        _held = _delayBytes;
    }

    /// <summary>
    /// Drops everything held and re-primes the silence, so the line is a full delay's worth
    /// of nothing again - what a seek asks for, where the audio it carries belongs to a
    /// moment the listener has just left.
    /// </summary>
    private void Clear()
    {
        Array.Clear(_ring);
        _head = 0;
        _held = _delayBytes;      // primed with silence, so the delay starts full length
        _resetRequested = false;
    }

    /// <summary>
    /// Asks for the contents to be dropped and the silence re-primed, at the next write.
    ///
    /// Deferred rather than immediate because seek and pause arrive on the UI thread while
    /// the audio thread is inside <see cref="Process"/>; clearing the ring underneath it
    /// would hand a sink a buffer half of one moment and half of another.
    /// </summary>
    public void RequestReset() => _resetRequested = true;

    /// <summary>
    /// Takes one buffer and returns what was written <see cref="DelayBytes"/> ago, filling
    /// <paramref name="output"/> with exactly as many bytes as came in.
    /// </summary>
    public void Process(ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (_resetRequested)
        {
            Clear();
        }

        if (_delayBytes <= 0)
        {
            input.CopyTo(output);
            return;
        }

        Grow(_delayBytes + input.Length);

        var tail = (_head + _held) % _ring.Length;
        var firstIn = Math.Min(input.Length, _ring.Length - tail);
        input[..firstIn].CopyTo(_ring.AsSpan(tail));
        if (firstIn < input.Length)
        {
            input[firstIn..].CopyTo(_ring.AsSpan(0));
        }
        _held += input.Length;

        var firstOut = Math.Min(input.Length, _ring.Length - _head);
        _ring.AsSpan(_head, firstOut).CopyTo(output);
        if (firstOut < input.Length)
        {
            _ring.AsSpan(0, input.Length - firstOut).CopyTo(output[firstOut..]);
        }

        _head = (_head + input.Length) % _ring.Length;
        _held -= input.Length;
    }

    /// <summary>Grows the ring, linearising what it holds - the wrap point moves with it.</summary>
    private void Grow(int capacity)
    {
        if (_ring.Length >= capacity)
        {
            return;
        }

        var grown = new byte[capacity];
        for (var i = 0; i < _held; i++)
        {
            grown[i] = _ring.Length == 0 ? (byte)0 : _ring[(_head + i) % _ring.Length];
        }

        _ring = grown;
        _head = 0;
    }
}
