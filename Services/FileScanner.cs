// Copyright (c) 2025 Fox Diller

using OrgZ.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OrgZ.Services;

/// <summary>
/// Service for scanning directories and loading audio files
/// </summary>
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

    /// <summary>
    /// Scans a directory and returns all audio files
    /// </summary>
    /// <param name="directoryPath">Path to the directory to scan</param>
    /// <param name="recursive">Whether to scan subdirectories</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of audio file information</returns>
    public static async Task<List<AudioFileInfo>> ScanDirectoryAsync(string directoryPath, bool recursive = true, CancellationToken cancellationToken = default)
    {
        return string.IsNullOrEmpty(directoryPath) || !Directory.Exists(directoryPath) ? [] : await Task.Run(() =>
        {
            List<AudioFileInfo> audioFiles = [];

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

                        audioFiles.Add(new AudioFileInfo
                        {
                            FilePath = filePath,
                            FileName = fileInfo.Name,
                            Extension = extension,
                            FileSize = fileInfo.Length
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
                // Log or handle other exceptions as needed
                throw;
            }

            return audioFiles;
        }, cancellationToken);
    }

    /// <summary>
    /// Gets the total count of audio files in a directory
    /// </summary>
    public static int CountAudioFiles(string directoryPath, bool recursive = true)
    {
        if (string.IsNullOrEmpty(directoryPath) || !Directory.Exists(directoryPath))
        {
            return 0;
        }

        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        try
        {
            return Directory.GetFiles(directoryPath, "*.*", searchOption)
                .Count(file => SupportedExtensions.Contains(Path.GetExtension(file)));
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Checks if a file extension is supported
    /// </summary>
    public static bool IsSupportedExtension(string filePath)
    {
        var extension = Path.GetExtension(filePath);

        return SupportedExtensions.Contains(extension);
    }
}
