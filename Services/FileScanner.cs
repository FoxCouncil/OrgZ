// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Services;

public class FileScanner
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac",
        ".mp3",
        ".m4a",
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

                    var extension = Path.GetExtension(filePath);

                    if (IsSupportedExtension(filePath))
                    {
                        FileInfo fileInfo = new(filePath);

                        audioFiles.Add(new MediaItem
                        {
                            Id = filePath,
                            Kind = MediaKind.Music,
                            FilePath = filePath,
                            FileName = fileInfo.Name,
                            Extension = extension,
                            FileSize = fileInfo.Length,
                            LastModified = fileInfo.LastWriteTimeUtc
                        });
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

    public static int CountAudioFiles(string directoryPath, bool recursive = true)
    {
        if (string.IsNullOrEmpty(directoryPath) || !Directory.Exists(directoryPath))
        {
            return 0;
        }

        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        try
        {
            return Directory.GetFiles(directoryPath, "*.*", searchOption).Count(file => SupportedExtensions.Contains(Path.GetExtension(file)));
        }
        catch
        {
            return 0;
        }
    }

    public static bool IsSupportedExtension(string filePath)
    {
        var extension = Path.GetExtension(filePath);

        return SupportedExtensions.Contains(extension);
    }
}
