// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Tests;

public class IPodModelDatabaseTests
{
    // ===== LookupBySerial - last-3-char suffix maps to 4-char model code, then to IPodInfo =====

    [Theory]
    // Sampled across all generations represented in the libgpod table
    // Suffix → expected (Generation, Color, CapacityGb)
    [InlineData("ABCDEFLG6", "1G",          "",         5)]    // LG6 → 8541 → iPod 1G
    [InlineData("0000ML1",   "1G",          "",        10)]    // ML1 → 8709 → iPod 1G 10GB
    [InlineData("XXXXMMB",   "2G",          "",        10)]    // MMB → 8737 → iPod 2G
    [InlineData("YYYYNRH",   "3G",          "",        10)]    // NRH → 8976 → iPod 3G
    [InlineData("9999PS9",   "4G",          "",        20)]    // PS9 → 9282 → iPod 4G
    [InlineData("XYZTDU",    "Photo",       "",        20)]    // TDU → A079 → Photo
    [InlineData("8K7285PFW", "Mini 1G",     "Silver",   4)]    // PFW → 9160
    [InlineData("8K7285QKL", "Mini 1G",     "Blue",     4)]    // QKL → 9436
    [InlineData("0000UNA",   "Nano 1G",     "White",    1)]    // UNA → A350
    [InlineData("8K72WEC",   "Video 5G",    "White",   30)]    // WEC → A002
    [InlineData("8K7285TXL", "Video 5G",    "Black",   60)]    // TXL → A147
    // FOXPOD case (real device): suffix V9P → A448 → Video 5.5G White 80GB
    [InlineData("8K7285V9P", "Video 5.5G",  "White",   80)]
    [InlineData("8K72V9M",   "Video 5.5G",  "Black",   30)]    // V9M → A446
    [InlineData("00000Y5N",  "Classic 6G",  "Silver",  80)]    // Y5N → B029
    [InlineData("99999ZS",   "Classic 7G",  "Silver", 160)]    // 9ZS → C293
    [InlineData("AAAA37P",   "Nano 4G",     "Green",    4)]    // 37P → B663
    [InlineData("BBBBW4N",   "Touch 1G",    "Silver",   8)]    // W4N → A623
    [InlineData("XYZRS9",    "Shuffle 1G",  "",         0)]    // RS9 → 9724 (512MB shows as 0GB)
    [InlineData("ZZZCMJ",    "Shuffle 4G",  "Silver",   2)]    // CMJ → C584
    public void LookupBySerial_known_suffixes(string serial, string expectedGen, string expectedColor, int expectedGb)
    {
        var info = IPodModelDatabase.LookupBySerial(serial);
        Assert.NotNull(info);
        Assert.Equal(expectedGen, info!.Generation);
        Assert.Equal(expectedColor, info.Color);
        Assert.Equal(expectedGb, info.CapacityGb);
    }

    [Theory]
    // CONFORMANCE: one real serial suffix per IN-SCOPE generation (the /goal scope - binary
    // iTunesDB iPods: no-checksum 1G-4G/Mini/Photo/Video/Nano 1G-2G, hash58 Nano 3G-4G/Classic
    // 6G-7G, iTunesSD Shuffle 1G-4G; NOT Nano 5G+ SQLite). Suffixes are from libgpod's verified
    // serial_to_model_mapping. This is the "exact identity for EVERY in-scope generation" proof.
    [InlineData("8L645KABLG6",  "1G",           "",       5)]   // LG6 → 8541
    [InlineData("8L645KABMMB",  "2G",           "",      10)]   // MMB → 8737
    [InlineData("8L645KABNRH",  "3G",           "",      10)]   // NRH → 8976
    [InlineData("8L645KABPS9",  "4G",           "",      20)]   // PS9 → 9282
    [InlineData("8L645KABPFW",  "Mini 1G",      "Silver", 4)]   // PFW → 9160
    [InlineData("8L645KABS41",  "Mini 2G",      "Silver", 4)]   // S41 → 9800
    [InlineData("8L645KABTDU",  "Photo",        "",      20)]   // TDU → A079
    [InlineData("8L645KABWEC",  "Video 5G",     "White", 30)]   // WEC → A002
    [InlineData("8L645KABV9M",  "Video 5.5G",   "Black", 30)]   // V9M → A446 (BriPod, real serial)
    [InlineData("8L645KABUNA",  "Nano 1G",      "White",  1)]   // UNA → A350
    [InlineData("8L645KABVQ5",  "Nano 2G",      "Silver", 2)]   // VQ5 → A477
    [InlineData("8L645KABY0P",  "Nano 3G",      "Silver", 4)]   // Y0P → A978
    [InlineData("8L645KAB37P",  "Nano 4G",      "Green",  4)]   // 37P → B663
    [InlineData("8L645KABY5N",  "Classic 6G",   "Silver",80)]   // Y5N → B029
    [InlineData("8L645KAB2C5",  "Classic 6.5G", "Silver",120)]  // 2C5 → B562
    [InlineData("8L645KAB9ZS",  "Classic 7G",   "Silver",160)]  // 9ZS → C293
    [InlineData("8L645KABRS9",  "Shuffle 1G",   "",       0)]   // RS9 → 9724 (512MB → 0GB)
    [InlineData("8L645KABVTE",  "Shuffle 2G",   "Silver", 1)]   // VTE → A546
    [InlineData("8L645KABA1S",  "Shuffle 3G",   "Silver", 2)]   // A1S → C306
    [InlineData("8L645KABCMJ",  "Shuffle 4G",   "Silver", 2)]   // CMJ → C584
    public void LookupBySerial_covers_every_in_scope_generation(string serial, string gen, string color, int gb)
    {
        var info = IPodModelDatabase.LookupBySerial(serial);
        Assert.NotNull(info);
        Assert.Equal(gen, info!.Generation);
        Assert.Equal(color, info.Color);
        Assert.Equal(gb, info.CapacityGb);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("AB")]      // too short
    [InlineData("XX")]      // too short
    public void LookupBySerial_empty_or_short_returns_null(string? serial)
    {
        Assert.Null(IPodModelDatabase.LookupBySerial(serial!));
    }

    [Fact]
    public void LookupBySerial_unknown_suffix_returns_null()
    {
        Assert.Null(IPodModelDatabase.LookupBySerial("8K72ZZZ"));
        Assert.Null(IPodModelDatabase.LookupBySerial("AAAA000"));
    }

    [Fact]
    public void LookupBySerial_is_case_insensitive()
    {
        var upper = IPodModelDatabase.LookupBySerial("8K7285v9p");
        var lower = IPodModelDatabase.LookupBySerial("8k7285v9p");
        Assert.NotNull(upper);
        Assert.NotNull(lower);
        Assert.Equal(upper!.Generation, lower!.Generation);
    }

    // ===== LookupByModelNumber - Apple's "MA446LL/A" form, with strip-leading-letter + slash region =====

    [Theory]
    // Apple's model numbers always have a leading letter prefix (M, S, etc.) - libgpod's
    // canonical input form. The strip-letter step is unconditional, so bare "A446" would
    // become "446" and miss the table; never pass codes without a prefix.
    [InlineData("MA446LL/A", "Video 5.5G", "Black",  30)]   // strip M, drop /A → A446
    [InlineData("MA448LL/A", "Video 5.5G", "White",  80)]   // strip M → A448
    [InlineData("MB147LL/A", "Classic 6G", "Black",  80)]   // strip M → B147
    [InlineData("MB029",     "Classic 6G", "Silver", 80)]   // strip M, no slash
    [InlineData("MA446",     "Video 5.5G", "Black",  30)]   // strip M, no slash
    [InlineData("ma446ll/a", "Video 5.5G", "Black",  30)]   // lowercase passes (case-insensitive)
    public void LookupByModelNumber_strips_letter_and_slash(string modelNum, string expectedGen, string expectedColor, int expectedGb)
    {
        var info = IPodModelDatabase.LookupByModelNumber(modelNum);
        Assert.NotNull(info);
        Assert.Equal(expectedGen, info!.Generation);
        Assert.Equal(expectedColor, info.Color);
        Assert.Equal(expectedGb, info.CapacityGb);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void LookupByModelNumber_empty_returns_null(string? modelNum)
    {
        Assert.Null(IPodModelDatabase.LookupByModelNumber(modelNum!));
    }

    [Fact]
    public void LookupByModelNumber_unknown_returns_null()
    {
        Assert.Null(IPodModelDatabase.LookupByModelNumber("MZ999LL/A"));
    }

    // ===== IPodInfo.DisplayName - formatting =====

    [Theory]
    [InlineData("Video 5.5G", "Black", 30, "iPod Video 5.5G 30GB Black")]
    [InlineData("Classic 7G", "Silver", 160, "iPod Classic 7G 160GB Silver")]
    [InlineData("3G",         "",       10, "iPod 3G 10GB")]                      // empty color collapses
    [InlineData("Shuffle 1G", "",        0, "iPod Shuffle 1G")]                   // 0GB suppresses capacity tag
    public void IPodInfo_DisplayName_formats(string gen, string color, int gb, string expected)
    {
        var info = new IPodModelDatabase.IPodInfo(gen, color, gb);
        Assert.Equal(expected, info.DisplayName);
    }

    // ===== IPodInfo.DisplayNameForActualCapacity - modded-drive detection =====

    [Fact]
    public void DisplayNameForActualCapacity_factory_size_shows_capacity_tag()
    {
        var info = new IPodModelDatabase.IPodInfo("Video 5.5G", "White", 80);
        // 80GB Apple decimal = 80 * 10^9 bytes
        var actual = info.DisplayNameForActualCapacity(80_000_000_000L);
        Assert.Equal("iPod Video 5.5G White (80GB)", actual);
    }

    [Fact]
    public void DisplayNameForActualCapacity_within_tolerance_not_modded()
    {
        var info = new IPodModelDatabase.IPodInfo("Video 5.5G", "White", 80);
        // 95GB is < 80 * 1.2 = 96GB - should NOT trigger Modded
        var actual = info.DisplayNameForActualCapacity(95_000_000_000L);
        Assert.Equal("iPod Video 5.5G White (80GB)", actual);
    }

    [Fact]
    public void DisplayNameForActualCapacity_far_above_factory_marks_modded()
    {
        var info = new IPodModelDatabase.IPodInfo("Video 5.5G", "White", 80);
        // 512GB CF swap - well above 80 * 1.2
        var actual = info.DisplayNameForActualCapacity(512_000_000_000L);
        Assert.Equal("iPod Video 5.5G White (Modded)", actual);
    }

    [Theory]
    [InlineData(0)]              // unknown actual capacity → fall back to plain DisplayName
    [InlineData(-1)]             // negative bogus value → fall back
    public void DisplayNameForActualCapacity_invalid_actual_falls_back(long actual)
    {
        var info = new IPodModelDatabase.IPodInfo("Video 5.5G", "White", 80);
        Assert.Equal(info.DisplayName, info.DisplayNameForActualCapacity(actual));
    }

    [Fact]
    public void DisplayNameForActualCapacity_zero_factory_capacity_falls_back()
    {
        // Shuffle 1G 512MB has CapacityGb=0; mod check shouldn't apply
        var info = new IPodModelDatabase.IPodInfo("Shuffle 1G", "", 0);
        Assert.Equal(info.DisplayName, info.DisplayNameForActualCapacity(8_000_000_000L));
    }
}
