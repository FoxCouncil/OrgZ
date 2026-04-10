// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Tests;

public class MediaItemTests
{
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

    // -- NormalizedGenre --

    [Theory]
    [InlineData("rock", "Classic Rock")]
    [InlineData("classic rock,oldies", "Classic Rock")]
    [InlineData("indie,alternative", "Alt/Modern Rock")]
    [InlineData("death metal", "Hard Rock / Metal")]
    [InlineData("christian rock", "Religious")]
    [InlineData("pop,top 40", "Top 40/Pop")]
    [InlineData("80s,new wave", "70s/80s Pop")]
    [InlineData(null, "Other")]
    [InlineData("", "Other")]
    [InlineData("klingon opera ceremonial", "Classical")] // opera matches
    public void NormalizedGenre_MapsProviderTagsToCanonical(string? tags, string expected)
    {
        var item = new MediaItem { Id = "x", Kind = MediaKind.Radio, Tags = tags };
        Assert.Equal(expected, item.NormalizedGenre);
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

    // -- RatingDisplay --

    [Theory]
    [InlineData(1, "★☆☆☆☆")]
    [InlineData(2, "★★☆☆☆")]
    [InlineData(3, "★★★☆☆")]
    [InlineData(4, "★★★★☆")]
    [InlineData(5, "★★★★★")]
    public void RatingDisplay_PositiveRating_RendersStars(int rating, string expected)
    {
        var item = new MediaItem { Id = "x", Kind = MediaKind.Music, Rating = rating };
        Assert.Equal(expected, item.RatingDisplay);
    }

    [Fact]
    public void RatingDisplay_NullOrZero_IsEmpty()
    {
        var nullItem = new MediaItem { Id = "x", Kind = MediaKind.Music };
        var zeroItem = new MediaItem { Id = "y", Kind = MediaKind.Music, Rating = 0 };
        Assert.Equal("", nullItem.RatingDisplay);
        Assert.Equal("", zeroItem.RatingDisplay);
    }

    [Fact]
    public void RatingDisplay_OutOfRange_IsClamped()
    {
        var over = new MediaItem { Id = "x", Kind = MediaKind.Music, Rating = 99 };
        Assert.Equal("★★★★★", over.RatingDisplay);
    }

    [Fact]
    public void RatingDisplay_NotifiesOnRatingChange()
    {
        var item = new MediaItem { Id = "x", Kind = MediaKind.Music };
        var notified = new List<string?>();
        item.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        item.Rating = 3;

        Assert.Contains(nameof(MediaItem.Rating), notified);
        Assert.Contains(nameof(MediaItem.RatingDisplay), notified);
    }
}
