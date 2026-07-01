// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.ViewModels;

namespace OrgZ.Tests;

/// <summary>
/// Guards <see cref="MainWindowViewModel.ResolveDeviceMountPath"/> - the sidebar-key → device lookup
/// that sits behind every device CRUD action (Remove from iPod, podcast sync, import into iPod). It
/// regressed once: a device sub-view key ("Device:E:\:Podcast") failed the bare mount-path lookup and
/// silently returned null, so "Remove from iPod" and podcast sync no-op'd from the Podcasts/Audiobooks
/// views with no error. These lock the resolution in. No hardware - pure string matching over a fixed
/// set of mount paths.
/// </summary>
public class DeviceViewKeyResolutionTests
{
    private static readonly string[] TwoWindowsDevices = ["E:\\", "F:\\"];

    [Theory]
    [InlineData("Device:E:\\", "E:\\")]           // the device's own root node
    [InlineData("Device:E:\\:Podcast", "E:\\")]   // Podcasts sub-view - the exact shape that regressed
    [InlineData("Device:E:\\:Audiobook", "E:\\")] // Audiobooks sub-view
    [InlineData("Device:F:\\", "F:\\")]           // resolves to the right device when several are attached
    [InlineData("Device:F:\\:Podcast", "F:\\")]
    public void Device_view_keys_resolve_to_their_mount(string viewKey, string expected)
    {
        Assert.Equal(expected, MainWindowViewModel.ResolveDeviceMountPath(viewKey, TwoWindowsDevices));
    }

    [Theory]
    [InlineData("Podcasts")]           // a library view, not a device
    [InlineData("Music")]
    [InlineData("Radio")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Device:Z:\\")]         // a device that isn't connected
    [InlineData("Device:Z:\\:Podcast")]
    public void Non_device_or_unknown_keys_resolve_to_null(string? viewKey)
    {
        Assert.Null(MainWindowViewModel.ResolveDeviceMountPath(viewKey, TwoWindowsDevices));
    }

    [Fact]
    public void Longest_mount_prefix_wins_so_a_child_view_never_mis_routes()
    {
        // One mount path being a prefix of another must not misroute the deeper one's child view.
        string[] mounts = ["/media/ipod", "/media/ipod-red"];
        Assert.Equal("/media/ipod-red", MainWindowViewModel.ResolveDeviceMountPath("Device:/media/ipod-red:Podcast", mounts));
        Assert.Equal("/media/ipod", MainWindowViewModel.ResolveDeviceMountPath("Device:/media/ipod:Podcast", mounts));
    }

    [Fact]
    public void Linux_style_mount_resolves_from_a_sub_view()
    {
        string[] mounts = ["/run/media/fox/IPOD"];
        Assert.Equal("/run/media/fox/IPOD", MainWindowViewModel.ResolveDeviceMountPath("Device:/run/media/fox/IPOD:Audiobook", mounts));
    }
}
