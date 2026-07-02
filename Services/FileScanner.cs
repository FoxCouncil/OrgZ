// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Services;

public class FileScanner
{
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

    public static async Task<List<MediaItem>> ScanDirectoryAsync(string directoryPath, bool recursive = true, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(directoryPath) || !Directory.Exists(directoryPath))
        {
            return [];
        }

        return await Task.Run(() =>
        {
            List<MediaItem> audioFiles = [];

            var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

            try
            {
                var files = Directory.GetFiles(directoryPath, "*.*", searchOption);

                foreach (var filePath in files)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
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
            catch (UnauthorizedAccessException)
            {
                // Skip directories we don't have access to
            }
            catch (Exception)
            {
                throw;
            }

            return audioFiles;
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
