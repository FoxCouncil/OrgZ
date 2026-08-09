// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Tests;

public class MediaItemTests
{
    // -- AdoptUserStateFrom --

    [Fact]
    public void AdoptUserStateFrom_carries_every_user_owned_field()
    {
        var existing = new MediaItem
        {
            Id = "x",
            Kind = MediaKind.Music,
            IsFavorite = true,
            IsIgnored = true,
            LastPlayed = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            DateAdded = new DateTime(2020, 6, 7, 8, 9, 10, DateTimeKind.Utc),
            Rating = 4,
            PlayCount = 17,
            VolumeAdjustment = -20,
            EqPreset = "Rock",
            StartTime = TimeSpan.FromSeconds(3),
            StopTime = TimeSpan.FromSeconds(180),
            UseStartTime = true,
            UseStopTime = true,
            LastPositionMs = 42_000,
        };

        var rescanned = new MediaItem { Id = "x", Kind = MediaKind.Music, Title = "Fresh Tags" };
        rescanned.AdoptUserStateFrom(existing);

        Assert.True(rescanned.IsFavorite);
        Assert.True(rescanned.IsIgnored);
        Assert.Equal(existing.LastPlayed, rescanned.LastPlayed);
        Assert.Equal(existing.DateAdded, rescanned.DateAdded);
        Assert.Equal(4, rescanned.Rating);
        Assert.Equal(17, rescanned.PlayCount);
        Assert.Equal(-20, rescanned.VolumeAdjustment);
        Assert.Equal("Rock", rescanned.EqPreset);
        Assert.Equal(TimeSpan.FromSeconds(3), rescanned.StartTime);
        Assert.Equal(TimeSpan.FromSeconds(180), rescanned.StopTime);
        Assert.True(rescanned.UseStartTime);
        Assert.True(rescanned.UseStopTime);
        Assert.Equal(42_000, rescanned.LastPositionMs);
        Assert.Equal("Fresh Tags", rescanned.Title);   // file facts stay from the rescan
    }

    // -- KindLabel --

    [Theory]
    [InlineData(".mp3", "MPEG audio file")]
    [InlineData(".MP3", "MPEG audio file")]
    [InlineData(".flac", "FLAC audio file")]
    [InlineData(".m4a", "AAC audio file")]
    [InlineData(".aac", "AAC audio file")]
    [InlineData(".ogg", "OGG Vorbis file")]
    [InlineData(".wav", "WAV audio file")]
    [InlineData(".wma", "WMA audio file")]
    [InlineData(".ape", "APE audio file")]
    [InlineData(".opus", "Opus audio file")]
    public void KindLabel_KnownExtensions_MapToFriendlyNames(string extension, string expected)
    {
        var item = new MediaItem { Id = "x", Kind = MediaKind.Music, Extension = extension };
        Assert.Equal(expected, item.KindLabel);
    }

    [Fact]
    public void KindLabel_UnknownExtension_FallsBackToMimeType()
    {
        var item = new MediaItem { Id = "x", Kind = MediaKind.Music, Extension = ".xyz", MimeType = "audio/xyz" };
        Assert.Equal("audio/xyz", item.KindLabel);
    }

    [Fact]
    public void KindLabel_NoExtensionNoMimeType_FallsBackToGeneric()
    {
        var item = new MediaItem { Id = "x", Kind = MediaKind.Music };
        Assert.Equal("Audio file", item.KindLabel);
    }

    // -- ChannelsLabel --

    [Theory]
    [InlineData(1, "Mono")]
    [InlineData(2, "Stereo")]
    [InlineData(5, "5 channels")]
    [InlineData(8, "8 channels")]
    public void ChannelsLabel_MapsCorrectly(int channels, string expected)
    {
        var item = new MediaItem { Id = "x", Kind = MediaKind.Music, AudioChannels = channels };
        Assert.Equal(expected, item.ChannelsLabel);
    }

    [Fact]
    public void ChannelsLabel_NullOrZero_IsEmpty()
    {
        var nullItem = new MediaItem { Id = "x", Kind = MediaKind.Music };
        var zeroItem = new MediaItem { Id = "y", Kind = MediaKind.Music, AudioChannels = 0 };
        Assert.Equal("", nullItem.ChannelsLabel);
        Assert.Equal("", zeroItem.ChannelsLabel);
    }

    // -- CodecLabel (radio) --

    [Theory]
    [InlineData("audio/mpeg", "MP3")]
    [InlineData("AUDIO/MPEG", "MP3")]
    [InlineData("audio/aacp", "AAC+")]
    [InlineData("aac+", "AAC+")]
    [InlineData("audio/aac", "AAC")]
    [InlineData("audio/ogg", "OGG")]
    [InlineData("audio/flac", "FLAC")]
    [InlineData("application/vnd.apple.mpegurl", "HLS")]
    [InlineData("application/x-mpegURL", "HLS")]
    [InlineData("audio/mpegurl", "HLS")]
    [InlineData("audio/x-mpegurl", "HLS")]
    [InlineData("unknown", "-")]
    [InlineData("UNDEFINED", "-")]
    public void CodecLabel_MapsKnownMimeTypes(string codec, string expected)
    {
        var item = new MediaItem { Id = "x", Kind = MediaKind.Radio, Codec = codec };
        Assert.Equal(expected, item.CodecLabel);
    }

    [Fact]
    public void CodecLabel_NullOrEmpty_ReturnsDash()
    {
        var nullItem = new MediaItem { Id = "x", Kind = MediaKind.Radio };
        var emptyItem = new MediaItem { Id = "y", Kind = MediaKind.Radio, Codec = "" };
        Assert.Equal("-", nullItem.CodecLabel);
        Assert.Equal("-", emptyItem.CodecLabel);
    }

    [Fact]
    public void CodecLabel_UnknownCodec_ReturnsUppercase()
    {
        var item = new MediaItem { Id = "x", Kind = MediaKind.Radio, Codec = "weirdo" };
        Assert.Equal("WEIRDO", item.CodecLabel);
    }

    // -- BitrateLabel --

    [Theory]
    [InlineData(128, "128 kbps")]
    [InlineData(320, "320 kbps")]
    public void BitrateLabel_PositiveBitrate_FormatsKbps(int bitrate, string expected)
    {
        var item = new MediaItem { Id = "x", Kind = MediaKind.Radio, Bitrate = bitrate };
        Assert.Equal(expected, item.BitrateLabel);
    }

    [Fact]
    public void BitrateLabel_NullOrZero_IsEmpty()
    {
        var nullItem = new MediaItem { Id = "x", Kind = MediaKind.Radio };
        var zeroItem = new MediaItem { Id = "y", Kind = MediaKind.Radio, Bitrate = 0 };
        Assert.Equal("", nullItem.BitrateLabel);
        Assert.Equal("", zeroItem.BitrateLabel);
    }

    // -- FormatIssues --

    [Fact]
    public void FormatIssues_CleanTrack_ReturnsEmpty()
    {
        var item = new MediaItem
        {
            Id = "x", Kind = MediaKind.Music,
            Title = "Song", Artist = "Artist", Year = 2020,
            HasAlbumArt = true, Extension = ".flac"
        };
        Assert.Equal("", item.FormatIssues);
    }

    [Fact]
    public void FormatIssues_MissingTitle_Flagged()
    {
        var item = new MediaItem
        {
            Id = "x", Kind = MediaKind.Music,
            Artist = "Artist", Year = 2020,
            HasAlbumArt = true, Extension = ".flac"
        };
        Assert.Contains("No Title", item.FormatIssues);
    }

    [Fact]
    public void FormatIssues_MissingArtist_Flagged()
    {
        var item = new MediaItem
        {
            Id = "x", Kind = MediaKind.Music,
            Title = "Song", Year = 2020,
            HasAlbumArt = true, Extension = ".flac"
        };
        Assert.Contains("No Artist", item.FormatIssues);
    }

    [Fact]
    public void FormatIssues_MissingYear_Flagged()
    {
        var item = new MediaItem
        {
            Id = "x", Kind = MediaKind.Music,
            Title = "Song", Artist = "Artist",
            HasAlbumArt = true, Extension = ".flac"
        };
        Assert.Contains("No Year", item.FormatIssues);
    }

    [Fact]
    public void FormatIssues_MissingAlbumArt_Flagged()
    {
        var item = new MediaItem
        {
            Id = "x", Kind = MediaKind.Music,
            Title = "Song", Artist = "Artist", Year = 2020,
            HasAlbumArt = false, Extension = ".flac"
        };
        Assert.Contains("No Album Art", item.FormatIssues);
    }

    [Fact]
    public void FormatIssues_LossyFormat_Flagged()
    {
        var item = new MediaItem
        {
            Id = "x", Kind = MediaKind.Music,
            Title = "Song", Artist = "Artist", Year = 2020,
            HasAlbumArt = true, Extension = ".mp3"
        };
        Assert.Contains("Lossy Format (.mp3)", item.FormatIssues);
    }

    [Fact]
    public void FormatIssues_MultipleIssues_CommaJoined()
    {
        var item = new MediaItem
        {
            Id = "x", Kind = MediaKind.Music,
            Extension = ".mp3"
        };
        var issues = item.FormatIssues;
        Assert.Contains("No Title", issues);
        Assert.Contains("No Artist", issues);
        Assert.Contains("No Year", issues);
        Assert.Contains("Lossy Format", issues);
        Assert.Contains(", ", issues);
    }

    [Fact]
    public void FormatIssues_RadioItem_AlwaysEmpty()
    {
        var item = new MediaItem { Id = "x", Kind = MediaKind.Radio };
        Assert.Equal("", item.FormatIssues);
    }

    [Fact]
    public void Rating_NotifiesOnChange()
    {
        var item = new MediaItem { Id = "x", Kind = MediaKind.Music };
        var notified = new List<string?>();
        item.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        item.Rating = 3;

        Assert.Contains(nameof(MediaItem.Rating), notified);
    }
}
