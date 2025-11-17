// Copyright (c) 2025 Fox Diller

using OrgZ.Models;
using System;

namespace OrgZ.Services;

/// <summary>
/// Service for analyzing audio files using TagLib
/// </summary>
public class AudioFileAnalyzer
{
    /// <summary>
    /// Analyzes an audio file and populates metadata information
    /// </summary>
    /// <param name="audioFile">The audio file info to analyze</param>
    public static void AnalyzeFile(AudioFileInfo audioFile)
    {
        if (audioFile.IsAnalyzed)
        {
            return;
        }

        try
        {
            using TagLib.File file = TagLib.File.Create(audioFile.FilePath);

            // Extract metadata

            audioFile.Artist = file.Tag.FirstPerformer;
            audioFile.Year = file.Tag.Year;
            audioFile.Album = file.Tag.Album;
            audioFile.Title = file.Tag.Title;
            audioFile.Duration = file.Properties.Duration;


            audioFile.Track = file.Tag.Track;
            audioFile.TotalTracks = file.Tag.TrackCount;
            audioFile.Disc = file.Tag.Disc;
            audioFile.TotalDiscs = file.Tag.DiscCount;

            // Check for album art
            audioFile.HasAlbumArt = file.Tag.Pictures?.Length > 0;

            // Check if filename matches headers (extension match)
            audioFile.FileNameMatchesHeaders = CheckFileNameMatchesHeaders(audioFile, file);

            // Identify issues
            IdentifyIssues(audioFile);

            audioFile.IsAnalyzed = true;
        }
        catch (Exception ex)
        {
            audioFile.Issues.Add($"Failed to analyze: {ex.Message}");
            audioFile.IsAnalyzed = true;
        }
    }

    /// <summary>
    /// Checks if the file extension matches the actual audio format
    /// </summary>
    private static bool CheckFileNameMatchesHeaders(AudioFileInfo audioFile, TagLib.File file)
    {
        string extension = audioFile.Extension.ToLowerInvariant();
        string mimeType = file.MimeType.ToLowerInvariant();

        return extension switch
        {
            ".flac" => mimeType.Contains("flac"),
            ".mp3" => mimeType.Contains("mpeg") || mimeType.Contains("mp3"),
            ".m4a" => mimeType.Contains("mp4") || mimeType.Contains("m4a"),
            ".aac" => mimeType.Contains("aac"),
            ".ogg" => mimeType.Contains("ogg") || mimeType.Contains("vorbis"),
            ".wav" => mimeType.Contains("wav"),
            ".wma" => mimeType.Contains("asf") || mimeType.Contains("wma"),
            ".ape" => mimeType.Contains("ape"),
            ".opus" => mimeType.Contains("opus"),
            _ => true // Unknown format, assume it's fine
        };
    }

    /// <summary>
    /// Identifies common issues with audio files
    /// </summary>
    private static void IdentifyIssues(AudioFileInfo audioFile)
    {
        if (audioFile.FileNameMatchesHeaders == false)
        {
            audioFile.Issues.Add("File extension doesn't match audio format");
        }

        if (audioFile.HasAlbumArt == false)
        {
            audioFile.Issues.Add("No album art found");
        }

        if (string.IsNullOrWhiteSpace(audioFile.Title))
        {
            audioFile.Issues.Add("Missing title tag");
        }

        if (string.IsNullOrWhiteSpace(audioFile.Artist))
        {
            audioFile.Issues.Add("Missing artist tag");
        }

        if (string.IsNullOrWhiteSpace(audioFile.Album))
        {
            audioFile.Issues.Add("Missing album tag");
        }
    }

    /// <summary>
    /// Filters audio files based on specific criteria
    /// </summary>
    public static class Filters
    {
        public static bool HasMissingAlbumArt(AudioFileInfo file)
        {
            return file.IsAnalyzed && file.HasAlbumArt == false;
        }

        public static bool HasExtensionMismatch(AudioFileInfo file)
        {
            return file.IsAnalyzed && file.FileNameMatchesHeaders == false;
        }

        public static bool IsFlacFile(AudioFileInfo file)
        {
            return file.Extension.Equals(".flac", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsMp3File(AudioFileInfo file)
        {
            return file.Extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase);
        }

        public static bool HasAnyIssues(AudioFileInfo file)
        {
            return file.IsAnalyzed && file.Issues.Count > 0;
        }

        public static bool HasMissingTags(AudioFileInfo file)
        {
            return file.IsAnalyzed && (string.IsNullOrWhiteSpace(file.Title) ||
                               string.IsNullOrWhiteSpace(file.Artist) ||
                               string.IsNullOrWhiteSpace(file.Album));
        }
    }
}
