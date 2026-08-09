// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Services;

namespace OrgZ.Tests;

public class DataDiscTranscoderTests
{
    // -- ExtensionFor --

    [Theory]
    [InlineData("mp3", ".mp3")]
    [InlineData("aac", ".m4a")]
    [InlineData("alac", ".m4a")]
    [InlineData("flac", ".flac")]
    [InlineData("wav", ".wav")]
    public void ExtensionFor_KnownFormats(string format, string expected)
    {
        Assert.Equal(expected, DataDiscTranscoder.ExtensionFor(format));
    }

    [Theory]
    [InlineData("original")]
    [InlineData("")]
    [InlineData("ogg")]
    public void ExtensionFor_OriginalOrUnknown_IsNull(string format)
    {
        Assert.Null(DataDiscTranscoder.ExtensionFor(format));
    }

    // -- AlreadyTargetFormat (extension-driven cases; the .m4a AAC/ALAC split needs a real file) --

    [Theory]
    [InlineData(@"C:\music\song.mp3", "mp3", true)]
    [InlineData(@"C:\music\song.MP3", "mp3", true)]
    [InlineData(@"C:\music\song.flac", "mp3", false)]
    [InlineData(@"C:\music\song.flac", "flac", true)]
    [InlineData(@"C:\music\song.wav", "wav", true)]
    [InlineData(@"C:\music\song.aac", "aac", true)]
    [InlineData(@"C:\music\song.mp3", "wav", false)]
    public void AlreadyTargetFormat_ByExtension(string path, string format, bool expected)
    {
        Assert.Equal(expected, DataDiscTranscoder.AlreadyTargetFormat(path, format));
    }

    [Fact]
    public void AlreadyTargetFormat_MissingM4a_CountsAsAacNotAlac()
    {
        // TagLib can't open a nonexistent file, so the ALAC probe answers false:
        // the file reads as AAC (no transcode) and never as ALAC.
        Assert.True(DataDiscTranscoder.AlreadyTargetFormat(@"C:\nope\ghost.m4a", "aac"));
        Assert.False(DataDiscTranscoder.AlreadyTargetFormat(@"C:\nope\ghost.m4a", "alac"));
    }

    // -- Argument builders --

    [Fact]
    public void BuildFfmpegArgs_Aac_ClampsBitrateAndFaststarts()
    {
        var args = DataDiscTranscoder.BuildFfmpegArgs("in.flac", "out.m4a", "aac", 999);

        Assert.Contains("-b:a", args);
        Assert.Equal("320k", args[args.IndexOf("-b:a") + 1]);
        Assert.Contains("+faststart", args);
        Assert.Equal("out.m4a", args[^1]);
    }

    [Theory]
    [InlineData("alac", "alac")]
    [InlineData("flac", "flac")]
    [InlineData("wav", "pcm_s16le")]
    public void BuildFfmpegArgs_LosslessFormats_PickTheRightCodec(string format, string codec)
    {
        var args = DataDiscTranscoder.BuildFfmpegArgs("in.mp3", "out.x", format, 256);

        Assert.Equal(codec, args[args.IndexOf("-c:a") + 1]);
        // Lossless never gets a bitrate cap.
        Assert.DoesNotContain("-b:a", args);
    }

    [Fact]
    public void BuildFfmpegArgs_MapsAudioOnlyWithMetadata()
    {
        var args = DataDiscTranscoder.BuildFfmpegArgs("in.flac", "out.flac", "flac", 256);

        Assert.Equal("0:a:0", args[args.IndexOf("-map") + 1]);
        Assert.Contains("-map_metadata", args);
    }

    [Fact]
    public void BuildFfmpegArgs_UnknownFormat_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DataDiscTranscoder.BuildFfmpegArgs("in.mp3", "out.ogg", "ogg", 256));
    }

    [Fact]
    public void BuildLameArgs_CbrAtClampedBitrate_ReadsRawPcmFromStdin()
    {
        var args = DataDiscTranscoder.BuildLameArgs("out.mp3", 10);

        Assert.Equal("32", args[args.IndexOf("-b") + 1]);
        Assert.Contains("--cbr", args);
        Assert.Contains("-r", args);
        Assert.Equal("-", args[^2]);
        Assert.Equal("out.mp3", args[^1]);
    }
}
