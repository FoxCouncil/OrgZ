// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Text.Json;
using OrgZ;

namespace OrgZ.Tests;

public class CdHelperSerializationTests
{
    [Fact]
    public void Rip_Spec_Roundtrips_Through_SourceGenerated_Context()
    {
        var spec = new CdHelperSpec
        {
            Operation = "rip",
            DrivePath = "D:",
            OutputDirectory = @"X:\Music\Artist\Album",
            Tracks =
            [
                new CdHelperTrack { TrackNumber = 1, Title = "Song One", Artist = "Artist", Album = "Album" },
                new CdHelperTrack { TrackNumber = 2, Title = "Song Two", Artist = "Artist", Album = "Album" },
            ],
        };

        var json = JsonSerializer.Serialize(spec, CdHelperJsonContext.Default.CdHelperSpec);
        var back = JsonSerializer.Deserialize(json, CdHelperJsonContext.Default.CdHelperSpec);

        Assert.NotNull(back);
        Assert.Equal("rip", back!.Operation);
        Assert.Equal("D:", back.DrivePath);
        Assert.Equal(@"X:\Music\Artist\Album", back.OutputDirectory);
        Assert.Equal(2, back.Tracks?.Count);
        Assert.Equal(1, back.Tracks![0].TrackNumber);
        Assert.Equal("Song One", back.Tracks[0].Title);
    }

    [Fact]
    public void Burn_Spec_Roundtrips_With_TestWrite_And_DiscMetadata()
    {
        var spec = new CdHelperSpec
        {
            Operation = "burn",
            DrivePath = @"\\.\D:",
            DiscTitle = "Roadtrip Mix",
            DiscPerformer = "Fox",
            TestWrite = true,
            WriteSpeedKBps = 1412,
            Tracks =
            [
                new CdHelperTrack { TrackNumber = 1, WavFilePath = @"C:\tmp\01.wav", Title = "A", Artist = "X" },
                new CdHelperTrack { TrackNumber = 2, WavFilePath = @"C:\tmp\02.wav", Title = "B", Artist = "Y" },
            ],
        };

        var json = JsonSerializer.Serialize(spec, CdHelperJsonContext.Default.CdHelperSpec);
        var back = JsonSerializer.Deserialize(json, CdHelperJsonContext.Default.CdHelperSpec)!;

        Assert.Equal("burn", back.Operation);
        Assert.True(back.TestWrite);
        Assert.Equal("Roadtrip Mix", back.DiscTitle);
        Assert.Equal(1412, back.WriteSpeedKBps);
        Assert.Equal(@"C:\tmp\01.wav", back.Tracks![0].WavFilePath);
    }

    [Fact]
    public void Rip_Progress_Event_Roundtrips()
    {
        var evt = new CdHelperEvent
        {
            Type = "rip-progress",
            TrackNumber = 3,
            TrackCount = 10,
            TrackTitle = "Funky Beat",
            SectorsDone = 1500,
            SectorsTotal = 4500,
            RetryCount = 2,
        };

        var line = JsonSerializer.Serialize(evt, CdHelperJsonContext.Default.CdHelperEvent);
        var back = JsonSerializer.Deserialize(line, CdHelperJsonContext.Default.CdHelperEvent)!;

        Assert.Equal("rip-progress", back.Type);
        Assert.Equal(3, back.TrackNumber);
        Assert.Equal(1500, back.SectorsDone);
        Assert.Equal(2, back.RetryCount);
    }

    [Fact]
    public void Rip_Done_Event_Carries_Outcomes_With_AccurateRip_Crcs()
    {
        var evt = new CdHelperEvent
        {
            Type = "rip-done",
            Outcomes =
            [
                new CdHelperOutcome { TrackNumber = 1, OutputPath = "out1.wav", SectorsRipped = 17000, AccurateRipV1 = 0xAABBCCDDu, AccurateRipV2 = 0x11223344u, HadErrors = false },
                new CdHelperOutcome { TrackNumber = 2, OutputPath = "out2.wav", SectorsRipped = 20000, AccurateRipV1 = 0xDEADBEEFu, AccurateRipV2 = 0xCAFEBABEu, HadErrors = true },
            ],
        };

        var line = JsonSerializer.Serialize(evt, CdHelperJsonContext.Default.CdHelperEvent);
        var back = JsonSerializer.Deserialize(line, CdHelperJsonContext.Default.CdHelperEvent)!;

        Assert.Equal(2, back.Outcomes?.Count);
        Assert.Equal(0xAABBCCDDu, back.Outcomes![0].AccurateRipV1);
        Assert.True(back.Outcomes[1].HadErrors);
    }

    [Fact]
    public void DataBurn_Spec_Roundtrips_With_DiscPaths()
    {
        var spec = new CdHelperSpec
        {
            Operation = "burn-data",
            DrivePath = "I:",
            DiscTitle = "Burn Test 1",
            Tracks =
            [
                new CdHelperTrack { TrackNumber = 1, SourcePath = @"X:\Music\A\B\01.mp3", DiscPath = "A/B/01.mp3" },
                new CdHelperTrack { TrackNumber = 2, SourcePath = @"X:\Music\A\B\02.flac", DiscPath = "A/B/02.flac" },
            ],
        };

        var json = JsonSerializer.Serialize(spec, CdHelperJsonContext.Default.CdHelperSpec);
        var back = JsonSerializer.Deserialize(json, CdHelperJsonContext.Default.CdHelperSpec)!;

        Assert.Equal("burn-data", back.Operation);
        Assert.Equal("Burn Test 1", back.DiscTitle);
        Assert.Equal(2, back.Tracks?.Count);
        Assert.Equal(@"X:\Music\A\B\01.mp3", back.Tracks![0].SourcePath);
        Assert.Equal("A/B/01.mp3", back.Tracks[0].DiscPath);
        Assert.Null(back.Tracks[0].WavFilePath);
    }

    [Fact]
    public void Erase_Spec_And_Done_Event_Roundtrip()
    {
        var spec = new CdHelperSpec
        {
            Operation = "erase",
            DrivePath = @"\\.\I:",
        };

        var json = JsonSerializer.Serialize(spec, CdHelperJsonContext.Default.CdHelperSpec);
        var back = JsonSerializer.Deserialize(json, CdHelperJsonContext.Default.CdHelperSpec)!;

        Assert.Equal("erase", back.Operation);
        Assert.Equal(@"\\.\I:", back.DrivePath);
        Assert.Null(back.Tracks);

        var evt = new CdHelperEvent { Type = "erase-done" };
        var line = JsonSerializer.Serialize(evt, CdHelperJsonContext.Default.CdHelperEvent);
        var backEvt = JsonSerializer.Deserialize(line, CdHelperJsonContext.Default.CdHelperEvent)!;

        Assert.Equal("erase-done", backEvt.Type);
    }

    [Fact]
    public void Error_Event_Carries_Message()
    {
        var evt = new CdHelperEvent { Type = "error", Message = "Disc is not blank" };
        var line = JsonSerializer.Serialize(evt, CdHelperJsonContext.Default.CdHelperEvent);
        var back = JsonSerializer.Deserialize(line, CdHelperJsonContext.Default.CdHelperEvent)!;

        Assert.Equal("error", back.Type);
        Assert.Equal("Disc is not blank", back.Message);
    }

    [Fact]
    public void ShouldRun_Detects_Switch_Anywhere_In_Args()
    {
        Assert.True(CdHelperMode.ShouldRun(["--cd-helper", "--spec", "a", "--progress", "b"]));
        Assert.True(CdHelperMode.ShouldRun(["arg1", "--cd-helper"]));
        Assert.False(CdHelperMode.ShouldRun(["--help"]));
        Assert.False(CdHelperMode.ShouldRun([]));
    }
}
