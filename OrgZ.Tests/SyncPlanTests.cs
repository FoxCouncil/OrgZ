// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Tests;

/// <summary>
/// The per-iPod sync plan: its identity keying (the stable device id a plan is saved under) and the
/// "configured vs never-configured vs deliberately-empty" distinction the unified Sync gesture
/// keys off. The Settings-backed persistence round-trip is exercised by the live app, not CI.
/// </summary>
public class SyncPlanTests
{
    [Fact]
    public void Identity_key_prefers_the_firewire_guid()
    {
        var dev = new ConnectedDevice { MountPath = @"E:\", DeviceType = DeviceType.StockIPod, FireWireGuid = "000A2700DEADBEEF", Serial = "SER123" };
        Assert.Equal("guid:000A2700DEADBEEF", SyncPlanStore.KeyFor(dev));
    }

    [Fact]
    public void Identity_key_falls_back_to_serial_then_mount()
    {
        Assert.Equal("serial:SER123",
            SyncPlanStore.KeyFor(new ConnectedDevice { MountPath = @"E:\", DeviceType = DeviceType.StockIPod, Serial = "SER123" }));
        Assert.Equal(@"mount:E:\",
            SyncPlanStore.KeyFor(new ConnectedDevice { MountPath = @"E:\", DeviceType = DeviceType.StockIPod }));
    }

    [Fact]
    public void Two_devices_never_share_a_plan_key()
    {
        var a = new ConnectedDevice { MountPath = @"E:\", DeviceType = DeviceType.StockIPod, FireWireGuid = "AAAA" };
        var b = new ConnectedDevice { MountPath = @"E:\", DeviceType = DeviceType.StockIPod, FireWireGuid = "BBBB" };   // same reused drive letter
        Assert.NotEqual(SyncPlanStore.KeyFor(a), SyncPlanStore.KeyFor(b));
    }

    [Theory]
    [InlineData(false, false, false, 0, false)]   // saved-but-empty: a real "sync nothing"
    [InlineData(true, false, false, 0, true)]
    [InlineData(false, true, false, 0, true)]
    [InlineData(false, false, true, 0, true)]
    [InlineData(false, false, false, 1, true)]
    public void SyncsAnything_reflects_the_selection(bool pod, bool book, bool fav, int playlistCount, bool expected)
    {
        var plan = new SyncPlan
        {
            Podcasts = pod,
            Audiobooks = book,
            Favorites = fav,
            PlaylistIds = Enumerable.Range(1, playlistCount).ToList(),
        };
        Assert.Equal(expected, plan.SyncsAnything);
    }
}
