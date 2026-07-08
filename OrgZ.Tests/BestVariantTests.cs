// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.StationCurator.Models;

namespace OrgZ.Tests;

/// <summary>
/// The export-selection order for a station's stream variants: probe health first, then
/// metadata support (a working now-playing display beats transport and bitrate), then
/// direct-over-HLS, bitrate, codec preference, and redirect count.
/// </summary>
public class BestVariantTests
{
    private static StreamVariant Ok(string url, string format, int bitrate, int? metaint = null, string? hlsMeta = null) => new()
    {
        Url = url,
        Format = format,
        Bitrate = bitrate,
        ProbeStatus = ProbeStatus.Ok,
        ProbedAtUtc = DateTimeOffset.UnixEpoch,
        ProbeMetaint = metaint,
        ProbeHlsMeta = hlsMeta,
    };

    [Fact]
    public void MetadataBeatsHigherBitrate()
    {
        var station = new CuratedStation
        {
            Streams =
            [
                Ok("http://a/320", "mp3", 320),
                Ok("http://b/128", "mp3", 128, metaint: 16000),
            ],
        };

        Assert.Equal("http://b/128", station.BestVariant()!.Url);
    }

    [Fact]
    public void HlsWithMetadataBeatsDirectWithout()
    {
        var station = new CuratedStation
        {
            Streams =
            [
                Ok("http://a/direct", "mp3", 128),
                Ok("http://b/hls", "hls", 128, hlsMeta: "ts"),
            ],
        };

        Assert.Equal("http://b/hls", station.BestVariant()!.Url);
    }

    [Fact]
    public void DirectBeatsHlsWhenMetadataIsEqual()
    {
        var station = new CuratedStation
        {
            Streams =
            [
                Ok("http://a/hls", "hls", 128, hlsMeta: "ts"),
                Ok("http://b/direct", "mp3", 128, metaint: 16000),
            ],
        };

        Assert.Equal("http://b/direct", station.BestVariant()!.Url);
    }

    [Fact]
    public void ProbeHealthStillOutranksMetadata()
    {
        var dead = Ok("http://a/dead", "mp3", 320, metaint: 16000);
        dead.ProbeStatus = ProbeStatus.Dead;

        var station = new CuratedStation
        {
            Streams =
            [
                dead,
                Ok("http://b/alive", "mp3", 128),
            ],
        };

        Assert.Equal("http://b/alive", station.BestVariant()!.Url);
    }

    [Fact]
    public void ExplicitPreferenceOverridesEverything()
    {
        var preferred = Ok("http://a/low", "mp3", 64);
        var station = new CuratedStation
        {
            PreferredStreamId = preferred.Id,
            Streams =
            [
                preferred,
                Ok("http://b/meta", "mp3", 320, metaint: 16000),
            ],
        };

        Assert.Equal("http://a/low", station.BestVariant()!.Url);
    }

    [Fact]
    public void ExtInfMetadataCountsAsMetadata()
    {
        var station = new CuratedStation
        {
            Streams =
            [
                Ok("http://a/direct", "mp3", 128),
                Ok("http://b/iheart-hls", "hls", 128, hlsMeta: "extinf"),
            ],
        };

        // iHeart's plaintext EXTINF now-playing is a working metadata channel - it must
        // outrank a metadata-less direct stream just like a timed-ID3 channel does.
        Assert.Equal("http://b/iheart-hls", station.BestVariant()!.Url);
    }

    [Fact]
    public void MeasuredTuneInBreaksTiesBeforeHopCount()
    {
        var slow = Ok("http://a/slow", "mp3", 128, metaint: 16000);
        slow.ProbeTuneInMs = 2600;
        slow.ProbeRedirects = 0;

        var fast = Ok("http://b/fast", "mp3", 128, metaint: 16000);
        fast.ProbeTuneInMs = 240;
        fast.ProbeRedirects = 3;   // more hops, but measurably faster to first audio byte

        var station = new CuratedStation { Streams = [slow, fast] };

        Assert.Equal("http://b/fast", station.BestVariant()!.Url);
    }
}
