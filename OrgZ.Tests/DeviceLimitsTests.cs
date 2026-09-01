// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Services;
using OrgZ.Services.DeviceLimits;

namespace OrgZ.Tests;

/// <summary>
/// The limits catalog: which numbers apply to which kind of device, and how two providers speaking
/// to the same limit are reconciled. The numbers themselves are pinned so a change to one is a
/// deliberate, reviewed edit rather than a drift.
/// </summary>
public class DeviceLimitsTests
{
    private static ConnectedDevice Device(DeviceType type, string? generation = null) => new()
    {
        MountPath = @"L:\",
        DeviceType = type,
        Name = "DEV",
        IpodGeneration = generation,
    };

    [Fact]
    public void An_unknown_device_has_no_limits()
    {
        Assert.Empty(DeviceLimitCatalog.For(Device(DeviceType.Unknown)));
    }

    [Theory]
    [InlineData(DeviceType.StockIPod)]
    [InlineData(DeviceType.RockboxIPod)]
    [InlineData(DeviceType.RockboxOther)]
    [InlineData(DeviceType.GenericPlayer)]
    public void Every_player_gets_the_FAT32_limits(DeviceType type)
    {
        var limits = DeviceLimitCatalog.For(Device(type, "Video 5.5G"));

        Assert.Equal(4L * 1024 * 1024 * 1024 - 1, limits.Find(LimitIds.FileMaxBytes)!.Value);
        Assert.Equal(65_534, limits.Find(LimitIds.DirectoryMaxEntries)!.Value);
        Assert.Equal(2L * 1024 * 1024 * 1024 * 1024, limits.Find(LimitIds.VolumeMaxBytes)!.Value);
        Assert.All(new[] { LimitIds.FileMaxBytes, LimitIds.DirectoryMaxEntries, LimitIds.VolumeMaxBytes },
            id => Assert.Equal(LimitSeverity.Hard, limits.Find(id)!.Severity));
    }

    [Fact]
    public void A_Classic_gets_the_RAM_bound_database_and_track_budgets()
    {
        var limits = DeviceLimitCatalog.For(Device(DeviceType.StockIPod, "Classic 7G"));

        var db = limits.Find(LimitIds.DatabaseMaxBytes)!;
        Assert.Equal(50L * 1024 * 1024, db.Value);
        Assert.Equal(LimitSeverity.Soft, db.Severity);
        Assert.Contains("32 MB", db.What);   // the smaller-RAM models are named so a reader knows to halve it

        Assert.Equal(50_000, limits.Find(LimitIds.TracksMax)!.Value);
        Assert.Equal(IPodTrackImporter.MusicFolderCount, limits.Find(LimitIds.MusicFolders)!.Value);
        Assert.Equal(IPodArtworkFiles.DefaultMaxFileBytes, limits.Find(LimitIds.ArtworkFileMaxBytes)!.Value);
    }

    [Theory]
    [InlineData("Nano 3G")]
    [InlineData("Video 5.5G")]
    [InlineData("Nano 5G")]
    public void Non_Classic_iPods_get_no_database_budget_but_still_the_artwork_ceiling(string generation)
    {
        var limits = DeviceLimitCatalog.For(Device(DeviceType.StockIPod, generation));

        Assert.Null(limits.Find(LimitIds.DatabaseMaxBytes));
        Assert.Null(limits.Find(LimitIds.TracksMax));
        Assert.NotNull(limits.Find(LimitIds.ArtworkFileMaxBytes));
        Assert.NotNull(limits.Find(LimitIds.MusicFolders));
    }

    [Fact]
    public void Rockbox_players_get_the_browser_limit_and_not_the_iPod_ones()
    {
        var limits = DeviceLimitCatalog.For(Device(DeviceType.RockboxIPod));

        Assert.Equal(400, limits.Find(LimitIds.BrowserMaxEntries)!.Value);
        Assert.Null(limits.Find(LimitIds.MusicFolders));
        Assert.Null(limits.Find(LimitIds.DatabaseMaxBytes));
    }

    [Fact]
    public void The_artwork_rollover_point_sits_below_the_FAT32_ceiling()
    {
        // Rolling over exactly at the ceiling would let the last append cross it.
        Assert.True(IPodArtworkFiles.DefaultMaxFileBytes < Fat32LimitProvider.MaxFileBytes);
        Assert.True(Fat32LimitProvider.MaxFileBytes - IPodArtworkFiles.DefaultMaxFileBytes >= 320 * 320 * 2);
    }

    [Fact]
    public void Merging_keeps_the_strictest_value_and_Hard_beats_Soft()
    {
        var merged = DeviceLimitCatalog.Merge(
        [
            new DeviceLimit("x", LimitScope.File, 100, LimitSeverity.Soft, "loose", "a"),
            new DeviceLimit("x", LimitScope.File, 60, LimitSeverity.Hard, "tight", "b"),
            new DeviceLimit("x", LimitScope.File, 80, LimitSeverity.Soft, "middle", "c"),
            new DeviceLimit("y", LimitScope.Volume, 5, LimitSeverity.Soft, "only", "d"),
        ]);

        var x = Assert.Single(merged, l => l.Id == "x");
        Assert.Equal(60, x.Value);
        Assert.Equal(LimitSeverity.Hard, x.Severity);
        Assert.Equal("tight", x.What);

        var y = Assert.Single(merged, l => l.Id == "y");
        Assert.Equal(LimitSeverity.Soft, y.Severity);
    }

    [Fact]
    public void Merging_a_Hard_limit_with_a_stricter_Soft_one_stays_Hard()
    {
        // The strictest number wins, but the strictest severity travels with it - a device is only
        // as forgiving as its least forgiving layer.
        var merged = DeviceLimitCatalog.Merge(
        [
            new DeviceLimit("x", LimitScope.File, 100, LimitSeverity.Hard, "hard", "a"),
            new DeviceLimit("x", LimitScope.File, 50, LimitSeverity.Soft, "soft", "b"),
        ]);

        var x = Assert.Single(merged);
        Assert.Equal(50, x.Value);
        Assert.Equal(LimitSeverity.Hard, x.Severity);
    }

    [Fact]
    public void Every_limit_names_its_source()
    {
        foreach (var type in new[] { DeviceType.StockIPod, DeviceType.RockboxIPod, DeviceType.GenericPlayer })
        {
            foreach (var limit in DeviceLimitCatalog.For(Device(type, "Classic 7G")))
            {
                Assert.False(string.IsNullOrWhiteSpace(limit.Source), $"{limit.Id} has no source");
                Assert.False(string.IsNullOrWhiteSpace(limit.What), $"{limit.Id} has no description");
                Assert.True(limit.Value > 0, $"{limit.Id} has no value");
            }
        }
    }
}
