// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Services;

namespace OrgZ.Tests;

/// <summary>
/// Per-device sidebar expansion memory: auto-expand on first connect of a session,
/// then the user's choice survives reconnects. Adversarial cases attack missing
/// serials, drive-letter reuse, and cross-device contamination.
/// </summary>
public class DeviceExpansionMemoryTests
{
    [Fact]
    public void First_connect_of_a_session_auto_expands()
    {
        var memory = new DeviceExpansionMemory();
        Assert.True(memory.GetOrDefault("8K7285F4V9P", @"E:\"));
    }

    [Fact]
    public void A_collapse_survives_disconnect_and_reconnect()
    {
        var memory = new DeviceExpansionMemory();
        memory.Remember("8K7285F4V9P", @"E:\", expanded: false);

        Assert.False(memory.GetOrDefault("8K7285F4V9P", @"E:\"));
    }

    [Fact]
    public void Same_device_at_a_new_drive_letter_is_recognized_by_serial()
    {
        var memory = new DeviceExpansionMemory();
        memory.Remember("8K7285F4V9P", @"E:\", expanded: false);

        // Reconnected at G: - the serial, not the mount, carries the memory.
        Assert.False(memory.GetOrDefault("8K7285F4V9P", @"G:\"));
    }

    [Fact]
    public void Serialless_devices_fall_back_to_the_mount_path()
    {
        var memory = new DeviceExpansionMemory();
        memory.Remember(null, @"E:\", expanded: false);

        Assert.False(memory.GetOrDefault(null, @"E:\"));
        Assert.False(memory.GetOrDefault("", @"E:\"));      // empty == missing
        Assert.True(memory.GetOrDefault(null, @"F:\"));     // different mount = different device
    }

    [Fact]
    public void A_serialless_mount_key_never_collides_with_a_serial_key()
    {
        // Adversarial: a device whose SERIAL string equals another device's mount
        // path must not share expansion state (distinct key namespaces).
        var memory = new DeviceExpansionMemory();
        memory.Remember(@"E:\", @"X:\", expanded: false);   // weird serial that looks like a mount

        Assert.True(memory.GetOrDefault(null, @"E:\"));     // mount-keyed lookup unaffected
    }

    [Fact]
    public void Two_devices_do_not_contaminate_each_other()
    {
        var memory = new DeviceExpansionMemory();
        memory.Remember("NANO5G", @"E:\", expanded: false);
        memory.Remember("BRIPOD", @"F:\", expanded: true);

        Assert.False(memory.GetOrDefault("NANO5G", @"E:\"));
        Assert.True(memory.GetOrDefault("BRIPOD", @"F:\"));
    }
}
