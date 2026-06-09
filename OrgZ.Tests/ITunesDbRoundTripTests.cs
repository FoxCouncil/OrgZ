// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Services;

namespace OrgZ.Tests;

/// <summary>
/// End-to-end write→read tests: build a DB with <see cref="ITunesDbWriter"/>,
/// serialize it, and parse it back with <see cref="ITunesDbReader"/>. This is the
/// regression net for MHIT field offsets and MHOD string encoding - if a writer
/// offset and a reader offset ever drift, a field comes back wrong here.
/// </summary>
public class ITunesDbRoundTripTests
{
    /// <summary>Writes tracks into a fresh DB, serializes, and reads them back via the reader.</summary>
    private static List<ITunesDbReader.ITunesTrack> WriteThenRead(string mount, params NewTrack[] tracks)
    {
        var doc = ITunesDbWriter.CreateEmpty();
        foreach (var t in tracks)
        {
            ITunesDbWriter.AddTrack(doc, t);
        }
        ITunesDbChunkTree.Normalize(doc.Root);
        var bytes = ITunesDbChunkTree.Serialize(doc);

        var tmp = Path.Combine(Path.GetTempPath(), $"orgz_itdb_{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(tmp, bytes);
            ITunesDbReader.ReadAll(tmp, mount, out var read, out _);
            return read;
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
        }
    }

    private static NewTrack FullTrack => new()
    {
        TrackId = 1,
        IpodPath = ":iPod_Control:Music:F00:NEVR.m4a",
        Title = "Neverender",
        Artist = "Justice",
        Album = "Hyperdrama",
        Genre = "Electronic",
        Year = 2024,
        TrackNumber = 1,
        FileSize = 12_345_678,
        LengthMs = 234_000,
        Bitrate = 256,
        SampleRate = 44100,
        DateAddedUtc = new DateTime(2026, 6, 8, 12, 0, 0, DateTimeKind.Utc),
        Dbid = 0x4b4c9a84483caeb5UL,
        HasArtwork = true,
        ArtworkSize = 100_000,
    };

    [Fact]
    public void Empty_db_reads_back_with_no_tracks_or_playlists()
    {
        var doc = ITunesDbWriter.CreateEmpty();
        ITunesDbChunkTree.Normalize(doc.Root);
        var bytes = ITunesDbChunkTree.Serialize(doc);
        var tmp = Path.Combine(Path.GetTempPath(), $"orgz_itdb_{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(tmp, bytes);
            ITunesDbReader.ReadAll(tmp, @"X:\", out var tracks, out var playlists);
            Assert.Empty(tracks);
            Assert.Empty(playlists);   // the only playlist is the hidden master
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Fact]
    public void Full_metadata_roundtrips()
    {
        var t = Assert.Single(WriteThenRead(@"X:\", FullTrack));
        Assert.Equal(1u, t.TrackId);
        Assert.Equal("Neverender", t.Title);
        Assert.Equal("Justice", t.Artist);
        Assert.Equal("Hyperdrama", t.Album);
        Assert.Equal("Electronic", t.Genre);
        Assert.Equal(2024, t.Year);
        Assert.Equal(1, t.TrackNumber);
        Assert.Equal(12_345_678, t.FileSize);
        Assert.Equal(234_000, t.DurationMs);
        Assert.Equal(256, t.Bitrate);
        Assert.Equal(44100, t.SampleRate);
    }

    [Fact]
    public void DateAdded_roundtrips_for_a_modern_date()
    {
        // Guards the uint32/1904-epoch overflow: a 2026 date must NOT come back as ~1972.
        var t = Assert.Single(WriteThenRead(@"X:\", FullTrack));
        Assert.Equal(new DateTime(2026, 6, 8, 12, 0, 0, DateTimeKind.Utc), t.DateAdded);
    }

    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]   // boundary: still < 65536 so the <<16 packing is lossless
    [InlineData(32000)]
    [InlineData(22050)]
    public void SampleRate_roundtrips(int rate)
    {
        var t = Assert.Single(WriteThenRead(@"X:\", FullTrack with { SampleRate = rate }));
        Assert.Equal(rate, t.SampleRate);
    }

    [Fact]
    public void FilePath_converts_ipod_colon_path_to_mount_relative()
    {
        var t = Assert.Single(WriteThenRead(@"X:\", FullTrack));
        Assert.EndsWith("NEVR.m4a", t.FilePath);
        Assert.Contains("iPod_Control", t.FilePath);
    }

    [Theory]
    [InlineData("Café del Mar")]
    [InlineData("日本語のタイトル")]
    [InlineData("emoji 🎵 title")]
    [InlineData("quote \" and amp & and <tag>")]
    public void Unicode_titles_roundtrip(string title)
    {
        var t = Assert.Single(WriteThenRead(@"X:\", FullTrack with { Title = title }));
        Assert.Equal(title, t.Title);
    }

    [Fact]
    public void Absent_optional_fields_read_back_null()
    {
        var bare = new NewTrack
        {
            TrackId = 1,
            IpodPath = ":iPod_Control:Music:F00:BARE.m4a",
            // no title/artist/album/genre
        };
        var t = Assert.Single(WriteThenRead(@"X:\", bare));
        Assert.Null(t.Title);
        Assert.Null(t.Artist);
        Assert.Null(t.Album);
        Assert.Null(t.Genre);
        Assert.EndsWith("BARE.m4a", t.FilePath);
    }

    [Fact]
    public void Multiple_tracks_roundtrip_in_order_with_distinct_ids()
    {
        var tracks = Enumerable.Range(1, 5).Select(i => new NewTrack
        {
            TrackId = (uint)i,
            IpodPath = $":iPod_Control:Music:F00:T{i}.m4a",
            Title = $"Song {i}",
            Artist = $"Artist {i}",
        }).ToArray();

        var read = WriteThenRead(@"X:\", tracks);

        Assert.Equal(5, read.Count);
        Assert.Equal([1u, 2u, 3u, 4u, 5u], read.Select(t => t.TrackId));
        Assert.Equal(["Song 1", "Song 2", "Song 3", "Song 4", "Song 5"], read.Select(t => t.Title));
    }

    [Fact]
    public void Normalize_is_idempotent_byte_for_byte()
    {
        var doc = ITunesDbWriter.CreateEmpty();
        ITunesDbWriter.AddTrack(doc, FullTrack);
        ITunesDbChunkTree.Normalize(doc.Root);
        var first = ITunesDbChunkTree.Serialize(doc);

        // Re-parsing and re-normalizing well-formed bytes must reproduce them exactly.
        var reparsed = ITunesDbChunkTree.Parse(first);
        ITunesDbChunkTree.Normalize(reparsed.Root);
        var second = ITunesDbChunkTree.Serialize(reparsed);

        Assert.Equal(first, second);
    }
}
