// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Tests;

public class OrgZDeviceRecordTests : IDisposable
{
    // Each test uses a fresh temp directory that mimics a mounted device root.
    private readonly string _mountPath;

    public OrgZDeviceRecordTests()
    {
        _mountPath = Path.Combine(Path.GetTempPath(), "OrgZ-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_mountPath);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_mountPath))
            {
                Directory.Delete(_mountPath, recursive: true);
            }
        }
        catch
        {
            // Best-effort — Windows test runners occasionally hold file locks briefly
        }
    }

    [Fact]
    public void TryLoad_returns_null_when_file_missing()
    {
        Assert.Null(OrgZDeviceRecord.TryLoad(_mountPath));
    }

    [Fact]
    public void TrySave_creates_orgz_subfolder_and_device_file()
    {
        var record = new OrgZDeviceRecord
        {
            Serial = "8K7285F4V9P",
            Model = "iPod Video 5.5G White (Modded)",
        };

        Assert.True(record.TrySave(_mountPath));

        var devicePath = Path.Combine(_mountPath, ".orgz", "device");
        Assert.True(File.Exists(devicePath));
    }

    [Fact]
    public void TrySave_then_TryLoad_round_trip_all_fields()
    {
        var firstSeen = new DateTime(2026, 4, 1, 10, 30, 0, DateTimeKind.Utc);
        var lastSeenStock = new DateTime(2026, 4, 15, 22, 0, 0, DateTimeKind.Utc);
        var lastSeenRockbox = new DateTime(2026, 4, 17, 9, 15, 0, DateTimeKind.Utc);

        var saved = new OrgZDeviceRecord
        {
            Version = 1,
            Serial = "8K7285F4V9P",
            Model = "iPod Video 5.5G White (Modded)",
            AppleModelNumber = "MA448LL/A",
            IpodGeneration = "Video 5.5G",
            AppleFirmwareVersion = "iPod OS 1.3",
            FireWireGuid = "000A2700153A9E6B",
            HardwareModel = "iFlash",
            RockboxTarget = "ipodvideo",
            RockboxVersion = "Rockbox 3.15",
            FirstSeen = firstSeen,
            LastSeen = lastSeenStock,
            LastSeenStock = lastSeenStock,
            LastSeenRockbox = lastSeenRockbox,
        };

        Assert.True(saved.TrySave(_mountPath));

        var loaded = OrgZDeviceRecord.TryLoad(_mountPath);
        Assert.NotNull(loaded);

        Assert.Equal(saved.Version, loaded!.Version);
        Assert.Equal(saved.Serial, loaded.Serial);
        Assert.Equal(saved.Model, loaded.Model);
        Assert.Equal(saved.AppleModelNumber, loaded.AppleModelNumber);
        Assert.Equal(saved.IpodGeneration, loaded.IpodGeneration);
        Assert.Equal(saved.AppleFirmwareVersion, loaded.AppleFirmwareVersion);
        Assert.Equal(saved.FireWireGuid, loaded.FireWireGuid);
        Assert.Equal(saved.HardwareModel, loaded.HardwareModel);
        Assert.Equal(saved.RockboxTarget, loaded.RockboxTarget);
        Assert.Equal(saved.RockboxVersion, loaded.RockboxVersion);
        Assert.Equal(saved.FirstSeen, loaded.FirstSeen);
        Assert.Equal(saved.LastSeen, loaded.LastSeen);
        Assert.Equal(saved.LastSeenStock, loaded.LastSeenStock);
        Assert.Equal(saved.LastSeenRockbox, loaded.LastSeenRockbox);
    }

    [Fact]
    public void Empty_string_fields_are_not_persisted()
    {
        var saved = new OrgZDeviceRecord
        {
            Serial = "ABC",
            Model = "",                  // empty — should NOT be written
            AppleModelNumber = "   ",    // whitespace — should NOT be written
            IpodGeneration = null,       // null — should NOT be written
            HardwareModel = "iFlash",
        };

        Assert.True(saved.TrySave(_mountPath));

        // Inspect the per-line keys to confirm absent fields aren't written. Use a
        // line-by-line key check rather than substring contains, since "HardwareModel="
        // would otherwise trigger a false-positive match for "Model=".
        var lines = File.ReadAllLines(Path.Combine(_mountPath, ".orgz", "device"));
        var keys = lines
            .Where(l => !l.StartsWith('#') && l.Contains('='))
            .Select(l => l[..l.IndexOf('=')])
            .ToHashSet();

        Assert.Contains("Serial", keys);
        Assert.Contains("HardwareModel", keys);
        Assert.Contains("Version", keys);
        Assert.DoesNotContain("Model", keys);
        Assert.DoesNotContain("AppleModelNumber", keys);
        Assert.DoesNotContain("IpodGeneration", keys);

        // The header comment line is preserved
        Assert.Contains(lines, l => l.StartsWith("# OrgZ device record"));
    }

    [Fact]
    public void TrySave_overwrites_existing_record_atomically()
    {
        var first = new OrgZDeviceRecord { Serial = "ORIGINAL" };
        Assert.True(first.TrySave(_mountPath));

        var second = new OrgZDeviceRecord { Serial = "REPLACED" };
        Assert.True(second.TrySave(_mountPath));

        var loaded = OrgZDeviceRecord.TryLoad(_mountPath);
        Assert.NotNull(loaded);
        Assert.Equal("REPLACED", loaded!.Serial);

        // No leftover .tmp file should remain after a successful save
        var tempPath = Path.Combine(_mountPath, ".orgz", "device.tmp");
        Assert.False(File.Exists(tempPath), ".tmp file should be cleaned up after atomic rename");
    }

    [Fact]
    public void TryLoad_skips_comments_and_blank_lines()
    {
        var dir = Path.Combine(_mountPath, ".orgz");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "device");

        File.WriteAllText(path, """
            # leading comment
            Version=1

            # interior comment
            Serial=ABCDEF

              # indented comment
            Model=Test Model
            """);

        var loaded = OrgZDeviceRecord.TryLoad(_mountPath);
        Assert.NotNull(loaded);
        Assert.Equal(1, loaded!.Version);
        Assert.Equal("ABCDEF", loaded.Serial);
        Assert.Equal("Test Model", loaded.Model);
    }

    [Fact]
    public void TryLoad_ignores_unknown_keys_and_malformed_lines()
    {
        var dir = Path.Combine(_mountPath, ".orgz");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "device");

        File.WriteAllText(path, """
            Serial=8K7285F4V9P
            UnknownKey=should be ignored
            no equals sign here
            =empty key
            Model=iPod Video 5.5G
            """);

        var loaded = OrgZDeviceRecord.TryLoad(_mountPath);
        Assert.NotNull(loaded);
        Assert.Equal("8K7285F4V9P", loaded!.Serial);
        Assert.Equal("iPod Video 5.5G", loaded.Model);
    }

    [Fact]
    public void TryLoad_handles_round_trip_of_DateTime_with_offset()
    {
        var saved = new OrgZDeviceRecord
        {
            // Local-kind date — round-trip "o" format preserves the offset
            FirstSeen = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Local),
        };

        Assert.True(saved.TrySave(_mountPath));
        var loaded = OrgZDeviceRecord.TryLoad(_mountPath);
        Assert.NotNull(loaded);
        Assert.Equal(saved.FirstSeen!.Value.ToUniversalTime(), loaded!.FirstSeen!.Value.ToUniversalTime());
    }

    [Fact]
    public void TryLoad_invalid_date_string_falls_back_to_null()
    {
        var dir = Path.Combine(_mountPath, ".orgz");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "device"), "FirstSeen=not-a-date\n");

        var loaded = OrgZDeviceRecord.TryLoad(_mountPath);
        Assert.NotNull(loaded);
        Assert.Null(loaded!.FirstSeen);
    }
}
