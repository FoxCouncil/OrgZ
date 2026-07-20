// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Services;

/// <summary>
/// The on-device <c>iPod_Control/iTunes/iTunesPrefs</c> host-association table. Byte 0x1F is
/// iTunes's "Enable disk use" flag - found empirically (2026-07-20) by diffing the file before and
/// after the checkbox on a real Shuffle 2G. When it's 0, Apple Mobile Device Service ejects the
/// volume after every iTunes session, yanking the drive out from under everything else. OrgZ etches
/// it ON for every iPod it sees: disk use is the contract that keeps the device mounted for all
/// comers, iTunes included. (The file also carries every host that ever adopted the iPod - user
/// name at 0x2C0, computer name at 0x300, earlier hosts in other slots.)
/// </summary>
public static class IPodHostPrefs
{
    private static readonly Serilog.ILogger _log = Logging.For("IPodHostPrefs");

    private const int DiskUseFlagOffset = 0x1F;

    public static string PrefsPath(string mountPath) => Path.Combine(IPodPaths.ITunesDir(mountPath), "iTunesPrefs");

    /// <summary>Asserts the disk-use flag. Returns true when the byte was flipped (false when it was
    /// already set, the file is absent/short, or the volume refused the write - all non-fatal: a
    /// device without prefs just mounts the pre-iTunes way).</summary>
    public static bool EtchDiskUse(string mountPath)
    {
        try
        {
            var path = PrefsPath(mountPath);
            if (!File.Exists(path))
            {
                return false;
            }
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length <= DiskUseFlagOffset || bytes[DiskUseFlagOffset] == 1)
            {
                return false;
            }
            bytes[DiskUseFlagOffset] = 1;
            AtomicFile.WriteAllBytes(path, bytes, backup: path + ".orgzbak");
            _log.Information("Etched disk-use on {Mount} (iTunesPrefs 0x1F -> 1)", mountPath);
            return true;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Couldn't etch disk-use on {Mount}", mountPath);
            return false;
        }
    }
}
