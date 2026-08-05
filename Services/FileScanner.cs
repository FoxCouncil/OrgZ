// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Serilog;

namespace OrgZ.Services;

/// <summary>
/// The outcome of a library folder walk. <see cref="Complete"/> is the authority bit: only a scan
/// that walked the whole tree may be used to decide a file is GONE. A missing folder (an unplugged
/// external drive), a cancelled scan, or an enumeration that died mid-walk all return whatever was
/// found so far with <c>Complete = false</c> - treating any of those as "the library is empty now"
/// is how ratings, play counts, and playlist memberships get mass-deleted.
/// </summary>
public sealed record FileScanResult(List<MediaItem> Items, bool Complete);

public class FileScanner
{
    private static readonly ILogger _log = Logging.For("FileScanner");

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
                // One locked folder skips itself instead of aborting the whole walk - the old
                // eager GetFiles threw on the first inaccessible directory and returned NOTHING,
                // which the reconciler read as "every track was deleted".
                IgnoreInaccessible = true,
                RecurseSubdirectories = recursive,
                // GetFiles never skipped hidden/system entries; keep that (the default here
                // would silently drop hidden files that have always been scanned).
                AttributesToSkip = 0,
            };

            try
            {
                foreach (var filePath in Directory.EnumerateFiles(directoryPath, "*", options))
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        complete = false;
                        break;
                    }

                    // Skip only OrgZ's own .podcasts/ downloads (Podcasts view owns them).
                    // NOT a blanket "any dotted folder" rule -- dot-named albums get scanned.
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
                // The enumerator itself died mid-walk (drive yanked, IO error). The list is
                // partial; hand it back for display but never let it drive deletions.
                complete = false;
                _log.Warning(ex, "Library scan of {Directory} aborted mid-walk; results are partial", directoryPath);
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
    /// The ONLY folders kept out of the music scan - skipped by exact name, not by any "starts with
    /// a dot" rule (that wrongly dropped legit dot-named albums like "...Baby One More Time"). Just
    /// <c>.podcasts</c>: its episode files are MP3s that would otherwise pollute the Music view, and
    /// they're owned by the Podcasts view instead. Everything else - <c>.audiobooks</c> (library
    /// content), a user's <c>.tools</c>, any dotted album - is walked normally.
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
