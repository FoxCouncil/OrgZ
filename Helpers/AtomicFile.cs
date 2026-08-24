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
    /// <paramref name="backup"/> is given, the original is copied aside once (and never
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

    /// <summary>
    /// Applies an in-place mutation - a tag write - to a sibling COPY of <paramref name="path"/>
    /// and swaps it over the original only once it has completed. TagLib saves in place and shifts
    /// the entire audio payload whenever a new frame doesn't fit the existing padding, so a crash
    /// or a pulled drive during that shift truncates the user's only copy of the track.
    /// <para>
    /// The copy keeps the <c>.orgztmp</c> extension rather than the audio one, so the music-folder
    /// watcher never sees a track appear and vanish. That means <paramref name="mutate"/> must open
    /// the path it is handed by MIME type, not by extension.
    /// </para>
    /// </summary>
    public static void MutateCopy(string path, Action<string> mutate)
    {
        var tmp = path + TempSuffix;

        try
        {
            File.Copy(path, tmp, overwrite: true);
            mutate(tmp);

            using (var fs = new FileStream(tmp, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                fs.Flush(flushToDisk: true);   // durable before the rename, exactly as above
            }

            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // The original is untouched either way; a temp left behind is the only debris.
            try { File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
    }
}
