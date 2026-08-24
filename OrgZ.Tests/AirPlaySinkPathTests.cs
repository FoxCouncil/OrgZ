// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Services.AudioOutput;
using OrgZ.Services.AudioOutput.AirPlay;

namespace OrgZ.Tests;

/// <summary>
/// The APP's path, offline: bus -> <see cref="AirPlayRaopSink"/> -> <see cref="AirPlay2Session"/>,
/// driven exactly as <see cref="AudioSinkBus"/> drives it, against the software receiver.
///
/// <see cref="AirPlayConformanceTests"/> pins what the SESSION says. This pins the state
/// machine above it, which is where the behaviours a person notices live: the classic RAOP
/// probe and the 401 fallback, the track announced before the handshake and replayed after
/// it, the hold that keeps a selected speaker alive with silence and says nothing until real
/// music arrives, the speaker's own level adopted into the app's slider, and the latency the
/// bus needs to line this output up against a sound card. All of that used to be checkable
/// only by a person with a HomePod; none of it ran in CI.
/// </summary>
[Collection(RealSocketCollection.Name)]
public class AirPlaySinkPathTests : IClassFixture<AirPlaySenderFixture>
{
    private static AudioDeviceInfo Device => new()
    {
        DeviceId = "fake@loopback",
        DisplayName = "Fake Receiver",
        ProviderId = AirPlayDeviceProvider.Id,
        ProviderName = "AirPlay",
        IsAvailable = true,
    };

    [Fact]
    public async Task Sink_probes_classic_raop_then_holds_the_speaker_until_real_audio_arrives()
    {
        using var fake = new FakeAirPlayReceiver { CurrentVolumeDb = -15.0 };
        using var sink = new AirPlayRaopSink(Device, fake.Host, fake.Port, null, null);

        string? failure = null;
        sink.ConnectFailed += (_, reason) => failure = reason;

        // The bus announces the track BEFORE the sink opens - the receiver latches the
        // stream's length from the first progress it sees, so it has to be right first time.
        sink.SetTrackInfo("Sink Probe", "OrgZ", "Conformance", TimeSpan.FromMinutes(4), Jpeg());
        sink.Open(AudioFormat.CdDaStereo16);

        await WaitAsync(() => sink.ProvidesClock || failure is not null, "the sink to start streaming");
        Assert.Null(failure);
        Assert.True(sink.ProvidesClock, "a streaming sink is the bus's clock");

        // Two protocol generations answer on one discovery record, so the sink asks the old
        // way first and takes the 401 as "this is an AirPlay 2 device".
        var sequence = fake.Sequence.ToList();
        Assert.Equal("OPTIONS", sequence[0]);
        Assert.Equal("GET /info", sequence[1]);
        Assert.Contains("POST /pair-setup", sequence);

        // RECORD sits BETWEEN the session SETUP and the stream SETUP. Doing the stream setup
        // first gets audio out and leaves the receiver ignoring everything we say about it.
        var firstSetup = sequence.IndexOf("SETUP");
        var record = sequence.IndexOf("RECORD");
        var streamSetup = sequence.LastIndexOf("SETUP");
        Assert.True(firstSetup >= 0 && record > firstSetup && streamSetup > record,
            $"expected SETUP -> RECORD -> SETUP, got {string.Join(", ", sequence)}");

        // The hold: the pump's own silence keeps the session alive, and the receiver is told
        // NOTHING about what is playing - no item, no controls, and no running position. The
        // one progress triple of the start sequence carries the seeded length and stops
        // there; a position that kept ticking would show a silent speaker as playing.
        var progressWhileHeld = fake.Progress.Count;
        await Task.Delay(TimeSpan.FromSeconds(1.5), CancellationToken.None);

        Assert.Empty(fake.Metadata);
        Assert.Empty(fake.SupportedCommands);
        Assert.Equal(progressWhileHeld, fake.Progress.Count);

        // The level someone left the speaker at wins, and it lands in the app's slider.
        Assert.NotEmpty(fake.Volumes);
        Assert.Equal(-15.0, fake.Volumes[0], 3);
        Assert.Equal(0.5, sink.Volume, 3);

        // The release: the first packet that is real music rather than the pump's silence.
        for (var i = 0; i < 8; i++)
        {
            sink.Write(Tone());
        }

        fake.WaitFor(() => fake.Metadata.Any(m => m.Title == "Sink Probe"), "the release to announce the seeded track");
        fake.WaitFor(() => fake.SupportedCommands.Count == 6, "the release to offer the controls");

        Assert.Equal(240000, fake.Metadata[^1].DurationMs);
        Assert.True(fake.Progress.Count > 0, "the release must place the item on the timeline");

        // The only thing this receiver refused is the classic probe it was right to refuse.
        Assert.All(fake.Rejected, reason => Assert.EndsWith("before pair-setup", reason));
    }

    /// <summary>
    /// What the bus needs to line this output up against a sound card.
    ///
    /// An AirPlay receiver plays what it is sent about two seconds later; a local card plays
    /// it now. A sink that reports no latency and no position leaves the bus's alignment inert
    /// for the one output the feature exists for - the same song from two rooms, seconds apart.
    /// </summary>
    [Fact]
    public async Task Sink_reports_the_receivers_latency_and_the_listeners_position()
    {
        using var fake = new FakeAirPlayReceiver();
        using var sink = new AirPlayRaopSink(Device, fake.Host, fake.Port, null, null);

        string? failure = null;
        sink.ConnectFailed += (_, reason) => failure = reason;

        sink.Open(AudioFormat.CdDaStereo16);
        await WaitAsync(() => sink.ProvidesClock || failure is not null, "the sink to start streaming");
        Assert.Null(failure);

        for (var i = 0; i < 8; i++)
        {
            sink.Write(Tone());
        }

        await WaitAsync(() => sink.PlaybackPosition is not null, "the sink to report a position");

        // The receiver's buffer, which is what the whole alignment is compensating for.
        Assert.True(sink.OutputLatency >= TimeSpan.FromSeconds(2), $"the sink reports {sink.OutputLatency} of output latency");

        // The LISTENER's position, not the sender's: what has actually come out of the
        // speaker is what was sent a receiver-buffer ago, so this can never run ahead of the
        // wall clock since the stream started.
        var position = sink.PlaybackPosition;
        Assert.NotNull(position);
        Assert.True(position!.Value < TimeSpan.FromMinutes(1), $"the reported position is {position}, which is not a listener position");
    }

    private static async Task WaitAsync(Func<bool> condition, string what, int timeoutMs = 20000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25, CancellationToken.None);
        }

        throw new TimeoutException($"Timed out waiting for {what}.");
    }

    /// <summary>A packet's worth of quiet 16-bit stereo that is NOT the pump's own silence.</summary>
    private static byte[] Tone()
    {
        var pcm = new byte[RaopAlac.PcmBytesPerPacket];
        for (var frame = 0; frame * 4 + 3 < pcm.Length; frame++)
        {
            var sample = (short)(frame % 2 == 0 ? 512 : -512);
            var offset = frame * 4;
            pcm[offset] = (byte)(sample & 0xFF);
            pcm[offset + 1] = (byte)((sample >> 8) & 0xFF);
            pcm[offset + 2] = pcm[offset];
            pcm[offset + 3] = pcm[offset + 1];
        }

        return pcm;
    }

    /// <summary>The smallest thing that parses as a JPEG - artwork content, not a real image.</summary>
    private static byte[] Jpeg()
    {
        var jpeg = new byte[64];
        jpeg[0] = 0xFF;
        jpeg[1] = 0xD8;
        jpeg[2] = 0xFF;
        jpeg[3] = 0xE0;
        jpeg[^2] = 0xFF;
        jpeg[^1] = 0xD9;
        return jpeg;
    }
}
