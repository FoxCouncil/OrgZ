// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Services.AudioOutput;
using OrgZ.Services.AudioOutput.AirPlay;
using Xunit.Abstractions;

namespace OrgZ.Tests;

/// <summary>
/// Drives a REAL AirPlay receiver end to end. Skipped unless ORGZ_AIRPLAY_HOST is set, so
/// the normal suite stays offline and silent.
///
/// These are not where the protocol gets developed - that is
/// <see cref="AirPlayConformanceTests"/>, which runs against a software receiver on loopback
/// in seconds and says which byte was wrong. Roughly forty hardware cycles established less
/// than one run of that does, because a speaker's only diagnostic is whether a person in the
/// room can see anything on it.
///
/// What hardware is still for: the two things a receiver we wrote cannot prove - that our
/// crypto agrees with Apple's, and that a HomePod renders what we send. Point these at one
/// when a change reaches that far, not while iterating.
/// </summary>
public class AirPlayLiveTests
{
    private const int SampleRate = 44100;

    private readonly ITestOutputHelper _output;

    public AirPlayLiveTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static (string Host, string? Password) Target()
    {
        var host = Environment.GetEnvironmentVariable("ORGZ_AIRPLAY_HOST");
        Skip.If(string.IsNullOrEmpty(host), "Set ORGZ_AIRPLAY_HOST to run the live AirPlay tests.");
        return (host!, Environment.GetEnvironmentVariable("ORGZ_AIRPLAY_PASSWORD"));
    }

    private static AudioDeviceInfo Device => new()
    {
        DeviceId = "live@test",
        DisplayName = "Live",
        ProviderId = AirPlayDeviceProvider.Id,
        ProviderName = "AirPlay",
        IsAvailable = true,
    };

    /// <summary>
    /// The APP's path: bus -> AirPlayRaopSink -> AirPlay2Session, driven exactly as
    /// AudioSinkBus drives it, with the track announced before the handshake as the bus does.
    /// </summary>
    [SkippableFact]
    public async Task Sink_path_connects_and_announces_a_track()
    {
        var (host, password) = Target();

        using var sink = new AirPlayRaopSink(Device, host, 7000, null, password);

        string? failure = null;
        sink.ConnectFailed += (_, reason) => failure = reason;

        sink.SetTrackInfo("Live Probe", "OrgZ", "Conformance", TimeSpan.FromMinutes(4), MinimalJpeg());
        sink.Open(AudioFormat.CdDaStereo16);

        var silence = new byte[RaopAlac.PcmBytesPerPacket];
        for (var i = 0; i < 200 && failure is null && !sink.ProvidesClock; i++)
        {
            sink.Write(silence);
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.True(sink.ProvidesClock, $"sink never started streaming (failure: {failure ?? "none reported"})");

        // Keep it alive long enough for the receiver to render, and for the ~30s point where
        // an unserviced event channel used to end the stream.
        for (var i = 0; i < 1600; i++)
        {
            sink.Write(silence);
            await Task.Delay(25, CancellationToken.None);
        }
    }

    /// <summary>
    /// The session directly, for checking what a speaker's DISPLAY does with a metadata
    /// change - the one thing no software receiver can answer.
    /// </summary>
    [SkippableFact]
    public async Task Now_playing_survives_a_track_change_on_real_hardware()
    {
        var (host, password) = Target();

        using var session = new AirPlay2Session(host, 7000, password);
        await session.ConnectAsync(CancellationToken.None);

        Assert.True(session.IsConnected, "session should be up after ConnectAsync");

        // Audio FIRST, then the announcement: metadata is anchored to an RTP timestamp, and a
        // timestamp means nothing in a stream that has never sent a packet.
        var silence = new byte[AirPlay2Session.FramesPerPacket * 4];
        for (var i = 0; i < 40; i++)
        {
            await session.SendPacketAsync(silence, CancellationToken.None);
        }

        Assert.True(session.HasSentAudio, "audio should be flowing before the announcement");

        await session.SetTrackInfoAsync("First Track", "OrgZ", "Live", TimeSpan.FromMinutes(3.5), MinimalJpeg());
        for (var i = 0; i < 600; i++)
        {
            await session.SendPacketAsync(silence, CancellationToken.None);
        }

        await session.SetTrackInfoAsync("Second Track", "OrgZ", "Live", TimeSpan.FromMinutes(2), null);
        for (var i = 0; i < 600; i++)
        {
            await session.SendPacketAsync(silence, CancellationToken.None);
        }
    }

    /// <summary>Something audible, for the "clean sine or static?" check that only ears make.</summary>
    [SkippableFact]
    public async Task Streams_a_tone_to_a_real_receiver()
    {
        var (host, password) = Target();

        using var session = new AirPlay2Session(host, 7000, password);
        await session.ConnectAsync(CancellationToken.None);

        Assert.True(session.IsConnected, "session should be up after ConnectAsync");

        // Four seconds of a quiet 440 Hz tone, packet-paced by the session itself. Kept low:
        // this plays out loud in someone's room.
        var packets = 4 * SampleRate / AirPlay2Session.FramesPerPacket;
        var phase = 0.0;

        for (var i = 0; i < packets; i++)
        {
            var pcm = new byte[AirPlay2Session.FramesPerPacket * 4];
            for (var frame = 0; frame < AirPlay2Session.FramesPerPacket; frame++)
            {
                var sample = (short)(0.05 * short.MaxValue * Math.Sin(phase));
                phase += 2 * Math.PI * 440.0 / SampleRate;

                var offset = frame * 4;
                pcm[offset] = (byte)(sample & 0xFF);
                pcm[offset + 1] = (byte)((sample >> 8) & 0xFF);
                pcm[offset + 2] = pcm[offset];
                pcm[offset + 3] = pcm[offset + 1];
            }

            await session.SendPacketAsync(pcm, CancellationToken.None);
        }

        Assert.True(session.IsConnected, "session should still be up after streaming");
    }

    /// <summary>
    /// Prints what the receiver says about ITSELF, before any pairing.
    ///
    /// /info is plaintext and needs no credentials, which makes it the one diagnostic that
    /// costs nothing and can be run against a locked-down speaker. Its features bits and
    /// statusFlags say which of the things we are doing it actually supports - and when the
    /// display stays blank while every request returns 200, what the receiver believes about
    /// itself is the only evidence left.
    /// </summary>
    [SkippableFact]
    public async Task Dumps_what_the_receiver_says_about_itself()
    {
        var (host, _) = Target();

        using var rtsp = new RtspClient();
        await rtsp.ConnectAsync(host, 7000, CancellationToken.None);

        rtsp.DefaultHeaders["User-Agent"] = "AirPlay/550.10";
        var info = await rtsp.SendAsync("GET", "/info", ct: CancellationToken.None);

        Assert.True(info.IsSuccess, $"/info returned {info.StatusCode} {info.StatusText}");

        var plist = BinaryPlist.Read(info.BodyBytes) as Dictionary<string, object?>;
        Assert.NotNull(plist);

        var report = new System.Text.StringBuilder();
        report.AppendLine($"=== {host} /info ===");
        foreach (var (key, value) in plist!.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            report.AppendLine($"  {key} = {Describe(value)}");
        }

        // Features is a 64-bit bitfield; the bits that decide whether metadata and the
        // control paths are on are the whole question here.
        if (plist.TryGetValue("features", out var features) && features is long bits)
        {
            report.AppendLine($"  features bits set: {string.Join(", ", Enumerable.Range(0, 64).Where(b => (bits & (1L << b)) != 0))}");
        }

        if (plist.TryGetValue("statusFlags", out var status) && status is long flags)
        {
            report.AppendLine($"  statusFlags = 0x{flags:X} (password={(flags & 0x80) != 0}, pin={(flags & 0x8) != 0}, oneTimePairing={(flags & 0x200) != 0})");
        }

        var path = Path.Combine(Path.GetTempPath(), $"airplay-info-{host}.txt");
        await File.WriteAllTextAsync(path, report.ToString(), CancellationToken.None);

        // The report goes to the test OUTPUT, not through a failure. Failing to print it
        // meant a live run was red whatever the receiver said, which buries a genuine red in
        // the three tests that actually stream.
        report.AppendLine($"  (also written to {path})");
        _output.WriteLine(report.ToString());

        // What a receiver must tell us about itself before any of this is worth trying: who
        // it is, what it supports, and whether it wants a password.
        Assert.True(plist.ContainsKey("deviceid"), "/info carried no deviceid");
        Assert.True(plist.TryGetValue("features", out var featureBits) && featureBits is long, "/info carried no features bitfield");
        Assert.True(plist.TryGetValue("statusFlags", out var statusFlags) && statusFlags is long, "/info carried no statusFlags");
    }

    private static string Describe(object? value) => value switch
    {
        null => "(null)",
        byte[] data => $"<{data.Length} bytes> {Convert.ToHexString(data[..Math.Min(24, data.Length)])}",
        List<object?> list => $"[{string.Join(", ", list.Select(Describe))}]",
        Dictionary<string, object?> map => $"{{{string.Join(", ", map.Select(kv => $"{kv.Key}: {Describe(kv.Value)}"))}}}",
        _ => value.ToString() ?? "",
    };

    /// <summary>The smallest thing that passes as a JPEG - artwork content, not a real image.</summary>
    private static byte[] MinimalJpeg()
    {
        var jpeg = new byte[512];
        jpeg[0] = 0xFF;
        jpeg[1] = 0xD8;
        jpeg[2] = 0xFF;
        jpeg[3] = 0xE0;
        jpeg[^2] = 0xFF;
        jpeg[^1] = 0xD9;
        return jpeg;
    }
}
