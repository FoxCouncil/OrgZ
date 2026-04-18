// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Tests;

public class IPodBuildIdDatabaseTests
{
    // ===== Algorithmic linear-form decode (1G–4G, Mini, Photo, 5G/5.5G pre-1.3) =====

    [Theory]
    // Verified against dstaley/ipod-sysinfo captures
    [InlineData(0x02308000u, "2.3")]      // iPod 3G — patch byte zero, drops to "2.3"
    [InlineData(0x03118000u, "3.1.1")]    // iPod 4G
    [InlineData(0x04218000u, "4.2.1")]    // Photo 4G internal buildID (no override on this generation key)
    [InlineData(0x02618000u, "2.6.1")]    // Mini internal buildID without generation key
    public void DecodeLinearForm_known_values(uint vers, string expected)
    {
        // Pass null generation so the table lookup misses and we hit the algorithmic path
        var actual = IPodBuildIdDatabase.LookupVersion(generation: null, buildId: vers);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(0x01108000u, "1.1")]       // major=1, minor=1, patch=0, release stage
    [InlineData(0x01218000u, "1.2.1")]     // Photo visibleBuildID form
    [InlineData(0x09998000u, "9.9.9")]     // boundary: max valid BCD digits
    public void DecodeLinearForm_boundary_values(uint vers, string expected)
    {
        Assert.Equal(expected, IPodBuildIdDatabase.LookupVersion(null, vers));
    }

    [Theory]
    // Build-tag form (major byte == 0) — must NOT be decoded by the linear path
    [InlineData(0x0000B011u)]
    [InlineData(0x0000B012u)]
    [InlineData(0x0000B021u)]
    public void DecodeLinearForm_returns_null_for_build_tag_form(uint vers)
    {
        // No matching generation in the table either, so result must be null
        Assert.Null(IPodBuildIdDatabase.LookupVersion(generation: "Bogus Generation", buildId: vers));
    }

    [Theory]
    // Invalid BCD nibbles — high or low > 9
    [InlineData(0x01A18000u)]  // minor nibble 0xA
    [InlineData(0x011B8000u)]  // patch nibble 0xB
    [InlineData(0x01FA8000u)]  // both nibbles invalid
    public void DecodeLinearForm_returns_null_for_invalid_BCD(uint vers)
    {
        Assert.Null(IPodBuildIdDatabase.LookupVersion(null, vers));
    }

    [Theory]
    // Invalid release-stage byte — only 0x20/0x40/0x60/0x80 are valid
    [InlineData(0x01100000u)]  // stage = 0x00
    [InlineData(0x01101000u)]  // stage = 0x10
    [InlineData(0x011050FFu)]  // stage = 0x50
    public void DecodeLinearForm_returns_null_for_invalid_stage(uint vers)
    {
        Assert.Null(IPodBuildIdDatabase.LookupVersion(null, vers));
    }

    [Theory]
    [InlineData(0x01102000u, "1.1")]   // dev stage still decodes
    [InlineData(0x01104000u, "1.1")]   // alpha stage
    [InlineData(0x01106000u, "1.1")]   // beta stage
    [InlineData(0x01108000u, "1.1")]   // release stage
    public void DecodeLinearForm_accepts_all_release_stages(uint vers, string expected)
    {
        Assert.Equal(expected, IPodBuildIdDatabase.LookupVersion(null, vers));
    }

    // ===== Per-(generation, buildId) table lookups =====

    [Theory]
    [InlineData("Video 5G",   0x0000B011u, "1.3")]
    [InlineData("Video 5G",   0x0000B012u, "1.3")]
    [InlineData("Video 5G",   0x0000B021u, "1.3.1")]
    [InlineData("Video 5.5G", 0x0000B011u, "1.3")]
    [InlineData("Video 5.5G", 0x0000B012u, "1.3")]   // FOXPOD
    [InlineData("Video 5.5G", 0x0000B021u, "1.3.1")]
    public void Table_resolves_build_tag_form_for_Video_iPods(string generation, uint vers, string expected)
    {
        Assert.Equal(expected, IPodBuildIdDatabase.LookupVersion(generation, vers));
    }

    [Theory]
    // Visible-buildID overrides — when generation key matches, we prefer the user-facing string
    [InlineData("Photo",   0x04218000u, "1.2.1")]
    [InlineData("Mini 1G", 0x02618000u, "1.4.1")]
    [InlineData("Mini 2G", 0x02618000u, "1.4.1")]
    public void Table_overrides_algorithmic_decode_when_generation_matches(string generation, uint vers, string expected)
    {
        Assert.Equal(expected, IPodBuildIdDatabase.LookupVersion(generation, vers));
    }

    [Fact]
    public void Table_misses_fall_through_to_algorithmic_decode()
    {
        // 0x04218000 is in the table for "Photo" → "1.2.1", but not for other generations.
        // For an unrelated generation we should fall through to the algorithmic decode → "4.2.1"
        Assert.Equal("4.2.1", IPodBuildIdDatabase.LookupVersion("Classic 6G", 0x04218000u));
    }

    [Fact]
    public void Empty_or_whitespace_generation_skips_table_lookup()
    {
        Assert.Equal("2.3", IPodBuildIdDatabase.LookupVersion("", 0x02308000u));
        Assert.Equal("2.3", IPodBuildIdDatabase.LookupVersion("   ", 0x02308000u));
    }

    [Fact]
    public void Unknown_buildId_with_known_generation_returns_null()
    {
        // Build-tag form with no entry in the table for this generation → null
        Assert.Null(IPodBuildIdDatabase.LookupVersion("Video 5.5G", 0x0000B099u));
    }
}
