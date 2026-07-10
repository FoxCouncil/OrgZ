// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Helpers;

/// <summary>
/// Writes a file so a crash - or a USB cable yanked mid-sync - can never leave the destination
/// half-written: the bytes land in a sibling temp file that is flushed to disk and then
/// atomically renamed over the target. On the FAT/exFAT filesystems iPods use, the rename is
/// the closest thing to an atomic primitive available, so every on-device database goes through
/// here rather than a bare <see cref="File.WriteAllBytes(string, byte[])"/>.
/// </summary>
public static class AtomicFile
{
    private const string TempSuffix = ".orgztmp";

    /// <summary>
    /// Atomically replaces <paramref name="path"/> with <paramref name="bytes"/>. When
    /// <paramref name="backup"/> is given, the ORIGINAL is copied aside once (and never
    /// overwritten on later writes), so the pre-OrgZ state stays recoverable.
    /// </summary>
    public static void WriteAllBytes(string path, byte[] bytes, string? backup = null)
    {
        if (backup is not null && File.Exists(path) && !File.Exists(backup))
        {
            File.Copy(path, backup);
        }

        var tmp = path + TempSuffix;
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(flushToDisk: true);   // durable before the rename, so the swap is all-or-nothing
        }

        File.Move(tmp, path, overwrite: true);
    }
}
