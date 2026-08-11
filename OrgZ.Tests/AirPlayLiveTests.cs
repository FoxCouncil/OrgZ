// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Services.AudioOutput.AirPlay;

namespace OrgZ.Tests;

/// <summary>
/// Drives a real AirPlay 2 receiver end to end. Skipped unless ORGZ_AIRPLAY_HOST is set,
/// so the normal suite stays offline and silent.
///
/// This exists because every other test here proves a piece in isolation - the SRP maths,
/// the TLV framing, the sealed audio frame - and the protocol kept failing in the joins
/// between them. The RTSP sequence is the part that can only be verified against hardware.
///
/// Run it with:
///   $env:ORGZ_AIRPLAY_HOST="192.168.1.90"; $env:ORGZ_AIRPLAY_PASSWORD="..."
/// The password is read from the environment and never lives in the repo.
/// </summary>
public class AirPlayLiveTests
{
    private const int SampleRate = 44100;

    [SkippableFact]
    public async Task Streams_a_tone_to_a_real_receiver()
    {
        var host = Environment.GetEnvironmentVariable("ORGZ_AIRPLAY_HOST");
        Skip.If(string.IsNullOrEmpty(host), "Set ORGZ_AIRPLAY_HOST to run the live AirPlay test.");

        var password = Environment.GetEnvironmentVariable("ORGZ_AIRPLAY_PASSWORD");

        using var session = new AirPlay2Session(host!, 7000, password);
        await session.ConnectAsync(CancellationToken.None);

        Assert.True(session.IsConnected, "session should be up after ConnectAsync");

        // One second of a quiet 440 Hz tone, packet-paced by the session itself. Kept short
        // and low: this is a "clean sine or noise?" check, and it plays out loud in someone's
        // room.
        var packets = SampleRate / AirPlay2Session.FramesPerPacket;
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
}
