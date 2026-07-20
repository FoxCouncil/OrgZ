// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Services;

namespace OrgZ.Tests;

/// <summary>
/// Structure + guard tests for the iTunesDB mutators: an empty DB is well-formed,
/// ids advance, and AddTrack places the MHIT in the track dataset and an MHIP in
/// the master playlist (and refuses a malformed DB).
/// </summary>
public class ITunesDbWriterTests
{
    private static NewTrack Sample(uint id) => new()
    {
        TrackId = id,
        IpodPath = $":iPod_Control:Music:F00:T{id}.m4a",
        Title = $"Track {id}",
        Artist = "Artist",
        Album = "Album",
        Genre = "Genre",
        Year = 2024,
        TrackNumber = (int)id,
        FileSize = 1000 + id,
        LengthMs = 200_000,
        Bitrate = 256,
        SampleRate = 44100,
        DateAddedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        Dbid = 0x1000UL + id,
    };

    private static ITunesDbChunk TracksMhsd(ITunesDbDocument doc)
        => doc.Root.Children.Single(c => c.Magic == "mhsd" && c.ReadHeaderInt32(0x0C) == 1);

    private static ITunesDbChunk Master(ITunesDbDocument doc)
        => doc.Root.Children.Single(c => c.Magic == "mhsd" && c.ReadHeaderInt32(0x0C) == 3)
               .Children.Single(c => c.Magic == "mhyp" && c.Header[0x14] == 1);

    [Fact]
    public void AddTrack_writes_soundcheck_from_replaygain()
    {
        // iTunes Sound Check units: 1000·10^(−gain/10). A quiet track (−6.5 dB gain means it plays
        // LOUD and needs attenuating... the field encodes the correction the firmware applies.
        var doc = ITunesDbWriter.CreateEmpty();
        ITunesDbWriter.AddTrack(doc, Sample(1) with { ReplayGainDb = -6.5 });
        var mhit = TracksMhsd(doc).Children.Single(c => c.Magic == "mhit");
        Assert.Equal(4467, mhit.ReadHeaderInt32(0x4C));

        // No gain -> field stays 0 (firmware treats it as "no adjustment").
        var plain = ITunesDbWriter.CreateEmpty();
        ITunesDbWriter.AddTrack(plain, Sample(1));
        Assert.Equal(0, TracksMhsd(plain).Children.Single(c => c.Magic == "mhit").ReadHeaderInt32(0x4C));
    }

    [Fact]
    public void ReorderMasterPlaylists_rewrites_mhip_order_and_positions()
    {
        var doc = ITunesDbWriter.CreateEmpty();
        ITunesDbWriter.AddTrack(doc, Sample(1));
        ITunesDbWriter.AddTrack(doc, Sample(2));
        ITunesDbWriter.AddTrack(doc, Sample(3));

        // Reorder mentioning only two ids - the unmentioned track keeps its relative slot at the end.
        ITunesDbWriter.ReorderMasterPlaylists(doc, [3u, 1u]);

        var mhips = Master(doc).Children.Where(c => c.Magic == "mhip").ToList();
        Assert.Equal(new[] { 3, 1, 2 }, mhips.Select(c => c.ReadHeaderInt32(0x18)).ToArray());
        // Every entry carries exactly one position MHOD (type 100), re-stamped 0..n - without it the
        // firmware reads the playlist as empty.
        Assert.All(mhips, m => Assert.Single(m.Children, c => c.Magic == "mhod" && c.ReadHeaderInt32(0x0C) == 100));
    }

    [Fact]
    public void CreateEmpty_is_well_formed_and_empty()
    {
        var doc = ITunesDbWriter.CreateEmpty();
        Assert.Equal("mhbd", doc.Root.Magic);
        Assert.DoesNotContain(TracksMhsd(doc).Children, c => c.Magic == "mhit");
        Assert.Equal(1u, ITunesDbWriter.NextTrackId(doc));
    }

    [Fact]
    public void NextTrackId_increments_with_each_added_track()
    {
        var doc = ITunesDbWriter.CreateEmpty();
        for (uint expected = 1; expected <= 3; expected++)
        {
            Assert.Equal(expected, ITunesDbWriter.NextTrackId(doc));
            ITunesDbWriter.AddTrack(doc, Sample(ITunesDbWriter.NextTrackId(doc)));
        }
        Assert.Equal(4u, ITunesDbWriter.NextTrackId(doc));
    }

    [Fact]
    public void AddTrack_places_mhit_in_tracks_and_mhip_in_master()
    {
        var doc = ITunesDbWriter.CreateEmpty();
        ITunesDbWriter.AddTrack(doc, Sample(1));

        Assert.Single(TracksMhsd(doc).Children, c => c.Magic == "mhit");
        Assert.Single(Master(doc).Children, c => c.Magic == "mhip");
    }

    [Fact]
    public void AddTrack_mhip_references_the_track_id()
    {
        var doc = ITunesDbWriter.CreateEmpty();
        ITunesDbWriter.AddTrack(doc, Sample(7));
        var mhip = Master(doc).Children.Single(c => c.Magic == "mhip");
        Assert.Equal(7, mhip.ReadHeaderInt32(0x18));
    }

    [Fact]
    public void AddTrack_writes_64bit_dbid_at_0x70()
    {
        var doc = ITunesDbWriter.CreateEmpty();
        ITunesDbWriter.AddTrack(doc, Sample(1) with { Dbid = 0x4b4c9a84483caeb5UL });
        var mhit = TracksMhsd(doc).Children.Single(c => c.Magic == "mhit");

        ulong dbid = 0;
        for (int i = 0; i < 8; i++)
        {
            dbid |= (ulong)mhit.Header[0x70 + i] << (8 * i);
        }
        Assert.Equal(0x4b4c9a84483caeb5UL, dbid);
    }

    [Fact]
    public void AddTrack_sets_artwork_flags_only_when_art_present()
    {
        var doc = ITunesDbWriter.CreateEmpty();
        ITunesDbWriter.AddTrack(doc, Sample(1) with { HasArtwork = true, ArtworkSize = 100_000 });
        ITunesDbWriter.AddTrack(doc, Sample(2) with { HasArtwork = false });

        var mhits = TracksMhsd(doc).Children.Where(c => c.Magic == "mhit").ToList();
        Assert.Equal(1, mhits[0].Header[0xA4]);          // has_artwork
        Assert.Equal(100_000, mhits[0].ReadHeaderInt32(0x80));
        Assert.Equal(0, mhits[1].Header[0xA4]);
    }

    [Fact]
    public void AddTrack_emits_one_mhod_per_present_string_field()
    {
        var doc = ITunesDbWriter.CreateEmpty();
        // Only path + title present -> 2 string MHODs.
        ITunesDbWriter.AddTrack(doc, new NewTrack
        {
            TrackId = 1,
            IpodPath = ":iPod_Control:Music:F00:X.m4a",
            Title = "Only Title",
        });
        var mhit = TracksMhsd(doc).Children.Single(c => c.Magic == "mhit");
        Assert.Equal(2, mhit.Children.Count(c => c.Magic == "mhod"));
    }

    [Fact]
    public void AddTrack_throws_without_track_dataset()
    {
        var doc = ITunesDbWriter.CreateEmpty();
        doc.Root.Children.RemoveAll(c => c.Magic == "mhsd" && c.ReadHeaderInt32(0x0C) == 1);
        Assert.Throws<InvalidDataException>(() => ITunesDbWriter.AddTrack(doc, Sample(1)));
    }

    [Fact]
    public void AddTrack_throws_without_master_playlist()
    {
        var doc = ITunesDbWriter.CreateEmpty();
        doc.Root.Children.RemoveAll(c => c.Magic == "mhsd" && c.ReadHeaderInt32(0x0C) == 3);
        Assert.Throws<InvalidDataException>(() => ITunesDbWriter.AddTrack(doc, Sample(1)));
    }
}
