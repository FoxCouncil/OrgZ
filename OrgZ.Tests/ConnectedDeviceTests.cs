// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Tests;

public class ConnectedDeviceTests
{
    private static ConnectedDevice MakeDevice(
        string mountPath = "L:\\",
        DeviceType type = DeviceType.StockIPod,
        string? name = null)
    {
        return new ConnectedDevice
        {
            MountPath = mountPath,
            DeviceType = type,
            Name = name ?? "FOXPOD",
        };
    }

    // ===== ModelDisplay / SerialDisplay / FireWireGuidDisplay — em-dash fallback =====

    [Fact]
    public void ModelDisplay_returns_emdash_when_empty()
    {
        Assert.Equal("\u2014", MakeDevice().ModelDisplay);
    }

    [Fact]
    public void ModelDisplay_returns_value_when_set()
    {
        var d = MakeDevice();
        d.Model = "iPod Video 5.5G White (80GB)";
        Assert.Equal("iPod Video 5.5G White (80GB)", d.ModelDisplay);
    }

    [Fact]
    public void SerialDisplay_returns_emdash_when_empty()
    {
        Assert.Equal("\u2014", MakeDevice().SerialDisplay);
    }

    [Fact]
    public void FireWireGuidDisplay_returns_emdash_when_empty()
    {
        Assert.Equal("\u2014", MakeDevice().FireWireGuidDisplay);
    }

    [Fact]
    public void HasFireWireGuid_tracks_value_set()
    {
        var d = MakeDevice();
        Assert.False(d.HasFireWireGuid);
        d.FireWireGuid = "000A2700153A9E6B";
        Assert.True(d.HasFireWireGuid);
    }

    // ===== ModelLabelDisplay — click-toggle between Model and HardwareModel =====

    [Fact]
    public void ModelLabelDisplay_shows_Model_by_default()
    {
        var d = MakeDevice();
        d.Model = "iPod Video 5.5G";
        d.HardwareModel = "iFlash";
        Assert.Equal("iPod Video 5.5G", d.ModelLabelDisplay);
    }

    [Fact]
    public void ModelLabelDisplay_shows_HardwareModel_when_toggled()
    {
        var d = MakeDevice();
        d.Model = "iPod Video 5.5G";
        d.HardwareModel = "iFlash";
        d.ShowHardwareModel = true;
        Assert.Equal("iFlash", d.ModelLabelDisplay);
    }

    [Fact]
    public void ModelLabelDisplay_falls_back_when_HardwareModel_missing_even_if_toggled()
    {
        var d = MakeDevice();
        d.Model = "iPod Video 5.5G";
        d.ShowHardwareModel = true;
        // No HardwareModel set, should still show Model
        Assert.Equal("iPod Video 5.5G", d.ModelLabelDisplay);
    }

    [Fact]
    public void HasHardwareModel_tracks_value()
    {
        var d = MakeDevice();
        Assert.False(d.HasHardwareModel);
        d.HardwareModel = "iFlash";
        Assert.True(d.HasHardwareModel);
    }

    // ===== FormatDisplay — per-platform tagging by filesystem =====

    [Theory]
    [InlineData("FAT",     "Windows (FAT)")]
    [InlineData("FAT32",   "Windows (FAT32)")]
    [InlineData("exFAT",   "Windows (exFAT)")]
    [InlineData("NTFS",    "Windows (NTFS)")]
    [InlineData("HFS",     "Mac (HFS)")]
    [InlineData("HFS+",    "Mac (HFS+)")]
    [InlineData("hfsplus", "Mac (hfsplus)")]
    [InlineData("APFS",    "Mac (APFS)")]
    [InlineData("ext2",    "Linux (ext2)")]
    [InlineData("ext3",    "Linux (ext3)")]
    [InlineData("ext4",    "Linux (ext4)")]
    [InlineData("btrfs",   "Linux (btrfs)")]
    [InlineData("xfs",     "Linux (xfs)")]
    [InlineData("f2fs",    "Linux (f2fs)")]
    public void FormatDisplay_tags_filesystem_by_platform(string format, string expected)
    {
        var d = MakeDevice();
        d.Format = format;
        Assert.Equal(expected, d.FormatDisplay);
    }

    [Fact]
    public void FormatDisplay_returns_emdash_when_empty()
    {
        Assert.Equal("\u2014", MakeDevice().FormatDisplay);
    }

    [Fact]
    public void FormatDisplay_returns_raw_when_unknown()
    {
        var d = MakeDevice();
        d.Format = "ZFS";   // not in the per-platform map
        Assert.Equal("ZFS", d.FormatDisplay);
    }

    // ===== FirmwareVersionDisplay — Apple + Rockbox concat =====

    [Fact]
    public void FirmwareVersionDisplay_emdash_when_neither_set()
    {
        Assert.Equal("\u2014", MakeDevice().FirmwareVersionDisplay);
    }

    [Fact]
    public void FirmwareVersionDisplay_apple_only()
    {
        var d = MakeDevice();
        d.AppleFirmwareVersion = "iPod OS 1.3";
        Assert.Equal("iPod OS 1.3", d.FirmwareVersionDisplay);
    }

    [Fact]
    public void FirmwareVersionDisplay_rockbox_only()
    {
        var d = MakeDevice();
        d.FirmwareVersion = "Rockbox 3.15";
        Assert.Equal("Rockbox 3.15", d.FirmwareVersionDisplay);
    }

    [Fact]
    public void FirmwareVersionDisplay_concatenates_both_with_slash()
    {
        var d = MakeDevice();
        d.AppleFirmwareVersion = "iPod OS 1.3";
        d.FirmwareVersion = "Rockbox 3.15";
        Assert.Equal("iPod OS 1.3 / Rockbox 3.15", d.FirmwareVersionDisplay);
    }

    // ===== SidebarLabel — per-platform formatting =====
    // The runtime check inside SidebarLabel keys off OperatingSystem.Is* so we can only
    // assert the branch matching the host. Tests use OS-conditional skip.

    [Fact]
    public void SidebarLabel_includes_drive_letter_on_Windows()
    {
        if (!OperatingSystem.IsWindows()) return;

        var d = MakeDevice("L:\\", name: "FOXPOD");
        Assert.Equal("FOXPOD (L:)", d.SidebarLabel);
    }

    [Fact]
    public void SidebarLabel_falls_back_to_name_when_mount_empty_on_Windows()
    {
        if (!OperatingSystem.IsWindows()) return;

        var d = MakeDevice("\\", name: "FOXPOD");
        Assert.Equal("FOXPOD", d.SidebarLabel);
    }

    [Fact]
    public void SidebarLabel_strips_dev_prefix_on_Linux()
    {
        if (!OperatingSystem.IsLinux()) return;

        var d = MakeDevice("/dev/sdb1", name: "FOXPOD");
        Assert.Equal("FOXPOD (sdb1)", d.SidebarLabel);
    }

    [Fact]
    public void SidebarLabel_just_name_on_macOS()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var d = MakeDevice("/Volumes/FOXPOD", name: "FOXPOD");
        Assert.Equal("FOXPOD", d.SidebarLabel);
    }

    // ===== DisplayName — info bar header =====

    [Fact]
    public void DisplayName_just_name_when_model_empty_or_matches_name()
    {
        var d = MakeDevice(name: "FOXPOD");
        Assert.Equal("FOXPOD", d.DisplayName);

        d.Model = "FOXPOD";   // Model equal to Name → don't duplicate
        Assert.Equal("FOXPOD", d.DisplayName);
    }

    [Fact]
    public void DisplayName_appends_model_when_distinct_from_name()
    {
        var d = MakeDevice(name: "FOXPOD");
        d.Model = "iPod Video 5.5G";
        Assert.Equal("FOXPOD (iPod Video 5.5G)", d.DisplayName);
    }

    // ===== Capacity bar math — AudioPercent / OtherPercent / FreePercent =====

    [Fact]
    public void Capacity_percentages_zero_when_total_zero()
    {
        var d = MakeDevice();
        Assert.Equal(0, d.AudioPercent);
        Assert.Equal(0, d.OtherPercent);
        Assert.Equal(0, d.FreePercent);
    }

    [Fact]
    public void Capacity_percentages_compute_against_total()
    {
        var d = MakeDevice();
        d.TotalSpace = 100_000_000_000L;   // 100 GB
        d.FreeSpace  =  20_000_000_000L;   // 20 GB free
        d.AudioSpace =  60_000_000_000L;   // 60 GB audio

        Assert.Equal(60.0, d.AudioPercent, precision: 2);   // 60%
        Assert.Equal(20.0, d.FreePercent,  precision: 2);   // 20%
        Assert.Equal(20.0, d.OtherPercent, precision: 2);   // remainder = 100 - 60 - 20 = 20%
    }

    [Fact]
    public void OtherSpace_clamped_to_zero_when_components_overshoot()
    {
        // Edge case: AudioSpace > Total - Free (shouldn't normally happen but the math
        // uses Math.Max(0, ...) — verify we don't go negative)
        var d = MakeDevice();
        d.TotalSpace = 10_000_000_000L;
        d.FreeSpace  =  5_000_000_000L;
        d.AudioSpace = 20_000_000_000L;   // overshoot

        Assert.Equal(0, d.OtherSpace);
        Assert.Equal(0.0, d.OtherPercent);
    }

    // ===== FormatBytes via *Label properties =====

    [Theory]
    [InlineData(0L,                "0 B")]
    [InlineData(512L,              "512 B")]
    [InlineData(2048L,             "2 KB")]
    [InlineData(1_500_000L,        "1 MB")]
    [InlineData(80_000_000_000L,   "74.51 GB")]
    [InlineData(1_500_000_000_000L,"1.36 TB")]
    public void FormatBytes_via_TotalSpaceLabel(long bytes, string expected)
    {
        var d = MakeDevice();
        d.TotalSpace = bytes;
        Assert.Equal(expected, d.TotalSpaceLabel);
    }

    // ===== HasGenerationImage / GenerationImage slug detection =====

    [Theory]
    [InlineData("Video 5.5G",  true)]
    [InlineData("Video 5G",    true)]
    [InlineData("Photo",       true)]
    [InlineData("Mini 1G",     true)]
    [InlineData("Classic 6G",  true)]
    [InlineData("Nano 7G",     true)]
    [InlineData("Shuffle 4G",  true)]
    public void HasGenerationImage_true_for_known_generations(string generation, bool expected)
    {
        var d = MakeDevice();
        d.IpodGeneration = generation;
        Assert.Equal(expected, d.HasGenerationImage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bogus 99G")]
    [InlineData("Touch 1G")]    // not in the KnownGenerationImages set yet
    public void HasGenerationImage_false_for_unknown_or_empty(string? generation)
    {
        var d = MakeDevice();
        d.IpodGeneration = generation;
        Assert.False(d.HasGenerationImage);
    }

    // ===== IsReadOnly — by device type =====

    [Theory]
    [InlineData(DeviceType.StockIPod,    true)]
    [InlineData(DeviceType.RockboxIPod,  false)]
    [InlineData(DeviceType.RockboxOther, false)]
    [InlineData(DeviceType.GenericPlayer, false)]
    public void IsReadOnly_true_only_for_StockIPod(DeviceType type, bool expected)
    {
        var d = MakeDevice(type: type);
        Assert.Equal(expected, d.IsReadOnly);
    }

    // ===== Icon — by device type =====

    [Theory]
    [InlineData(DeviceType.StockIPod,    "fa-solid fa-music")]
    [InlineData(DeviceType.RockboxIPod,  "fa-solid fa-music")]
    [InlineData(DeviceType.RockboxOther, "fa-solid fa-headphones")]
    [InlineData(DeviceType.GenericPlayer, "fa-solid fa-hard-drive")]
    [InlineData(DeviceType.Unknown,      "fa-solid fa-hard-drive")]
    public void Icon_maps_by_device_type(DeviceType type, string expected)
    {
        var d = MakeDevice(type: type);
        Assert.Equal(expected, d.Icon);
    }

    // ===== SubLabel — interpunct-joined parts =====

    [Fact]
    public void SubLabel_empty_when_no_metadata()
    {
        Assert.Equal("", MakeDevice().SubLabel);
    }

    [Fact]
    public void SubLabel_just_model_when_only_model_set()
    {
        var d = MakeDevice();
        d.Model = "iPod Video 5.5G";
        Assert.Equal("iPod Video 5.5G", d.SubLabel);
    }

    [Fact]
    public void SubLabel_joins_all_parts_with_interpunct()
    {
        var d = MakeDevice();
        d.Model = "iPod Video 5.5G";
        d.FirmwareVersion = "Rockbox 3.15";
        d.Serial = "8K7285F4V9P";

        Assert.Equal("iPod Video 5.5G  \u00B7  Rockbox 3.15  \u00B7  S/N: 8K7285F4V9P", d.SubLabel);
    }

    [Fact]
    public void SubLabel_skips_blank_fields()
    {
        var d = MakeDevice();
        d.Model = "";
        d.FirmwareVersion = "Rockbox 3.15";
        d.Serial = "8K7285F4V9P";

        Assert.Equal("Rockbox 3.15  \u00B7  S/N: 8K7285F4V9P", d.SubLabel);
    }

    // ===== FormatBytes (via *Label properties) — additional coverage =====

    [Theory]
    [InlineData(-1L,  "0 B")]            // negative → 0 B branch
    [InlineData(1L,   "1 B")]            // 1 byte: under-KB branch
    [InlineData(999L, "999 B")]
    [InlineData(1024L,           "1 KB")]
    [InlineData(1024L * 1024,    "1 MB")]
    public void FormatBytes_via_FreeSpaceLabel_covers_below_GB_branches(long bytes, string expected)
    {
        // < GB uses the {size:0} format (no decimals)
        var d = MakeDevice();
        d.FreeSpace = bytes;
        Assert.Equal(expected, d.FreeSpaceLabel);
    }

    [Fact]
    public void AudioSpaceLabel_and_OtherSpaceLabel_format_via_same_helper()
    {
        var d = MakeDevice();
        d.TotalSpace = 100_000_000_000L;   // 100 GB
        d.AudioSpace = 60_000_000_000L;
        d.FreeSpace  = 20_000_000_000L;

        Assert.Equal("55.88 GB", d.AudioSpaceLabel);
        Assert.Equal("18.63 GB", d.OtherSpaceLabel);    // 100 - 60 - 20 = 20 GB ≈ 18.63 GiB
    }

    // ===== Capacity bar percentages — boundary coverage =====

    [Fact]
    public void Capacity_percentages_exactly_100_when_fully_filled()
    {
        var d = MakeDevice();
        d.TotalSpace = 100;
        d.AudioSpace = 100;
        d.FreeSpace  = 0;

        Assert.Equal(100.0, d.AudioPercent);
        Assert.Equal(0.0,   d.FreePercent);
        Assert.Equal(0.0,   d.OtherPercent);
    }

    // ===== HasGenerationImage — slug normalization for periods and case =====

    [Theory]
    [InlineData("VIDEO 5.5G",  true)]    // case-insensitive normalization
    [InlineData("video 5.5g",  true)]
    [InlineData("Video  5.5G", false)]   // double space breaks the slug
    public void HasGenerationImage_handles_case_but_not_extra_spaces(string generation, bool expected)
    {
        var d = MakeDevice();
        d.IpodGeneration = generation;
        Assert.Equal(expected, d.HasGenerationImage);
    }

    // ===== Persistence date fields — round-trip through plain setters =====

    [Fact]
    public void Date_fields_round_trip_via_record_persistence()
    {
        // ConnectedDevice doesn't have FirstSeen/LastSeen — those live on OrgZDeviceRecord.
        // But the Format/Source/Serial setters should round-trip via the [ObservableProperty]
        // generator, so this exercises them.
        var d = MakeDevice();
        d.Format = "FAT32";
        d.Serial = "8K7285F4V9P";
        d.AppleModelNumber = "MA448LL/A";

        Assert.Equal("FAT32", d.Format);
        Assert.Equal("8K7285F4V9P", d.Serial);
        Assert.Equal("MA448LL/A", d.AppleModelNumber);
    }
}
