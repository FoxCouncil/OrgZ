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

                    // Skip dot-prefixed folders -- .podcasts/ holds downloaded
                    // episodes which belong to the Podcasts view, not Music.
                    // Generalised so any future .* directory the user (or we)
                    // create is treated as hidden the way unix conventions imply.
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
    /// OrgZ-managed scratch folders kept OUT of the music scan by exact name. Deliberately NOT a
    /// blanket "any dotted folder" rule - that wrongly skipped legitimate dot-named ALBUMS like
    /// "...Baby One More Time" and "...And Justice for All". <c>.audiobooks</c> is absent on
    /// purpose: it's library content the scan must walk.
    /// </summary>
    private static readonly HashSet<string> ManagedSkipFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ".podcasts",   // podcast downloads belong to the Podcasts view, not the music library
        ".orgz",       // any OrgZ cache/scratch a user's library might carry
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
