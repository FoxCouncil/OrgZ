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
            item.Genre = file.Tag.JoinedGenres;
            item.Composer = file.Tag.JoinedComposers;
            item.Comment = file.Tag.Comment;
            item.Bpm = file.Tag.BeatsPerMinute;

            item.AudioBitrate = file.Properties.AudioBitrate;
            item.SampleRate = file.Properties.AudioSampleRate;
            item.AudioChannels = file.Properties.AudioChannels;

            // Codec description from the file's codec info (e.g. "MPEG Audio Version 1 Layer 3")
            item.CodecDescription = file.Properties.Codecs?
                .OfType<TagLib.IAudioCodec>()
                .FirstOrDefault()?.Description;

            // Encoder software — format-specific extraction
            item.EncoderSettings = ExtractEncoderSettings(file);

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

    private static string? ExtractEncoderSettings(TagLib.File file)
    {
        // ID3v2: TSSE frame = "Software/Hardware and settings used for encoding"
        //        TENC frame = "Encoded by"
        if (file.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3v2)
        {
            var tsse = id3v2.GetTextAsString(new TagLib.ByteVector("TSSE"));
            var tenc = id3v2.GetTextAsString(new TagLib.ByteVector("TENC"));

            if (!string.IsNullOrWhiteSpace(tsse) && !string.IsNullOrWhiteSpace(tenc))
            {
                return $"{tenc}, {tsse}";
            }

            return tsse ?? tenc;
        }

        // MP4/AAC: ©too atom (encoding tool)
        if (file.GetTag(TagLib.TagTypes.Apple) is TagLib.Mpeg4.AppleTag apple)
        {
            // ©too = 0xA9 + "too" in Latin1
            var tooId = new TagLib.ByteVector([(byte)0xA9, (byte)'t', (byte)'o', (byte)'o']);
            var tooItems = apple.GetText(tooId);
            if (tooItems?.Length > 0)
            {
                return string.Join(", ", tooItems);
            }

            var tool = apple.GetDashBox("com.apple.iTunes", "tool");
            if (!string.IsNullOrWhiteSpace(tool))
            {
                return tool;
            }
        }

        // Vorbis/FLAC: ENCODER comment
        if (file.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph)
        {
            var encoder = xiph.GetFirstField("ENCODER");
            if (!string.IsNullOrWhiteSpace(encoder))
            {
                return encoder;
            }
        }

        return null;
    }

    public static void WriteTagsAndReanalyze(MediaItem item, string title, string? artist, string? album,
        uint? year, uint? track, uint? totalTracks, uint? disc, uint? totalDiscs,
        string? genre, string? composer, string? comment, uint? bpm)
    {
        if (item.Kind != MediaKind.Music || string.IsNullOrEmpty(item.FilePath))
        {
            return;
        }

        using (var file = TagLib.File.Create(item.FilePath))
        {
            file.Tag.Title = title;
            file.Tag.Performers = string.IsNullOrWhiteSpace(artist) ? [] : [artist];
            file.Tag.Album = album;
            file.Tag.Year = year ?? 0;
            file.Tag.Track = track ?? 0;
            file.Tag.TrackCount = totalTracks ?? 0;
            file.Tag.Disc = disc ?? 0;
            file.Tag.DiscCount = totalDiscs ?? 0;
            file.Tag.Genres = string.IsNullOrWhiteSpace(genre) ? [] : [genre];
            file.Tag.Composers = string.IsNullOrWhiteSpace(composer) ? [] : [composer];
            file.Tag.Comment = comment;
            file.Tag.BeatsPerMinute = bpm ?? 0;

            file.Save();
        }

        // Re-read everything from the file
        item.IsAnalyzed = false;
        item.Issues.Clear();
        AnalyzeFile(item);
    }
}
