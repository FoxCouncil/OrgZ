// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Tests;

public class RipEncoderTests
{
    [Theory]
    [InlineData(RipFormat.Wav, ".wav")]
    [InlineData(RipFormat.Flac, ".flac")]
    [InlineData(RipFormat.Mp3, ".mp3")]
    public void ExtensionFor_Maps_Each_Format_To_Canonical_Extension(RipFormat format, string expected)
    {
        Assert.Equal(expected, RipEncoder.ExtensionFor(format));
    }

    [Fact]
    public void BuildFileName_Uses_Format_Extension()
    {
        Assert.Equal("01 - Song.wav", CdRipService.BuildFileName(1, "Song", RipFormat.Wav));
        Assert.Equal("02 - Song.flac", CdRipService.BuildFileName(2, "Song", RipFormat.Flac));
        Assert.Equal("03 - Song.mp3", CdRipService.BuildFileName(3, "Song", RipFormat.Mp3));
    }

    [Theory]
    [InlineData(0, "-0")]
    [InlineData(5, "-5")]
    [InlineData(8, "-8")]
    public void FlacArgs_Encode_Compression_Level_As_Short_Flag(int level, string expectedFlag)
    {
        var options = new CdRipOptions { Format = RipFormat.Flac, FlacCompression = level };
        var args = RipEncoder.BuildFlacArgs("out.flac", new RipTrackMetadata(), options);
        Assert.Contains(expectedFlag, args);
    }

    [Fact]
    public void FlacArgs_Clamp_Out_Of_Range_Levels()
    {
        var tooHigh = new CdRipOptions { Format = RipFormat.Flac, FlacCompression = 99 };
        var tooLow = new CdRipOptions { Format = RipFormat.Flac, FlacCompression = -2 };

        Assert.Contains("-8", RipEncoder.BuildFlacArgs("out.flac", new RipTrackMetadata(), tooHigh));
        Assert.Contains("-0", RipEncoder.BuildFlacArgs("out.flac", new RipTrackMetadata(), tooLow));
    }

    [Fact]
    public void FlacArgs_Include_Raw_Format_Flags_And_Stdin_Marker()
    {
        var meta = new RipTrackMetadata { Title = "T", Artist = "A", Album = "Al", TrackNumber = 5, Year = 1999 };
        var args = RipEncoder.BuildFlacArgs(@"X:\out.flac", meta, new CdRipOptions { Format = RipFormat.Flac });

        Assert.Contains("--silent", args);
        Assert.Contains("--force-raw-format", args);
        Assert.Contains("--endian=little", args);
        Assert.Contains("--sign=signed", args);
        Assert.Contains("--channels=2", args);
        Assert.Contains("--bps=16", args);
        Assert.Contains("--sample-rate=44100", args);
        Assert.Contains("-o", args);
        Assert.Contains(@"X:\out.flac", args);
        Assert.Contains("--tag=TITLE=T", args);
        Assert.Contains("--tag=ARTIST=A", args);
        Assert.Contains("--tag=ALBUM=Al", args);
        Assert.Contains("--tag=TRACKNUMBER=5", args);
        Assert.Contains("--tag=DATE=1999", args);
        Assert.Equal("-", args[^1]);
    }

    [Fact]
    public void FlacArgs_Omit_Tags_When_Metadata_Is_Empty()
    {
        var args = RipEncoder.BuildFlacArgs("out.flac", new RipTrackMetadata(), new CdRipOptions { Format = RipFormat.Flac });
        Assert.DoesNotContain(args, a => a.StartsWith("--tag=TITLE="));
        Assert.DoesNotContain(args, a => a.StartsWith("--tag=ARTIST="));
        Assert.DoesNotContain(args, a => a.StartsWith("--tag=DATE="));
    }

    [Fact]
    public void LameArgs_Vbr_Emits_V_Flag_With_Clamped_Quality()
    {
        var options = new CdRipOptions { Format = RipFormat.Mp3, Mp3Mode = Mp3Mode.Vbr, Mp3Quality = 3 };
        var args = RipEncoder.BuildLameArgs("out.mp3", new RipTrackMetadata(), options);

        var vIdx = args.IndexOf("-V");
        Assert.True(vIdx >= 0);
        Assert.Equal("3", args[vIdx + 1]);
        Assert.DoesNotContain("-b", args);
    }

    [Fact]
    public void LameArgs_Cbr_Emits_Bitrate_And_Cbr_Flag()
    {
        var options = new CdRipOptions { Format = RipFormat.Mp3, Mp3Mode = Mp3Mode.Cbr, Mp3Quality = 256 };
        var args = RipEncoder.BuildLameArgs("out.mp3", new RipTrackMetadata(), options);

        var bIdx = args.IndexOf("-b");
        Assert.True(bIdx >= 0);
        Assert.Equal("256", args[bIdx + 1]);
        Assert.Contains("--cbr", args);
        Assert.DoesNotContain("-V", args);
    }

    [Fact]
    public void LameArgs_Use_Raw_PCM_Input_Shape()
    {
        var args = RipEncoder.BuildLameArgs(@"X:\out.mp3", new RipTrackMetadata(), new CdRipOptions { Format = RipFormat.Mp3 });

        Assert.Contains("-r", args);
        Assert.Contains("--bitwidth", args);
        Assert.Contains("--signed", args);
        Assert.Contains("--little-endian", args);

        var sIdx = args.IndexOf("-s");
        Assert.True(sIdx >= 0 && args[sIdx + 1] == "44.1");

        var mIdx = args.IndexOf("-m");
        Assert.True(mIdx >= 0 && args[mIdx + 1] == "s");

        Assert.Equal(@"X:\out.mp3", args[^1]);
        Assert.Equal("-", args[^2]);
    }

    [Fact]
    public void LameArgs_Include_Metadata_Tag_Flags()
    {
        var meta = new RipTrackMetadata { Title = "T", Artist = "A", Album = "Al", TrackNumber = 5, Year = 2024 };
        var args = RipEncoder.BuildLameArgs("out.mp3", meta, new CdRipOptions { Format = RipFormat.Mp3 });

        Assert.Contains("--tt", args);
        Assert.Contains("--ta", args);
        Assert.Contains("--tl", args);
        Assert.Contains("--tn", args);
        Assert.Contains("--ty", args);
    }

    [Fact]
    public void RipOptions_ShortLabel_Reflects_Format_And_Quality()
    {
        Assert.Equal("WAV", new CdRipOptions { Format = RipFormat.Wav }.ShortLabel);
        Assert.Equal("FLAC (fast)", new CdRipOptions { Format = RipFormat.Flac, FlacCompression = 0 }.ShortLabel);
        Assert.Equal("FLAC (best)", new CdRipOptions { Format = RipFormat.Flac, FlacCompression = 8 }.ShortLabel);
        Assert.Equal("FLAC (level 5)", new CdRipOptions { Format = RipFormat.Flac, FlacCompression = 5 }.ShortLabel);
        Assert.Equal("MP3 VBR V0", new CdRipOptions { Format = RipFormat.Mp3, Mp3Mode = Mp3Mode.Vbr, Mp3Quality = 0 }.ShortLabel);
        Assert.Equal("MP3 CBR 320 kbps", new CdRipOptions { Format = RipFormat.Mp3, Mp3Mode = Mp3Mode.Cbr, Mp3Quality = 320 }.ShortLabel);
    }

    [Fact]
    public async Task Wav_Encoder_Writes_Header_Plus_Payload_To_Disk()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "orgz-wav-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            var payload = new byte[2352 * 3];
            for (int i = 0; i < payload.Length; i++) { payload[i] = (byte)(i & 0xFF); }

            var encoder = RipEncoder.Open(tmp, payload.Length, new RipTrackMetadata(), new CdRipOptions { Format = RipFormat.Wav });
            await using (encoder)
            {
                await encoder.WriteAsync(payload, default);
                // The encoder writes to a ".partial-rip" file and only renames it to the
                // final path on CompleteAsync - disposing without it discards the partial.
                await encoder.CompleteAsync(default);
            }

            var disk = await File.ReadAllBytesAsync(tmp);
            Assert.Equal(44 + payload.Length, disk.Length);
            Assert.Equal((byte)'R', disk[0]);
            Assert.Equal((byte)'F', disk[3]);
            for (int i = 0; i < payload.Length; i++)
            {
                Assert.Equal(payload[i], disk[44 + i]);
            }
        }
        finally
        {
            if (File.Exists(tmp)) { File.Delete(tmp); }
        }
    }
}
