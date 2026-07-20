// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Services;

namespace OrgZ.Tests;

/// <summary>
/// The disk-use etch: iTunesPrefs byte 0x1F is iTunes's "Enable disk use" flag (found by diffing a
/// real Shuffle 2G's file around the checkbox). OrgZ asserts it on adopt so AMDS stops ejecting the
/// volume; everything else in the file - including the embedded host-provenance strings - must
/// survive untouched.
/// </summary>
public class IPodHostPrefsTests
{
    [Fact]
    public void Etch_flips_only_the_disk_use_byte_and_is_idempotent()
    {
        var mount = Path.Combine(Path.GetTempPath(), "orgz-prefs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(mount, "iPod_Control", "iTunes"));
        try
        {
            var prefs = new byte[1232];
            new Random(42).NextBytes(prefs);
            prefs[0x1F] = 0;
            File.WriteAllBytes(IPodHostPrefs.PrefsPath(mount), prefs);

            Assert.True(IPodHostPrefs.EtchDiskUse(mount));
            var after = File.ReadAllBytes(IPodHostPrefs.PrefsPath(mount));
            Assert.Equal(1, after[0x1F]);
            for (int i = 0; i < prefs.Length; i++)
            {
                if (i != 0x1F)
                {
                    Assert.Equal(prefs[i], after[i]);
                }
            }

            Assert.False(IPodHostPrefs.EtchDiskUse(mount));   // already set - no rewrite
        }
        finally
        {
            Directory.Delete(mount, recursive: true);
        }
    }

    [Fact]
    public void Etch_tolerates_missing_or_short_prefs()
    {
        var mount = Path.Combine(Path.GetTempPath(), "orgz-prefs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(mount, "iPod_Control", "iTunes"));
        try
        {
            Assert.False(IPodHostPrefs.EtchDiskUse(mount));                       // absent
            File.WriteAllBytes(IPodHostPrefs.PrefsPath(mount), new byte[8]);
            Assert.False(IPodHostPrefs.EtchDiskUse(mount));                       // too short
        }
        finally
        {
            Directory.Delete(mount, recursive: true);
        }
    }
}
