// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Services;

public class AudioFileAnalyzer
{
    public static void AnalyzeFile(MediaItem item)
    {
        if (item.Kind != MediaKind.Music || item.IsAnalyzed || string.IsNullOrEmpty(item.FilePath))
        {
            return;
        }

        try
        {
            using TagLib.File file = TagLib.File.Create(item.FilePath);

            item.Artist = file.Tag.FirstPerformer;
            item.Year = file.Tag.Year;
            item.Album = file.Tag.Album;
            item.Title = file.Tag.Title;
            item.Duration = file.Properties.Duration;

            item.Track = file.Tag.Track;
            item.TotalTracks = file.Tag.TrackCount;
            item.Disc = file.Tag.Disc;
            item.TotalDiscs = file.Tag.DiscCount;

            item.HasAlbumArt = file.Tag.Pictures?.Length > 0;

            item.FileNameMatchesHeaders = CheckFileNameMatchesHeaders(item, file);

            IdentifyIssues(item);

            item.IsAnalyzed = true;
        }
        catch (Exception ex)
        {
            item.Issues.Add($"Failed to analyze: {ex.Message}");
            item.IsAnalyzed = true;
        }
    }

    private static bool CheckFileNameMatchesHeaders(MediaItem item, TagLib.File file)
    {
        string extension = (item.Extension ?? "").ToLowerInvariant();
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
            _ => true
        };
    }

    private static void IdentifyIssues(MediaItem item)
    {
        if (item.FileNameMatchesHeaders == false)
        {
            item.Issues.Add("File extension doesn't match audio format");
        }

        if (item.HasAlbumArt == false)
        {
            item.Issues.Add("No album art found");
        }

        if (string.IsNullOrWhiteSpace(item.Title))
        {
            item.Issues.Add("Missing title tag");
        }

        if (string.IsNullOrWhiteSpace(item.Artist))
        {
            item.Issues.Add("Missing artist tag");
        }

        if (string.IsNullOrWhiteSpace(item.Album))
        {
            item.Issues.Add("Missing album tag");
        }
    }

    public static class Filters
    {
        public static bool HasMissingAlbumArt(MediaItem file)
        {
            return file.IsAnalyzed && file.HasAlbumArt == false;
        }

        public static bool HasExtensionMismatch(MediaItem file)
        {
            return file.IsAnalyzed && file.FileNameMatchesHeaders == false;
        }

        public static bool IsFlacFile(MediaItem file)
        {
            return (file.Extension ?? "").Equals(".flac", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsMp3File(MediaItem file)
        {
            return (file.Extension ?? "").Equals(".mp3", StringComparison.OrdinalIgnoreCase);
        }

        public static bool HasAnyIssues(MediaItem file)
        {
            return file.IsAnalyzed && file.Issues.Count > 0;
        }

        public static bool HasMissingTags(MediaItem file)
        {
            return file.IsAnalyzed && (string.IsNullOrWhiteSpace(file.Title) || string.IsNullOrWhiteSpace(file.Artist) || string.IsNullOrWhiteSpace(file.Album));
        }
    }
}
