// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Tests;

internal static class TestHelpers
{
    /// <summary>
    /// Quickly create a Music MediaItem with sensible defaults. Override anything you care about.
    /// </summary>
    public static MediaItem Music(
        string id,
        string? title = null,
        string? artist = null,
        string? album = null,
        bool isFavorite = false,
        string? fileName = null,
        string? extension = null,
        uint? year = null)
    {
        return new MediaItem
        {
            Id = id,
            Kind = MediaKind.Music,
            Title = title ?? id,
            Artist = artist,
            Album = album,
            IsFavorite = isFavorite,
            FileName = fileName,
            Extension = extension,
            Year = year,
        };
    }

    /// <summary>
    /// Quickly create a Radio MediaItem with sensible defaults.
    /// </summary>
    public static MediaItem Radio(
        string id,
        string? title = null,
        string? country = null,
        string? tags = null,
        string? source = null,
        string? codec = null,
        int? bitrate = null,
        bool isFavorite = false)
    {
        return new MediaItem
        {
            Id = id,
            Kind = MediaKind.Radio,
            Title = title ?? id,
            Country = country,
            Tags = tags,
            Source = source,
            Codec = codec,
            Bitrate = bitrate,
            IsFavorite = isFavorite,
        };
    }

    public static List<MediaItem> MakeList(int count, string prefix = "track")
    {
        var list = new List<MediaItem>(count);
        for (int i = 0; i < count; i++)
        {
            list.Add(Music($"{prefix}-{i}", title: $"Title {i}"));
        }
        return list;
    }
}
