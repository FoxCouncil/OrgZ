// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Services.AudioOutput;

namespace OrgZ.Tests;

/// <summary>
/// Lining the outputs up with each other.
///
/// An AirPlay receiver plays what it is sent two seconds later; a sound card plays it now.
/// Ticking both used to mean the same song from two rooms, seconds apart - the kind of thing
/// that is obvious in a hallway and invisible in a log. These tests are byte-exact about the
/// delay because "roughly aligned" is what it already was.
///
/// The sinks here are in-test fakes, so they only pin the BUS's arithmetic. That the shipped
/// AirPlay sink actually reports a latency and a position for the bus to work with is pinned
/// against the software receiver in <see cref="AirPlaySinkPathTests"/> - without that, all of
/// this passes while the feature is inert for the one output it exists for.
/// </summary>
public class AudioSinkAlignmentTests
{
    /// <summary>One 50ms fan-out at CD rate - the size the bus aggregates to.</summary>
    private const int Chunk = 8820;

    private static AudioFormat Cd => AudioFormat.CdDaStereo16;

    private sealed class LatentSink : IAudioSink
    {
        private readonly List<byte> _received = [];

        public LatentSink(string id, TimeSpan latency)
        {
            Id = id;
            DisplayName = id;
            Latency = latency;
        }

        public TimeSpan Latency { get; set; }

        /// <summary>What this output would say the listener is hearing, if asked.</summary>
        public TimeSpan? Position { get; init; }

        public TimeSpan? PlaybackPosition => Position;
        public string Id { get; }
        public string DisplayName { get; }
        public AudioFormat? CurrentFormat { get; private set; }
        public float Volume { get; set; } = 1f;
        public bool IsMuted { get; set; }
        public bool IsOpen => CurrentFormat.HasValue;
        public TimeSpan OutputLatency => IsOpen ? Latency : TimeSpan.Zero;

        /// <summary>Everything this sink was handed, end to end.</summary>
        public byte[] Received => [.. _received];

        /// <summary>What a receiver that refuses to pair looks like from the bus's side.</summary>
        public bool RefuseToOpen { get; set; }

        public void Open(AudioFormat format)
        {
            if (RefuseToOpen)
            {
                throw new InvalidOperationException("this receiver refused the connection");
            }

            CurrentFormat = format;
        }
        public void Write(ReadOnlySpan<byte> pcm) => _received.AddRange(pcm);
        public void Close() => CurrentFormat = null;
        public void Pause() { }
        public void Resume() { }
        public void Flush() { }
        public void Drain() { }
        public void Dispose() => Close();
    }

    /// <summary>A distinct byte per chunk, so a misalignment names itself.</summary>
    private static byte[] Chunkful(byte value) => Enumerable.Repeat(value, Chunk).ToArray();

    private static void WriteChunks(AudioSinkBus bus, params byte[] values)
    {
        foreach (var value in values)
        {
            bus.Write(Chunkful(value));
        }
    }

    [Fact]
    public void A_quick_output_is_held_back_to_meet_a_slow_one()
    {
        using var bus = new AudioSinkBus();
        var local = new LatentSink("local", TimeSpan.Zero);
        var remote = new LatentSink("remote", TimeSpan.FromMilliseconds(100));

        bus.Add(local);
        bus.Add(remote);
        bus.SetFormat(Cd);

        // 100ms at CD rate is exactly two of these chunks.
        WriteChunks(bus, 1, 2, 3, 4);

        // The slow output gets everything as it arrives - it is the one being waited for.
        Assert.Equal([.. Chunkful(1), .. Chunkful(2), .. Chunkful(3), .. Chunkful(4)], remote.Received);

        // The quick one gets two chunks of silence first, and is then exactly two behind.
        Assert.Equal([.. new byte[Chunk * 2], .. Chunkful(1), .. Chunkful(2)], local.Received);
    }

    [Fact]
    public void An_output_on_its_own_is_never_delayed()
    {
        using var bus = new AudioSinkBus();
        var remote = new LatentSink("remote", TimeSpan.FromSeconds(2));

        bus.Add(remote);
        bus.SetFormat(Cd);
        WriteChunks(bus, 7, 8);

        // Nothing to line up against: delaying here would be two seconds of silence at the
        // start of every track for no reason at all.
        Assert.Equal([.. Chunkful(7), .. Chunkful(8)], remote.Received);
    }

    [Fact]
    public void Outputs_that_are_equally_quick_are_left_alone()
    {
        using var bus = new AudioSinkBus();
        var one = new LatentSink("one", TimeSpan.FromMilliseconds(80));
        var two = new LatentSink("two", TimeSpan.FromMilliseconds(80));

        bus.Add(one);
        bus.Add(two);
        bus.SetFormat(Cd);
        WriteChunks(bus, 5);

        Assert.Equal(Chunkful(5), one.Received);
        Assert.Equal(Chunkful(5), two.Received);
    }

    [Fact]
    public void Even_a_small_difference_is_corrected_because_it_only_costs_one_prime()
    {
        using var bus = new AudioSinkBus();
        var one = new LatentSink("one", TimeSpan.Zero);
        var two = new LatentSink("two", TimeSpan.FromMilliseconds(10));

        bus.Add(one);
        bus.Add(two);
        bus.SetFormat(Cd);
        WriteChunks(bus, 5, 6);

        // The alignment is worked out once, when the set of outputs changes, so applying it
        // costs one prime whatever its size - there is no reason to ignore a small one. (When
        // the line was re-primed on every drift instead, small corrections were the dangerous
        // ones: cheap to trigger, and each costing a delay's worth of silence.)
        const int TenMs = 44100 / 100 * 4;

        Assert.Equal(Chunk * 2, one.Received.Length);
        Assert.All(one.Received[..TenMs], b => Assert.Equal(0, b));
        Assert.Equal(5, one.Received[TenMs]);
    }

    [Fact]
    public void Seeking_drops_what_the_delay_line_was_holding()
    {
        using var bus = new AudioSinkBus();
        var local = new LatentSink("local", TimeSpan.Zero);
        var remote = new LatentSink("remote", TimeSpan.FromMilliseconds(100));

        bus.Add(local);
        bus.Add(remote);
        bus.SetFormat(Cd);

        WriteChunks(bus, 1, 2, 3);        // local is now holding 1 and 2
        bus.FlushAll();
        WriteChunks(bus, 9, 9, 9);

        // Chunk 1 reached it before the seek; 2 and 3 were still in the line and belong to a
        // moment the listener has left, so they are dropped rather than played after the jump.
        Assert.Equal([.. new byte[Chunk * 2], .. Chunkful(1), .. new byte[Chunk * 2], .. Chunkful(9)], local.Received);
    }

    [Fact]
    public void A_track_change_does_not_drop_the_tail_still_on_its_way()
    {
        using var bus = new AudioSinkBus();
        var local = new LatentSink("local", TimeSpan.Zero);
        var remote = new LatentSink("remote", TimeSpan.FromMilliseconds(100));

        bus.Add(local);
        bus.Add(remote);
        bus.SetFormat(Cd);

        WriteChunks(bus, 1, 2, 3);
        bus.DrainAll();                   // end of track - fires between every pair of songs
        WriteChunks(bus, 4);

        // The end of the last song is still travelling to this speaker. Flushing it here
        // would punch a two-second hole into every gapless transition.
        Assert.Equal([.. new byte[Chunk * 2], .. Chunkful(1), .. Chunkful(2)], local.Received);
    }

    [Fact]
    public void Losing_the_slow_output_puts_the_other_back_on_real_time()
    {
        using var bus = new AudioSinkBus();
        var local = new LatentSink("local", TimeSpan.Zero);
        var remote = new LatentSink("remote", TimeSpan.FromMilliseconds(100));

        bus.Add(local);
        bus.Add(remote);
        bus.SetFormat(Cd);

        WriteChunks(bus, 1, 2);
        bus.Remove(remote.Id);
        WriteChunks(bus, 3, 4);

        // Two chunks of priming silence, then nothing further held back once the receiver
        // it was waiting for has gone.
        Assert.Equal([.. new byte[Chunk * 2], .. Chunkful(3), .. Chunkful(4)], local.Received);
    }

    [Fact]
    public void A_receiver_that_never_opened_holds_nothing_back()
    {
        using var bus = new AudioSinkBus();
        var local = new LatentSink("local", TimeSpan.Zero);
        var failed = new LatentSink("failed", TimeSpan.FromSeconds(2)) { RefuseToOpen = true };

        bus.Add(local);
        bus.Add(failed);
        bus.SetFormat(Cd);

        WriteChunks(bus, 1, 2);

        // Delaying everything else by two seconds to match an output that isn't playing is
        // how one unreachable speaker takes the whole house with it.
        Assert.Equal([.. Chunkful(1), .. Chunkful(2)], local.Received);
    }

    [Fact]
    public void A_wobbling_latency_report_does_not_keep_re_priming_the_line()
    {
        using var bus = new AudioSinkBus();
        var local = new LatentSink("local", TimeSpan.Zero);
        var remote = new LatentSink("remote", TimeSpan.FromMilliseconds(100));

        bus.Add(local);
        bus.Add(remote);
        bus.SetFormat(Cd);

        WriteChunks(bus, 1, 2, 3, 4, 5, 6);

        // A sink reports its depth in whole buffers, so the same output answers 200ms or
        // 150ms depending which side of a callback it is asked on. Re-priming on that costs
        // it a delay's worth of silence EVERY time - and re-priming more often than the delay
        // is long means it never emits a real sample at all.
        remote.Latency = TimeSpan.FromMilliseconds(150);
        WriteChunks(bus, 7, 8);
        remote.Latency = TimeSpan.FromMilliseconds(100);
        WriteChunks(bus, 9, 10);

        // Two chunks of priming silence at the start, and then an unbroken run: no second
        // prime anywhere in the middle.
        Assert.Equal(
            [.. new byte[Chunk * 2], .. Chunkful(1), .. Chunkful(2), .. Chunkful(3), .. Chunkful(4),
             .. Chunkful(5), .. Chunkful(6), .. Chunkful(7), .. Chunkful(8)],
            local.Received);
    }

    [Fact]
    public void A_speaker_joining_late_re_aligns_the_others()
    {
        using var bus = new AudioSinkBus();
        var local = new LatentSink("local", TimeSpan.Zero);

        bus.Add(local);
        bus.SetFormat(Cd);
        WriteChunks(bus, 1, 2);

        // Alone, it is never delayed. Then a receiver joins mid-song and everything has to
        // line up against it.
        Assert.Equal([.. Chunkful(1), .. Chunkful(2)], local.Received);

        bus.Add(new LatentSink("remote", TimeSpan.FromMilliseconds(100)));
        WriteChunks(bus, 3, 4, 5);

        Assert.Equal(
            [.. Chunkful(1), .. Chunkful(2), .. new byte[Chunk * 2], .. Chunkful(3)],
            local.Received);
    }

    [Fact]
    public void The_listener_position_is_only_reported_when_every_output_is_distant()
    {
        using var bus = new AudioSinkBus();
        var remote = new LatentSink("remote", TimeSpan.FromSeconds(2)) { Position = TimeSpan.FromSeconds(10) };
        var local = new LatentSink("local", TimeSpan.Zero) { Position = TimeSpan.FromSeconds(12) };

        bus.Add(remote);
        bus.SetFormat(Cd);

        // Only a distant speaker: the room is two seconds behind the decoder, and that is
        // what the transport should say.
        Assert.Equal(TimeSpan.FromSeconds(10), bus.ListenerPosition);

        // Add anything local and the decoder's own clock is honest again - correcting it
        // would make the timeline disagree with the speakers on the desk. (Opened explicitly:
        // an ordinary output waits for audio before it opens, and only an output that is OPEN
        // has an opinion about where the listener is.)
        bus.Add(local);
        local.Open(Cd);
        Assert.Null(bus.ListenerPosition);
    }

    // ===== The delay line on its own =====

    [Fact]
    public void The_delay_line_returns_what_it_was_given_a_set_time_ago()
    {
        var line = new SinkDelayLine();
        line.Configure(delayBytes: 8, chunkBytes: 4);

        var output = new byte[4];

        line.Process([1, 2, 3, 4], output);
        Assert.Equal([0, 0, 0, 0], output);

        line.Process([5, 6, 7, 8], output);
        Assert.Equal([0, 0, 0, 0], output);

        line.Process([9, 10, 11, 12], output);
        Assert.Equal([1, 2, 3, 4], output);

        line.Process([13, 14, 15, 16], output);
        Assert.Equal([5, 6, 7, 8], output);
    }

    [Fact]
    public void The_delay_line_survives_a_write_larger_than_the_delay()
    {
        var line = new SinkDelayLine();
        line.Configure(delayBytes: 2, chunkBytes: 2);

        var output = new byte[6];
        line.Process([1, 2, 3, 4, 5, 6], output);
        Assert.Equal([0, 0, 1, 2, 3, 4], output);

        line.Process([7, 8, 9, 10, 11, 12], output);
        Assert.Equal([5, 6, 7, 8, 9, 10], output);
    }

    [Fact]
    public void The_delay_line_keeps_its_contents_in_order_across_the_wrap()
    {
        var line = new SinkDelayLine();
        line.Configure(delayBytes: 5, chunkBytes: 3);

        var output = new byte[3];
        var expected = new List<byte> { 0, 0, 0, 0, 0 };
        var actual = new List<byte>();

        for (byte value = 1; value <= 30; value++)
        {
            line.Process([value, value, value], output);
            actual.AddRange(output);
            expected.AddRange([value, value, value]);
        }

        // Every byte comes out once, in order, five behind - many times around the ring.
        Assert.Equal(expected.Take(actual.Count), actual);
    }

    [Fact]
    public void A_reset_re_primes_the_silence_rather_than_replaying_it()
    {
        var line = new SinkDelayLine();
        line.Configure(delayBytes: 4, chunkBytes: 4);

        var output = new byte[4];
        line.Process([1, 2, 3, 4], output);
        line.RequestReset();

        line.Process([5, 6, 7, 8], output);
        Assert.Equal([0, 0, 0, 0], output);

        line.Process([9, 10, 11, 12], output);
        Assert.Equal([5, 6, 7, 8], output);
    }
}
