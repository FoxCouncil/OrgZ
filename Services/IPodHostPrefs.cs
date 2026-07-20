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

    // Identity slots mapped empirically on a real Shuffle 2G: 0x2C0 = user name; the others carry
    // computer names - every host that ever adopted the iPod (this one remembered DEBBIE-PC in
    // three slots and Jermaine Waller's MacBook until iTunes overwrote the active pair).
    private const int UserSlot = 0x2C0;
    private static readonly int[] ComputerSlots = [0x1C0, 0x280, 0x300, 0x380];
    private const int SlotSize = 0x40;

    /// <summary>One engraved host-name slot: its byte offset in iTunesPrefs and its content
    /// (null when empty). Every populated slot is a custody witness - no deduping, no collapsing;
    /// an old iPod should read as the trip of names it really carries.</summary>
    public sealed record HostSlot(int Offset, string? Value);

    /// <summary>The identity record an iPod carries. <see cref="Computer"/> is the ACTIVE slot
    /// (0x300) - iTunes overwrites it on adoption; <see cref="LegacySlots"/> (0x1C0/0x280/0x380) are
    /// layers modern iTunes stopped rewriting, fossilizing earlier custody.</summary>
    public sealed record HostIdentity(string? UserName, string? Computer, IReadOnlyList<HostSlot> LegacySlots);

    private const int CurrentComputerSlot = 0x300;
    private static readonly int[] LegacyComputerSlots = [0x1C0, 0x280, 0x380];

    /// <summary>Reads the identity trail. The people have a right to know what their hardware
    /// testifies about.</summary>
    public static HostIdentity ReadHosts(string mountPath)
    {
        try
        {
            var path = PrefsPath(mountPath);
            if (!File.Exists(path))
            {
                return new HostIdentity(null, null, []);
            }
            var bytes = File.ReadAllBytes(path);
            var legacy = LegacyComputerSlots.Select(slot => new HostSlot(slot, ReadSlot(bytes, slot))).ToList();
            return new HostIdentity(ReadSlot(bytes, UserSlot), ReadSlot(bytes, CurrentComputerSlot), legacy);
        }
        catch
        {
            return new HostIdentity(null, null, []);
        }
    }

    /// <summary>Privacy scrub for Erase: every computer-name slot becomes "{user}'s Computer" so a
    /// wiped iPod stops testifying about which machines it lived on. The user-name slot stays - it
    /// names a person the owner already is, not their hardware. No backup is kept (a scrub that
    /// archives what it scrubbed isn't one).</summary>
    public static bool ScrubHosts(string mountPath)
    {
        try
        {
            var path = PrefsPath(mountPath);
            if (!File.Exists(path))
            {
                return false;
            }
            var bytes = File.ReadAllBytes(path);
            var user = ReadSlot(bytes, UserSlot);
            var generic = $"{(string.IsNullOrWhiteSpace(user) ? "Someone" : user)}'s Computer";

            bool changed = false;
            foreach (var slot in ComputerSlots)
            {
                var current = ReadSlot(bytes, slot);
                if (string.IsNullOrWhiteSpace(current) || string.Equals(current, generic, StringComparison.Ordinal))
                {
                    continue;
                }
                WriteSlot(bytes, slot, generic);
                changed = true;
            }
            if (changed)
            {
                File.WriteAllBytes(path, bytes);
                _log.Information("Scrubbed host names on {Mount} -> “{Generic}”", mountPath, generic);
            }
            return changed;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Couldn't scrub host names on {Mount}", mountPath);
            return false;
        }
    }

    /// <summary>Deletes OrgZ's own .orgzbak ghosts under iPod_Control/iTunes - an erased iPod must
    /// not keep pre-erase databases (with their track lists and old device name) hanging around.</summary>
    public static void PurgeBackups(string mountPath)
    {
        try
        {
            var dir = IPodPaths.ITunesDir(mountPath);
            if (!Directory.Exists(dir))
            {
                return;
            }
            foreach (var bak in Directory.EnumerateFiles(dir, "*.orgzbak"))
            {
                File.Delete(bak);
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Couldn't purge backups on {Mount}", mountPath);
        }
    }

    private static string? ReadSlot(byte[] bytes, int offset)
    {
        if (bytes.Length < offset + 1)
        {
            return null;
        }
        int end = offset;
        int max = Math.Min(bytes.Length, offset + SlotSize);
        while (end < max && bytes[end] != 0)
        {
            end++;
        }
        return end == offset ? null : System.Text.Encoding.UTF8.GetString(bytes, offset, end - offset);
    }

    private static void WriteSlot(byte[] bytes, int offset, string value)
    {
        if (bytes.Length < offset + SlotSize)
        {
            return;
        }
        Array.Clear(bytes, offset, SlotSize);
        var utf8 = System.Text.Encoding.UTF8.GetBytes(value);
        Array.Copy(utf8, 0, bytes, offset, Math.Min(utf8.Length, SlotSize - 1));
    }

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
