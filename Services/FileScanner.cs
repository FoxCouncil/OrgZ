// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Serilog;

namespace OrgZ.Services;

/// <summary>
/// The outcome of a library folder walk. Only a scan with <see cref="Complete"/> set may be used
/// to decide a file is gone. A missing folder (an unplugged external drive), a cancelled scan, a
/// subfolder the walk couldn't read, or an enumeration that died mid-walk all return what was
/// found so far with <c>Complete = false</c>;
/// treating those as "the library is empty now" mass-deletes ratings, play counts and playlist
/// memberships.
/// </summary>
public sealed record FileScanResult(List<MediaItem> Items, bool Complete);

public class FileScanner
{
    private static readonly ILogger _log = Logging.For("FileScanner");

    /// <summary>
    /// Folders a normal process can never read, which therefore say nothing about whether a
    /// scan saw the whole library. Only reachable when someone points the library at a drive
    /// root, which people with a dedicated music disk do.
    /// </summary>
    private static readonly HashSet<string> AlwaysDenied = new(StringComparer.OrdinalIgnoreCase)
    {
        "System Volume Information", "$RECYCLE.BIN", "$Recycle.Bin", ".Trash", ".Trashes", "lost+found",
    };

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac",
        ".mp3",
        ".m4a",
        ".m4b",
        ".aac",
        ".ogg",
        ".wav",
        ".wma",
        ".ape",
        ".opus"
    };

    public static async Task<FileScanResult> ScanDirectoryAsync(string directoryPath, bool recursive = true, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(directoryPath) || !Directory.Exists(directoryPath))
        {
            // "Couldn't look" is not "nothing there": the folder may be an unplugged drive.
            return new FileScanResult([], Complete: false);
        }

        return await Task.Run(() =>
        {
            List<MediaItem> audioFiles = [];
            var complete = true;

            var options = new EnumerationOptions
            {
                // Faults surface here rather than being swallowed: the walk descends one directory
                // at a time so a folder it can't read skips itself (like IgnoreInaccessible did)
                // AND lowers Complete, which is what withdraws the authority to delete rows.
                IgnoreInaccessible = false,
                RecurseSubdirectories = false,
                // GetFiles never skipped hidden/system entries; keep that (the default here
                // would silently drop hidden files that have always been scanned).
                AttributesToSkip = 0,
            };

            var stack = new Stack<string>();
            stack.Push(directoryPath);

            while (stack.Count > 0)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    complete = false;
                    break;
                }

                var directory = stack.Pop();

                try
                {
                    foreach (var filePath in Directory.EnumerateFiles(directory, "*", options))
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            complete = false;
                            break;
                        }

                        // Skip only OrgZ's own .podcasts/ downloads (the Podcasts view owns them).
                        // Not a blanket "any dotted folder" rule - dot-named albums get scanned.
                        if (IsInHiddenSubdirectory(filePath, directoryPath))
                        {
                            continue;
                        }

                        var item = CreateMediaItemFromPath(filePath);

                        if (item != null)
                        {
                            audioFiles.Add(item);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // A folder we couldn't read (deny-read ACE, dropped share, IO error) contributes
                    // nothing. The list stays useful for display, but the tracks that live under it
                    // must not be read as deletions.
                    complete = false;
                    _log.Warning(ex, "Library scan skipped {Directory}; results are partial", directory);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    complete = false;
                    break;
                }

                if (!recursive)
                {
                    break;
                }

                try
                {
                    foreach (var subDirectory in Directory.EnumerateDirectories(directory, "*", options))
                    {
                        // Never descend into the folders the OS denies to every non-elevated
                        // process. They can hold no library content, but they DO throw - and
                        // because an unreadable folder now withdraws the authority to prune,
                        // a library that lives on a drive root would otherwise be marked
                        // partial on every single scan and never reconcile a deleted file.
                        if (AlwaysDenied.Contains(Path.GetFileName(subDirectory)))
                        {
                            continue;
                        }

                        stack.Push(subDirectory);
                    }
                }
                catch (Exception ex)
                {
                    // Couldn't list this folder's children: whatever is under them is unaccounted for.
                    complete = false;
                    _log.Warning(ex, "Library scan couldn't list the subfolders of {Directory}; results are partial", directory);
                }
            }

            return new FileScanResult(audioFiles, complete);
        }, cancellationToken);
    }

    public static MediaItem? CreateMediaItemFromPath(string filePath)
    {
        if (!IsSupportedExtension(filePath))
        {
            return null;
        }

        var fileInfo = new FileInfo(filePath);

        if (!fileInfo.Exists)
        {
            return null;
        }

        return new MediaItem
        {
            Id = filePath,
            Kind = AudiobookDetector.KindForPath(filePath),
            FilePath = filePath,
            FileName = fileInfo.Name,
            Extension = fileInfo.Extension,
            FileSize = fileInfo.Length,
            LastModified = fileInfo.LastWriteTimeUtc
        };
    }

    public static bool IsSupportedExtension(string filePath)
    {
        var extension = Path.GetExtension(filePath);

        return SupportedExtensions.Contains(extension);
    }

    /// <summary>
    /// The folders kept out of the music scan, skipped by exact name rather than a "starts with a
    /// dot" rule (which dropped dot-named albums like "...Baby One More Time"). Only
    /// <c>.podcasts</c>: its episodes are MP3s that would otherwise show up in the Music view, and
    /// the Podcasts view owns them. Everything else - <c>.audiobooks</c>, a user's <c>.tools</c>,
    /// any dotted album - is walked normally.
    /// </summary>
    private static readonly HashSet<string> ManagedSkipFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ".podcasts",
    };

    /// <summary>
    /// True when any parent directory of <paramref name="filePath"/> (under
    /// <paramref name="rootDirectory"/>) is an OrgZ-managed scratch folder. Only those exact names
    /// are skipped - an ordinary folder that happens to start with a dot is walked normally.
    /// </summary>
    private static bool IsInHiddenSubdirectory(string filePath, string rootDirectory)
    {
        var relative = Path.GetRelativePath(rootDirectory, filePath);
        var sep = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        foreach (var segment in relative.Split(sep, StringSplitOptions.RemoveEmptyEntries))
        {
            // Last segment is the filename itself - only parent directory names gate the skip.
            if (segment == Path.GetFileName(filePath)) break;
            if (ManagedSkipFolders.Contains(segment)) return true;
        }
        return false;
    }
}
