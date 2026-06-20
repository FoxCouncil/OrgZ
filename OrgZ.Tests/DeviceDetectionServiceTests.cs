// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Tests;

public class DeviceDetectionServiceTests
{
    private static ConnectedDevice Device(
        string mount,
        string? guid = null,
        string? serial = null,
        DeviceType type = DeviceType.StockIPod,
        string name = "iPod")
        => new()
        {
            MountPath = mount,
            DeviceType = type,
            Name = name,
            FireWireGuid = guid,
            Serial = serial,
        };

    // ===== IsSameConnectedDevice - identity comparison =====

    [Fact]
    public void IsSameConnectedDevice_Same_FireWireGuid_Is_Same()
    {
        var a = Device("E:\\", guid: "000A2700AAAA0001");
        var b = Device("E:\\", guid: "000a2700aaaa0001"); // case-insensitive
        Assert.True(DeviceDetectionService.IsSameConnectedDevice(a, b));
    }

    [Fact]
    public void IsSameConnectedDevice_Different_FireWireGuid_Is_Different()
    {
        var a = Device("E:\\", guid: "000A2700AAAA0001");
        var b = Device("E:\\", guid: "000A2700BBBB0002");
        Assert.False(DeviceDetectionService.IsSameConnectedDevice(a, b));
    }

    [Fact]
    public void IsSameConnectedDevice_Falls_Back_To_Serial_When_No_Guid()
    {
        Assert.True(DeviceDetectionService.IsSameConnectedDevice(
            Device("E:\\", serial: "ABC123"), Device("E:\\", serial: "abc123")));
        Assert.False(DeviceDetectionService.IsSameConnectedDevice(
            Device("E:\\", serial: "ABC123"), Device("E:\\", serial: "XYZ789")));
    }

    [Fact]
    public void IsSameConnectedDevice_Guid_Takes_Priority_Over_Serial()
    {
        // Different GUIDs but a coincidentally-shared serial - GUID is authoritative.
        var a = Device("E:\\", guid: "000A2700AAAA0001", serial: "SHARED");
        var b = Device("E:\\", guid: "000A2700BBBB0002", serial: "SHARED");
        Assert.False(DeviceDetectionService.IsSameConnectedDevice(a, b));
    }

    [Fact]
    public void IsSameConnectedDevice_Different_DeviceType_Without_Identity_Is_Swap()
    {
        var a = Device("E:\\", type: DeviceType.StockIPod);
        var b = Device("E:\\", type: DeviceType.RockboxOther);
        Assert.False(DeviceDetectionService.IsSameConnectedDevice(a, b));
    }

    [Fact]
    public void IsSameConnectedDevice_No_Identity_Same_Type_Assumed_Same()
    {
        var a = Device("E:\\", type: DeviceType.RockboxOther);
        var b = Device("E:\\", type: DeviceType.RockboxOther);
        Assert.True(DeviceDetectionService.IsSameConnectedDevice(a, b));
    }

    // ===== RegisterIdentifiedDevice - the same-drive-letter hot-swap fix =====

    [Fact]
    public void RegisterIdentifiedDevice_First_Device_Raises_Connected()
    {
        using var svc = new DeviceDetectionService();
        var events = new List<string>();
        svc.DeviceConnected += d => events.Add($"connect:{d.MountPath}:{d.FireWireGuid}");
        svc.DeviceDisconnected += m => events.Add($"disconnect:{m}");

        svc.RegisterIdentifiedDevice(Device("E:\\", guid: "GUID-A"));

        Assert.Equal(["connect:E:\\:GUID-A"], events);
    }

    [Fact]
    public void RegisterIdentifiedDevice_Same_Device_Rearrival_Is_Ignored()
    {
        using var svc = new DeviceDetectionService();
        svc.RegisterIdentifiedDevice(Device("E:\\", guid: "GUID-A"));

        var events = new List<string>();
        svc.DeviceConnected += _ => events.Add("connect");
        svc.DeviceDisconnected += _ => events.Add("disconnect");

        // Duplicate WMI arrival for the same GUID + mount - no events.
        svc.RegisterIdentifiedDevice(Device("E:\\", guid: "GUID-A"));

        Assert.Empty(events);
    }

    [Fact]
    public void RegisterIdentifiedDevice_Different_Device_Same_Mount_Swaps_In_Order()
    {
        // The bug: a second iPod at the same drive letter (after a missed removal event)
        // must replace the first, not be dropped as a duplicate - otherwise the app keeps
        // showing the first device's library.
        using var svc = new DeviceDetectionService();
        svc.RegisterIdentifiedDevice(Device("E:\\", guid: "GUID-A", name: "iPod A"));

        var events = new List<string>();
        svc.DeviceConnected += d => events.Add($"connect:{d.FireWireGuid}");
        svc.DeviceDisconnected += m => events.Add($"disconnect:{m}");

        svc.RegisterIdentifiedDevice(Device("E:\\", guid: "GUID-B", name: "iPod B"));

        // Old device disconnected first, then the new one connected - in that order, so the
        // VM clears iPod A's tracks before scanning iPod B.
        Assert.Equal(["disconnect:E:\\", "connect:GUID-B"], events);
    }

    [Fact]
    public void RegisterIdentifiedDevice_Different_Mounts_Coexist_Without_Swap()
    {
        using var svc = new DeviceDetectionService();
        var connects = new List<string>();
        var disconnects = new List<string>();
        svc.DeviceConnected += d => connects.Add(d.MountPath);
        svc.DeviceDisconnected += m => disconnects.Add(m);

        svc.RegisterIdentifiedDevice(Device("E:\\", guid: "GUID-A"));
        svc.RegisterIdentifiedDevice(Device("F:\\", guid: "GUID-B"));

        Assert.Equal(["E:\\", "F:\\"], connects);
        Assert.Empty(disconnects); // different drive letters - no swap
    }
}
