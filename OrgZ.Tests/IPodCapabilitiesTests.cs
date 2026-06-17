// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Services;

namespace OrgZ.Tests;

/// <summary>
/// Capability gating across the click-wheel iPod family: each generation gets the
/// right cover-art formats and checksum tier, and only generations whose checksum
/// is implemented are declared writable (wrong art IDs blank/crash; a bad DB
/// checksum shows "0 songs").
/// </summary>
public class IPodCapabilitiesTests
{
    [Theory]
    [InlineData("Video 5G", 1028, 1029)]
    [InlineData("Video 5.5G", 1028, 1029)]
    [InlineData("Nano 1G", 1031, 1027)]
    [InlineData("Nano 2G", 1031, 1027)]
    [InlineData("Photo", 1017, 1016)]
    public void Cover_formats_match_generation(string generation, int firstId, int secondId)
    {
        var formats = IPodCapabilities.CoverFormatsFor(generation);
        Assert.Equal(2, formats.Count);
        Assert.Equal(firstId, formats[0].FormatId);
        Assert.Equal(secondId, formats[1].FormatId);
    }

    [Fact]
    public void Classic_has_its_four_cover_formats()
    {
        var ids = IPodCapabilities.CoverFormatsFor("Classic 6G").Select(f => f.FormatId);
        Assert.Equal([1055, 1061, 1068, 1060], ids);
    }

    [Theory]
    [InlineData("1G")]          // monochrome
    [InlineData("Mini 1G")]     // monochrome
    [InlineData("Touch 4G")]    // iOS - not in the table
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonsense")]
    public void Generations_without_artwork_return_no_formats(string? generation)
    {
        Assert.Empty(IPodCapabilities.CoverFormatsFor(generation));
    }

    [Theory]
    [InlineData("Video 5.5G", IPodChecksum.None)]
    [InlineData("Nano 2G", IPodChecksum.None)]
    [InlineData("Classic 6G", IPodChecksum.Hash58)]
    [InlineData("Nano 4G", IPodChecksum.Hash58)]
    [InlineData("Nano 5G", IPodChecksum.Hash72)]
    [InlineData("Nano 6G", IPodChecksum.HashAB)]
    public void Checksum_tier_is_correct(string generation, IPodChecksum expected)
    {
        Assert.Equal(expected, IPodCapabilities.ChecksumFor(generation));
    }

    [Theory]
    [InlineData("Video 5G")]     // no checksum
    [InlineData("Video 5.5G")]
    [InlineData("Nano 1G")]
    [InlineData("Nano 2G")]
    [InlineData("Photo")]
    [InlineData("Mini 2G")]
    [InlineData("4G")]
    [InlineData("Classic 6G")]   // hash58
    [InlineData("Classic 7G")]
    [InlineData("Nano 3G")]
    [InlineData("Nano 4G")]
    [InlineData("Nano 5G")]      // hash72 + SQLite (proven on hardware)
    public void Writable_generations(string generation)
    {
        Assert.True(IPodCapabilities.SupportsDatabaseWrite(generation));
    }

    [Theory]
    [InlineData("Nano 6G")]      // hashAB + SQLite (proprietary blob)
    [InlineData("Touch 2G")]     // iOS
    [InlineData(null)]
    [InlineData("")]
    public void Non_writable_generations(string? generation)
    {
        Assert.False(IPodCapabilities.SupportsDatabaseWrite(generation));
    }
}
